using Microsoft.Extensions.AI;
using Spectre.Console;
using fuseraft.Core.Images;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>Terminal/bridge feedback for image attachments — what was attached, and what was refused and why.</summary>
internal static class ReplImages
{
    /// <summary>One dim line per attached image, so it is clear the picture — not just the words — is being sent.</summary>
    internal static void Announce(ReplSessionContext ctx, IReadOnlyList<DataContent> images)
    {
        // The VS Code webview renders its own attachment chips.
        if (ctx.JsonMode) return;
        foreach (var img in images)
            AnsiConsole.MarkupLine($"[dim]  attached: {Markup.Escape(ImageAttachments.Describe(img))}[/]");
    }

    internal static void ReportErrors(ReplSessionContext ctx, IReadOnlyList<string> errors)
    {
        foreach (var e in errors)
        {
            if (ctx.JsonMode) ReplJsonBridge.Emit(new { type = "warning", text = $"Image not attached: {e}" });
            else              AnsiConsole.MarkupLine($"[yellow]  ⚠ image not attached: {Markup.Escape(e)}[/]");
        }
    }
}

internal static partial class ReplCommands
{
    // -------------------------------------------------------------------------
    // /image <path>… [message]
    // -------------------------------------------------------------------------

    private static CommandResult CmdImage(ReplSessionContext ctx, string arg)
    {
        var parsed = ImageAttachments.ParseImageCommand(arg, ctx.Cwd);

        // An explicit request: a typo'd path must not quietly degrade into sending the words alone.
        if (parsed.Errors.Count > 0)
        {
            ReplImages.ReportErrors(ctx, parsed.Errors);
            return CommandResult.Continue;
        }

        ReplImages.Announce(ctx, parsed.Images);
        return CommandResult.Send(parsed.Text, attachments: parsed.Images);
    }
}
