using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Manager.BLE;

namespace MOSAIC.Tests.Components.Manager.BLE;

[TestClass]
public class GattOperationQueueTests
{
    [TestMethod]
    public async Task WritesOnDifferentCharacteristicsWaitForMatchingCallback()
    {
        var queue = new GattOperationQueue();
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = queue.ExecuteAsync("cccd-1", () => true);
        var second = queue.ExecuteAsync("cccd-2", () => { secondStarted.SetResult(); return true; });

        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(secondStarted.Task.IsCompleted);
        queue.Complete("unrelated", 0);
        Assert.IsFalse(first.IsCompleted);
        queue.Complete("cccd-1", 0);
        await first;
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsFalse(second.IsCompleted);
        queue.Complete("cccd-2", 0);
        await second;
    }

    [TestMethod]
    public async Task CallbackFailureStopsQueuedWrites()
    {
        var queue = new GattOperationQueue();
        bool started = false;
        var first = queue.ExecuteAsync("command", () => true);
        var second = queue.ExecuteAsync("cccd", () => { started = true; return true; });
        queue.Complete("command", 133);
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => second);
        Assert.IsFalse(started);
    }

    [TestMethod]
    public async Task RejectedWriteFailsImmediately()
    {
        var queue = new GattOperationQueue();
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ExecuteAsync("command", () => false));
    }

    [TestMethod]
    public async Task TimeoutPreventsLateCallbackFromCompletingRetry()
    {
        var queue = new GattOperationQueue(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => queue.ExecuteAsync("command", () => true));
        queue.Complete("command", 0);
        bool started = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ExecuteAsync("command", () => { started = true; return true; }));
        Assert.IsFalse(started);
    }

    [TestMethod]
    public async Task CancellationDuringWriteInvalidatesConnection()
    {
        var queue = new GattOperationQueue();
        using var ct = new CancellationTokenSource();
        var write = queue.ExecuteAsync("command", () => true, ct.Token);
        ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.ExecuteAsync("command", () => true));
    }

    [TestMethod]
    public async Task CancellingQueuedWriteDoesNotAffectActiveWrite()
    {
        var queue = new GattOperationQueue();
        var first = queue.ExecuteAsync("first", () => true);
        using var ct = new CancellationTokenSource();
        var second = queue.ExecuteAsync("second", () => throw new AssertFailedException("Must not start"), ct.Token);
        ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second);
        queue.Complete("first", 0);
        await first;
        var third = queue.ExecuteAsync("third", () => true);
        queue.Complete("third", 0);
        await third;
    }

    [TestMethod]
    public async Task DisconnectUnblocksPendingWriteWithError()
    {
        var queue = new GattOperationQueue();
        var write = queue.ExecuteAsync("command", () => true);
        queue.Fail(new InvalidOperationException("Disconnected"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => write);
    }
}
