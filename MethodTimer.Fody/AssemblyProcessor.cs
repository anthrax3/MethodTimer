﻿using System.Linq;
using Mono.Cecil;

public partial class ModuleWeaver
{
    void ProcessAssembly()
    {
        if (ModuleDefinition.Assembly.ContainsTimeAttribute() || ModuleDefinition.ContainsTimeAttribute())
        {
            foreach (var type in types)
            {
                if (type.IsInterceptor())
                {
                    continue;
                }

                if (type.IsCompilerGenerated())
                {
                    continue;
                }

                foreach (var method in type.ConcreteMethods())
                {
                    ProcessMethod(method);
                }
            }

            return;
        }

        foreach (var type in types)
        {
            if (type.IsInterceptor())
            {
                continue;
            }

            if (type.IsCompilerGenerated())
            {
                continue;
            }

            if (type.ContainsTimeAttribute())
            {
                foreach (var method in type.ConcreteMethods())
                {
                    ProcessMethod(method);
                }

                continue;
            }

            foreach (var method in type.ConcreteMethods().Where(x => x.ContainsTimeAttribute()))
            {
                ProcessMethod(method);
            }
        }
    }

    void ProcessMethod(MethodDefinition method)
    {
        if (method.IsYield())
        {
            if (method.ContainsTimeAttribute())
            {
                LogError("Could not process '" + method.FullName + "' since methods that yield are currently not supported. Please remove the [Time] attribute from that method.");
                return;
            }

            LogInfo("Skipping '" + method.FullName + "' since methods that yield are not supported.");
            return;
        }

        var timeAttribute = method.GetTimeAttribute();
        if (timeAttribute != null)
        {
            var format = timeAttribute.ConstructorArguments.FirstOrDefault().Value as string;
            if (!string.IsNullOrWhiteSpace(format))
            {
                var hasErrors = false;

                var logWithMessageMethod = LogWithMessageMethod;
                if (logWithMessageMethod == null)
                {
                    hasErrors = true;
                    LogError("Feature with parameter formatting is being used, but no useable log method can be found. Either disable the feature usage or update the logger signature to 'public static void Log(MethodBase methodBase, long milliseconds, string message)'");
                }

                var info = parameterFormattingProcessor.ParseParameterFormatting(format);
                for (var i = 0; i < info.ParameterNames.Count; i++)
                {
                    var parameterName = info.ParameterNames[i];
                    var containsParameter = method.Parameters.Any(x => x.Name.Equals(parameterName));
                    if (!containsParameter)
                    {
                        hasErrors = true;
                        LogError(string.Format("Could not process '" + method.FullName + "' because the format uses '{0}' which is not available as method parameter.", parameterName));
                    }
                }

                if (hasErrors)
                {
                    return;
                }
            }
        }

        if (method.IsAsync())
        {
            var asyncProcessor = new AsyncMethodProcessor
            {
                ModuleWeaver = this,
                Method = method,
            };

            asyncProcessor.Process();
            return;
        }

        var methodProcessor = new MethodProcessor
        {
            ModuleWeaver = this,
            Method = method,
        };

        methodProcessor.Process();
    }
}