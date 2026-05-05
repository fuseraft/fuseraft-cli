using fuseraft.Core;

namespace fuseraft.Cli.Commands;

internal static partial class InitTemplates
{
    private static string Minimal(string model, string? endpoint) => $"""
        Orchestration:
          Name: Minimal Agent
          Description: A single general-purpose agent for simple tasks.

          Agents:
            - Name: Agent
              Description: Completes the given task using available tools.
              Instructions: |
                You are a capable, methodical assistant. Complete the task step by step,
                using the available tools. When the task is fully done, end with: TASK_COMPLETE
              Model:
                ModelId: {model}{Ep(endpoint, "        ")}
              Plugins:
                - FileSystem
                - Shell

              # ContextWindow:
              #   TextOnly: true          # strip tool-call frames from cross-turn history
              # FunctionChoice: required  # force at least one tool call per turn (auto|required|none)
              # TrustScore: 0.8           # 0.0–1.0; lower scores increase sandbox ring restrictions
              # MaxTokens: 4096           # override model's default max output tokens
              # Capabilities:             # per-plugin tool allowlist
              #   Shell: [shell_run]
              #   FileSystem: [read_file, list_files]

          Selection:
            Type: sequential

          Termination:
            Type: regex
            Pattern: TASK_COMPLETE
            MaxIterations: 20
        {OptionalSections(model, endpoint)}
        """;
}
