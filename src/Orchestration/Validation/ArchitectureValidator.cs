using Microsoft.Extensions.AI;
using fuseraft.Core;
using fuseraft.Core.Interfaces;
using fuseraft.Core.Models;
using fuseraft.Infrastructure;

namespace fuseraft.Orchestration.Validation;

/// <summary>
/// Routing validator that blocks a handoff when architecture layer violations are
/// present in the project source tree.
///
/// <para>
/// Loads the manifest from <c>.fuseraft/architecture.yaml</c> (or the path supplied at
/// construction) and delegates scanning to <see cref="ArchitectureScanner"/>. The
/// <paramref name="history"/> argument is not consulted — this validator checks current
/// filesystem state, not agent conversation content.
/// </para>
///
/// <para>
/// When no manifest file exists the validator passes unconditionally, so projects that
/// have not yet defined an architecture manifest are unaffected.
/// </para>
/// </summary>
public sealed class ArchitectureValidator(
    string? manifestPath = null,
    string? projectRoot = null,
    EvidenceStore? evidenceStore = null,
    ProvenanceRegistry? provenanceRegistry = null) : IRoutingValidator
{
    private readonly string _manifestPath = manifestPath ?? FuseraftPaths.LocalArchitectureManifest;
    private readonly string _projectRoot  = projectRoot  ?? Directory.GetCurrentDirectory();

    public async Task<RoutingValidationResult> ValidateAsync(
        IList<ChatMessage> history,
        CancellationToken ct = default)
    {
        var manifest = ArchitectureScanner.TryLoadManifest(_manifestPath);
        if (manifest is null)
            return RoutingValidationResult.Pass();

        var violations = await ArchitectureScanner.ScanAsync(manifest, _projectRoot, ct);

        if (violations.Count > 0)
        {
            await EmitViolationNodesAsync(violations, ct);

            var lines = violations
                .Take(10)
                .Select(v => $"  {v.File}:{v.Line} — {v.SourceLayer} → {v.TargetLayer} ({v.Namespace})");

            var summary = string.Join("\n", lines);
            if (violations.Count > 10)
                summary += $"\n  … and {violations.Count - 10} more violation(s)";

            return RoutingValidationResult.Fail(
                $"Architecture violations detected ({violations.Count}):\n{summary}\n\n" +
                "Fix the illegal dependencies before handing off.");
        }

        if (provenanceRegistry is not null)
        {
            var claim = new ClaimRecord
            {
                Claim   = "No architecture layer violations detected",
                Support = [EvidenceClass.Validator],
            };
            try { await provenanceRegistry.RecordAsync(claim, ct); }
            catch { /* best-effort */ }
        }

        return RoutingValidationResult.Pass();
    }

    private async Task EmitViolationNodesAsync(
        IReadOnlyList<ArchitectureViolation> violations,
        CancellationToken ct)
    {
        if (evidenceStore is null) return;

        var nodes = violations.Select(v => new EvidenceNode
        {
            NodeType   = "Violation",
            Agent      = "ArchitectureValidator",
            Path       = v.File,
            SymbolName = v.Namespace,
            Evidence   = $"{v.SourceLayer} → {v.TargetLayer}",
            Status     = "FAIL",
        }).ToList();

        try { await evidenceStore.RecordAsync(nodes, ct: ct); }
        catch { /* best-effort */ }
    }
}
