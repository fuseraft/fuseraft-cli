using System.Reflection;
using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Transparent proxy that fires an async callback before each tool invocation and
/// validates that all required parameters are present. If any required parameters are
/// missing, returns a structured error the model can read and correct without calling
/// the inner function.
/// </summary>
internal sealed class NotifyingAIFunction(
    AIFunction inner,
    string agentName,
    Func<string, string, string?, Task> onBeforeInvoke)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        await onBeforeInvoke(agentName, Name, ToolCallHelper.SummarizeArgs(arguments));

        var validationError = ValidateRequiredParameters(arguments);
        if (validationError is not null)
            return validationError;

        return await InnerFunction.InvokeAsync(arguments, cancellationToken);
    }

    private string? ValidateRequiredParameters(AIFunctionArguments arguments)
    {
        var method = InnerFunction.GetType()
            .GetProperty("UnderlyingMethod", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(InnerFunction) as MethodInfo;

        if (method is null)
            return null;

        var missing  = new List<string>();
        var nullCtx  = new NullabilityInfoContext();

        foreach (var param in method.GetParameters())
        {
            if (param.ParameterType == typeof(CancellationToken)) continue;

            bool isOptional = param.IsOptional || param.HasDefaultValue;
            bool isNullable = param.ParameterType.IsValueType
                ? Nullable.GetUnderlyingType(param.ParameterType) is not null
                : nullCtx.Create(param).WriteState != NullabilityState.NotNull;

            if (!isOptional && !isNullable && !arguments.ContainsKey(param.Name!))
                missing.Add(param.Name!);
        }

        if (missing.Count == 0)
            return null;

        var paramList = string.Join(", ", missing.Select(p => $"'{p}'"));
        var plural    = missing.Count > 1 ? "parameters" : "parameter";
        return $"[ERROR] Tool call failed: required {plural} {paramList} not provided.\n\n" +
               $"To fix: Call {Name} again with all required parameters included.";
    }
}
