using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MOSAIC.Components.Basics;
using MOSAIC.Diagnostics;

namespace MOSAIC.Views;

public partial class MainWindow : Window
{
    private readonly MainView _mainView;
    private bool _closingPipeline;
    private bool _pipelineClosed;
    
    /// <summary>
    /// Path to the currently loaded config file (for Save functionality).
    /// </summary>
    private string? _currentConfigPath;

    public MainWindow(MainView mainView)
    {
        InitializeComponent();
        _mainView = mainView;
        PartMainContent.Content = mainView;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_pipelineClosed) return;
        e.Cancel = true;
        if (_closingPipeline) return;
        _closingPipeline = true;
        try { await _mainView.ShutdownAsync(); }
        catch (Exception ex) { Log.Error("MainWindow", ex, "Pipeline shutdown failed."); }
        finally
        {
            _pipelineClosed = true;
            Avalonia.Threading.Dispatcher.UIThread.Post(Close);
        }
    }
    
    private async void OnOpenConfigMenuClicked(object? sender, RoutedEventArgs e)
    {
        var filePath = await _mainView.OpenConfigAsync();
        if (!string.IsNullOrEmpty(filePath))
        {
            _currentConfigPath = filePath;
            UpdateTitle();
        }
    }
    
    private async void OnRecentFileClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { CommandParameter: string filePath })
        {
            await _mainView.OpenConfigFileAsync(filePath);
            _currentConfigPath = filePath;
            UpdateTitle();
        }
    }

    private async void OnSaveConfigMenuClicked(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentConfigPath))
        {
            await SaveConfigAsync(_currentConfigPath);
        }
        else
        {
            await SaveConfigAsAsync();
        }
    }

    private async void OnSaveAsConfigMenuClicked(object? sender, RoutedEventArgs e)
    {
        await SaveConfigAsAsync();
    }

    private async Task SaveConfigAsync(string filePath)
    {
        try
        {
            await JsonExporter.SaveToFileAsync(_mainView.Blocks, filePath);
            _currentConfigPath = filePath;
            UpdateTitle();
            Log.Info("MainWindow", $"Saved config to '{filePath}'");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error saving config", ex.Message);
        }
    }

    private async Task SaveConfigAsAsync()
    {
        try
        {
            if (StorageProvider == null) return;

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save MOSAIC Config",
                SuggestedFileName = GetSuggestedFileName(),
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("JSON files")
                    {
                        Patterns = new[] { "*.json" },
                        MimeTypes = new[] { "application/json" }
                    }
                },
                DefaultExtension = "json"
            });

            if (file != null)
            {
                var filePath = file.Path.LocalPath ?? file.Path.ToString();
                await SaveConfigAsync(filePath);
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Error saving config", ex.Message);
        }
    }

    private string GetSuggestedFileName()
    {
        if (!string.IsNullOrEmpty(_currentConfigPath))
        {
            return Path.GetFileName(_currentConfigPath);
        }
        return "config.json";
    }

    private void UpdateTitle()
    {
        Title = string.IsNullOrEmpty(_currentConfigPath)
            ? "MOSAIC"
            : $"MOSAIC - {Path.GetFileName(_currentConfigPath)}";
    }

    private void OnExitMenuClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 12 };
        panel.Children.Add(new TextBlock 
        { 
            Text = message, 
            TextWrapping = Avalonia.Media.TextWrapping.Wrap 
        });

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 80
        };
        ok.Click += (_, _) => dialog.Close();
        panel.Children.Add(ok);

        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }
}
