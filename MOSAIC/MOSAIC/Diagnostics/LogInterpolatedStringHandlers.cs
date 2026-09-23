using System;
using System.Runtime.CompilerServices;

namespace MOSAIC.Diagnostics;

/// <summary>
/// Interpolated-string handler for <see cref="Log.Debug(string, ref DebugLogHandler)"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the mechanism that makes a switched-off level genuinely free, and it is the only reason
/// per-packet call sites on the Delsys, Muovi and BLE data threads are allowed to log at all.
/// </para>
/// <para>
/// Because the constructor has an <c>out bool shouldAppend</c> parameter, Roslyn rewrites
/// <c>Log.Debug("Delsys", $"frame {n} of {total}")</c> into a handler construction followed by
/// <c>if (shouldAppend) { ...AppendLiteral/AppendFormatted... }</c>. The interpolation holes
/// themselves live inside that <c>if</c>, so when the level is off nothing is boxed, nothing is
/// formatted, no <c>string.Format</c> runs and no array is allocated - the call costs one volatile
/// read of <see cref="Log.MinimumLevel"/> and a branch.
/// </para>
/// <para>
/// The inner <see cref="DefaultInterpolatedStringHandler"/> is constructed only when enabled, which
/// is why every member checks <c>_enabled</c> before forwarding: an unconstructed inner handler must
/// never be touched, and a logger that throws while logging is worse than no logger.
/// </para>
/// </remarks>
[InterpolatedStringHandler]
public ref struct DebugLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>Called by the compiler. Sets <paramref name="shouldAppend"/> from the level gate.</summary>
    /// <param name="literalLength">Total length of the literal parts, supplied by the compiler.</param>
    /// <param name="formattedCount">Number of interpolation holes, supplied by the compiler.</param>
    /// <param name="shouldAppend">False when <see cref="LogLevel.Debug"/> is switched off.</param>
    public DebugLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        _enabled = Log.IsEnabled(LogLevel.Debug);
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Appends a literal fragment of the interpolated string.</summary>
    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    /// <summary>Appends an interpolation hole.</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends an interpolation hole with a format string.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    /// <summary>Appends an interpolation hole with an alignment.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment);
    }

    /// <summary>Appends an interpolation hole with an alignment and a format string.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>Appends a string hole without boxing it through the generic overload.</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a span hole without an intermediate string.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    internal bool Enabled => _enabled;

    internal string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated-string handler for <see cref="Log.Info(string, ref InfoLogHandler)"/>.
/// </summary>
/// <remarks>Same zero-cost-when-disabled mechanism as <see cref="DebugLogHandler"/>.</remarks>
[InterpolatedStringHandler]
public ref struct InfoLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>Called by the compiler. Sets <paramref name="shouldAppend"/> from the level gate.</summary>
    /// <param name="literalLength">Total length of the literal parts, supplied by the compiler.</param>
    /// <param name="formattedCount">Number of interpolation holes, supplied by the compiler.</param>
    /// <param name="shouldAppend">False when <see cref="LogLevel.Info"/> is switched off.</param>
    public InfoLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        _enabled = Log.IsEnabled(LogLevel.Info);
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Appends a literal fragment of the interpolated string.</summary>
    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    /// <summary>Appends an interpolation hole.</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends an interpolation hole with a format string.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    /// <summary>Appends an interpolation hole with an alignment.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment);
    }

    /// <summary>Appends an interpolation hole with an alignment and a format string.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>Appends a string hole without boxing it through the generic overload.</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a span hole without an intermediate string.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    internal bool Enabled => _enabled;

    internal string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated-string handler for <see cref="Log.Warn(string, ref WarnLogHandler)"/>.
/// </summary>
/// <remarks>Same zero-cost-when-disabled mechanism as <see cref="DebugLogHandler"/>.</remarks>
[InterpolatedStringHandler]
public ref struct WarnLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>Called by the compiler. Sets <paramref name="shouldAppend"/> from the level gate.</summary>
    /// <param name="literalLength">Total length of the literal parts, supplied by the compiler.</param>
    /// <param name="formattedCount">Number of interpolation holes, supplied by the compiler.</param>
    /// <param name="shouldAppend">False when <see cref="LogLevel.Warn"/> is switched off.</param>
    public WarnLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        _enabled = Log.IsEnabled(LogLevel.Warn);
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Appends a literal fragment of the interpolated string.</summary>
    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    /// <summary>Appends an interpolation hole.</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends an interpolation hole with a format string.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    /// <summary>Appends an interpolation hole with an alignment.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment);
    }

    /// <summary>Appends an interpolation hole with an alignment and a format string.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>Appends a string hole without boxing it through the generic overload.</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a span hole without an intermediate string.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    internal bool Enabled => _enabled;

    internal string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>
/// Interpolated-string handler for <see cref="Log.Error(string, ref ErrorLogHandler)"/>.
/// </summary>
/// <remarks>Same zero-cost-when-disabled mechanism as <see cref="DebugLogHandler"/>.</remarks>
[InterpolatedStringHandler]
public ref struct ErrorLogHandler
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    /// <summary>Called by the compiler. Sets <paramref name="shouldAppend"/> from the level gate.</summary>
    /// <param name="literalLength">Total length of the literal parts, supplied by the compiler.</param>
    /// <param name="formattedCount">Number of interpolation holes, supplied by the compiler.</param>
    /// <param name="shouldAppend">False when <see cref="LogLevel.Error"/> is switched off.</param>
    public ErrorLogHandler(int literalLength, int formattedCount, out bool shouldAppend)
    {
        _enabled = Log.IsEnabled(LogLevel.Error);
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    /// <summary>Appends a literal fragment of the interpolated string.</summary>
    public void AppendLiteral(string value)
    {
        if (_enabled) _inner.AppendLiteral(value);
    }

    /// <summary>Appends an interpolation hole.</summary>
    public void AppendFormatted<T>(T value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends an interpolation hole with a format string.</summary>
    public void AppendFormatted<T>(T value, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, format);
    }

    /// <summary>Appends an interpolation hole with an alignment.</summary>
    public void AppendFormatted<T>(T value, int alignment)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment);
    }

    /// <summary>Appends an interpolation hole with an alignment and a format string.</summary>
    public void AppendFormatted<T>(T value, int alignment, string? format)
    {
        if (_enabled) _inner.AppendFormatted(value, alignment, format);
    }

    /// <summary>Appends a string hole without boxing it through the generic overload.</summary>
    public void AppendFormatted(string? value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    /// <summary>Appends a span hole without an intermediate string.</summary>
    public void AppendFormatted(ReadOnlySpan<char> value)
    {
        if (_enabled) _inner.AppendFormatted(value);
    }

    internal bool Enabled => _enabled;

    internal string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}
