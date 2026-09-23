using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;

namespace MOSAIC.Components.Basics;

/// <summary>
/// Provides helper methods for selecting and loading a MOSAIC JSON pipeline configuration via an Avalonia file picker.
/// </summary>
/// <remarks>
/// <para>
/// This utility is intended for UI workflows. It opens the platform-native file picker (through
/// <see cref="IStorageProvider"/>) and filters for <c>.json</c> files.
/// </para>
/// <para>
/// If the user cancels the dialog or no file is selected, the method returns <see langword="null"/>.
/// </para>
/// <para>
/// Parsing is delegated to <see cref="JsonParser"/>.
/// </para>
/// </remarks>
public static class ConfigPicker
{
    /// <summary>
    /// Opens a file picker to select a MOSAIC <c>.json</c> configuration file and parses it into a block dictionary.
    /// </summary>
    /// <param name="storageProvider">Avalonia storage provider used to display the platform-native file picker.</param>
    /// <param name="ct">Optional cancellation token forwarded to the JSON parser.</param>
    /// <returns>
    /// A read-only dictionary mapping block names to <see cref="JsonModel"/>, or <see langword="null"/>
    /// if the user cancels or no file is selected.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown if <paramref name="storageProvider"/> is <see langword="null"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The returned dictionary keys are the canonical block names from the configuration and typically match
    /// <see cref="JsonModel.Name"/> after parsing.
    /// </para>
    /// <para>
    /// Any parsing or I/O errors are surfaced from
    /// <see cref="JsonParser.ParseStreamAsync(System.IO.Stream, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, JsonModel>?> PickAndLoadAsync(
        IStorageProvider storageProvider,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(storageProvider);

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select MOSAIC config (.json)",
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
        if (file is null) return null;

        await using var stream = await file.OpenReadAsync();
        return await JsonParser.ParseStreamAsync(stream, ct);
    }
}