using System;
using System.Globalization;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MOSAIC.Components.Factory;
using MOSAIC.Components.Interfaces;
using MOSAIC.Diagnostics;
using MOSAIC.Services;
using MOSAIC.ViewModels;
using MOSAIC.Views;

namespace MOSAIC;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// Current theme (Dark or Light).
    /// </summary>
    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    /// <summary>
    /// Event fired when theme changes.
    /// </summary>
    public static event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>
    /// True while the pipeline-building flyout is open. Graph nodes show their build affordances
    /// (input/output ports, arity badge) only in build mode, staying clean while a pipeline runs.
    /// </summary>
    public static bool BuildMode { get; private set; }

    /// <summary>Event fired when <see cref="BuildMode"/> changes.</summary>
    public static event EventHandler<bool>? BuildModeChanged;

    /// <summary>Sets <see cref="BuildMode"/> and notifies listeners (no-op if unchanged).</summary>
    public static void SetBuildMode(bool on)
    {
        if (BuildMode == on) return;
        BuildMode = on;
        BuildModeChanged?.Invoke(Current, on);
    }

    // Theme resource dictionaries (created once, reused)
    private static ResourceDictionary? _darkTheme;
    private static ResourceDictionary? _lightTheme;
    private static ResourceDictionary? _currentThemeDict;

    public override void Initialize()
    {
        // First statement in the app's life: every head reaches this before anything below can
        // fail, so a XAML load or theme-dictionary failure is captured rather than silently
        // killing a GUI-subsystem build that has no console to report it on.
        Log.Initialize();

        // Set culture for consistent number formatting
        Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");

        AvaloniaXamlLoader.Load(this);

        // Initialize theme dictionaries
        InitializeThemes();
    }

    private void InitializeThemes()
    {
        // Create dark theme
        _darkTheme = CreateDarkTheme();
        _lightTheme = CreateLightTheme();

        // Apply default (dark) theme
        _currentThemeDict = _darkTheme;
        if (Resources?.MergedDictionaries != null)
        {
            Resources.MergedDictionaries.Add(_currentThemeDict);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IBlockFactory, BlockFactory>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainView>();
        services.AddSingleton<MobileMainView>();
        services.AddSingleton<MainWindow>();
        services.AddSingleton<IFilePickerService, StorageFilePickerService>();

        // One recording destination for the whole app: the folder is chosen once in the header and
        // every block that has recording switched on writes into the current session under it.
        services.AddSingleton<RecordingService>();
        services.AddSingleton<IRecordingDestination>(sp => sp.GetRequiredService<RecordingService>());

        Services = services.BuildServiceProvider();

        // e.Handled stays false: swallowing UI exceptions would change what the app does, and this
        // hook exists only so the file says what happened before the process went down.
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Log.Error("Crash", e.Exception, "Unhandled exception on the UI thread");
            Log.FlushCritical(TimeSpan.FromSeconds(2));
        };

        switch (ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                desktop.MainWindow = Services.GetRequiredService<MainWindow>();
                break;
            case ISingleViewApplicationLifetime single:
                single.MainView = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
                    ? Services.GetRequiredService<MobileMainView>()
                    : Services.GetRequiredService<MainView>();
                break;
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Toggles between Dark and Light themes.
    /// </summary>
    public static void ToggleTheme()
    {
        SetTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);
    }

    /// <summary>
    /// Sets the application theme.
    /// </summary>
    public static void SetTheme(AppTheme theme)
    {
        if (Application.Current is not App app) return;
        if (app.Resources?.MergedDictionaries == null) return;
        if (_darkTheme == null || _lightTheme == null) return;

        var merged = app.Resources.MergedDictionaries;

        // Remove current theme
        if (_currentThemeDict != null && merged.Contains(_currentThemeDict))
        {
            merged.Remove(_currentThemeDict);
        }

        // Add new theme
        _currentThemeDict = theme == AppTheme.Dark ? _darkTheme : _lightTheme;
        merged.Add(_currentThemeDict);

        CurrentTheme = theme;

        // Update Avalonia's built-in theme variant
        app.RequestedThemeVariant = theme == AppTheme.Dark
            ? ThemeVariant.Dark
            : ThemeVariant.Light;

        // Notify listeners
        ThemeChanged?.Invoke(app, theme);

        Console.WriteLine($"[App] Theme changed to: {theme}");
    }

    #region Theme Definitions

    private static ResourceDictionary CreateDarkTheme()
    {
        var dict = new ResourceDictionary();

        // Backgrounds
        dict["ThemeBackground"] = Color.Parse("#0F0F0F");
        dict["ThemeBackgroundAlt"] = Color.Parse("#1A1A1A");
        dict["ThemeSurface"] = Color.Parse("#252525");
        dict["ThemeSurfaceHover"] = Color.Parse("#2A2A2A");
        dict["ThemeCard"] = Color.Parse("#1E1E1E");
        dict["ThemeCardHeader"] = Color.Parse("#252525");

        dict["ThemeBackgroundBrush"] = new SolidColorBrush(Color.Parse("#0F0F0F"));
        dict["ThemeBackgroundAltBrush"] = new SolidColorBrush(Color.Parse("#1A1A1A"));
        dict["ThemeSurfaceBrush"] = new SolidColorBrush(Color.Parse("#252525"));
        dict["ThemeSurfaceHoverBrush"] = new SolidColorBrush(Color.Parse("#2A2A2A"));
        dict["ThemeCardBrush"] = new SolidColorBrush(Color.Parse("#1E1E1E"));
        dict["ThemeCardHeaderBrush"] = new SolidColorBrush(Color.Parse("#252525"));

        // Card gradient
        var cardGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        cardGradient.GradientStops.Add(new GradientStop(Color.Parse("#252525"), 0));
        cardGradient.GradientStops.Add(new GradientStop(Color.Parse("#1A1A1A"), 1));
        dict["ThemeCardGradient"] = cardGradient;

        // Canvas
        dict["ThemeCanvas"] = Color.Parse("#0A1628");
        dict["ThemeCanvasGrid"] = Color.Parse("#1E3A5F");
        dict["ThemeCanvasNode"] = Color.Parse("#1A1A1A");
        dict["ThemeCanvasNodeBorder"] = Color.Parse("#3B82F6");

        dict["ThemeCanvasBrush"] = new SolidColorBrush(Color.Parse("#0A1628"));
        dict["ThemeCanvasGridBrush"] = new SolidColorBrush(Color.Parse("#1E3A5F"));
        dict["ThemeCanvasNodeBrush"] = new SolidColorBrush(Color.Parse("#1A1A1A"));
        dict["ThemeCanvasNodeBorderBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Borders
        dict["ThemeBorder"] = Color.Parse("#2A2A2A");
        dict["ThemeBorderLight"] = Color.Parse("#333333");
        dict["ThemeBorderFocus"] = Color.Parse("#3B82F6");

        dict["ThemeBorderBrush"] = new SolidColorBrush(Color.Parse("#2A2A2A"));
        dict["ThemeBorderLightBrush"] = new SolidColorBrush(Color.Parse("#333333"));
        dict["ThemeBorderFocusBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Text
        dict["ThemeTextPrimary"] = Color.Parse("#FFFFFF");
        dict["ThemeTextSecondary"] = Color.Parse("#B0B0B0");
        dict["ThemeTextMuted"] = Color.Parse("#808080");
        dict["ThemeTextDimmed"] = Color.Parse("#606060");
        dict["ThemeTextDisabled"] = Color.Parse("#404040");

        dict["ThemeTextPrimaryBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeTextSecondaryBrush"] = new SolidColorBrush(Color.Parse("#B0B0B0"));
        dict["ThemeTextMutedBrush"] = new SolidColorBrush(Color.Parse("#808080"));
        dict["ThemeTextDimmedBrush"] = new SolidColorBrush(Color.Parse("#606060"));
        dict["ThemeTextDisabledBrush"] = new SolidColorBrush(Color.Parse("#404040"));

        // Inputs
        dict["ThemeInputBackground"] = Color.Parse("#1A1A1A");
        dict["ThemeInputBorder"] = Color.Parse("#333333");
        dict["ThemeInputFocus"] = Color.Parse("#3B82F6");

        dict["ThemeInputBackgroundBrush"] = new SolidColorBrush(Color.Parse("#1A1A1A"));
        dict["ThemeInputBorderBrush"] = new SolidColorBrush(Color.Parse("#333333"));
        dict["ThemeInputFocusBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Accent
        dict["ThemeAccent"] = Color.Parse("#3B82F6");
        dict["ThemeAccentHover"] = Color.Parse("#2563EB");
        dict["ThemeAccentMuted"] = Color.Parse("#1E3A5F");

        dict["ThemeAccentBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));
        dict["ThemeAccentHoverBrush"] = new SolidColorBrush(Color.Parse("#2563EB"));
        dict["ThemeAccentMutedBrush"] = new SolidColorBrush(Color.Parse("#1E3A5F"));

        // Scope
        dict["ThemeScopeBackground"] = Color.Parse("#0A0C0F");
        dict["ThemeScopeGrid"] = Color.Parse("#1A1A2E");
        dict["ThemeScopeAxis"] = Color.Parse("#404040");

        dict["ThemeScopeBackgroundBrush"] = new SolidColorBrush(Color.Parse("#0A0C0F"));
        dict["ThemeScopeGridBrush"] = new SolidColorBrush(Color.Parse("#1A1A2E"));
        dict["ThemeScopeAxisBrush"] = new SolidColorBrush(Color.Parse("#404040"));

        // Section
        dict["ThemeSection"] = Color.Parse("#0D0D0D");
        dict["ThemeSectionBrush"] = new SolidColorBrush(Color.Parse("#0D0D0D"));

        return dict;
    }

    private static ResourceDictionary CreateLightTheme()
    {
        var dict = new ResourceDictionary();

        // Backgrounds
        dict["ThemeBackground"] = Color.Parse("#F8FAFC");
        dict["ThemeBackgroundAlt"] = Color.Parse("#F1F5F9");
        dict["ThemeSurface"] = Color.Parse("#FFFFFF");
        dict["ThemeSurfaceHover"] = Color.Parse("#F8FAFC");
        dict["ThemeCard"] = Color.Parse("#FFFFFF");
        dict["ThemeCardHeader"] = Color.Parse("#F8FAFC");

        dict["ThemeBackgroundBrush"] = new SolidColorBrush(Color.Parse("#F8FAFC"));
        dict["ThemeBackgroundAltBrush"] = new SolidColorBrush(Color.Parse("#F1F5F9"));
        dict["ThemeSurfaceBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeSurfaceHoverBrush"] = new SolidColorBrush(Color.Parse("#F8FAFC"));
        dict["ThemeCardBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeCardHeaderBrush"] = new SolidColorBrush(Color.Parse("#F8FAFC"));

        // Card gradient
        var cardGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative)
        };
        cardGradient.GradientStops.Add(new GradientStop(Color.Parse("#FFFFFF"), 0));
        cardGradient.GradientStops.Add(new GradientStop(Color.Parse("#F8FAFC"), 1));
        dict["ThemeCardGradient"] = cardGradient;

        // Canvas
        dict["ThemeCanvas"] = Color.Parse("#E2E8F0");
        dict["ThemeCanvasGrid"] = Color.Parse("#CBD5E1");
        dict["ThemeCanvasNode"] = Color.Parse("#FFFFFF");
        dict["ThemeCanvasNodeBorder"] = Color.Parse("#3B82F6");

        dict["ThemeCanvasBrush"] = new SolidColorBrush(Color.Parse("#E2E8F0"));
        dict["ThemeCanvasGridBrush"] = new SolidColorBrush(Color.Parse("#CBD5E1"));
        dict["ThemeCanvasNodeBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeCanvasNodeBorderBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Borders
        dict["ThemeBorder"] = Color.Parse("#E2E8F0");
        dict["ThemeBorderLight"] = Color.Parse("#F1F5F9");
        dict["ThemeBorderFocus"] = Color.Parse("#3B82F6");

        dict["ThemeBorderBrush"] = new SolidColorBrush(Color.Parse("#E2E8F0"));
        dict["ThemeBorderLightBrush"] = new SolidColorBrush(Color.Parse("#F1F5F9"));
        dict["ThemeBorderFocusBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Text
        dict["ThemeTextPrimary"] = Color.Parse("#0F172A");
        dict["ThemeTextSecondary"] = Color.Parse("#475569");
        dict["ThemeTextMuted"] = Color.Parse("#64748B");
        dict["ThemeTextDimmed"] = Color.Parse("#94A3B8");
        dict["ThemeTextDisabled"] = Color.Parse("#CBD5E1");

        dict["ThemeTextPrimaryBrush"] = new SolidColorBrush(Color.Parse("#0F172A"));
        dict["ThemeTextSecondaryBrush"] = new SolidColorBrush(Color.Parse("#475569"));
        dict["ThemeTextMutedBrush"] = new SolidColorBrush(Color.Parse("#64748B"));
        dict["ThemeTextDimmedBrush"] = new SolidColorBrush(Color.Parse("#94A3B8"));
        dict["ThemeTextDisabledBrush"] = new SolidColorBrush(Color.Parse("#CBD5E1"));

        // Inputs
        dict["ThemeInputBackground"] = Color.Parse("#FFFFFF");
        dict["ThemeInputBorder"] = Color.Parse("#E2E8F0");
        dict["ThemeInputFocus"] = Color.Parse("#3B82F6");

        dict["ThemeInputBackgroundBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeInputBorderBrush"] = new SolidColorBrush(Color.Parse("#E2E8F0"));
        dict["ThemeInputFocusBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));

        // Accent
        dict["ThemeAccent"] = Color.Parse("#3B82F6");
        dict["ThemeAccentHover"] = Color.Parse("#2563EB");
        dict["ThemeAccentMuted"] = Color.Parse("#DBEAFE");

        dict["ThemeAccentBrush"] = new SolidColorBrush(Color.Parse("#3B82F6"));
        dict["ThemeAccentHoverBrush"] = new SolidColorBrush(Color.Parse("#2563EB"));
        dict["ThemeAccentMutedBrush"] = new SolidColorBrush(Color.Parse("#DBEAFE"));

        // Scope
        dict["ThemeScopeBackground"] = Color.Parse("#FFFFFF");
        dict["ThemeScopeGrid"] = Color.Parse("#F1F5F9");
        dict["ThemeScopeAxis"] = Color.Parse("#94A3B8");

        dict["ThemeScopeBackgroundBrush"] = new SolidColorBrush(Color.Parse("#FFFFFF"));
        dict["ThemeScopeGridBrush"] = new SolidColorBrush(Color.Parse("#F1F5F9"));
        dict["ThemeScopeAxisBrush"] = new SolidColorBrush(Color.Parse("#94A3B8"));

        // Section
        dict["ThemeSection"] = Color.Parse("#F8FAFC");
        dict["ThemeSectionBrush"] = new SolidColorBrush(Color.Parse("#F8FAFC"));

        return dict;
    }

    #endregion
}

/// <summary>
/// Available application themes.
/// </summary>
public enum AppTheme
{
    Dark,
    Light
}
