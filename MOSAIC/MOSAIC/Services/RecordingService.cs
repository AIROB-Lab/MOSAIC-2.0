using System;
using System.IO;
using System.Text.Json;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;

namespace MOSAIC.Services;

/// <summary>
/// Holds the one folder every recording block writes into, so a path is chosen once per machine
/// instead of once per block.
/// </summary>
/// <remarks>
/// <para>
/// A block that records does not own a path — it owns a boolean. The path comes from here, and the
/// file it gets is <c>&lt;RootFolder&gt;/&lt;session&gt;/&lt;BlockName&gt;.csv</c>. That is what makes the
/// per-block toggle usable from the pipeline builder: dropping a block on the canvas and ticking
/// "Record" is enough, because the folder was already answered.
/// </para>
/// <para>
/// <b>Sessions.</b> <see cref="SessionFolder"/> is a timestamped subfolder created on first use and
/// kept for the rest of the run. Without it every run would append into the same per-block file and
/// two experiments would be indistinguishable inside one CSV. <see cref="StartNewSession"/> begins a
/// fresh one; blocks that are already recording pick it up the next time they are toggled.
/// </para>
/// <para>
/// <see cref="RootFolder"/> persists to <c>%AppData%/MOSAIC/recording.json</c> and deliberately does
/// <em>not</em> live in the pipeline JSON — it is machine-specific, and a config shared with a
/// colleague should not carry someone else's drive layout.
/// </para>
/// <para>
/// <b>What the pipeline JSON does carry is <see cref="MOSAIC.Components.Basics.JsonModel.Path"/>, and
/// nothing else.</b> A block with a <c>Path</c> loads already recording into that folder; a block
/// without one loads not recording. There is deliberately no separate record flag — the folder being
/// set <em>is</em> the flag. Switching recording on from the card is therefore a session-only
/// convenience that writes to <see cref="SessionFolder"/> and does not survive a save.
/// </para>
/// </remarks>
public sealed class RecordingService : IRecordingDestination
{
    private const string SettingsFileName = "recording.json";

    private readonly string _settingsPath;
    private string? _rootFolder;
    private string? _sessionFolder;

    /// <summary>Shape persisted to disk. Kept separate from the service so the file stays additive.</summary>
    private sealed record Settings(string? RootFolder);

    public RecordingService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mosaicFolder = Path.Combine(appData, "MOSAIC");
        Directory.CreateDirectory(mosaicFolder);

        _settingsPath = Path.Combine(mosaicFolder, SettingsFileName);
        Load();
    }

    /// <summary>Raised whenever <see cref="RootFolder"/> changes, so the header and any open menus refresh.</summary>
    public event EventHandler? RootFolderChanged;

    /// <summary>
    /// Destination folder for all recordings, or <see langword="null"/> when the user has not picked one.
    /// Setting it persists immediately and starts a new session.
    /// </summary>
    public string? RootFolder
    {
        get => _rootFolder;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value.Trim());
            if (string.Equals(_rootFolder, normalized, StringComparison.OrdinalIgnoreCase)) return;

            _rootFolder = normalized;
            _sessionFolder = null;   // a new root means a new session
            Save();
            RootFolderChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>True once a root folder is set and blocks can actually be recorded.</summary>
    public bool IsConfigured => _rootFolder is not null;

    /// <summary>
    /// Timestamped subfolder for this run, created on first access, or <see langword="null"/> when no
    /// root folder is set.
    /// </summary>
    /// <remarks>
    /// Creation is deferred so that merely launching MOSAIC does not litter the root with empty
    /// folders — the directory appears the first time a block actually starts recording.
    /// </remarks>
    public string? SessionFolder
    {
        get
        {
            if (_rootFolder is null) return null;

            if (_sessionFolder is null)
            {
                _sessionFolder = Path.Combine(_rootFolder, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(_sessionFolder);
            }

            return _sessionFolder;
        }
    }

    /// <summary>
    /// Drops the current session so the next recording lands in a fresh timestamped folder.
    /// </summary>
    /// <remarks>
    /// Blocks already recording keep writing to the old session until they are toggled off and on —
    /// stopping their dumper mid-file from here would truncate a capture in progress.
    /// </remarks>
    public void StartNewSession() => _sessionFolder = null;

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;

            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
            var saved = settings?.RootFolder;

            // A drive that has since been unplugged should not resurrect as a valid destination.
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _rootFolder = saved;
        }
        catch (Exception ex)
        {
            Log.Error("RecordingService", ex, $"Could not read {SettingsFileName}.");
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new Settings(_rootFolder)));
        }
        catch (Exception ex)
        {
            Log.Error("RecordingService", ex, $"Could not write {SettingsFileName}.");
        }
    }
}
