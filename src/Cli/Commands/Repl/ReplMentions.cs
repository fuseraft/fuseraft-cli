using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using fuseraft.Core.Images;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>
/// Files and directories the user names with @ in a message, such as "@src/app.py". Each one inside the working
/// directory is read through the session's own <c>read_file</c> or <c>list_files</c> tool, so the sandbox and deny
/// rules (and /safe-mode, /tools restrict) apply as if the model had read it, and handed to the model next to the
/// message. Images are left to <see cref="ImageAttachments"/>.
/// </summary>
internal static partial class ReplMentions
{
    public const int MaxFiles = 10;

    /// <summary>The paths <paramref name="text"/> mentions, relative to <paramref name="root"/>, in order and without repeats.</summary>
    public static List<string> Parse(string text, string root)
    {
        List<string> paths = [];
        var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        foreach (Match m in Mention().Matches(text))
        {
            // "see @a.py." means a.py; the punctuation after a mention is only stripped when the path needs it.
            var token = m.Groups[1].Value;
            while (token.Length > 0 && !Exists(token) && ".,;:!?)]}'\"".Contains(token[^1]))
                token = token[..^1];
            if (token.Length == 0 || !Exists(token) || ImageAttachments.HasImageExtension(token))
                continue;
            var relative = Path.GetRelativePath(full, Path.GetFullPath(token, full)).Replace('\\', '/').TrimEnd('/');
            if (relative.Length > 0 && relative != "." && !paths.Contains(relative) && paths.Count < MaxFiles)
                paths.Add(relative);
        }
        return paths;

        bool Exists(string token)
        {
            var path = Path.GetFullPath(token, full);
            return path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                   && (File.Exists(path) || Directory.Exists(path));
        }
    }

    /// <summary>
    /// What the mentioned paths hold, as a note for the model: a file as read_file shows it, a directory as list_files
    /// does. <paramref name="report"/> hears what was attached or why not. Null when nothing was.
    /// </summary>
    public static async Task<string?> AttachAsync(
        string text, string root, IReadOnlyList<AIFunction> tools, Action<string, bool> report, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var path in Parse(text, root))
        {
            var directory = Directory.Exists(Path.GetFullPath(path, root));
            var name = directory ? "list_files" : "read_file";
            if (tools.FirstOrDefault(t => t.Name == name) is not { } tool)
            {
                report($"@{path} not attached: {name} isn't available in this session", false);
                continue;
            }
            var args = directory
                ? new AIFunctionArguments { ["directory"] = path }
                : new AIFunctionArguments { ["path"] = path };
            string content;
            try
            {
                content = (await tool.InvokeAsync(args, ct))?.ToString() ?? string.Empty;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                report($"@{path} not attached: {e.Message}", false);
                continue;
            }
            if (ReplTurn.IsToolFailureText(content))
            {
                report($"@{path} not attached: {FirstLine(content)}", false);
                continue;
            }
            sb.Append($"<file path=\"{path}\">\n{content.TrimEnd('\n')}\n</file>\n");
            report($"@{path}", true);
        }
        return sb.Length == 0 ? null
            : "[fuseraft] The user's message mentions these paths with @; here they are as read_file and list_files show them.\n\n" + sb;
    }

    static string FirstLine(string s)
    {
        var line = s.Split('\n', 2)[0];
        return line.Length > 200 ? line[..197] + "..." : line;
    }

    [GeneratedRegex(@"(?<![^\s(\[])@([^\s@]+)")]
    private static partial Regex Mention();
}
