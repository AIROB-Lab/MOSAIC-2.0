using System.Threading.Tasks;

namespace MOSAIC.Services;

/// <summary>
/// Abstracts file picker dialogs so ViewModels can open file browsers
/// without depending on Avalonia's <c>IStorageProvider</c> or <c>TopLevel</c>.
/// </summary>
/// <remarks>
/// Register a single implementation (backed by <c>TopLevel.StorageProvider</c>)
/// in the DI container at startup. ViewModels receive it via constructor injection.
/// </remarks>
public interface IFilePickerService
{
    /// <summary>
    /// Opens a file picker filtered to JSON files and returns the selected file's local path.
    /// </summary>
    /// <param name="title">Dialog title shown to the user.</param>
    /// <returns>The local file path, or <see langword="null"/> if the user cancelled.</returns>
    Task<string?> PickJsonFileAsync(string title);

    /// <summary>
    /// Opens a folder picker and returns the selected folder's local path.
    /// </summary>
    /// <param name="title">Dialog title shown to the user.</param>
    /// <param name="startIn">Folder to open the picker at, or <see langword="null"/> for the default.</param>
    /// <returns>The local folder path, or <see langword="null"/> if the user cancelled.</returns>
    Task<string?> PickFolderAsync(string title, string? startIn = null);
}