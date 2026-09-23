using System;
using System.Collections.Generic;
using System.Text;
using MOSAIC.Components.Basics;

namespace MOSAIC.Components.Manager.Serial;

/// <summary>
/// What to discard when the outbound queue is full.
/// </summary>
public enum DropPolicy
{
    /// <summary>
    /// Reject the incoming sample and keep the backlog. Preserves the oldest data, which suits
    /// command streams where every message matters and order is contractual.
    /// </summary>
    DropNewest,

    /// <summary>
    /// Evict the oldest queued sample to make room. Preserves recency, which suits continuous
    /// control signals where a stale set point is worse than a missing one.
    /// </summary>
    DropOldest
}

/// <summary>
/// Validated configuration for <c>SerialSender</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifecycle:</b> instances arrive raw — straight from JSON or a test — and are turned into a
/// canonical, checked copy by <see cref="Normalized"/>, which the block calls once from its
/// constructor. Everything downstream of that point can assume the values are sane, which is why the
/// hot path contains no configuration checks at all.
/// </para>
/// <para>
/// <b>Escaping:</b> <see cref="Terminator"/> and <see cref="Separator"/> accept the escapes
/// <c>\n</c>, <c>\r</c>, <c>\t</c> and <c>\\</c>. JSON already resolves these when the config is
/// written as <c>"\n"</c>; the extra pass exists so that a config written as <c>"\\n"</c>, or a
/// value typed into a text box in the UI, means the same thing.
/// </para>
/// </remarks>
public sealed record SerialSenderOptions
{
    /// <summary>Block display name.</summary>
    public string Name { get; init; } = "SerialSender";

    /// <summary>Emission rate in Hz. 0 = inherited from upstream.</summary>
    public double DesiredRate { get; init; }

    /// <summary>Serial port name, e.g. <c>"COM3"</c> or <c>"/dev/ttyUSB0"</c>. Required.</summary>
    public string PortName { get; init; } = "";

    /// <summary>Baud rate. Must match the firmware.</summary>
    public int BaudRate { get; init; } = 115200;

    /// <summary>Written after the last field of every line.</summary>
    public string Terminator { get; init; } = "\n";

    /// <summary>Written between adjacent fields.</summary>
    public string Separator { get; init; } = ",";

    /// <summary>Digits after the decimal point, used as <c>"F{Decimals}"</c>.</summary>
    public int Decimals { get; init; } = 2;

    /// <summary>Maximum number of formatted lines held for the writer thread.</summary>
    /// <remarks>
    /// Sizes the latency ceiling: at <i>R</i> Hz a full queue is <c>QueueCapacity / R</c> seconds of
    /// backlog. Large values trade staleness for tolerance of transient stalls.
    /// </remarks>
    public int QueueCapacity { get; init; } = 256;

    /// <summary>Milliseconds a single port write may block before it is abandoned.</summary>
    public int WriteTimeoutMs { get; init; } = 500;

    /// <summary>Milliseconds to wait after opening the port before writing.</summary>
    /// <remarks>
    /// Opening a USB-CDC port asserts DTR, which resets boards like the Arduino; anything written
    /// during the bootloader window is lost. 2000 ms covers the common bootloaders. Set to 0 for
    /// devices that do not reset on connect.
    /// </remarks>
    public int ResetDelayMs { get; init; } = 2000;

    /// <summary>Value substituted for <c>NaN</c> and infinities before formatting.</summary>
    /// <remarks>
    /// Firmware parsers rarely handle <c>"NaN"</c> or <c>"∞"</c>, and a single unparsable line often
    /// desynchronises a fixed-field reader for good. Substituting keeps the line width predictable.
    /// </remarks>
    public double NonFiniteValue { get; init; }

    /// <summary>Whether to keep retrying the port after a failed open or a broken link.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>What to discard when the queue is full.</summary>
    public DropPolicy DropPolicy { get; init; } = DropPolicy.DropNewest;

    /// <summary>
    /// Directory for the wire dump, or <see langword="null"/> to disable it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not a positional parameter — this carries the JSON <c>Path</c>, the same value that enables
    /// the CSV dumper, so the two logs turn on and off together. It lives on the options record
    /// rather than being set after construction because the writer thread starts inside the
    /// constructor: a dump opened afterwards could miss the first lines it was meant to witness.
    /// </para>
    /// <para>
    /// Deliberately unvalidated. An unusable path must degrade to "no dump", never to a failed
    /// pipeline load — a logging problem is not a reason to stop transmitting.
    /// </para>
    /// </remarks>
    public string? DumpPath { get; init; }

    /// <summary>
    /// Deferred parse failure from <see cref="FromParams"/>, rethrown by <see cref="Normalized"/>.
    /// </summary>
    /// <remarks>
    /// Parsing happens outside the block constructor, but the constructor is the only place allowed
    /// to throw. Carrying the message here keeps that rule intact without discarding the detail of
    /// what was actually wrong.
    /// </remarks>
    internal string? ParseError { get; init; }

    /// <summary>
    /// Validates the configuration and returns a canonical copy with escapes resolved and the port
    /// name trimmed.
    /// </summary>
    /// <returns>A validated copy. Never the same instance.</returns>
    /// <exception cref="ArgumentException">Thrown on any invalid value, naming the offending field.</exception>
    public SerialSenderOptions Normalized()
    {
        if (ParseError is not null)
            throw new ArgumentException($"SerialSender '{Name}': {ParseError}");

        var portName = PortName?.Trim() ?? "";
        var terminator = Unescape(Terminator);
        var separator = Unescape(Separator);

        if (portName.Length == 0)
            throw new ArgumentException($"SerialSender '{Name}': portName is required.");

        if (BaudRate <= 0)
            throw new ArgumentException($"SerialSender '{Name}': baudRate must be positive, got {BaudRate}.");

        if (terminator.Length == 0)
            throw new ArgumentException($"SerialSender '{Name}': terminator must not be empty — the receiver would never see a line boundary.");

        if (separator.Length == 0)
            throw new ArgumentException($"SerialSender '{Name}': separator must not be empty — multi-channel fields would run together.");

        // The wire format is ASCII by definition; a non-ASCII delimiter would be silently mangled by
        // the encoder, producing bytes the firmware never agreed to.
        if (!IsAscii(terminator))
            throw new ArgumentException($"SerialSender '{Name}': terminator must be ASCII.");

        if (!IsAscii(separator))
            throw new ArgumentException($"SerialSender '{Name}': separator must be ASCII.");

        if (Decimals is < 0 or > 15)
            throw new ArgumentException($"SerialSender '{Name}': decimals must be between 0 and 15, got {Decimals}.");

        if (QueueCapacity < 1)
            throw new ArgumentException($"SerialSender '{Name}': queueCapacity must be at least 1, got {QueueCapacity}.");

        if (WriteTimeoutMs < 1)
            throw new ArgumentException($"SerialSender '{Name}': writeTimeoutMs must be at least 1, got {WriteTimeoutMs}.");

        if (ResetDelayMs < 0)
            throw new ArgumentException($"SerialSender '{Name}': resetDelayMs must not be negative, got {ResetDelayMs}.");

        if (double.IsNaN(NonFiniteValue) || double.IsInfinity(NonFiniteValue))
            throw new ArgumentException($"SerialSender '{Name}': nonFiniteValue must itself be finite.");

        // Guards a cast from an out-of-range integer; the string form is already checked at parse time.
        if (!Enum.IsDefined(typeof(DropPolicy), DropPolicy))
            throw new ArgumentException($"SerialSender '{Name}': dropPolicy must be DropNewest or DropOldest.");

        return this with
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "SerialSender" : Name,
            PortName = portName,
            Terminator = terminator,
            Separator = separator
        };
    }

    /// <summary>
    /// Builds options from a positional JSON <c>Params</c> list.
    /// </summary>
    /// <param name="p">Positional parameters. May be <see langword="null"/> or short; missing and
    /// empty entries fall back to the default for that position.</param>
    /// <param name="name">Block name.</param>
    /// <param name="desiredRate">Emission rate in Hz.</param>
    /// <returns>
    /// Unvalidated options. Any value that could not be interpreted is recorded in
    /// <see cref="ParseError"/> and surfaces when <see cref="Normalized"/> runs.
    /// </returns>
    /// <remarks>
    /// This method never throws — configuration errors are the constructor's to report, so that a
    /// half-built block is never left behind.
    /// </remarks>
    public static SerialSenderOptions FromParams(IReadOnlyList<object>? p, string name, double desiredRate)
    {
        p ??= Array.Empty<object>();

        var defaults = new SerialSenderOptions();
        string? parseError = null;

        var dropPolicy = defaults.DropPolicy;
        var rawDropPolicy = Str(p, 10, null);
        if (rawDropPolicy is not null && !TryParseDropPolicy(rawDropPolicy, out dropPolicy))
        {
            dropPolicy = defaults.DropPolicy;
            parseError = $"dropPolicy '{rawDropPolicy}' is not recognised — use \"DropNewest\" or \"DropOldest\".";
        }

        return new SerialSenderOptions
        {
            Name = name,
            DesiredRate = desiredRate,
            PortName = Str(p, 0, "") ?? "",
            BaudRate = Num(p, 1, defaults.BaudRate),
            Terminator = Str(p, 2, defaults.Terminator)!,
            Separator = Str(p, 3, defaults.Separator)!,
            Decimals = Num(p, 4, defaults.Decimals),
            QueueCapacity = Num(p, 5, defaults.QueueCapacity),
            WriteTimeoutMs = Num(p, 6, defaults.WriteTimeoutMs),
            ResetDelayMs = Num(p, 7, defaults.ResetDelayMs),
            NonFiniteValue = Real(p, 8, defaults.NonFiniteValue),
            AutoReconnect = Flag(p, 9, defaults.AutoReconnect),
            DropPolicy = dropPolicy,
            ParseError = parseError
        };
    }

    /// <summary>Parses a drop-policy name, accepting the bare forms <c>"newest"</c>/<c>"oldest"</c>.</summary>
    /// <returns><see langword="true"/> if recognised.</returns>
    public static bool TryParseDropPolicy(string? text, out DropPolicy policy)
    {
        policy = DropPolicy.DropNewest;
        if (string.IsNullOrWhiteSpace(text)) return false;

        switch (text.Trim().ToLowerInvariant())
        {
            case "dropnewest":
            case "newest":
                policy = DropPolicy.DropNewest;
                return true;

            case "dropoldest":
            case "oldest":
                policy = DropPolicy.DropOldest;
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Resolves the escapes <c>\n</c>, <c>\r</c>, <c>\t</c> and <c>\\</c>. Other backslash sequences
    /// are left verbatim.
    /// </summary>
    /// <remarks>
    /// A trailing lone backslash is kept as-is rather than treated as an error — it is far more
    /// likely a typo in a delimiter than something worth refusing to start over.
    /// </remarks>
    public static string Unescape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.IndexOf('\\') < 0) return text;

        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                sb.Append(text[i]);
                continue;
            }

            switch (text[i + 1])
            {
                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append(text[i]); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Inverse of <see cref="Unescape"/>, for showing a delimiter in a UI or a log line without
    /// breaking the layout.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        var sb = new StringBuilder(text.Length + 2);

        foreach (var c in text)
        {
            switch (c)
            {
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\\': sb.Append("\\\\"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }

    /// <summary>Whether every character is representable in 7-bit ASCII.</summary>
    private static bool IsAscii(string text)
    {
        foreach (var c in text)
            if (c > 0x7F) return false;

        return true;
    }

    /// <summary>
    /// Reads a positional string, treating a missing or empty entry as "use the default".
    /// </summary>
    /// <remarks>
    /// Whitespace is preserved, so a single space is a usable separator; only a genuinely empty
    /// value falls back.
    /// </remarks>
    private static string? Str(IReadOnlyList<object> p, int index, string? fallback)
    {
        if (index >= p.Count) return fallback;

        var s = JsonModel.GetString(p[index], null);
        return string.IsNullOrEmpty(s) ? fallback : s;
    }

    /// <summary>Reads a positional integer, falling back on a missing or empty entry.</summary>
    private static int Num(IReadOnlyList<object> p, int index, int fallback)
        => index >= p.Count || IsBlank(p[index]) ? fallback : JsonModel.GetInt(p[index], fallback);

    /// <summary>Reads a positional double, falling back on a missing or empty entry.</summary>
    private static double Real(IReadOnlyList<object> p, int index, double fallback)
        => index >= p.Count || IsBlank(p[index]) ? fallback : JsonModel.GetDouble(p[index], fallback);

    /// <summary>Reads a positional boolean, falling back on a missing or empty entry.</summary>
    private static bool Flag(IReadOnlyList<object> p, int index, bool fallback)
        => index >= p.Count || IsBlank(p[index]) ? fallback : JsonModel.GetBool(p[index], fallback);

    /// <summary>Whether a raw parameter is the empty string, i.e. an explicit "use the default".</summary>
    private static bool IsBlank(object? raw) => JsonModel.GetString(raw, null) is not { Length: > 0 };
}
