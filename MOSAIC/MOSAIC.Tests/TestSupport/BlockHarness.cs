using System.Threading;
using MathNet.Numerics.LinearAlgebra;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Tests.TestSupport;

/// <summary>
/// Test helper for driving a <see cref="BaseBlock"/> and capturing the value it publishes.
/// </summary>
/// <remarks>
/// Blocks emit output through <see cref="BaseBlock.Publish"/>, which hands off to a per-subscriber
/// background pump in <see cref="OutputDispatcher"/> — delivery is asynchronous. So we attach a
/// capture subscriber and block on a <see cref="ManualResetEventSlim"/> with a timeout, ensuring the
/// value has actually been delivered before we assert. This keeps the tests deterministic while
/// exercising the real block wiring end to end.
/// </remarks>
public static class BlockHarness
{
    /// <summary>Captures the first value delivered to it and signals a wait handle.</summary>
    private sealed class CaptureSubscriber : ISubscriber
    {
        private readonly ManualResetEventSlim _received = new(false);
        public object? Captured { get; private set; }

        public void ReceiveInput(object sender, object value)
        {
            Captured = value;
            _received.Set();
        }

        public bool WaitForValue(int timeoutMs) => _received.Wait(timeoutMs);
    }

    /// <summary>
    /// Feeds <paramref name="input"/> into <paramref name="block"/> via its public
    /// <see cref="BaseBlock.ReceiveInput"/> entry point (which invokes the protected
    /// <c>OnReceive</c>), then returns the first value it publishes, cast to <typeparamref name="T"/>.
    /// </summary>
    public static T Capture<T>(BaseBlock block, object input, int timeoutMs = 2000) where T : class
    {
        var capture = new CaptureSubscriber();
        block.AddSubscriber(capture);

        block.ReceiveInput(sender: block, value: input);

        Assert.IsTrue(capture.WaitForValue(timeoutMs), "Block did not publish a value within the timeout.");
        Assert.IsNotNull(capture.Captured, "Block published a null value.");
        Assert.IsInstanceOfType(capture.Captured, typeof(T), $"Published value was not {typeof(T).Name}.");
        return (T)capture.Captured!;
    }

    /// <summary>Feeds one input and returns the published <see cref="Vector{T}"/>.</summary>
    public static Vector<double> CaptureVector(BaseBlock block, object input, int timeoutMs = 2000)
        => Capture<Vector<double>>(block, input, timeoutMs);

    /// <summary>Feeds one input and returns the published <see cref="Matrix{T}"/>.</summary>
    public static Matrix<double> CaptureMatrix(BaseBlock block, object input, int timeoutMs = 2000)
        => Capture<Matrix<double>>(block, input, timeoutMs);

    /// <summary>
    /// Feeds <paramref name="input"/> and reports whether the block published anything within
    /// <paramref name="timeoutMs"/>. Useful for blocks that only emit on certain conditions
    /// (triggers, gates) — assert <c>false</c> to prove a block correctly stayed silent.
    /// </summary>
    public static bool TryCapture(BaseBlock block, object input, out object? published, int timeoutMs = 500)
    {
        var capture = new CaptureSubscriber();
        block.AddSubscriber(capture);

        block.ReceiveInput(sender: block, value: input);

        bool got = capture.WaitForValue(timeoutMs);
        published = capture.Captured;
        return got;
    }
}
