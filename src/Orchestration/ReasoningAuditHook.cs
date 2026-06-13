using System.Security.Cryptography;
using System.Text;
using AgentGovernance.Audit;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;

namespace fuseraft.Orchestration;

/// <summary>
/// An <see cref="IOrchestrationHook"/> that appends a SHA-256 digest of each turn's
/// reasoning block to the hash-chain audit log.
///
/// <para>
/// The full reasoning text is never written to the audit record — only the first 16 hex
/// characters of the SHA-256 hash are included. This makes the reasoning tamper-evident
/// (anyone with access to the raw events.jsonl can verify the hash matches) without
/// exposing potentially sensitive model thinking in the compliance record.
/// </para>
/// </summary>
public sealed class ReasoningAuditHook(AuditLogger auditLogger) : IOrchestrationHook
{
    public Task OnEventAsync(OrchestrationEvent evt, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(evt.EventType, EventTypes.Reasoning, StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var text = ExtractText(evt.Payload);
        if (string.IsNullOrWhiteSpace(text))
            return Task.CompletedTask;

        var hash    = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        var agentId = evt.Agent ?? "unknown";
        auditLogger.Log(agentId, $"ReasoningEmitted:{hash[..16]}", "allow");

        return Task.CompletedTask;
    }

    private static string? ExtractText(object? payload)
    {
        if (payload is null) return null;
        try
        {
            var prop = payload.GetType().GetProperty("text",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.IgnoreCase);
            return prop?.GetValue(payload)?.ToString();
        }
        catch { return null; }
    }
}
