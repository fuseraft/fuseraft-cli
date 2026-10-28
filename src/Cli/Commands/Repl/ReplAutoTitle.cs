using Microsoft.Extensions.AI;

namespace fuseraft.Cli.Commands.Repl;

/// <summary>Names a chat from its first exchange, once, in the background; a title the user sets always wins.</summary>
internal static class ReplAutoTitle
{
    private const int MaxExcerptChars = 1000;
    internal const int MaxTitleChars  = 60;
    private const int MaxTitleWords   = 10;

    /// <summary>
    /// Starts naming the chat when it has a first exchange and no title of either kind, at most once per session;
    /// returns the running task (tests await it), or null when there's nothing to do. Off with <c>repl.autoTitle false</c>.
    /// </summary>
    internal static Task? MaybeStart(ReplSessionContext ctx, Func<IChatClient>? createClient = null)
    {
        if (ctx.AutoTitleStarted || ctx.Title is not null || ctx.AutoTitle is not null) return null;
        if (ctx.UserCfg?.Repl?.AutoTitle == false) return null;
        // Read on this thread: the next turn may be changing the history while the title is being written.
        if (FirstExchange(ctx.History) is not var (user, reply)) return null;

        ctx.AutoTitleStarted = true;
        var generation = ctx.AutoTitleGeneration;
        var client = createClient?.Invoke() ?? CreateClient(ctx);
        return Task.Run(async () =>
        {
            try
            {
                // /clear or /switch since it started means the title is for a conversation that's gone.
                if (await GenerateAsync(client, user, reply) is { } title && ctx.AutoTitleGeneration == generation)
                    await ApplyAsync(ctx, title);
            }
            catch { /* the chat keeps its first message as its label */ }
            finally { (client as IDisposable)?.Dispose(); }
        });
    }

    // The memory.model override is the user's choice for cheap side calls; otherwise the chat's own model.
    private static IChatClient CreateClient(ReplSessionContext ctx)
    {
        var modelCfg = ctx.UserCfg?.Memory is { Model: { Length: > 0 } mm } mem
            ? ReplFactory.ResolveOverrideModel(ctx.Factory, ctx.ModelConfig, mm, mem.Provider, mem.Endpoint, mem.ApiKeyEnvVar)
            : ctx.ModelConfig;
        return ctx.Factory.Create(modelCfg);
    }

    /// <summary>The first user message and the first reply after it with text, each cut to a short excerpt; null until both exist.</summary>
    internal static (string User, string Reply)? FirstExchange(IReadOnlyList<ChatMessage> history)
    {
        var userAt = -1;
        for (var i = 0; i < history.Count; i++)
            if (history[i].Role == ChatRole.User && !string.IsNullOrWhiteSpace(history[i].Text)) { userAt = i; break; }
        if (userAt < 0) return null;
        for (var i = userAt + 1; i < history.Count; i++)
            if (history[i].Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(history[i].Text))
                return (Excerpt(history[userAt].Text), Excerpt(history[i].Text));
        return null;
    }

    private static string Excerpt(string text) =>
        text.Length <= MaxExcerptChars ? text.Trim() : text[..MaxExcerptChars].Trim() + " …";

    internal static async Task<string?> GenerateAsync(IChatClient client, string user, string reply, CancellationToken ct = default)
    {
        var prompt =
            "Name this chat. Reply with a title of 3 to 6 words that says what it is about. " +
            "Plain text only: no quotes, no trailing punctuation, no emoji, no \"Title:\" prefix.\n\n" +
            $"<user>\n{user}\n</user>\n\n<assistant>\n{reply}\n</assistant>";
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, prompt)], cancellationToken: ct);
        return Clean(response.Text);
    }

    /// <summary>A model's answer as a one-line title, or null when it isn't one (empty, or a sentence rather than a name).</summary>
    internal static string? Clean(string? raw)
    {
        var line = (raw ?? string.Empty).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
        if (line.StartsWith("title:", StringComparison.OrdinalIgnoreCase)) line = line[6..];
        // Wrappers and end punctuation nest either way round ("`Title`." or "**Title.**"), so peel until nothing changes.
        string before;
        do
        {
            before = line;
            line = line.Trim().Trim('"', '\'', '`', '*', '#', '“', '”', '‘', '’').TrimEnd('.', '!', '?', ':', ';', ',');
        } while (line != before);
        if (ReplCommands.NormalizeTitle(line) is not { } title) return null;
        if (title.Split(' ').Length > MaxTitleWords) return null;
        if (title.Length > MaxTitleChars)
        {
            var cut = title.LastIndexOf(' ', MaxTitleChars);
            title = cut > 0 ? title[..cut] : title[..MaxTitleChars];
        }
        return title;
    }

    private static async Task ApplyAsync(ReplSessionContext ctx, string title)
    {
        ctx.AutoTitle    = title;
        ctx.TitleUnsaved = true;
        if (ctx.JsonMode)
            ReplJsonBridge.Emit(new { type = "title", sessionId = ctx.SessionId, title = ctx.Title, autoTitle = ctx.AutoTitle });
        // While the REPL waits for input nothing else will save soon; a turn that starts meanwhile saves it anyway.
        if (ctx.AwaitingInput) await ReplTurn.SaveSnapshotAsync(ctx);
    }
}
