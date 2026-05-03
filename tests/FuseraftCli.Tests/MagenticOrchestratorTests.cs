using fuseraft.Core.Models;
using fuseraft.Infrastructure;
using fuseraft.Infrastructure.Plugins;
using fuseraft.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FuseraftCli.Tests;

/// <summary>
/// Unit tests for <see cref="MagenticOrchestrator"/> logic that does not require
/// live LLM calls. Uses the InternalsVisibleTo grant in FuseraftCli.csproj to access
/// <c>internal</c> helpers.
/// </summary>
public sealed class MagenticOrchestratorTests : IDisposable
{
    private const string FakeApiKeyVar = "FUSERAFT_TEST_API_KEY";
    private const string FakeApiKey    = "sk-test-key-not-used-in-unit-tests";

    private readonly PluginRegistry      _registry;
    private readonly AgentFactory        _agentFactory;
    private readonly Mock<IChatClient>   _managerClient;
    private readonly MagenticOrchestrator _orchestrator;

    public MagenticOrchestratorTests()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, FakeApiKey);

        _registry      = new PluginRegistry(NullLoggerFactory.Instance).RegisterDefaults();
        _agentFactory  = new AgentFactory(new ChatClientFactory(), _registry);
        _managerClient = new Mock<IChatClient>();

        var config = new OrchestrationConfig
        {
            Agents    = [new AgentConfig { Name = "Worker", Model = new ModelConfig { ModelId = "gpt-4o", ApiKeyEnvVar = FakeApiKeyVar, Endpoint = "https://api.openai.com/v1" } }],
            Models    = new Dictionary<string, ModelConfig>(),
            Selection = new SelectionStrategyConfig
            {
                Type     = "magentic",
                Magentic = new MagenticManagerConfig
                {
                    Model = new ModelConfig { ModelId = "gpt-4o", ApiKeyEnvVar = FakeApiKeyVar, Endpoint = "https://api.openai.com/v1" }
                }
            },
        };

        _orchestrator = new MagenticOrchestrator(
            config, _agentFactory, _managerClient.Object,
            NullLogger<MagenticOrchestrator>.Instance);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(FakeApiKeyVar, null);
        _registry.Dispose();
    }

    // ParseLedger — field deserialization

    [Fact]
    public void ParseLedger_CleanJson_DeserializesAllFieldsViaSnakeCase()
    {
        // This test verifies the PropertyNamingPolicy = SnakeCaseLower fix.
        // Without it every field returns the default (false / null) because the
        // JSON keys (is_request_satisfied etc.) are not matched case-insensitively.
        const string json = """
            {
              "is_request_satisfied": true,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Write the summary.",
              "final_answer": "All done."
            }
            """;

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        Assert.True(ledger.IsRequestSatisfied);
        Assert.False(ledger.IsInLoop);
        Assert.True(ledger.IsProgressBeingMade);
        Assert.Equal("Worker", ledger.NextSpeaker);
        Assert.Equal("Write the summary.", ledger.InstructionOrQuestion);
        Assert.Equal("All done.", ledger.FinalAnswer);
    }

    [Fact]
    public void ParseLedger_InProgressRound_DeserializesProgressFields()
    {
        const string json = """
            {
              "is_request_satisfied": false,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Continue working on the feature.",
              "final_answer": null
            }
            """;

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        Assert.False(ledger.IsRequestSatisfied);
        Assert.True(ledger.IsProgressBeingMade);
        Assert.Null(ledger.FinalAnswer);
    }

    // ParseLedger — markdown fence stripping

    [Fact]
    public void ParseLedger_MarkdownFencedJson_StripsFenceAndParses()
    {
        const string fenced = """
            ```json
            {
              "is_request_satisfied": false,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Do step 2.",
              "final_answer": null
            }
            ```
            """;

        var ledger = _orchestrator.ParseLedger(fenced);

        Assert.NotNull(ledger);
        Assert.False(ledger.IsRequestSatisfied);
        Assert.Equal("Worker", ledger.NextSpeaker);
    }

    [Fact]
    public void ParseLedger_PlainBacktickFence_StripsFenceAndParses()
    {
        const string fenced = """
            ```
            {"is_request_satisfied":false,"is_in_loop":false,"is_progress_being_made":true,"next_speaker":"Worker","instruction_or_question":"Go.","final_answer":null}
            ```
            """;

        var ledger = _orchestrator.ParseLedger(fenced);

        Assert.NotNull(ledger);
        Assert.Equal("Worker", ledger.NextSpeaker);
    }

    // ParseLedger — prose prefix extraction

    [Fact]
    public void ParseLedger_ProsePrefixed_ExtractsJsonObjectAndParses()
    {
        const string prose = """
            Here is my evaluation of current progress:
            {
              "is_request_satisfied": false,
              "is_in_loop": true,
              "is_progress_being_made": false,
              "next_speaker": "Worker",
              "instruction_or_question": "Try a different approach.",
              "final_answer": null
            }
            """;

        var ledger = _orchestrator.ParseLedger(prose);

        Assert.NotNull(ledger);
        Assert.True(ledger.IsInLoop);
        Assert.False(ledger.IsProgressBeingMade);
    }

    // ParseLedger — "null" string guard

    [Fact]
    public void ParseLedger_FinalAnswerStringNull_ReturnsFinalAnswerAsNullString()
    {
        // Some models output the string "null" instead of JSON null when told to
        // use null. ParseLedger should return the string as-is; the caller is
        // responsible for guarding against it (which it does in StreamAsync).
        const string json = """
            {
              "is_request_satisfied": false,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Continue.",
              "final_answer": "null"
            }
            """;

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        // The raw string "null" is returned; StreamAsync guards with
        // string.Equals(ledger.FinalAnswer, "null", OrdinalIgnoreCase).
        Assert.Equal("null", ledger.FinalAnswer);
    }

    [Fact]
    public void ParseLedger_FinalAnswerJsonNull_ReturnsCSharpNull()
    {
        const string json = """
            {
              "is_request_satisfied": true,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Done.",
              "final_answer": null
            }
            """;

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        Assert.True(ledger.IsRequestSatisfied);
        Assert.Null(ledger.FinalAnswer);
    }

    // ParseLedger — error tolerance

    [Fact]
    public void ParseLedger_InvalidJson_ReturnsNull()
    {
        var ledger = _orchestrator.ParseLedger("this is not json at all");

        Assert.Null(ledger);
    }

    [Fact]
    public void ParseLedger_EmptyString_ReturnsNull()
    {
        var ledger = _orchestrator.ParseLedger(string.Empty);

        Assert.Null(ledger);
    }

    [Fact]
    public void ParseLedger_WhitespaceOnly_ReturnsNull()
    {
        // Whitespace-only input trims to empty string, which is not valid JSON.
        var ledger = _orchestrator.ParseLedger("   \t\n   ");

        Assert.Null(ledger);
    }

    [Fact]
    public void ParseLedger_JsonArray_ReturnsNull()
    {
        // A JSON array contains no {...} object for the regex extractor to find,
        // so Deserialize receives a raw array and throws JsonException → null.
        var ledger = _orchestrator.ParseLedger("""["foo", "bar", "baz"]""");

        Assert.Null(ledger);
    }

    [Fact]
    public void ParseLedger_MissingFields_ReturnsLedgerWithDefaults()
    {
        // Partial JSON — missing fields should default to false / null, not throw.
        const string json = """{ "is_request_satisfied": true }""";

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        Assert.True(ledger.IsRequestSatisfied);
        Assert.False(ledger.IsInLoop);
        Assert.False(ledger.IsProgressBeingMade);
        Assert.Null(ledger.NextSpeaker);
        Assert.Null(ledger.FinalAnswer);
    }

    [Fact]
    public void ParseLedger_TrailingCommas_ParsesSuccessfully()
    {
        // AllowTrailingCommas = true should handle relaxed JSON from models that add them.
        const string json = """
            {
              "is_request_satisfied": false,
              "is_in_loop": false,
              "is_progress_being_made": true,
              "next_speaker": "Worker",
              "instruction_or_question": "Keep going.",
              "final_answer": null,
            }
            """;

        var ledger = _orchestrator.ParseLedger(json);

        Assert.NotNull(ledger);
        Assert.Equal("Worker", ledger.NextSpeaker);
    }
}
