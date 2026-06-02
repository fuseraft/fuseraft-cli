using Microsoft.Extensions.AI;

namespace fuseraft.Infrastructure.Plugins;

/// <summary>
/// Transparent proxy that offloads oversized tool results to the
/// <see cref="ToolResultArtifactStore"/> so large outputs never enter the conversation
/// history verbatim. The inline result is replaced with a compact reference stub that
/// tells the agent how to access specific sections via targeted follow-up reads.
/// </summary>
internal sealed class ToolResultOffloadFilter(AIFunction inner, ToolResultArtifactStore store)
    : DelegatingAIFunction(inner)
{
    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var result = await InnerFunction.InvokeAsync(arguments, cancellationToken);

        if (result is string s)
        {
            var hint = ToolCallHelper.SummarizeArgs(arguments) ?? string.Empty;
            if (store.TryOffload(Name, hint, s, out var stub))
                return stub;
        }

        return result;
    }
}
