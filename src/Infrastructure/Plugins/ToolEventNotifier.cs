using Microsoft.Extensions.AI;
using fuseraft.Orchestration;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Transparent proxy that fires a <c>sub_agent_tool_call</c> event the moment a tool begins
/// executing, making sub-agent activity visible between <c>sub_agent_start</c> and
/// <c>sub_agent_end</c> in the event log.
/// </summary>
internal sealed class ToolEventNotifier(AIFunction inner, EventEmitter emitter, string? agentName)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        await emitter.EmitAsync("sub_agent_tool_call",
            agent:   agentName,
            payload: new { tool = Name, args = SummarizeArgs(arguments) });
        
        // Deterministically validate required parameters BEFORE invocation
        var validationError = ValidateRequiredParameters(arguments);
        if (validationError is not null)
            return validationError;
        
        return await InnerFunction.InvokeAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Validates that all required parameters are present. Returns a structured error message if any are missing.
    /// </summary>
    private string? ValidateRequiredParameters(AIFunctionArguments arguments)
    {
        // Access the underlying C# method to get accurate parameter metadata
        var method = InnerFunction.GetType()
            .GetProperty("UnderlyingMethod", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(InnerFunction) as System.Reflection.MethodInfo;

        if (method is null)
            return null; // Can't validate without method metadata

        var missing = new List<string>();
        foreach (var param in method.GetParameters())
        {
            // Skip CancellationToken
            if (param.ParameterType == typeof(CancellationToken))
                continue;

            // A parameter is required if it's not optional and not nullable
            bool isOptional = param.IsOptional || param.HasDefaultValue;
            bool isNullable = param.ParameterType.IsClass || 
                              Nullable.GetUnderlyingType(param.ParameterType) != null;

            if (!isOptional && !isNullable && !arguments.ContainsKey(param.Name!))
            {
                missing.Add(param.Name!);
            }
        }

        if (missing.Count == 0)
            return null;

        var paramList = string.Join(", ", missing.Select(p => $"'{p}'"));
        var plural = missing.Count > 1 ? "parameters" : "parameter";
        return $"[ERROR] Tool call failed: required {plural} {paramList} not provided.\n\n" +
               $"To fix: Call {Name} again with all required parameters included.";
    }

    private static string? SummarizeArgs(AIFunctionArguments? args)
    {
        if (args is null) return null;
        ReadOnlySpan<string> priority = ["path", "command", "script", "url", "key", "query", "message", "branch"];
        foreach (var key in priority)
        {
            var match = args.FirstOrDefault(kv =>
                string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (match.Value is not null)
            {
                var val = match.Value.ToString() ?? string.Empty;
                return $"{key}={System.Net.WebUtility.HtmlDecode(val.Length > 60 ? val[..60] : val)}";
            }
        }
        var first = args.FirstOrDefault();
        if (first.Value is null) return null;
        var fv = first.Value.ToString() ?? string.Empty;
        return $"{first.Key}={System.Net.WebUtility.HtmlDecode(fv.Length > 60 ? fv[..60] : fv)}";
    }
}
