using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

public class AsyncMethodProcessor
{
    public ModuleWeaver ModuleWeaver;
    public MethodDefinition Method;
    MethodBody body;
    FieldDefinition stopwatchField;
    TypeDefinition stateMachineType;
    List<Instruction> returnPoints;
    ParameterFormattingProcessor parameterFormattingProcessor = new ParameterFormattingProcessor();

    public void Process()
    {
        try
        {
            InnerProcess();
        }
        catch (Exception exception)
        {
            throw new Exception($"An error occurred processing '{Method.FullName}'. Error: {exception.Message}", exception);
        }
    }

    void InnerProcess()
    {
        var asyncAttribute = Method.GetAsyncStateMachineAttribute();
        stateMachineType = asyncAttribute.ConstructorArguments
            .Select(ctor => (TypeDefinition)ctor.Value)
            .Single();
        var moveNextMethod = stateMachineType.Methods
            .Single(x => x.Name == "MoveNext");
        body = moveNextMethod.Body;

        body.SimplifyMacros();

        returnPoints = GetAsyncReturns(body.Instructions)
            .ToList();

        // First, fall back to old mechanism
        int index;

        // Check roslyn usage
        var firstStateUsage = (from instruction in body.Instructions
                               let fieldReference = instruction.Operand as FieldReference
                               where instruction.OpCode == OpCodes.Ldfld &&
                                     fieldReference != null &&
                                     fieldReference.Name.Contains("__state")
                               select instruction).FirstOrDefault();
        if (firstStateUsage == null)
        {
            // Probably compiled without roslyn, inject at first line
            index = 0;
        }
        else
        {
            // Initial code looks like this (hence the -1):
            //
            // <== this is where we want to start the stopwatch
            // ldarg.0
            // ldfld __state
            // stloc.0
            // ldloc.0
            index = body.Instructions.IndexOf(firstStateUsage) - 1;
        }

        InjectStopwatch(index, body.Instructions[index]);

        HandleReturns();
        body.InitLocals = true;
        body.OptimizeMacros();
    }

    void InjectStopwatch(int index, Instruction nextInstruction)
    {
        var boolVariable = new VariableDefinition(ModuleWeaver.BooleanType.Resolve());
        body.Variables.Add(boolVariable);

        stopwatchField = new FieldDefinition("methodTimerStopwatch", new FieldAttributes(), ModuleWeaver.StopwatchType);
        stateMachineType.Fields.Add(stopwatchField);
        body.Insert(index, new[]
        {
            // This code looks like this:
            // if (_stopwatch == null)
            // {
            //    _stopwatch = Stopwatch.StartNew();
            // }

            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Ldfld, stopwatchField),
            Instruction.Create(OpCodes.Ldnull),
            Instruction.Create(OpCodes.Ceq),
            Instruction.Create(OpCodes.Stloc, boolVariable),
            Instruction.Create(OpCodes.Ldloc, boolVariable),
            Instruction.Create(OpCodes.Brfalse_S, nextInstruction),
            Instruction.Create(OpCodes.Ldarg_0),
            Instruction.Create(OpCodes.Call, ModuleWeaver.StartNewMethod),
            Instruction.Create(OpCodes.Stfld, stopwatchField)
        });
    }

    void HandleReturns()
    {
        foreach (var returnPoint in returnPoints)
        {
            FixReturn(returnPoint);
        }
    }

    static IEnumerable<Instruction> GetAsyncReturns(Collection<Instruction> instructions)
    {
        // There are 3 possible return points:
        //
        // 1) async code:
        //      awaiter.GetResult();
        //      awaiter = new TaskAwaiter();
        //
        // 2) exception handling
        //      L_00d5: ldloc.1
        //      L_00d6: call instance void [mscorlib]System.Runtime.CompilerServices.AsyncTaskMethodBuilder::SetException(class [mscorlib]System.Exception)
        //
        // 3) all other returns
        //
        // We can do this smart by searching for all leave and leave_S op codes and check if they point to the last
        // instruction of the method. This equals a "return" call.

        var returnStatements = new List<Instruction>();

        var possibleReturnStatements = new List<Instruction>();

        // Look for the last leave statement (that is the line all "return" statements go to)
        for (var i = instructions.Count - 1; i >= 0; i--)
        {
            var instruction = instructions[i];
            if (instruction.IsLeaveInstruction())
            {
                possibleReturnStatements.Add(instructions[i + 1]);
                break;
            }
        }

        for (var i = 0; i < instructions.Count; i++)
        {
            var instruction = instructions[i];
            if (instruction.IsLeaveInstruction())
            {
                if (possibleReturnStatements.Any(x => ReferenceEquals(instruction.Operand, x)))
                {
                    // This is a return statement, this covers scenarios 1 and 3
                    returnStatements.Add(instruction);
                }
                else
                {
                    // Check if we set an exception in this block, this covers scenario 2
                    for (var j = i - 3; j < i; j++)
                    {
                        var previousInstruction = instructions[j];
                        if (previousInstruction.OpCode == OpCodes.Call)
                        {
                            if (previousInstruction.Operand is MethodReference methodReference)
                            {
                                if (methodReference.Name.Equals("SetException"))
                                {
                                    returnStatements.Add(instruction);
                                    break;
                                }
                            }
                        }
                    }
                }
            }
        }

        return returnStatements;
    }

    void FixReturn(Instruction returnPoint)
    {
        var opCode = returnPoint.OpCode;
        var operand = returnPoint.Operand as Instruction;

        returnPoint.OpCode = OpCodes.Nop;
        returnPoint.Operand = null;

        var instructions = body.Instructions;
        var indexOf = instructions.IndexOf(returnPoint);
        foreach (var instruction in GetWriteTimeInstruction(Method))
        {
            indexOf++;
            instructions.Insert(indexOf, instruction);
        }

        indexOf++;

        if (opCode == OpCodes.Leave || opCode == OpCodes.Leave_S)
        {
            instructions.Insert(indexOf, Instruction.Create(opCode, operand));
        }
        else
        {
            instructions.Insert(indexOf, Instruction.Create(opCode));
        }
    }


    IEnumerable<Instruction> GetWriteTimeInstruction(MethodDefinition methodDefinition)
    {
        yield return Instruction.Create(OpCodes.Ldarg_0);
        yield return Instruction.Create(OpCodes.Ldfld, stopwatchField);
        yield return Instruction.Create(OpCodes.Call, ModuleWeaver.StopMethod);

        var logWithMessageMethod = ModuleWeaver.LogWithMessageMethod;
        var logMethod = ModuleWeaver.LogMethod;

        if (logWithMessageMethod != null)
        {
            // Important notes:
            // 1. Because async works with state machines, use the state machine & fields instead of method & variables.
            // 2. The ldarg_0 calls are required to load the state machine class and is required before every field call.

            //var moveNextMethodDefinition = stateMachineType.Methods.First(x => x.Name == "MoveNext");
            //var formattedVariableDefinition = new VariableDefinition(ModuleWeaver.ModuleDefinition.TypeSystem.String);
            //moveNextMethodDefinition.Body.Variables.Add(formattedVariableDefinition);

            var formattedFieldDefinition = stateMachineType.Fields.FirstOrDefault(x => x.Name.Equals("methodTimerMessage"));
            if (formattedFieldDefinition == null)
            {
                formattedFieldDefinition = new FieldDefinition("methodTimerMessage", FieldAttributes.Private | FieldAttributes.CompilerControlled, ModuleWeaver.ModuleDefinition.TypeSystem.String);
                stateMachineType.Fields.Add(formattedFieldDefinition);
            }

            // Load everything for a string format
            var timeAttribute = methodDefinition.GetTimeAttribute();
            if (timeAttribute != null)
            {
                var value = timeAttribute.ConstructorArguments.FirstOrDefault().Value as string;
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var info = parameterFormattingProcessor.ParseParameterFormatting(value);

                    yield return Instruction.Create(OpCodes.Ldarg_0);
                    yield return Instruction.Create(OpCodes.Ldstr, info.Format);
                    yield return Instruction.Create(OpCodes.Ldc_I4, info.ParameterNames.Count);
                    yield return Instruction.Create(OpCodes.Newarr, ModuleWeaver.ModuleDefinition.TypeSystem.Object);

                    for (var i = 0; i < info.ParameterNames.Count; i++)
                    {
                        yield return Instruction.Create(OpCodes.Dup);
                        yield return Instruction.Create(OpCodes.Ldc_I4, i);

                        var field = stateMachineType.Fields.FirstOrDefault(x => x.Name.Equals(info.ParameterNames[i]));
                        if (field == null)
                        {
                            ModuleWeaver.LogError($"Parameter '{info.ParameterNames[i]}' is not available on the async state machine. Probably it has been optimized away by the compiler. Please update the format so it excludes this parameter.");

                            yield return Instruction.Create(OpCodes.Ldnull);
                        }
                        else
                        {

                            yield return Instruction.Create(OpCodes.Ldarg_0);
                            yield return Instruction.Create(OpCodes.Ldfld, field);

                            if (field.FieldType.IsBoxingRequired(ModuleWeaver.ModuleDefinition.TypeSystem.Object))
                            {
                                yield return Instruction.Create(OpCodes.Box, ModuleWeaver.ModuleDefinition.ImportReference(field.FieldType));
                            }
                        }

                        yield return Instruction.Create(OpCodes.Stelem_Ref);
                    }

                    yield return Instruction.Create(OpCodes.Call, ModuleWeaver.StringFormatWithArray);
                }
                else
                {
                    // Load null a string
                    yield return Instruction.Create(OpCodes.Ldarg_0);
                    yield return Instruction.Create(OpCodes.Ldnull);
                }

                yield return Instruction.Create(OpCodes.Stfld, formattedFieldDefinition);
            }

            // Handle call to log method
            yield return Instruction.Create(OpCodes.Ldtoken, methodDefinition);
            yield return Instruction.Create(OpCodes.Ldtoken, methodDefinition.DeclaringType);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.GetMethodFromHandle);
            yield return Instruction.Create(OpCodes.Ldarg_0);
            yield return Instruction.Create(OpCodes.Ldfld, stopwatchField);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ElapsedMilliseconds);
            yield return Instruction.Create(OpCodes.Ldarg_0);
            yield return Instruction.Create(OpCodes.Ldfld, formattedFieldDefinition);
            yield return Instruction.Create(OpCodes.Call, logWithMessageMethod);
        }
        else if (logMethod != null)
        {
            yield return Instruction.Create(OpCodes.Ldtoken, methodDefinition);
            yield return Instruction.Create(OpCodes.Ldtoken, methodDefinition.DeclaringType);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.GetMethodFromHandle);
            yield return Instruction.Create(OpCodes.Ldarg_0);
            yield return Instruction.Create(OpCodes.Ldfld, stopwatchField);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ElapsedMilliseconds);
            yield return Instruction.Create(OpCodes.Call, logMethod);
        }
        else
        {
            yield return Instruction.Create(OpCodes.Ldstr, methodDefinition.MethodName());
            yield return Instruction.Create(OpCodes.Ldarg_0);
            yield return Instruction.Create(OpCodes.Ldfld, stopwatchField);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ElapsedMilliseconds);
            yield return Instruction.Create(OpCodes.Box, ModuleWeaver.ModuleDefinition.TypeSystem.Int64);
            yield return Instruction.Create(OpCodes.Ldstr, "ms");
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.ConcatMethod);
            yield return Instruction.Create(OpCodes.Call, ModuleWeaver.TraceWriteLineMethod);
        }
    }
}

