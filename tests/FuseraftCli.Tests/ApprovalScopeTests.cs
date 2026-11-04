using fuseraft.Core;

namespace FuseraftCli.Tests;

public sealed class ApprovalScopeTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ApprovalsAskedSideBySide_AreAskedOneAtATime()
    {
        var inside = 0;
        var overlapped = false;
        var approve = ApprovalScope.Guard(async (string _) =>
        {
            if (Interlocked.Increment(ref inside) > 1) overlapped = true;
            await Task.Delay(20);
            Interlocked.Decrement(ref inside);
            return true;
        });

        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => approve($"cmd-{i}"))));

        Assert.All(answers, Assert.True);
        Assert.False(overlapped);
    }

    [Fact]
    public async Task AnApprovalThatAsksAnother_DoesNotWaitOnItself()
    {
        var inner = ApprovalScope.Guard((string _) => Task.FromResult(true));
        var outer = ApprovalScope.Guard((string a) => inner(a));

        Assert.True(await outer("cmd").WaitAsync(Wait));
    }

    [Fact]
    public async Task ARefusingFlow_IsNotQueuedBehindAnOpenPrompt()
    {
        var open = new TaskCompletionSource<bool>();
        var asked = new TaskCompletionSource();
        var approve = ApprovalScope.Guard((string _) => { asked.TrySetResult(); return open.Task; });
        var pending = Task.Run(() => approve("first"));
        await asked.Task.WaitAsync(Wait);

        var refused = await Task.Run(() => { ApprovalScope.RefuseInThisFlow(); return approve("second"); }).WaitAsync(Wait);

        Assert.False(refused);
        open.SetResult(true);
        Assert.True(await pending);
    }
}
