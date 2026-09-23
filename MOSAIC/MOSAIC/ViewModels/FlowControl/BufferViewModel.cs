using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MathNet.Numerics.LinearAlgebra;
using Buffer = MOSAIC.Models.FlowControl.Buffer;


namespace MOSAIC.ViewModels.FlowControl;

public partial class BufferViewModel(Buffer buffer) : ObservableObject
{
    public Buffer Buffer => buffer;

    [ObservableProperty] private bool isExpanded;

    public int NumberOfCluster => Buffer.dB.Count;


    public string ClusterInformation =>
        string.Join(", ",
            Buffer.dB.Select(pair => $"{Helper.Print(pair.Target)} ({pair.Data.RowCount})"));

    [RelayCommand]
    private void Refresh()
    {
        OnPropertyChanged(nameof(NumberOfCluster));
        OnPropertyChanged(nameof(ClusterInformation));
    }

    [RelayCommand]
    private void TestToggle()
    {
        Buffer.tempFlag = !Buffer.tempFlag;
    }

    [RelayCommand]
    private void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
        if (IsExpanded) Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        Buffer.dB.Clear();
        Refresh();
    }

    [RelayCommand]
    private async Task SaveToFolderAsync(object? storageParam, CancellationToken ct = default)
    {
        if (storageParam is not IStorageProvider storage) return;

        // Pick a folder
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder to save clusteredData.txt",
            AllowMultiple = false
        });
        if (folders is null || folders.Count == 0) return;

        var folder = folders[0];

        // Create the file in the chosen folder
        var file = await folder.CreateFileAsync("clusteredData.txt");
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream, Encoding.UTF8);

        // Write rows: "<target csv>,<row csv>"
        foreach (var (Target, Data) in Buffer.dB)
        {
            var targetCsv = Helper.Print(Target);
            for (int r = 0; r < Data.RowCount; r++)
            {
                ct.ThrowIfCancellationRequested();
                await writer.WriteLineAsync($"{targetCsv},{Helper.Print(Data.Row(r))}");
            }
        }

        await writer.FlushAsync();
    }
}

public static class Helper
{
    public static string Print(Vector<double> v, int decimals = 3, string separator = ", ")
    {
        if (v.Count == 0) return "";
        var sb = new StringBuilder(v.Count * (decimals + 4));
        var fmt = "0." + new string('#', decimals);
        var ci = CultureInfo.InvariantCulture;

        for (int i = 0; i < v.Count; i++)
        {
            if (i > 0) sb.Append(separator);
            sb.Append(v[i].ToString(fmt, ci));
        }

        return sb.ToString();
    }
}