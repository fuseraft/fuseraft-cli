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
[Collection("FuseraftTestApiKeyEnv")]
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

    // SummarizeParticipantActivityAsync — two-history isolation invariant.
    //
    // The manager must never reason over raw participant dialogue directly (only over
    // "explicit summaries derived from sharedHistory"). BuildLedgerPrompt/BuildReplanPrompt/
    // BuildFinalAnswerPrompt now take a plain `string historyText` rather than
    // `IReadOnlyList<ChatMessage> sharedHistory`, so there is no code path left for raw
    // transcript text to reach those prompts except through this summarization step. These
    // tests verify the summarization call itself: it sends the raw window to the manager
    // *client* as an isolated, one-shot request under a neutral summarizer system prompt —
    // never the manager's own persona (_magConfig.Instructions) — and only the model's
    // returned summary is ever handed back to the caller.

    [Fact]
    public async Task SummarizeParticipantActivityAsync_UsesNeutralSummarizerPrompt_NotManagerPersona()
    {
        IEnumerable<ChatMessage>? captured = null;
        _managerClient
            .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>((msgs, _, _) => captured = msgs)
            .ReturnsAsync(new ChatResponse(new ChatMessage(ChatRole.Assistant, "- Developer wrote Foo.cs\n- Tests passed")));

        var window = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "I'll implement the Foo class now.") { AuthorName = "Developer" },
            new(ChatRole.Assistant, "Running tests... all green.") { AuthorName = "Tester" },
        };

        var (summary, _) = await _orchestrator.SummarizeParticipantActivityAsync(window, CancellationToken.None);

        Assert.Equal("- Developer wrote Foo.cs\n- Tests passed", summary);

        var sent = Assert.IsAssignableFrom<IEnumerable<ChatMessage>>(captured).ToList();
        var systemMessage = Assert.Single(sent, m => m.Role == ChatRole.System);
        Assert.Contains("neutral progress summarizer", systemMessage.Text, StringComparison.OrdinalIgnoreCase);

        // The raw participant dialogue goes INTO this isolated call (expected — it has to be
        // summarized from something) but never comes back OUT as the result: the caller only
        // ever receives the mocked summary text asserted above, not "[Developer]: I'll..." etc.
        var userMessage = Assert.Single(sent, m => m.Role == ChatRole.User);
        Assert.Contains("[Developer]: I'll implement the Foo class now.", userMessage.Text);
        Assert.DoesNotContain("[Developer]", summary);
    }

    [Fact]
    public async Task SummarizeParticipantActivityAsync_EmptyWindow_ReturnsEmptyWithoutCallingManager()
    {
        var (summary, usage) = await _orchestrator.SummarizeParticipantActivityAsync([], CancellationToken.None);

        Assert.Equal(string.Empty, summary);
        Assert.Null(usage);
        _managerClient.Verify(
            c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
