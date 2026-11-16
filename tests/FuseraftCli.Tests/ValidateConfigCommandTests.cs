using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using fuseraft.Cli.Commands;
using fuseraft.Cli.Protocol;
using fuseraft.Infrastructure.Plugins;

namespace fuseraft.Tests;

[Collection("FuseraftHomeEnv")] // --json tests swap the process-wide WireProtocol.Writer
public class ValidateConfigCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var file in _tempFiles)
            try { if (File.Exists(file)) File.Delete(file); } catch { /* best effort */ }
    }

    // A one-agent team; {agent} adds agent fields, {top} adds top-level keys.
    private static string Team(string agent = "", string top = "", string model = "gpt-4o") => $$"""
        Team: Test
        Agents:
          A:
            Instructions: ok
            Model: { ModelId: {{model}} }
        {{Indent(agent, 4)}}
        Flow:
          Start: A
          Steps:
            A:
              - To: end
        {{top}}
        """;

    private static string Indent(string text, int spaces) =>
        string.Join("\n", text.Split('\n').Select(l => l.Length > 0 ? new string(' ', spaces) + l : l));

    private string Yaml(string content)
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".yaml");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private string Json(string content)
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private static async Task<(int Exit, List<(string Level, string Message)> Issues, JsonElement Item)> Validate(
        string path, bool strict = false)
    {
        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var original = WireProtocol.Writer;
        var sw = new StringWriter();
        WireProtocol.Writer = sw;
        int exit;
        try { exit = await new ValidateConfigCommand(registry).ExecuteAsync(null!, new ValidateConfigSettings { Path = path, Json = true, Strict = strict }); }
        finally { WireProtocol.Writer = original; }

        var item = JsonDocument.Parse(sw.ToString()).RootElement.GetProperty("item").Clone();
        var issues = item.GetProperty("issues").EnumerateArray()
            .Select(i => (i.GetProperty("level").GetString()!, i.GetProperty("message").GetString()!))
            .ToList();
        return (exit, issues, item);
    }

    private static void HasError(List<(string Level, string Message)> issues, string text) =>
        Assert.Contains(issues, i => i.Level == "error" && i.Message.Contains(text));

    private static void HasWarning(List<(string Level, string Message)> issues, string text) =>
        Assert.Contains(issues, i => i.Level == "warning" && i.Message.Contains(text));

    private static void NoErrors(List<(string Level, string Message)> issues) =>
        Assert.DoesNotContain(issues, i => i.Level == "error");

    // ── File and format ────────────────────────────────────────────────────

    [Fact]
    public async Task FileNotFound_Errors()
    {
        var path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.yaml");
        var (exit, issues, _) = await Validate(path);

        Assert.Equal(1, exit);
        HasError(issues, "File not found");
    }

    [Fact]
    public async Task InvalidYaml_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml("Team:\n\t A: bad"));

        Assert.Equal(1, exit);
        HasError(issues, "Invalid YAML");
    }

    [Fact]
    public async Task InvalidJson_Errors()
    {
        var (exit, issues, item) = await Validate(Json("invalid { json"));

        Assert.Equal(1, exit);
        HasError(issues, "Invalid JSON");
        Assert.Equal(JsonValueKind.Null, item.GetProperty("config").ValueKind);
    }

    [Fact]
    public async Task ValidYaml_Returns0()
    {
        var (exit, issues, _) = await Validate(Yaml(Team()));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task ValidJson_Returns0()
    {
        var json = """
            {"Team": "Test",
             "Agents": {"A": {"Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}},
             "Flow": {"Start": "A", "Steps": {"A": [{"To": "end"}]}}}
            """;
        var (exit, issues, _) = await Validate(Json(json));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task YmlExtension_Accepted()
    {
        var path = Path.ChangeExtension(Path.GetTempFileName(), ".yml");
        File.WriteAllText(path, Team());
        _tempFiles.Add(path);

        var (exit, _, _) = await Validate(path);
        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task OldFormat_ReportsMigrateHint()
    {
        var (exit, issues, _) = await Validate(Yaml("Orchestration:\n  Name: old\n"));

        Assert.Equal(1, exit);
        HasError(issues, "fuseraft migrate");
    }

    [Fact]
    public async Task CompileError_IsReported()
    {
        var (exit, issues, _) = await Validate(Yaml(Team().Replace("- To: end", "- To: Ghost")));

        Assert.Equal(1, exit);
        HasError(issues, "to 'Ghost' isn't declared");
    }

    [Fact]
    public async Task Json_ValidConfig_ReportsSummary()
    {
        var path = Yaml(Team());
        var (exit, _, item) = await Validate(path);

        Assert.Equal(0, exit);
        Assert.True(item.GetProperty("valid").GetBoolean());
        Assert.Equal(Path.GetFullPath(path), item.GetProperty("path").GetString());
        var config = item.GetProperty("config");
        Assert.Equal("Test", config.GetProperty("name").GetString());
        Assert.Equal("A", Assert.Single(config.GetProperty("agents").EnumerateArray()).GetString());
        Assert.Equal("statemachine", config.GetProperty("selectionType").GetString());
    }

    [Fact]
    public async Task Json_WithDiagram_IsRejected()
    {
        var original = WireProtocol.Writer;
        var sw = new StringWriter();
        WireProtocol.Writer = sw;
        int exit;
        try { exit = await new ValidateConfigCommand(new PluginRegistry()).ExecuteAsync(null!, new ValidateConfigSettings { Path = "x.yaml", Json = true, Diagram = true }); }
        finally { WireProtocol.Writer = original; }

        Assert.Equal(1, exit);
        Assert.Contains("--diagram", JsonDocument.Parse(sw.ToString()).RootElement.GetProperty("error").GetString());
    }

    // ── Agents and models ──────────────────────────────────────────────────

    [Fact]
    public async Task EmptyInstructions_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Team().Replace("Instructions: ok", "Instructions: \"\"")));

        Assert.Equal(1, exit);
        HasError(issues, "instructions: required");
    }

    [Fact]
    public async Task ModelWithKnownPrefix_NeedsNoEndpoint()
    {
        var (_, issues, _) = await Validate(Yaml(Team(model: "claude-sonnet-4-6")));
        Assert.DoesNotContain(issues, i => i.Message.Contains("endpoint is empty"));
    }

    [Fact]
    public async Task ModelWithoutPrefixOrEndpoint_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Team(model: "mystery-model")));

        Assert.Equal(1, exit);
        HasError(issues, "endpoint is empty");
    }

    [Fact]
    public async Task ModelAlias_ResolvesEndpoint()
    {
        var yaml = """
            Team: Test
            Models:
              fast: { ModelId: mystery-model, Endpoint: https://example.com/v1 }
            Agents:
              A:
                Instructions: ok
                Model: fast
            Flow:
              Start: A
              Steps:
                A:
                  - To: end
            """;
        var (exit, issues, _) = await Validate(Yaml(yaml));

        Assert.Equal(0, exit);
        Assert.DoesNotContain(issues, i => i.Message.Contains("endpoint is empty"));
    }

    [Theory]
    [InlineData("llama3:8b")]
    [InlineData("phi3:mini")]
    public async Task OllamaModel_NoApiKeyWarning(string modelId)
    {
        var (_, issues, _) = await Validate(Yaml(Team(model: $"\"{modelId}\"")));
        Assert.DoesNotContain(issues, i => i.Message.Contains("apiKeyEnvVar"));
    }

    [Theory]
    [InlineData("invalidchoice")]
    [InlineData("\"\"")]
    public async Task InvalidFunctionChoice_Errors(string choice)
    {
        var (exit, issues, _) = await Validate(Yaml(Team(agent: $"Advanced:\n  FunctionChoice: {choice}")));

        Assert.Equal(1, exit);
        HasError(issues, "functionChoice");
    }

    [Theory]
    [InlineData("1.5", true)]
    [InlineData("-0.1", true)]
    [InlineData("0.0", false)]
    [InlineData("1.0", false)]
    public async Task TrustScore_Range(string value, bool errors)
    {
        var (exit, issues, _) = await Validate(Yaml(Team(agent: $"Advanced:\n  TrustScore: {value}")));

        if (errors) HasError(issues, "trustScore must be 0.0–1.0");
        else NoErrors(issues);
        Assert.Equal(errors ? 1 : 0, exit);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StrictMode_UnregisteredTool_WarnsNeverFails(bool strict)
    {
        var (exit, issues, _) = await Validate(Yaml(Team(agent: "Tools: [NotARealPlugin]")), strict);

        Assert.Equal(0, exit);
        if (strict) HasWarning(issues, "plugin 'NotARealPlugin' is not registered");
        else Assert.DoesNotContain(issues, i => i.Message.Contains("NotARealPlugin"));
    }

    // ── Patterns ───────────────────────────────────────────────────────────

    private static string Pattern(string flow, string agents = "S, M, R") => $$"""
        Team: P
        Models:
          manager: { ModelId: gpt-4o }
        Agents:
        {{string.Join("\n", agents.Split(", ").Select(a => $"  {a}: {{ Instructions: ok, Model: {{ ModelId: gpt-4o }} }}"))}}
        Flow:
        {{Indent(flow, 2)}}
        """;

    [Fact]
    public async Task Magentic_Valid_Returns0()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: magentic\nModel: manager", "A, B")));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Theory]
    [InlineData("MaxRoundCount: 0", "maxRoundCount must be at least 1")]
    [InlineData("MaxStallCount: 0", "maxStallCount must be at least 1")]
    [InlineData("MaxResetCount: -1", "maxResetCount must be >= 0")]
    public async Task Magentic_BadLimits_Error(string field, string message)
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern($"Pattern: magentic\nModel: manager\n{field}", "A")));

        Assert.Equal(1, exit);
        HasError(issues, message);
    }

    [Fact]
    public async Task Magentic_WithoutModel_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: magentic", "A")));

        Assert.Equal(1, exit);
        HasError(issues, "magentic");
    }

    [Fact]
    public async Task Magentic_HandoffSees_Errors()
    {
        var yaml = Pattern("Pattern: magentic\nModel: manager", "A").Replace("Instructions: ok,", "Instructions: ok, Sees: handoff,");
        var (exit, issues, _) = await Validate(Yaml(yaml));

        Assert.Equal(1, exit);
        HasError(issues, "needs every agent to see the conversation");
    }

    [Fact]
    public async Task MapReduce_Valid_Returns0()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: mapreduce\nSplitter: S\nMapper: M\nReducer: R")));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task MapReduce_UnknownAgent_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: mapreduce\nSplitter: Ghost\nMapper: M\nReducer: R")));

        Assert.Equal(1, exit);
        HasError(issues, "splitter 'Ghost' isn't declared");
    }

    [Fact]
    public async Task MapReduce_MaxSplitterRetriesZero_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: mapreduce\nSplitter: S\nMapper: M\nReducer: R\nMaxSplitterRetries: 0")));

        Assert.Equal(1, exit);
        HasError(issues, "maxSplitterRetries must be at least 1");
    }

    [Fact]
    public async Task ScatterGather_Valid_Returns0()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: scattergather\nParticipants: [S, M]\nSynthesizer: R")));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task ScatterGather_NoParticipants_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: scattergather\nParticipants: []\nSynthesizer: R")));

        Assert.Equal(1, exit);
        HasError(issues, "participants needs at least one agent");
    }

    [Fact]
    public async Task UnknownPattern_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Pattern("Pattern: keyword")));

        Assert.Equal(1, exit);
        HasError(issues, "'keyword' isn't a pattern");
    }

    // ── Advanced layers ────────────────────────────────────────────────────

    [Theory]
    [InlineData("1.1")]
    [InlineData("-0.05")]
    public async Task AntiThrashMinSavingsRatio_OutOfRange_Errors(string value)
    {
        var (exit, issues, _) = await Validate(Yaml(Team(top: $"Advanced:\n  Compaction:\n    AntiThrashMinSavingsRatio: {value}")));

        Assert.Equal(1, exit);
        HasError(issues, "antiThrashMinSavingsRatio must be 0.0–1.0");
    }

    // ── MCP servers ────────────────────────────────────────────────────────

    private string WithMcp(string servers) => Yaml(Team(top: "McpServers:\n" + Indent(servers, 2)));

    [Theory]
    [InlineData("- { Name: Remote, Transport: http }", "url is required for http transport")]
    [InlineData("- { Name: Local, Transport: stdio }", "command is required for stdio transport")]
    [InlineData("- { Name: Dup, Transport: stdio, Command: npx }\n- { Name: Dup, Transport: stdio, Command: python }", "Duplicate MCP server name")]
    [InlineData("- { Name: Local, Transport: stdio, Command: npx, OAuth: { CallbackPort: 1179 } }", "oauth requires transport: http")]
    [InlineData("- { Name: Remote, Transport: http, Url: \"https://example.com/mcp\", TransportMode: bogus }", "transportMode")]
    public async Task McpServers_Invalid_Error(string servers, string message)
    {
        var (exit, issues, _) = await Validate(WithMcp(servers));

        Assert.Equal(1, exit);
        HasError(issues, message);
    }

    [Fact]
    public async Task McpServers_ValidHttpWithHeadersAndOAuth_Returns0()
    {
        var (exit, issues, _) = await Validate(WithMcp("""
            - Name: Remote
              Transport: http
              Url: https://example.com/mcp
              TransportMode: streamable-http
              Headers: { Authorization: Bearer literal-value }
              OAuth: { ClientId: abc123 }
            """));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task McpServers_ValidStdio_Returns0()
    {
        var (exit, issues, _) = await Validate(WithMcp("- { Name: Local, Transport: stdio, Command: npx, Args: [\"-y\", some-server] }"));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task McpServers_UnsetEnvVarInHeader_WarnsButPasses()
    {
        var (exit, issues, _) = await Validate(WithMcp("""
            - Name: Remote
              Transport: http
              Url: https://example.com/mcp
              Headers: { Authorization: "Bearer ${DEFINITELY_UNSET_TEST_VAR_XYZ}" }
            """));

        Assert.Equal(0, exit);
        HasWarning(issues, "DEFINITELY_UNSET_TEST_VAR_XYZ");
    }

    // ── Security ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("segments")]
    [InlineData("SEGMENTS")]
    [InlineData("substring")]
    public async Task ShellPolicy_KnownAllowMode_IsValid(string mode)
    {
        var (exit, issues, _) = await Validate(Yaml(Team(top: $"Security:\n  ShellPolicy: {{ Allow: [go test], AllowMode: {mode} }}")));

        Assert.Equal(0, exit);
        NoErrors(issues);
    }

    [Fact]
    public async Task ShellPolicy_UnknownAllowMode_Errors()
    {
        var (exit, issues, _) = await Validate(Yaml(Team(top: "Security:\n  ShellPolicy: { Allow: [go test], AllowMode: bogus }")));

        Assert.Equal(1, exit);
        HasError(issues, "allowMode");
    }

    [Fact]
    public async Task ShellPolicy_SegmentsWithEmptyAllow_WarnsButPasses()
    {
        var (exit, _, _) = await Validate(Yaml(Team(top: "Security:\n  ShellPolicy: { AllowMode: segments }")));
        Assert.Equal(0, exit);
    }
}
