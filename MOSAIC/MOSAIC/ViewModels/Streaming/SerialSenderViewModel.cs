using System;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Components.Manager.Serial;
using MOSAIC.Models.Streaming;

namespace MOSAIC.ViewModels.Streaming;

/// <summary>
/// View model for <see cref="SerialSender"/>, surfacing link state, queue pressure and traffic
/// counters.
/// </summary>
/// <remarks>
/// <para>
/// <b>Polling, not events:</b> the block deliberately raises nothing per line — at 1 kHz that would
/// be a thousand notifications a second for a card that repaints sixty times at most. Instead this
/// samples <see cref="SerialSender.GetStats"/> on a <see cref="DispatcherTimer"/> at 4 Hz, which also
/// means every property here is written on the UI thread and needs no marshalling. Assignments to
/// unchanged values are dropped by <c>SetProperty</c>, so an idle link costs nothing but the tick.
/// </para>
/// <para>
/// <b>What is editable:</b> the port and its baud rate, through
/// <see cref="SerialSender.TryChangePort"/> — the one pair of settings whose right value is a
/// property of the machine the pipeline is running on rather than of the pipeline, and therefore the
/// one pair worth fixing without editing the JSON and reloading. Everything else on this card is a
/// read-out. The framing — separator, terminator, decimals — stays fixed at construction, because
/// changing it mid-stream would desynchronise the firmware's parser rather than reconfigure it.
/// </para>
/// </remarks>
public sealed partial class SerialSenderViewModel : ObservableObject, IDisposable
{
    /// <summary>Poll cadence in milliseconds, matching <c>BaseBlock</c>'s own status timer.</summary>
    private const int PollIntervalMs = 250;

    /// <summary>The block being mirrored.</summary>
    private readonly SerialSender _block;

    /// <summary>Drives <see cref="Poll"/> on the UI thread.</summary>
    private readonly DispatcherTimer _pollTimer;

    /// <summary>
    /// Initializes a new <see cref="SerialSenderViewModel"/> and starts polling.
    /// </summary>
    /// <param name="block">The block to mirror.</param>
    /// <exception cref="ArgumentNullException"><paramref name="block"/> is <see langword="null"/>.</exception>
    public SerialSenderViewModel(SerialSender block)
    {
        _block = block ?? throw new ArgumentNullException(nameof(block));

        var options = _block.Options;
        var separator = SerialSenderOptions.Escape(options.Separator);
        var terminator = SerialSenderOptions.Escape(options.Terminator);

        LineFormatText = $"ch0{separator}ch1{separator}…{terminator}";
        PrecisionText = $"F{options.Decimals}";

        // Backing fields directly: the generated setters would fire change notifications and
        // re-evaluate a command before anything is bound, for values the first Poll confirms anyway.
        _portText = options.PortName;
        _baudText = $"{options.BaudRate:N0}";
        _selectedPort = options.PortName;
        _baudInput = options.BaudRate.ToString(CultureInfo.InvariantCulture);

        RefreshPorts();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(PollIntervalMs) };
        _pollTimer.Tick += Poll;
        _pollTimer.Start();

        // Show real numbers immediately rather than a card full of zeros until the first tick.
        Poll(this, EventArgs.Empty);
    }

    #region Static Configuration

    /// <summary>The underlying block. Bound directly for <c>Viz</c>.</summary>
    public SerialSender Block => _block;

    /// <summary>Block display name.</summary>
    public string Name => _block.Name;

    /// <summary>One line of the wire format with delimiters shown escaped.</summary>
    public string LineFormatText { get; }

    /// <summary>The numeric format applied to every field.</summary>
    public string PrecisionText { get; }

    /// <summary>Queue size in lines.</summary>
    public int QueueCapacity => _block.Options.QueueCapacity;

    /// <summary>Which end of the queue is sacrificed when it fills.</summary>
    public string DropPolicyText => _block.Options.DropPolicy.ToString();

    #endregion

    #region Live State

    /// <summary>Whether the port is currently open.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Link state as a short badge caption.</summary>
    [ObservableProperty] private string _statusText = "Starting";

    /// <summary>Port the block is targeting, which a change makes different from what was configured.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionInfo))]
    private string _portText;

    /// <summary>Baud rate in effect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionInfo))]
    private string _baudText;

    /// <summary>Samples handed to the queue.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropRateText))]
    private long _enqueued;

    /// <summary>Lines that reached the port.</summary>
    [ObservableProperty] private long _written;

    /// <summary>Samples discarded, whether by queue policy or a failed write.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DropRateText))]
    private long _dropped;

    /// <summary>Writes abandoned on timeout.</summary>
    [ObservableProperty] private long _timeouts;

    /// <summary>Successful reopens after the first connect.</summary>
    [ObservableProperty] private long _reconnects;

    /// <summary>Lines waiting for the writer thread.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueueText))]
    [NotifyPropertyChangedFor(nameof(QueueUsage))]
    private int _pending;

    /// <summary>Most recent port failure, if any.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _lastError;

    /// <summary>Most recent line written, terminator trimmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastLine))]
    private string? _lastLine;

    #endregion

    #region Port Selection

    /// <summary>
    /// Serial ports offered in the picker.
    /// </summary>
    /// <remarks>
    /// The machine's enumerated ports, plus whichever one is currently wanted even if it is not among
    /// them. A board that has been unplugged, or a port the block is still retrying, has to stay
    /// visible — a picker that silently drops the port in use would misreport the block's state and
    /// leave no way to select it back.
    /// </remarks>
    public ObservableCollection<string> AvailablePorts { get; } = new();

    /// <summary>Port chosen in the picker. Nothing happens until it is applied.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyPortCommand))]
    private string? _selectedPort;

    /// <summary>Baud rate typed into the picker, as text. Empty means "leave it alone".</summary>
    [ObservableProperty] private string _baudInput;

    /// <summary>Outcome of the last apply, or <see langword="null"/> when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPortMessage))]
    private string? _portMessage;

    /// <summary>Whether <see cref="PortMessage"/> reports a failure rather than progress.</summary>
    [ObservableProperty] private bool _portMessageIsError;

    /// <summary>Whether there is a message to show.</summary>
    public bool HasPortMessage => !string.IsNullOrEmpty(PortMessage);

    /// <summary>
    /// The port and baud rate an applied change is waiting on, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// The change is asynchronous — the writer thread adopts it when it next comes round — so this
    /// records what to watch for, and <see cref="Poll"/> clears the message once the block reports it.
    /// Without that the card would keep saying "switching" long after it had switched.
    /// </remarks>
    private string? _awaitingChange;

    /// <summary>Re-reads the machine's serial ports, preserving the current selection.</summary>
    [RelayCommand]
    private void RefreshPorts()
    {
        // Captured first: clearing the collection makes the ComboBox drop its selection and write
        // null back through the two-way binding.
        var wanted = SelectedPort ?? _block.Options.PortName;

        AvailablePorts.Clear();

        foreach (var port in SystemSerialPort.AvailablePorts)
            AvailablePorts.Add(port);

        if (!string.IsNullOrWhiteSpace(wanted) && !AvailablePorts.Contains(wanted))
            AvailablePorts.Insert(0, wanted);

        SelectedPort = string.IsNullOrWhiteSpace(wanted) ? null : wanted;
    }

    /// <summary>Whether there is a port selected to apply.</summary>
    private bool CanApplyPort() => !string.IsNullOrWhiteSpace(SelectedPort);

    /// <summary>
    /// Hands the picked port and baud rate to the block.
    /// </summary>
    /// <remarks>
    /// Returning without an exception means the request was accepted, not that the port opened —
    /// that shows up on the status badge a moment later, the same way the initial connect does.
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanApplyPort))]
    private void ApplyPort()
    {
        var port = SelectedPort;
        if (string.IsNullOrWhiteSpace(port)) return;

        var current = _block.Options;
        var typed = BaudInput?.Trim();
        int? baud = null;

        if (!string.IsNullOrEmpty(typed))
        {
            // Invariant rather than current culture: a baud rate is a bare integer, and a locale that
            // groups digits would otherwise reject what the box itself displays.
            if (!int.TryParse(typed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                SetPortMessage($"'{typed}' is not a baud rate.", isError: true);
                return;
            }

            baud = parsed;
        }

        if (!_block.TryChangePort(port, baud, out var error))
        {
            SetPortMessage(error ?? "Port change refused.", isError: true);
            return;
        }

        _awaitingChange = ChangeKey(port, baud ?? current.BaudRate);
        SetPortMessage($"Switching to {port}…", isError: false);
    }

    /// <summary>Sets the picker's message and its severity together, so the two cannot disagree.</summary>
    private void SetPortMessage(string? message, bool isError)
    {
        PortMessage = message;
        PortMessageIsError = isError;
    }

    /// <summary>Identifies a port/baud pair, for recognising when a requested change has landed.</summary>
    private static string ChangeKey(string portName, int baudRate)
        => $"{portName.Trim()}|{baudRate}";

    #endregion

    #region Derived

    /// <summary>Port and baud rate, for the card subtitle.</summary>
    public string ConnectionInfo => $"Serial: {PortText} @ {BaudText} baud";

    /// <summary>Queue occupancy as <c>pending / capacity</c>.</summary>
    public string QueueText => $"{Pending} / {QueueCapacity}";

    /// <summary>Queue occupancy as a percentage, for a progress bar.</summary>
    public double QueueUsage => QueueCapacity <= 0 ? 0 : 100.0 * Pending / QueueCapacity;

    /// <summary>
    /// Share of samples that never reached the wire.
    /// </summary>
    /// <remarks>
    /// The number that matters on this card: a link can look connected and still be losing most of
    /// what the pipeline produces, because the queue absorbs the evidence.
    /// </remarks>
    public string DropRateText => Enqueued == 0 ? "—" : $"{100.0 * Dropped / Enqueued:F1} %";

    /// <summary>Whether a failure has been recorded.</summary>
    public bool HasError => !string.IsNullOrEmpty(LastError);

    /// <summary>Whether anything has been written yet.</summary>
    public bool HasLastLine => !string.IsNullOrEmpty(LastLine);

    #endregion

    /// <summary>Samples the block's counters and pushes them into the bound properties.</summary>
    private void Poll(object? sender, EventArgs e)
    {
        var stats = _block.GetStats();
        var options = _block.Options;

        PortText = options.PortName;
        BaudText = $"{options.BaudRate:N0}";

        // A requested change has been adopted by the writer thread; the badge tells the rest of the
        // story from here, so the picker's own message has done its job.
        if (_awaitingChange is not null && ChangeKey(options.PortName, options.BaudRate) == _awaitingChange)
        {
            _awaitingChange = null;
            SetPortMessage(null, isError: false);
        }

        IsConnected = stats.IsConnected;
        Enqueued = stats.Enqueued;
        Written = stats.Written;
        Dropped = stats.Dropped;
        Timeouts = stats.Timeouts;
        Reconnects = stats.Reconnects;
        Pending = stats.Pending;
        LastError = stats.LastError;
        LastLine = _block.LastLine;

        StatusText = stats.IsConnected
            ? "Connected"
            : options.AutoReconnect ? "Reconnecting" : "Offline";
    }

    /// <inheritdoc />
    /// <remarks>Stops the timer only. The block itself is owned by the graph, not by this card.</remarks>
    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Tick -= Poll;
    }
}
