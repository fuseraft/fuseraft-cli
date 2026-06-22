using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Xunit;
using fuseraft.Cli.Commands;
using fuseraft.Core.Models;
using fuseraft.Infrastructure.Plugins;
using Spectre.Console.Cli;

namespace fuseraft.Tests;

public class ValidateConfigCommandTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    public ValidateConfigCommandTests()
    {
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task FileNotFound_Returns1_WithError()
    {
        var tempPath = Path.GetTempFileName();
        File.Delete(tempPath);
        _tempFiles.Add(tempPath);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task InvalidJson_Returns1_WithError()
    {
        var tempPath = CreateTempFile("invalid { json");
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task SchemaBindingFailure_Returns1_WithError()
    {
        var badConfig = """
        {
          "Orchestration": {
            "Agents": "not an array"
          }
        }
        """;
        var tempPath = CreateTempFile(badConfig);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task DuplicateAgentNames_DetectsError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "Agent1", "Instructions": "ok", "Model": {"ModelId": "gpt"}},
              {"Name": "Agent1", "Instructions": "ok", "Model": {"ModelId": "gpt"}}
            ],
            "Selection": {"Type": "sequential"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task EmptyModelId_FailsValidation()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "Agent", "Instructions": "ok", "Model": {"ModelId": ""}}
            ],
            "Selection": {"Type": "sequential"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task UnknownSelectionType_ReportsError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "foo"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task KeywordSelectionNoRoutes_ReportsError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "keyword"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task LlmSelectionNoModel_ReportsError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "llm"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task CompositeTerminationNoStrategies_ReportsError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "composite"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StrictMode_DoesNotFailOnUnregisteredPlugin(bool strict)
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}, "Plugins": ["fake-plugin"]}
            ],
            "Selection": {"Type": "sequential"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath, Strict = strict };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warnings don't fail
    }

    [Fact]
    public async Task ValidConfig_Returns0_NoErrors()
    {
        var validConfig = """
        {
          "Orchestration": {
            "Name": "Test",
            "Agents": [
              {"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(validConfig);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ModelWithKnownPrefix_NoEndpointError()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ModelWithoutPrefix_NoEndpoint_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "A", "Instructions": "ok", "Model": {"ModelId": "unknown-model"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task ModelAlias_ResolvesEndpoint()
    {
        var config = """
        {
          "Orchestration": {
            "Models": {
              "myalias": {
                "ModelId": "unknown-model",
                "Endpoint": "http://example.com"
              }
            },
            "Agents": [
              {"Name": "A", "Instructions": "ok", "Model": {"ModelId": "myalias"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("llama3:8b")]
    [InlineData("phi3:mini")]
    [InlineData("unknown:tag")]
    public async Task OllamaModel_NoApiKeyWarning(string modelId)
    {
        var config = string.Format(@"
        {{
          ""Orchestration"": {{
            ""Agents"": [
              {{""Name"": ""A"", ""Instructions"": ""ok"", ""Model"": {{""ModelId"": ""{0}""}}}}
            ],
            ""Selection"": {{""Type"": ""sequential""}},
            ""Termination"": {{""Type"": ""maxiterations"", ""MaxIterations"": 10}}
          }}
        }}
        ", modelId);
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("invalidchoice")]
    [InlineData("")]
    public async Task InvalidFunctionChoice_Errors(string functionChoice)
    {
        var config = string.Format(@"
        {{
          ""Orchestration"": {{
            ""Agents"": [
              {{""Name"": ""A"", ""Instructions"": ""ok"", ""Model"": {{""ModelId"": ""gpt-4o""}}, ""FunctionChoice"": ""{0}""}}
            ],
            ""Selection"": {{""Type"": ""sequential""}},
            ""Termination"": {{""Type"": ""maxiterations"", ""MaxIterations"": 10}}
          }}
        }}
        ", functionChoice);
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Termination_UnknownType_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "foo"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Termination_Regex_NoPattern_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "regex"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Termination_MaxIterations_Zero_Warning()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 0}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warning only
    }

    [Fact]
    public async Task Termination_AgentName_Invalid_Warning()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10, "AgentNames": ["Missing"]}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warning only
    }

    [Fact]
    public async Task NestedTermination_Composite_Recursion()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {
              "Type": "composite",
              "Strategies": [
                {
                  "Type": "regex",
                  "Pattern": "ok"
                },
                {
                  "Type": "composite",
                  "Strategies": []
                }
              ]
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode); // nested composite no strategies error with prefix
    }

    [Fact]
    public async Task KeywordRoutes_ConflictingSignatures_Warning()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt"}}],
            "Selection": {
              "Type": "keyword",
              "Routes": [
                {"Keyword": "HANDOFF", "SourceAgents": ["A"], "Validator": "V1"},
                {"Keyword": "HANDOFF", "SourceAgents": ["A"], "Validator": "V2"}
              ]
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warning only
    }

    [Fact]
    public async Task OrchestrationName_Empty_Warning()
    {
        var config = """
        {
          "Orchestration": {
            "Name": "",
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warning only
    }

    [Fact]
    public async Task AgentInstructions_Empty_Warning()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "A", "Instructions": "", "Model": {"ModelId": "gpt-4o"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode); // warning only
    }

    [Fact]
    public async Task NoAgents_Error()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task AgentName_Empty_Error()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}
            ],
            "Selection": {"Type": "sequential"},
            "Termination": {"Type": "maxiterations", "MaxIterations": 10}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    // -----------------------------------------------------------------------
    // Magentic selection tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MagenticSelection_MissingManagerModel_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {"Type": "magentic"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_MaxRoundCountZero_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {
                "Model": {"ModelId": "gpt-4o"},
                "MaxRoundCount": 0
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_MaxStallCountZero_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {
                "Model": {"ModelId": "gpt-4o"},
                "MaxStallCount": 0
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_ValidConfig_Returns0()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "do work", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {
                "Model": {"ModelId": "gpt-4o"},
                "MaxRoundCount": 20,
                "MaxStallCount": 3,
                "MaxResetCount": 2
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_MaxResetCountNegative_Errors()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {
                "Model": {"ModelId": "gpt-4o"},
                "MaxResetCount": -1
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_ModelAlias_Resolves()
    {
        // The example YAML uses ModelId: "manager" which is defined in Models.
        // ValidateConfigCommand must resolve the alias before checking endpoint/key.
        var config = """
        {
          "Orchestration": {
            "Models": {
              "manager": {
                "ModelId": "gpt-4o",
                "Endpoint": "https://api.openai.com/v1",
                "ApiKeyEnvVar": "OPENAI_API_KEY"
              }
            },
            "Agents": [{"Name": "Worker", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {
                "Model": {"ModelId": "manager"}
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        // Warnings about the env var not being set in CI are expected; no errors.
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task MagenticSelection_TerminationConfigured_WarnsButPasses()
    {
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "Worker", "Instructions": "do work", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {
              "Type": "magentic",
              "Magentic": {"Model": {"ModelId": "gpt-4o"}}
            },
            "Termination": {"Type": "maxiterations", "MaxIterations": 50}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        // Termination with non-default type alongside Magentic is a warning, not an error.
        Assert.Equal(0, exitCode);
    }

    // -----------------------------------------------------------------------
    // Workflow selection tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task WorkflowSelection_ValidConfig_Returns0()
    {
        // Regression test: 'workflow' was missing from the selection-type allowlist entirely,
        // so even a fully valid config reported "Unknown selection type: 'workflow'".
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "Writer", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}, "Plugins": ["Handoff"]},
              {"Name": "Reviewer", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}, "Plugins": ["Handoff"]}
            ],
            "Selection": {
              "Type": "workflow",
              "Graph": {
                "EntryNode": "writer",
                "Nodes": [
                  {"Id": "writer", "Agent": "Writer"},
                  {"Id": "reviewer", "Agent": "Reviewer", "Terminal": true}
                ],
                "Edges": [
                  {"From": "writer", "To": "reviewer", "Keyword": "HANDOFF TO REVIEWER"}
                ]
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task WorkflowSelection_MissingHandoffPlugin_Errors()
    {
        // 'workflow' routes exclusively via handoff() tool calls (no text-keyword fallback),
        // so an agent referenced by a workflow node without the Handoff plugin must error.
        var config = """
        {
          "Orchestration": {
            "Agents": [
              {"Name": "Writer", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}},
              {"Name": "Reviewer", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}, "Plugins": ["Handoff"]}
            ],
            "Selection": {
              "Type": "workflow",
              "Graph": {
                "EntryNode": "writer",
                "Nodes": [
                  {"Id": "writer", "Agent": "Writer"},
                  {"Id": "reviewer", "Agent": "Reviewer", "Terminal": true}
                ],
                "Edges": [
                  {"From": "writer", "To": "reviewer", "Keyword": "HANDOFF TO REVIEWER"}
                ]
              }
            }
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    // -----------------------------------------------------------------------
    // MapReduce / ScatterGather selection-type recognition
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MapReduceSelection_Recognized_NotUnknownType()
    {
        // Regression test: 'mapreduce' was also missing from the selection-type allowlist —
        // found incidentally while fixing the same gap for 'workflow'. ValidateConfigCommand
        // has no dedicated structural checks for MapReduce (unlike Graph/Magentic/Adversarial),
        // so this only proves the type is recognized, not that its config block is validated.
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {"Type": "mapreduce"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task ScatterGatherSelection_Recognized_NotUnknownType()
    {
        // Same regression as MapReduce above, for 'scattergather'.
        var config = """
        {
          "Orchestration": {
            "Agents": [{"Name": "A", "Instructions": "ok", "Model": {"ModelId": "gpt-4o"}}],
            "Selection": {"Type": "scattergather"}
          }
        }
        """;
        var tempPath = CreateTempFile(config);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    // -----------------------------------------------------------------------
    // YAML config tests
    // -----------------------------------------------------------------------

    [Fact]
    public async Task ValidYaml_Returns0_NoErrors()
    {
        var yaml = """
        Orchestration:
          Name: TestWorkflow
          Agents:
            - Name: Agent1
              Instructions: "Do things."
              Model:
                ModelId: gpt-4o
          Selection:
            Type: sequential
          Termination:
            Type: maxiterations
            MaxIterations: 10
        """;
        var tempPath = CreateTempYamlFile(yaml);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task InvalidYaml_Returns1_WithError()
    {
        // Deliberately broken YAML: tab character in indentation (YAML disallows tabs)
        var yaml = "Orchestration:\n\t Name: bad";
        var tempPath = CreateTempYamlFile(yaml);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task YamlMissingOrchestrationKey_Returns1()
    {
        var yaml = """
        Name: TestWorkflow
        Agents:
          - Name: Agent1
        """;
        var tempPath = CreateTempYamlFile(yaml);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task YamlDuplicateAgentNames_DetectsError()
    {
        var yaml = """
        Orchestration:
          Agents:
            - Name: Agent1
              Instructions: ok
              Model:
                ModelId: gpt
            - Name: Agent1
              Instructions: ok
              Model:
                ModelId: gpt
          Selection:
            Type: sequential
        """;
        var tempPath = CreateTempYamlFile(yaml);
        var settings = new ValidateConfigSettings { Path = tempPath };

        var registry = new PluginRegistry();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task YmlExtension_AlsoAccepted()
    {
        var yaml = """
        Orchestration:
          Name: TestWorkflow
          Agents:
            - Name: A
              Instructions: ok
              Model:
                ModelId: gpt-4o
          Selection:
            Type: sequential
          Termination:
            Type: maxiterations
            MaxIterations: 5
        """;
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".yml");
        File.WriteAllText(tempPath, yaml);
        _tempFiles.Add(tempPath);

        var settings = new ValidateConfigSettings { Path = tempPath };
        var registry = new PluginRegistry();
        registry.RegisterDefaults();
        var command = new ValidateConfigCommand(registry);
        var exitCode = await command.ExecuteAsync(null!, settings);

        Assert.Equal(0, exitCode);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private string CreateTempFile(string content)
    {
        var tempPath = Path.GetTempFileName();
        File.WriteAllText(tempPath, content);
        _tempFiles.Add(tempPath);
        return tempPath;
    }

    private string CreateTempYamlFile(string content)
    {
        var tempPath = Path.ChangeExtension(Path.GetTempFileName(), ".yaml");
        File.WriteAllText(tempPath, content);
        _tempFiles.Add(tempPath);
        return tempPath;
    }
}