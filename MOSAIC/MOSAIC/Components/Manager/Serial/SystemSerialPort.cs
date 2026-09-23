using System;
using System.IO.Ports;
using System.Text;

namespace MOSAIC.Components.Manager.Serial;

/// <summary>
/// The serial port <c>SerialSender</c> transmits through: a <see cref="SerialPort"/> configured 8N1,
/// wrapped so that opening, closing and reopening behave predictably across USB re-enumeration.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threading:</b> used from a single writer thread, with the exception of <see cref="Close"/> and
/// <see cref="Dispose"/>, which the owner may call from another thread to unblock a stalled write.
/// Nothing here is thread-safe beyond that, and it does not need to be — closing concurrently with a
/// <see cref="Write"/> is expected to make that write throw, which is exactly how the block learns
/// the link is gone.
/// </para>
/// <para>
/// <b>Failure signalling:</b> the block distinguishes two failure classes by exception type, so this
/// wrapper must not blur them. A <see cref="TimeoutException"/> means "the peer is not draining its
/// buffer" and leaves the port open; any other exception is treated as a broken link and triggers a
/// close/reopen cycle. In particular, never wrap a timeout in an <see cref="System.IO.IOException"/>
/// — it would turn a merely slow receiver into a reconnect storm.
/// </para>
/// <para>
/// <b>Reopen strategy:</b> <see cref="Open"/> always constructs a fresh <see cref="SerialPort"/>
/// rather than reopening the previous instance. USB-CDC adapters re-enumerate when unplugged, and a
/// <see cref="SerialPort"/> that has seen its device disappear frequently refuses to reopen or
/// throws on close. Recreating costs nothing at reconnect frequency and avoids that whole class of
/// bug.
/// </para>
/// <para>
/// <b>Property caching:</b> <see cref="DtrEnable"/> and <see cref="WriteTimeout"/> are stored on
/// this wrapper and applied to each newly created port, so they can be set before the first
/// <see cref="Open"/> and survive reconnects.
/// </para>
/// <para>
/// <b>Platform:</b> mobile heads must bundle <c>System.IO.Ports</c> because shared block types refer
/// to this wrapper while the block catalog is initialized. Bundling the package makes the type
/// loadable on Android; access to USB-host devices still requires an Android USB serial transport
/// rather than assuming that a desktop-style COM device is exposed to the application.
/// </para>
/// </remarks>
public sealed class SystemSerialPort : IDisposable
{
    /// <summary>Port name as configured, before normalization.</summary>
    private readonly string _portName;

    /// <summary>Baud rate applied to every port instance.</summary>
    private readonly int _baudRate;

    /// <summary>The live port, or <see langword="null"/> while closed.</summary>
    private SerialPort? _port;

    /// <summary>Cached DTR state, applied on each <see cref="Open"/>.</summary>
    private bool _dtrEnable = true;

    /// <summary>Cached write timeout in ms, applied on each <see cref="Open"/>.</summary>
    private int _writeTimeout = 500;

    /// <summary>
    /// Initializes a wrapper for the given port. No hardware is touched until <see cref="Open"/>.
    /// </summary>
    /// <param name="portName">
    /// Port name. A bare number is expanded to <c>COM{n}</c>; POSIX paths such as
    /// <c>/dev/ttyUSB0</c> pass through unchanged.
    /// </param>
    /// <param name="baudRate">Baud rate, e.g. 115200.</param>
    public SystemSerialPort(string portName, int baudRate)
    {
        _portName = portName;
        _baudRate = baudRate;
    }

    /// <summary>Whether the port is currently open and writable.</summary>
    public bool IsOpen => _port is { IsOpen: true };

    /// <summary>
    /// Data Terminal Ready line state.
    /// </summary>
    /// <remarks>
    /// Settable before <see cref="Open"/>. On USB-CDC boards (Arduino and friends) asserting DTR
    /// resets the microcontroller, which is what the block's <c>resetDelayMs</c> waits out.
    /// </remarks>
    public bool DtrEnable
    {
        get => _dtrEnable;
        set
        {
            _dtrEnable = value;
            if (_port is not null) _port.DtrEnable = value;
        }
    }

    /// <summary>
    /// Milliseconds a <see cref="Write"/> may block before throwing <see cref="TimeoutException"/>.
    /// </summary>
    /// <remarks>Settable before <see cref="Open"/>.</remarks>
    public int WriteTimeout
    {
        get => _writeTimeout;
        set
        {
            _writeTimeout = value;
            if (_port is not null) _port.WriteTimeout = value;
        }
    }

    /// <summary>Opens the port, replacing any previous instance.</summary>
    /// <exception cref="Exception">
    /// Any failure to open (port missing, in use, access denied). The block catches all of them,
    /// records the message, and retries if auto-reconnect is enabled.
    /// </exception>
    public void Open()
    {
        Close();

        var port = new SerialPort(NormalizePortName(_portName), _baudRate, Parity.None, 8, StopBits.One)
        {
            Encoding = Encoding.ASCII,
            WriteTimeout = _writeTimeout,
            DtrEnable = _dtrEnable,
            NewLine = "\n"
        };

        port.Open();
        _port = port;
    }

    /// <summary>Closes the port. Safe to call when already closed.</summary>
    /// <remarks>
    /// Close failures are swallowed: the port is being torn down either way, and a device that has
    /// already vanished commonly throws here.
    /// </remarks>
    public void Close()
    {
        var port = _port;
        _port = null;
        if (port is null) return;

        try { port.Close(); } catch { /* the device may already be gone */ }
        try { port.Dispose(); } catch { /* no-op */ }
    }

    /// <summary>Writes <paramref name="count"/> bytes from <paramref name="buffer"/>.</summary>
    /// <param name="buffer">Source buffer. Not retained.</param>
    /// <param name="offset">Start offset within <paramref name="buffer"/>.</param>
    /// <param name="count">Number of bytes to write.</param>
    /// <exception cref="TimeoutException">
    /// The write did not complete within <see cref="WriteTimeout"/>. The port stays open.
    /// </exception>
    public void Write(byte[] buffer, int offset, int count)
    {
        var port = _port ?? throw new InvalidOperationException($"Serial port '{_portName}' is not open.");
        port.Write(buffer, offset, count);
    }

    /// <summary>Closes the port. Nothing else is held.</summary>
    public void Dispose() => Close();

    /// <summary>Names of the serial ports currently present on this machine.</summary>
    public static string[] AvailablePorts => SerialPort.GetPortNames();

    /// <summary>
    /// Expands a bare number to a Windows COM name, leaving anything else untouched.
    /// </summary>
    /// <remarks>
    /// This lets a JSON config saying <c>"3"</c> and one saying <c>"COM3"</c> behave the same.
    /// </remarks>
    public static string NormalizePortName(string portName)
    {
        var trimmed = portName.Trim();
        return int.TryParse(trimmed, out _) ? $"COM{trimmed}" : trimmed;
    }
}
