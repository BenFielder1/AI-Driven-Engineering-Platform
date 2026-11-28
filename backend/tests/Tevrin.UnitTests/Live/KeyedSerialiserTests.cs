using Tevrin.Api.Features.Live;

namespace Tevrin.UnitTests.Live;

/// <summary>FR-363: a map's progress sends (read the counts, then send) never overlap, so the final counts arrive last.</summary>
public sealed class KeyedSerialiserTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task FR363_a_second_send_of_the_same_map_waits_for_the_first_and_runs_after_it()
    {
        var serialiser = new KeyedSerialiser();
        var map = Guid.CreateVersion7();
        var order = new List<string>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = serialiser.RunAsync(map, async () =>
        {
            order.Add("first read");
            await releaseFirst.Task;
            order.Add("first sent");
        });
        var second = serialiser.RunAsync(map, () =>
        {
            secondStarted.SetResult();
            order.Add("second read and sent");
            return Task.CompletedTask;
        });

        await Task.Delay(Short, TestContext.Current.CancellationToken);
        Assert.False(secondStarted.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(["first read", "first sent", "second read and sent"], order);
        Assert.Equal(0, serialiser.ActiveKeys);
    }

    [Fact]
    public async Task FR363_sends_of_different_maps_run_concurrently()
    {
        var serialiser = new KeyedSerialiser();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = serialiser.RunAsync(Guid.CreateVersion7(), () => release.Task);

        var other = serialiser.RunAsync(Guid.CreateVersion7(), () => Task.CompletedTask);
        await other.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.False(blocked.IsCompleted);
        release.SetResult();
        await blocked;
        Assert.Equal(0, serialiser.ActiveKeys);
    }

    [Fact]
    public async Task FR363_a_failed_send_fails_only_itself_and_the_next_send_of_the_map_still_runs()
    {
        var serialiser = new KeyedSerialiser();
        var map = Guid.CreateVersion7();
        var failing = serialiser.RunAsync(map, () => Task.FromException(new InvalidOperationException("send failed")));
        var ran = false;
        var next = serialiser.RunAsync(map, () =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        await next;
        Assert.True(ran);
        Assert.Equal(0, serialiser.ActiveKeys);
    }
}
