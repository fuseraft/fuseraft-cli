using System.Text.Json.Serialization;

namespace fuseraft.Core.Models.Config;

/// <summary>
/// Configures skill discovery (see <c>fuseraft.Core.Skills.FuseraftSkillsSources</c>), shared by
/// the REPL, <c>fuseraft run</c>, and the <c>fuseraft skills</c> commands.
/// </summary>
public record SkillsConfig
{
    public const int DefaultSearchDepth = 5;
    public const int MaxSearchDepth     = 10;

    /// <summary>
    /// How many directory levels inside each skill directory are scanned for resources and
    /// scripts. <c>1</c> is the skill root only; <c>3</c> reaches
    /// <c>references/components/map_component.md</c>. A file deeper than this is invisible to
    /// <c>read_skill_resource</c>/<c>run_skill_script</c> ("Resource not found") even though
    /// it exists on disk. Null uses <see cref="DefaultSearchDepth"/>.
    /// </summary>
    [JsonPropertyName("searchDepth")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SearchDepth { get; init; }
}
