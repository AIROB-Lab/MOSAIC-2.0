using System;
using System.Buffers;
using System.IO;
using System.Threading;

namespace MOSAIC.Components.IO;

/// <summary>
/// Reads continuously from any <see cref="Stream"/> on a dedicated high-priority thread
/// and raises <see cref="DataReceived"/> whenever new data arrives.
/// </summary>
/// <remarks>
/// <para>
/// Designed for high sample-rate devices (2 kHz+). Key performance characteristics:
/// </para>
/// <list type="bullet">
///   <item>Dedicated <see cref="Thread"/> (not threadpool) — avoids starvation under load.</item>
///   <item><see cref="ArrayPool{T}"/> rental — zero per-read heap allocations in the hot path.</item>
///   <item>Synchronous <see cref="Stream.Read"/> — avoids async state-machine overhead.</item>
///   <item><see cref="Thread.SpinWait"/> + <see cref="Thread.Sleep(int)"/> hybrid idle — sub-ms
///         wake-up when data resumes after a dry read.</item>
/// </list>
/// <para>
/// Originally created because <c>SerialPort.DataReceived</c> is unreliable
/// (see https://www.sparxeng.com/blog/software/must-use-net-system-io-ports-serialport).
/// Works with serial ports, TCP sockets, file streams, or any other <see cref="Stream"/>.
/// </para>
/// </remarks>
public sealed class StreamManager : IDisposable
{
    private readonly Stream _stream;
    private readonly int    _bufferSize;
    private readonly int    _delayMs;

    private Thread?  _readThread;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>
    /// Fired on the reader thread whenever new data has been read from the stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Performance contract:</b> The byte array is a pooled rental. The handler
    /// <b>must not</b> hold a reference beyond the callback scope — copy if needed.
    /// The <c>int</c> parameter is the number of valid bytes (the array may be larger).
    /// </para>
    /// <para>
    /// The callback executes on the reader thread. Keep handlers fast to avoid
    /// blocking subsequent reads. For heavy processing, enqueue into a
    /// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> or ring buffer.
    /// </para>
    /// </remarks>
    public event Action<byte[], int>? DataReceived;

    /// <summary>
    /// Initialises a new <see cref="StreamManager"/>.
    /// </summary>
    /// <param name="stream">The underlying stream to read from.</param>
    /// <param name="bufferSize">
    /// Maximum bytes per read. Should match or exceed the expected frame/packet size.
    /// Defaults to 1024.
    /// </param>
    /// <param name="delayMs">
    /// Milliseconds to sleep when a read returns zero bytes. Prevents busy-spin on
    /// streams that return immediately with 0 when idle.
    /// Defaults to 1 ms. Set to 0 if the stream blocks until data arrives
    /// (e.g. <see cref="System.Net.Sockets.NetworkStream"/>).
    /// </param>
    public StreamManager(Stream stream, int bufferSize = 1024, int delayMs = 1)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream     = stream;
        _bufferSize = Math.Max(1, bufferSize);
        _delayMs    = Math.Max(0, delayMs);
    }

    #region Write

    /// <summary>Writes a byte array to the underlying stream (synchronous).</summary>
    public void Write(byte[] data) => _stream.Write(data, 0, data.Length);

    /// <summary>Writes a span to the underlying stream (synchronous, zero-copy).</summary>
    public void Write(ReadOnlySpan<byte> data) => _stream.Write(data);

    #endregion

    #region Read Loop

    /// <summary>
    /// Starts the reader on a dedicated high-priority background thread.
    /// Does nothing if already running.
    /// </summary>
    public void StartStreaming()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_running) return;

        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name         = "StreamManager-Reader",
            Priority     = ThreadPriority.AboveNormal
        };
        _readThread.Start();
    }

    /// <summary>
    /// Signals the reader thread to stop and waits for it to exit (up to 2 s).
    /// Safe to call multiple times.
    /// </summary>
    public void StopStreaming()
    {
        _running = false;
        _readThread?.Join(2000);
        _readThread = null;
    }

    /// <summary>
    /// Core read loop. Runs on a dedicated thread until stopped or the stream faults.
    /// </summary>
    private void ReadLoop()
    {
        // Single pooled buffer — rented once, returned once. Zero per-iteration allocation.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);

        try
        {
            while (_running)
            {
                int bytesRead;
                try
                {
                    // Synchronous read avoids async state-machine overhead.
                    // SerialPort.BaseStream and NetworkStream block here until
                    // data arrives (or timeout), which is exactly what we want.
                    bytesRead = _stream.Read(buffer, 0, _bufferSize);
                }
                catch (ObjectDisposedException) { break; }   // stream closed externally
                catch (IOException)             { break; }   // port disconnected / pipe broken
                catch (TimeoutException)                      // SerialPort read timeout (normal)
                {
                    if (_running) continue;
                    break;
                }

                if (bytesRead > 0)
                {
                    // Pass the pooled buffer directly — no copy, no allocation.
                    // Handler must not hold a reference past the callback.
                    DataReceived?.Invoke(buffer, bytesRead);
                }
                else if (_delayMs > 0)
                {
                    // Stream returned 0 without blocking — back off to avoid busy-spin.
                    Thread.Sleep(_delayMs);
                }
                else
                {
                    // delayMs == 0: stream blocks on its own (NetworkStream).
                    // Brief spin-wait just to re-check the cancellation flag.
                    Thread.SpinWait(32);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Stops the reader thread. Does <b>not</b> close the stream (caller owns it).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopStreaming();
    }

    #endregion
}