using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Models.Devices;


namespace MOSAIC.ViewModels.Devices;

/// <summary>
/// Represents a selectable stream type rendered as a checkbox in the UI.
/// </summary>
public partial class StreamTypeOption : ObservableObject
{
    /// <summary>The stream type this option represents.</summary>
    public EspStreamType Type { get; }

    /// <summary>Display label for the checkbox.</summary>
    public string Label { get; }

    /// <summary>Whether this stream type is currently selected.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>Initialises a stream type option.</summary>
    public StreamTypeOption(EspStreamType type, string label, bool selected = false)
    {
        Type = type; Label = label; _isSelected = selected;
    }
}

/// <summary>
/// ViewModel for the <see cref="CommandSender"/> device card.
/// Provides checkboxes for stream type selection, editable IP/port fields,
/// device number, on/off toggle, and a send button.
/// </summary>
public partial class CommandSenderViewModel : ObservableObject
{
    private CommandSender? _block;

    /// <summary>
    /// The four sensor streams, as checkboxes. A ticked box means "this stream should be ON".
    /// </summary>
    /// <remarks>
    /// <c>ALL</c> is deliberately not offered here. It is a shorthand on the wire, but as a
    /// checkbox alongside the individual streams it is ambiguous — ticking ALL and un-ticking
    /// ROT describes no coherent state. Ticking all four boxes expresses the same thing
    /// unambiguously.
    /// </remarks>
    public ObservableCollection<StreamTypeOption> StreamOptions { get; } = new()
    {
        new(EspStreamType.ROT, "Rotation"),
        new(EspStreamType.ACC, "Accelerometer"),
        new(EspStreamType.GYR, "Gyroscope"),
        new(EspStreamType.MAG, "Magnetometer")
    };

    /// <summary>Target ESP host IP address (editable).</summary>
    [ObservableProperty] private string _host = "192.168.1.1";

    /// <summary>Target UDP port number (editable as text).</summary>
    [ObservableProperty] private string _port = "5000";

    /// <summary>
    /// Which ESP nodes to address, as a spec like <c>"18"</c>, <c>"1,2,5"</c> or <c>"1-8, 18"</c>.
    /// </summary>
    /// <remarks>
    /// A body rig carries many nodes at once, and their ids are not a dense 1..n range — a rig
    /// can have sensor 18 with nothing below it. A free-text spec addresses a whole set in one
    /// action instead of forcing one Apply per node.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandPreview))]
    [NotifyPropertyChangedFor(nameof(DeviceCount))]
    private string _deviceSpec = "1";

    /// <summary>The node ids <see cref="DeviceSpec"/> currently resolves to; empty if malformed.</summary>
    public IReadOnlyList<int> Devices =>
        TryParseDevices(DeviceSpec, out var devices, out _) ? devices : [];

    /// <summary>How many nodes <see cref="DeviceSpec"/> resolves to.</summary>
    public int DeviceCount => Devices.Count;

    /// <summary>
    /// A summary of exactly what <see cref="ApplyCommand"/> will put on the wire. For a single
    /// node this is the literal command list (<c>ROT18on  ACC18off …</c>); for several it is a
    /// device list plus the per-node stream states, since spelling out 72 commands helps nobody.
    /// </summary>
    public string CommandPreview
    {
        get
        {
            if (!TryParseDevices(DeviceSpec, out var devices, out var error))
                return $"⚠ {error}";

            var states = StreamOptions.Select(o => $"{o.Type}{(o.IsSelected ? "on" : "off")}").ToList();

            if (devices.Count == 1)
                return "→ " + string.Join("  ", StreamOptions.Select(
                    o => $"{o.Type}{devices[0]}{(o.IsSelected ? "on" : "off")}"));

            return $"→ {devices.Count} devices [{Summarise(devices)}] × {string.Join(", ", states)}"
                 + $"  =  {devices.Count * states.Count} commands";
        }
    }

    /// <summary>Renders a sorted id list compactly, collapsing runs (1,2,3,7 → "1-3,7").</summary>
    private static string Summarise(IReadOnlyList<int> ids)
    {
        var parts = new List<string>();
        for (int i = 0; i < ids.Count; )
        {
            int start = i;
            while (i + 1 < ids.Count && ids[i + 1] == ids[i] + 1) i++;
            parts.Add(start == i ? $"{ids[start]}" : $"{ids[start]}-{ids[i]}");
            i++;
        }
        return string.Join(",", parts);
    }

    /// <summary>
    /// Parses a device spec — comma/space separated ids and inclusive ranges, e.g.
    /// <c>"1-8, 18"</c> — into a sorted, de-duplicated list of node ids.
    /// </summary>
    /// <param name="spec">The text to parse.</param>
    /// <param name="devices">The resolved ids, or empty when parsing fails.</param>
    /// <param name="error">A message naming the offending token, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the whole spec parsed.</returns>
    public static bool TryParseDevices(string? spec, out IReadOnlyList<int> devices, out string? error)
    {
        devices = [];
        error = null;

        if (string.IsNullOrWhiteSpace(spec))
        {
            error = "no device specified";
            return false;
        }

        var ids = new SortedSet<int>();
        foreach (var token in spec.Split([',', ';', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var range = token.Split('-', StringSplitOptions.RemoveEmptyEntries);

            if (range.Length == 1 && int.TryParse(range[0], out int single))
            {
                if (!InRange(single, out error)) return false;
                ids.Add(single);
            }
            else if (range.Length == 2 &&
                     int.TryParse(range[0], out int lo) && int.TryParse(range[1], out int hi))
            {
                // Accept either order so "8-1" is not a silent no-op.
                if (lo > hi) (lo, hi) = (hi, lo);
                if (!InRange(lo, out error) || !InRange(hi, out error)) return false;
                for (int i = lo; i <= hi; i++) ids.Add(i);
            }
            else
            {
                error = $"'{token}' is not a device number or range";
                return false;
            }
        }

        if (ids.Count == 0)
        {
            error = "no device specified";
            return false;
        }

        devices = ids.ToList();
        return true;

        static bool InRange(int id, out string? err)
        {
            if (id is >= 1 and <= 99) { err = null; return true; }
            err = $"device {id} is outside 1-99";
            return false;
        }
    }

    /// <summary>Whether the UDP client is currently connected.</summary>
    [ObservableProperty] private bool _isConnected;

    /// <summary>Indicates a command is currently being sent.</summary>
    [ObservableProperty] private bool _isSending;

    /// <summary>Status or error message displayed in the UI.</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>Observable info from the underlying block.</summary>
    public CommandSenderInfo? Info => _block?.Info;

    /// <summary>
    /// Initialises the ViewModel. If a block is provided (from JSON config),
    /// its host/port are used as defaults.
    /// </summary>
    public CommandSenderViewModel(CommandSender? block = null)
    {
        // The stream checkboxes are separate observable objects, so ticking one raises a
        // change on that option, not on this VM — the preview has to listen to each.
        foreach (var option in StreamOptions)
            option.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CommandPreview));

        if (block is not null)
        {
            _block = block;
            _host = block.Host;
            _port = block.Port.ToString();

            // The block connects itself during ConfigureInput, so the card must open
            // already showing that state rather than a stale "Disconnected".
            _isConnected = block.IsConnected;
            _statusMessage = block.Info.Status;
        }
    }

    /// <summary>
    /// Connects (or reconnects) the UDP client with the current host and port.
    /// </summary>
    [RelayCommand]
    private void Connect()
    {
        if (!int.TryParse(Port, out int portNum) || portNum is < 1 or > 65535)
        {
            StatusMessage = "Invalid port number.";
            return;
        }

        try
        {
            if (_block is not null)
            {
                _block.Host = Host;
                _block.Port = portNum;
            }
            else
            {
                _block = new CommandSender("ESP-Control", Host, portNum);
            }
            _block.Connect();
            IsConnected = true;
            StatusMessage = $"Connected to {Host}:{Port}";
            OnPropertyChanged(nameof(Info));
        }
        catch (Exception ex)
        {
            IsConnected = false;
            StatusMessage = ex.Message;
        }
    }

    /// <summary>
    /// Whether the block is bound to the endpoint the card is currently displaying.
    /// </summary>
    /// <remarks>
    /// An unparsable port counts as a mismatch, so Apply routes through <see cref="Connect"/>
    /// and gets "Invalid port number." instead of quietly using the last good endpoint.
    /// </remarks>
    private bool EndpointMatchesCard() =>
        _block is not null
        && int.TryParse(Port, out int portNum)
        && _block.Host == Host
        && _block.Port == portNum;

    /// <summary>
    /// Closes the UDP client, leaving the block reusable on a different host or port.
    /// </summary>
    [RelayCommand]
    private void Disconnect()
    {
        _block?.Disconnect();
        IsConnected = false;
        StatusMessage = "Disconnected";
        OnPropertyChanged(nameof(Info));
    }

    /// <summary>
    /// Drives the device to exactly the state shown by the checkboxes: every ticked stream is
    /// switched on and every un-ticked one is switched off.
    /// </summary>
    /// <remarks>
    /// Every stream receives an explicit desired state. Cleared checkboxes therefore send
    /// <c>off</c> rather than merely omitting a command.
    /// </remarks>
    /// <remarks>
    /// Rebinds first if the address displayed by the card differs from the block's active endpoint,
    /// ensuring the command is sent to the endpoint currently shown to the user.
    /// </remarks>
    [RelayCommand]
    private async Task ApplyAsync()
    {
        if (_block is null || !IsConnected)
        {
            StatusMessage = "Not connected — press Connect first.";
            return;
        }

        if (!EndpointMatchesCard())
        {
            Connect();

            // Both conditions are load-bearing. Connect() returns early on an unparsable port
            // without clearing IsConnected, and it assigns Host/Port to the block before the
            // socket call that can throw — so neither flag alone proves the card's address is
            // the one now bound. Connect() has already explained itself in StatusMessage.
            if (!IsConnected || !EndpointMatchesCard()) return;
        }

        if (!TryParseDevices(DeviceSpec, out var devices, out var error))
        {
            StatusMessage = error!;
            return;
        }

        IsSending = true;
        StatusMessage = string.Empty;
        try
        {
            int sent = 0;
            foreach (int device in devices)
                foreach (var opt in StreamOptions)
                {
                    await _block.SendCommand(opt.Type, device, opt.IsSelected);
                    sent++;
                }

            var on = StreamOptions.Where(o => o.IsSelected).Select(o => o.Type).ToList();
            var scope = devices.Count == 1 ? $"Dev {devices[0]}" : $"{devices.Count} devices";
            StatusMessage = on.Count == 0
                ? $"{scope}: all streams off ({sent} commands)"
                : $"{scope}: {string.Join(", ", on)} on, rest off ({sent} commands)";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>Ticks every stream. Only changes the checkboxes — press Apply to send.</summary>
    [RelayCommand]
    private void SelectAllStreams()
    {
        foreach (var opt in StreamOptions) opt.IsSelected = true;
    }

    /// <summary>Clears every stream. Only changes the checkboxes — press Apply to send.</summary>
    [RelayCommand]
    private void ClearAllStreams()
    {
        foreach (var opt in StreamOptions) opt.IsSelected = false;
    }
}
