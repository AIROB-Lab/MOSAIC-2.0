using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MOSAIC.Diagnostics;

namespace MOSAIC.Services;

/// <summary>
/// Represents a recent file entry
/// </summary>
public class RecentFile
{
    public string FilePath { get; set; } = string.Empty;
    public DateTime LastAccessed { get; set; }
    
    public string DisplayName => Path.GetFileName(FilePath);
    public string Directory => Path.GetDirectoryName(FilePath) ?? "";
}

/// <summary>
/// Service for managing recent files list
/// </summary>
public class RecentFilesService
{
    private const int MaxRecentFiles = 10;
    private const string RecentFilesFileName = "recent_files.json";
    
    private readonly string _recentFilesPath;
    private List<RecentFile> _recentFiles = new();

    public RecentFilesService()
    {
        // Store in user's AppData folder
        var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var mosaicFolder = Path.Combine(appDataPath, "MOSAIC");
        Directory.CreateDirectory(mosaicFolder);
        
        _recentFilesPath = Path.Combine(mosaicFolder, RecentFilesFileName);
        
        LoadRecentFiles();
    }

    /// <summary>
    /// Gets the list of recent files, ordered by most recent first
    /// </summary>
    public IReadOnlyList<RecentFile> GetRecentFiles()
    {
        // Remove files that no longer exist
        _recentFiles = _recentFiles.Where(f => File.Exists(f.FilePath)).ToList();
        
        return _recentFiles
            .OrderByDescending(f => f.LastAccessed)
            .Take(MaxRecentFiles)
            .ToList();
    }

    /// <summary>
    /// Adds a file to the recent files list
    /// </summary>
    public void AddRecentFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return;

        // Normalize path
        filePath = Path.GetFullPath(filePath);

        // Remove if already exists
        _recentFiles.RemoveAll(f => 
            string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));

        // Add to front
        _recentFiles.Insert(0, new RecentFile
        {
            FilePath = filePath,
            LastAccessed = DateTime.Now
        });

        // Trim to max size
        if (_recentFiles.Count > MaxRecentFiles)
        {
            _recentFiles = _recentFiles.Take(MaxRecentFiles).ToList();
        }

        SaveRecentFiles();
    }

    /// <summary>
    /// Clears all recent files
    /// </summary>
    public void ClearRecentFiles()
    {
        _recentFiles.Clear();
        SaveRecentFiles();
    }

    /// <summary>
    /// Removes a specific file from recent files
    /// </summary>
    public void RemoveRecentFile(string filePath)
    {
        _recentFiles.RemoveAll(f => 
            string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        SaveRecentFiles();
    }

    private void LoadRecentFiles()
    {
        try
        {
            if (File.Exists(_recentFilesPath))
            {
                var json = File.ReadAllText(_recentFilesPath);
                _recentFiles = JsonSerializer.Deserialize<List<RecentFile>>(json) ?? new List<RecentFile>();
            }
        }
        catch (Exception ex)
        {
            Log.Error("RecentFilesService", ex, "Failed to load recent files.");
            _recentFiles = new List<RecentFile>();
        }
    }

    private void SaveRecentFiles()
    {
        try
        {
            var json = JsonSerializer.Serialize(_recentFiles, new JsonSerializerOptions 
            { 
                WriteIndented = true 
            });
            File.WriteAllText(_recentFilesPath, json);
        }
        catch (Exception ex)
        {
            Log.Error("RecentFilesService", ex, "Failed to save recent files.");
        }
    }
}
