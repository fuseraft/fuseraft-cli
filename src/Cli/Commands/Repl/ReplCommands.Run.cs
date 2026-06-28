using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Spectre.Console;

namespace fuseraft.Cli.Commands.Repl;

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /run
    // -------------------------------------------------------------------------

    private static async Task<CommandResult> CmdRunAsync(
        ReplSessionContext ctx, string arg, CancellationToken cancellationToken)
    {
        // Resolve task text — accept inline text or a path to a task file.
        if (string.IsNullOrWhiteSpace(arg))
        {
            if (ctx.JsonMode)
            {
                Console.WriteLine("Usage: `/run <task>` or `/run <path-to-task-file>`");
                return CommandResult.Continue;
            }
            AnsiConsole.Markup("[dim]Task (or path to task file): [/]");
            arg = Console.ReadLine()?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(arg))
            {
                AnsiConsole.MarkupLine("[dim]No task provided.[/]");
                return CommandResult.Continue;
            }
        }

        string task;
        var absArg = Path.IsPathRooted(arg) ? arg : Path.GetFullPath(Path.Combine(ctx.Cwd, arg));
        if (File.Exists(absArg))
        {
            task = (await File.ReadAllTextAsync(absArg, cancellationToken)).Trim();
            if (string.IsNullOrWhiteSpace(task))
            {
                AnsiConsole.MarkupLine($"[red]✗ Task file is empty:[/] {Markup.Escape(absArg)}");
                return CommandResult.Continue;
            }
            if (!ctx.JsonMode)
                AnsiConsole.MarkupLine($"[dim]Task file:[/] {Markup.Escape(absArg)}");
        }
        else
        {
            task = arg;
        }

        var configPath = SelectRunConfig(ctx.Cwd, ctx.JsonMode);
        if (configPath is null)
            return CommandResult.Continue;

        var tmpTask = Path.Combine(Path.GetTempPath(), $"fuseraft-run-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tmpTask, task, System.Text.Encoding.UTF8, cancellationToken);

        try
        {
            var taskPreview = task.Length > 120 ? task[..120] + "…" : task;
            var configRel   = Path.GetRelativePath(ctx.Cwd, configPath);

            if (ctx.JsonMode)
                Console.WriteLine($"Running task with config `{configRel}`…\n");
            else
            {
                AnsiConsole.MarkupLine($"[dim]Config:[/] {Markup.Escape(configRel)}");
                AnsiConsole.MarkupLine($"[dim]Task:[/]   {Markup.Escape(taskPreview)}");
                AnsiConsole.WriteLine();
            }

            var exe = ResolveRunExe();
            var sw  = Stopwatch.StartNew();

            var (exitCode, output) = await RunOrchestrationSubprocessAsync(exe, configPath, tmpTask, cancellationToken);
            sw.Stop();

            var succeeded = exitCode == 0;
            var status    = succeeded ? "succeeded" : $"failed (exit code {exitCode})";

            if (ctx.JsonMode)
            {
                Console.WriteLine(succeeded
                    ? $"\n✓ Run succeeded ({sw.Elapsed.TotalSeconds:F1}s). Ask me what happened."
                    : $"\n✗ Run {status} ({sw.Elapsed.TotalSeconds:F1}s). Ask me what went wrong.");
            }
            else
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine(succeeded
                    ? $"[green]✓ Run {status}[/] [dim]({sw.Elapsed.TotalSeconds:F1}s)[/]"
                    : $"[red]✗ Run {status}[/] [dim]({sw.Elapsed.TotalSeconds:F1}s)[/]");
                AnsiConsole.MarkupLine("[dim]Run context added to conversation — ask me what happened.[/]");
                AnsiConsole.WriteLine();
            }

            InjectRunContext(ctx, task, configPath, succeeded, exitCode, sw.Elapsed, output);

            await ctx.Emitter.EmitAsync(EventTypes.Command, payload: new
            {
                command   = "/run",
                config    = configPath,
                succeeded,
                exit_code = exitCode,
                elapsed   = sw.Elapsed.TotalSeconds,
            });
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[dim](run cancelled)[/]");
            AnsiConsole.WriteLine();
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ /run failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.WriteLine();
        }
        finally
        {
            try { File.Delete(tmpTask); } catch { /* best effort */ }
        }

        return CommandResult.Continue;
    }

    private static void InjectRunContext(
        ReplSessionContext ctx, string task, string configPath,
        bool succeeded, int exitCode, TimeSpan elapsed, string output)
    {
        var taskPreview   = task.Length > 500  ? task[..500]   + "\n…(truncated)" : task;
        var outputPreview = output.Length > 3000 ? output[..3000] + "\n…(output truncated)" : output;
        var configRel     = Path.GetRelativePath(ctx.Cwd, configPath);
        var status        = succeeded ? "succeeded" : $"failed (exit code {exitCode})";

        var context =
            $"[Run result]\n" +
            $"Config:  {configRel}\n" +
            $"Task:    {taskPreview}\n" +
            $"Status:  {status}\n" +
            $"Elapsed: {elapsed.TotalSeconds:F1}s\n\n" +
            $"Output:\n```\n{outputPreview}\n```";

        ctx.History.Add(new ChatMessage(ChatRole.User, context));
        ctx.History.Add(new ChatMessage(ChatRole.Assistant,
            succeeded
                ? "The run completed successfully. I have the full output and can answer questions about what happened, what was produced, or what succeeded."
                : "The run failed. I have the captured output and can help diagnose what went wrong. Ask me about any specific error or step."));
    }

    private static string? SelectRunConfig(string cwd, bool jsonMode)
    {
        var configDir = Path.Combine(cwd, ".fuseraft", "config");

        if (!Directory.Exists(configDir))
            return Path.Combine(configDir, "orchestration.yaml");

        var configs = Directory.GetFiles(configDir, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".yml",  StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToList();

        if (configs.Count == 0)
            return Path.Combine(configDir, "orchestration.yaml");

        if (configs.Count == 1)
            return configs[0];

        // Multiple configs — in JSON mode just use the first; in terminal mode prompt.
        if (jsonMode)
        {
            var chosen = configs[0];
            Console.WriteLine($"Multiple configs found — using `{Path.GetRelativePath(cwd, chosen)}`.");
            Console.WriteLine("Re-run with `/run --config <path> <task>` to choose a different one.");
            return chosen;
        }

        AnsiConsole.MarkupLine($"[dim]{configs.Count} configs found — pick one:[/]");
        AnsiConsole.WriteLine();
        for (int i = 0; i < configs.Count; i++)
            AnsiConsole.MarkupLine($"  [bold cyan]{i + 1}.[/] {Markup.Escape(Path.GetRelativePath(cwd, configs[i]))}");
        AnsiConsole.WriteLine();
        AnsiConsole.Markup($"[dim]Select (1–{configs.Count}): [/]");

        var line = Console.ReadLine()?.Trim() ?? string.Empty;
        if (!int.TryParse(line, out var choice) || choice < 1 || choice > configs.Count)
        {
            AnsiConsole.MarkupLine("[yellow]Invalid selection — run cancelled.[/]");
            return null;
        }

        return configs[choice - 1];
    }

    private static async Task<(int ExitCode, string Output)> RunOrchestrationSubprocessAsync(
        string exe, string configPath, string taskFile, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var psi    = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--config");
        psi.ArgumentList.Add(configPath);
        psi.ArgumentList.Add("--task-file");
        psi.ArgumentList.Add(taskFile);
        psi.ArgumentList.Add("--no-banner");

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = ForwardStreamAsync(proc.StandardOutput, output, Console.Out);
        var stderrTask = ForwardStreamAsync(proc.StandardError,  output, Console.Error);

        try
        {
            await proc.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            await Task.WhenAll(stdoutTask, stderrTask);
            throw;
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        return (proc.ExitCode, output.ToString());
    }

    private static async Task ForwardStreamAsync(
        System.IO.StreamReader reader, StringBuilder buffer, System.IO.TextWriter console)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            console.WriteLine(line);
            lock (buffer) buffer.AppendLine(line);
        }
    }

    private static string ResolveRunExe()
    {
        var pp = Environment.ProcessPath;
        if (pp is not null
            && !pp.EndsWith("dotnet",     StringComparison.OrdinalIgnoreCase)
            && !pp.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            return pp;
        return "fuseraft";
    }
}
