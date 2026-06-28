using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Transparent proxy that emits structured tool_call, tool_result, tool_error, and
/// tool_timeout events to the session event log for every tool invocation.
///
/// Sits inside the <see cref="ToolResultOffloadFilter"/> in the filter chain so that the
/// logged result reflects the raw tool output before any offloading occurs. The
/// artifact_created event emitted by the offload filter then signals when the raw result
/// was replaced by a stub.
/// </summary>
internal sealed class ToolResultLoggingFilter(AIFunction inner, EventEmitter emitter)
    : DelegatingAIFunction(inner)
{
    private const int MaxArgValueChars   = 500;
    private const int MaxShellOutputChars = 500;
    private const int MaxErrorChars      = 300;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var args = BuildArgDict(arguments);
        _ = emitter.EmitAsync(EventTypes.ToolCall, payload: new { tool_name = Name, args });

        object? result;
        try
        {
            result = await InnerFunction.InvokeAsync(arguments, cancellationToken);
        }
        catch (Exception ex)
        {
            _ = emitter.EmitAsync(EventTypes.ToolError, payload: new { tool_name = Name, error = ex.Message });
            throw;
        }

        EmitResult(Name, result as string ?? result?.ToString() ?? string.Empty);
        return result;
    }

    private void EmitResult(string toolName, string resultText)
    {
        if (resultText.StartsWith("[TIMEOUT]", StringComparison.Ordinal))
        {
            _ = emitter.EmitAsync(EventTypes.ToolTimeout, payload: new { tool_name = toolName });
            return;
        }

        var isError = resultText.StartsWith("[EXIT",   StringComparison.Ordinal) ||
                      resultText.StartsWith("[ERROR]", StringComparison.Ordinal);
        if (isError)
        {
            var error = resultText.Length > MaxErrorChars
                ? resultText[..MaxErrorChars] + $"…[{resultText.Length - MaxErrorChars} chars truncated]"
                : resultText;
            _ = emitter.EmitAsync(EventTypes.ToolError, payload: new { tool_name = toolName, error });
            return;
        }

        string? shellOutput = null;
        if (toolName.Equals("shell_run", StringComparison.OrdinalIgnoreCase) && resultText.Length > 0)
        {
            shellOutput = resultText.Length > MaxShellOutputChars
                ? resultText[..MaxShellOutputChars] + $"…[{resultText.Length - MaxShellOutputChars} chars truncated]"
                : resultText;
        }

        _ = emitter.EmitAsync(EventTypes.ToolResult, payload: new
        {
            tool_name    = toolName,
            result_chars = resultText.Length,
            output       = shellOutput,
        });
    }

    private static Dictionary<string, string?> BuildArgDict(AIFunctionArguments arguments)
    {
        var dict = new Dictionary<string, string?>(arguments.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in arguments)
        {
            if (value is null) { dict[key] = null; continue; }
            var s = value is System.Text.Json.JsonElement je ? je.ToString() : value.ToString() ?? string.Empty;
            dict[key] = s.Length > MaxArgValueChars
                ? s[..MaxArgValueChars] + $"…[{s.Length - MaxArgValueChars} chars truncated]"
                : s;
        }
        return dict;
    }
}
