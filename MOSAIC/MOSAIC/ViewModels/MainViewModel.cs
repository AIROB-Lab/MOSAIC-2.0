using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MOSAIC.Services;

namespace MOSAIC.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly RecentFilesService _recentFilesService;

    [ObservableProperty]
    private ObservableCollection<RecentFile> _recentFiles = new();

    [ObservableProperty]
    private ObservableCollection<ModelFileItem> _modelFiles = new();

    public bool HasRecentFiles => RecentFiles.Count > 0;

// In constructor:
    public MainViewModel()
    {
        _recentFilesService = new RecentFilesService();
        RefreshRecentFiles();
    }

    public void LoadModelFiles()
    {
        ModelFiles.Clear();

        var modelsPath = Path.Combine(AppContext.BaseDirectory, "Models");

        Console.WriteLine($"LoadModelFiles called. Path exists: {Directory.Exists(modelsPath)}");

        if (Directory.Exists(modelsPath))
        {
            var csFiles = Directory.GetFiles(modelsPath, "*.cs", SearchOption.AllDirectories);
            Console.WriteLine($"Found {csFiles.Length} files");
            foreach (var file in csFiles.OrderBy(f => Path.GetFileNameWithoutExtension(f)))
            {
                ModelFiles.Add(new ModelFileItem
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    FullPath = file
                });
            }
            Console.WriteLine($"ModelFiles count: {ModelFiles.Count}");
        }
    }

    public void RefreshRecentFiles()
    {
        RecentFiles.Clear();
        foreach (var file in _recentFilesService.GetRecentFiles())
        {
            RecentFiles.Add(file);
        }
        OnPropertyChanged(nameof(HasRecentFiles));
    }

    public void AddRecentFile(string filePath)
    {
        Console.WriteLine("ADD FILE");
        _recentFilesService.AddRecentFile(filePath);
        RefreshRecentFiles();
    }

    public void RemoveRecentFile(string filePath)
    {
        _recentFilesService.RemoveRecentFile(filePath);
        RefreshRecentFiles();
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _recentFilesService.ClearRecentFiles();
        RefreshRecentFiles();
    }
}

public class ModelFileItem
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string Group { get; set; } = string.Empty;
}

public class ModelGroupItem
{
    public string GroupName { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<ModelFileItem> Files { get; set; } = new();
}
