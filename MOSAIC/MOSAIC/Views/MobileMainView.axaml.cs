using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MOSAIC.Views;

/// <summary>
/// Android/iOS touch-first shell around the shared pipeline workspace.
/// </summary>
public partial class MobileMainView : UserControl
{
    private readonly MainView? _workspace;

    public MobileMainView()
    {
        InitializeComponent();
        PlatformLabel.Text = OperatingSystem.IsIOS() ? "iOS"
            : OperatingSystem.IsAndroid() ? "ANDROID" : "MOBILE";
    }

    public MobileMainView(MainView workspace)
        : this()
    {
        _workspace = workspace;
        WorkspaceHost.Content = workspace;
        workspace.ConfigureForMobile();
        UpdateThemeGlyph(App.CurrentTheme);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        App.ThemeChanged += OnThemeChanged;
        _workspace?.ConfigureForMobile();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        App.ThemeChanged -= OnThemeChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeChanged(object? sender, AppTheme theme) => UpdateThemeGlyph(theme);

    private void UpdateThemeGlyph(AppTheme theme)
    {
        if (ThemeGlyph is not null)
            ThemeGlyph.Text = theme == AppTheme.Dark ? "☾" : "☀";
    }

    private async void OnOpenClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace is null) return;
        if (await _workspace.OpenConfigAsync() is not null)
            _workspace.ShowMobileCanvas();
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e) => App.ToggleTheme();

    private void OnCanvasClicked(object? sender, RoutedEventArgs e) => _workspace?.ShowMobileCanvas();

    private void OnBlocksClicked(object? sender, RoutedEventArgs e) => _workspace?.ToggleMobileBlocks();

    private void OnAddClicked(object? sender, RoutedEventArgs e) => _workspace?.ToggleMobilePalette();

    private void OnLogClicked(object? sender, RoutedEventArgs e) => _workspace?.ToggleMobileLog();

    private async void OnRecordFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (_workspace is not null)
            await _workspace.ChooseRecordingFolderAsync();
    }
}
