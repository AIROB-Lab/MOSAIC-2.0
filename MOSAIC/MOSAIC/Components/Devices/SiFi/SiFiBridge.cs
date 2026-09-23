using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MOSAIC.Models.Devices;

namespace MOSAIC.Components.Devices.SiFi;

/// <summary>
/// Manages the <c>sifibridge</c> CLI process for communicating with SiFi BLE devices.
/// </summary>
/// <remarks>
/// <para>
/// This class owns the process lifecycle: starting, sending commands via stdin,
/// receiving responses via stdout/stderr, and terminating. It is used internally
/// by <see cref="SiFi"/> and has no dependency on <c>BaseBlock</c> or any MOSAIC
/// infrastructure — it is a pure process wrapper.
/// </para>
/// <para>
/// The bridge process is started lazily on the first call to <see cref="EnsureRunning"/>
/// and is killed on <see cref="Dispose"/>. If the process exits unexpectedly, the
/// <see cref="Exited"/> event is raised.
/// </para>
/// </remarks>
public sealed class SifiBridge : IDisposable
{
    private readonly string _bridgePath;
    private Process? _process;
    private bool _disposed;

    /// <summary>Whether the bridge process is currently running.</summary>
    public bool IsRunning => _process is not null && !_process.HasExited;

    /// <summary>Raised for each line of stdout from the bridge (JSON packets, status, etc.).</summary>
    public event Action<string>? OutputReceived;

    /// <summary>Raised for each line of stderr from the bridge.</summary>
    public event Action<string>? ErrorReceived;

    /// <summary>Raised when the bridge process exits (expected or unexpected).</summary>
    public event Action? Exited;

    /// <summary>
    /// Initializes a new <see cref="SifiBridge"/> wrapper.
    /// </summary>
    /// <param name="bridgePath">
    /// Path to the <c>sifibridge</c> executable. If just a filename (e.g. <c>"sifibridge"</c>),
    /// it must be on the system PATH.
    /// </param>
    public SifiBridge(string bridgePath = "sifibridge")
    {
        _bridgePath = bridgePath ?? throw new ArgumentNullException(nameof(bridgePath));
    }

    /// <summary>
    /// Ensures the bridge process is running. Starts it if not already running.
    /// </summary>
    /// <exception cref="ObjectDisposedException">Thrown if the wrapper has been disposed.</exception>
    public void EnsureRunning()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRunning) return;

        _process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _bridgePath,
                Arguments = "",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                OutputReceived?.Invoke(e.Data);
        };

        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                ErrorReceived?.Invoke(e.Data);
        };

        _process.Exited += (_, _) => Exited?.Invoke();

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        Debug.WriteLine($"[SifiBridge] Started: {_bridgePath} (PID {_process.Id})");
    }

    /// <summary>
    /// Sends a command string to the bridge's stdin.
    /// </summary>
    /// <param name="command">The CLI command to send (e.g. <c>"connect AA:BB:CC:DD:EE:FF"</c>).</param>
    public async Task SendAsync(string command)
    {
        if (_process is null || _process.HasExited) return;

        await _process.StandardInput.WriteLineAsync(command);
        await _process.StandardInput.FlushAsync();
        Debug.WriteLine($"[SifiBridge] >> {command}");
    }

    /// <summary>
    /// Sends a command synchronously. Use for shutdown sequences where async is impractical.
    /// </summary>
    /// <param name="command">The CLI command to send.</param>
    public void SendSync(string command)
    {
        if (_process is null || _process.HasExited) return;

        _process.StandardInput.WriteLine(command);
        _process.StandardInput.Flush();
    }

    /// <summary>
    /// Attempts a graceful shutdown: sends stop + disconnect, waits briefly, then kills if needed.
    /// </summary>
    /// <param name="waitMs">Maximum milliseconds to wait for the process to exit. Default: 2000.</param>
    public void Shutdown(int waitMs = 2000)
    {
        if (_process is null || _process.HasExited) return;

        try
        {
            SendSync("stop");
            Thread.Sleep(100);
            SendSync("disconnect");
            _process.WaitForExit(waitMs);
        }
        catch { /* bridge may already be gone */ }

        if (!_process.HasExited)
        {
            try { _process.Kill(); }
            catch { /* ignore */ }
        }

        Debug.WriteLine("[SifiBridge] Shutdown complete");
    }

    /// <summary>
    /// Shuts down the bridge process and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Shutdown();
        _process?.Dispose();
    }
}
