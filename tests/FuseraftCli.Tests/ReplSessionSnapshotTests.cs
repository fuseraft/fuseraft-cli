using System.Text.Json;
using fuseraft.Core.Models.Session;
using fuseraft.Infrastructure.Plugins;
using Microsoft.Extensions.AI;

namespace FuseraftCli.Tests;

/// <summary>
/// Tests for <see cref="ReplSessionSnapshot"/>'s TodoItems field — added so a resumed REPL
/// session's todo_read doesn't contradict the last todo_write visible in the restored history.
/// </summary>
public sealed class ReplSessionSnapshotTests
{
    private static readonly List<ChatMessage> SampleHistory = [new(ChatRole.User, "hi")];

    [Fact]
    public void Capture_WithTodoItems_PopulatesTodoItems()
    {
        var todoItems = new[] { new TodoItem { Content = "Step one", Status = "in_progress" } };

        var snap = ReplSessionSnapshot.Capture(
            "session-1", "gpt-4o-mini", "/tmp",
            turnIndex: 1, history: SampleHistory, startedAt: DateTime.UtcNow,
            todoItems: todoItems);

        Assert.NotNull(snap.TodoItems);
        Assert.Single(snap.TodoItems!);
        Assert.Equal("Step one", snap.TodoItems![0].Content);
        Assert.Equal("in_progress", snap.TodoItems![0].Status);
    }

    [Fact]
    public void Capture_WithoutTodoItems_TodoItemsIsNull()
    {
        var snap = ReplSessionSnapshot.Capture(
            "session-1", "gpt-4o-mini", "/tmp",
            turnIndex: 1, history: SampleHistory, startedAt: DateTime.UtcNow);

        Assert.Null(snap.TodoItems);
    }

    [Fact]
    public void TodoItems_SurviveJsonRoundTrip()
    {
        var todoItems = new[]
        {
            new TodoItem { Content = "Read entry point", Status = "completed" },
            new TodoItem { Content = "Map request flow", Status = "in_progress" },
        };
        var snap = ReplSessionSnapshot.Capture(
            "session-1", "gpt-4o-mini", "/tmp",
            turnIndex: 1, history: SampleHistory, startedAt: DateTime.UtcNow,
            todoItems: todoItems);

        var json     = JsonSerializer.Serialize(snap);
        var restored = JsonSerializer.Deserialize<ReplSessionSnapshot>(json);

        Assert.NotNull(restored);
        Assert.NotNull(restored!.TodoItems);
        Assert.Equal(2, restored.TodoItems!.Length);
        Assert.Equal("Read entry point", restored.TodoItems![0].Content);
        Assert.Equal("completed", restored.TodoItems![0].Status);
        Assert.Equal("Map request flow", restored.TodoItems![1].Content);
        Assert.Equal("in_progress", restored.TodoItems![1].Status);
    }
}
