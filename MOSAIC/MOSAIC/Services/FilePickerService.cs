using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Services;

/// <summary>
/// Avalonia-backed implementation of <see cref="IFilePickerService"/>.
/// Resolves the <see cref="IStorageProvider"/> lazily from the application's main window
/// on each call, avoiding the chicken-and-egg problem during DI registration.
/// </summary>
/// <remarks>
/// Register as a parameterless singleton during DI setup — no window reference needed:
/// <code>
/// services.AddSingleton&lt;IFilePickerService, StorageFilePickerService&gt;();
/// </code>
/// </remarks>
public sealed class StorageFilePickerService : IFilePickerService
{
    /// <inheritdoc/>
    public async Task<string?> PickJsonFileAsync(string title)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider is null) return null;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("JSON files")
                {
                    Patterns = new[] { "*.json" },
                    MimeTypes = new[] { "application/json", "text/json" },
                    AppleUniformTypeIdentifiers = new[] { "public.json" }
                }
            }
        });

        var file = files?.FirstOrDefault();
        return file?.TryGetLocalPath();
    }

    /// <inheritdoc/>
    public async Task<string?> PickFolderAsync(string title, string? startIn = null)
    {
        var storageProvider = GetStorageProvider();
        if (storageProvider is null) return null;

        IStorageFolder? suggested = null;
        if (!string.IsNullOrWhiteSpace(startIn))
        {
            // Reopening where the user last chose saves re-navigating a deep lab drive each time.
            try { suggested = await storageProvider.TryGetFolderFromPathAsync(startIn); }
            catch (Exception ex) { Console.WriteLine($"[FilePicker] Ignoring start folder '{startIn}': {ex.Message}"); }
        }

        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = suggested
        });

        return folders?.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>
    /// Resolves the <see cref="IStorageProvider"/> from the current application's main window.
    /// </summary>
    private static IStorageProvider? GetStorageProvider()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            return desktop.MainWindow?.StorageProvider;

        if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime singleView)
            return TopLevel.GetTopLevel(singleView.MainView)?.StorageProvider;

        return null;
    }
}