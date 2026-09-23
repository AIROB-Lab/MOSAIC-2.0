using System;
using Avalonia;
using Avalonia.Skia;
using MOSAIC.Diagnostics;
#if MACOS
using MOSAIC.Components.Manager.BLE;
#endif

namespace MOSAIC.Desktop;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Idempotent, and earlier than App.Initialize: this also covers a failure inside AppBuilder
        // configuration itself. The published binary is a Windows GUI subsystem executable with no
        // console, so anything written before this line is written nowhere.
        Log.Initialize();
        Log.Info("App", $"TFM {AppContext.TargetFrameworkName}");

        try
        {
#if MACOS
            AppKit.NSApplication.Init();
            BleManager.ConfigurePlatformBackend(static () => new AppleBleBackend());
#endif
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Error("Crash", ex, "Unhandled exception escaped the application lifetime");
            Log.FlushCritical(TimeSpan.FromSeconds(3));
            throw;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 256L * 1024 * 1024 }) // 256 MB
            .WithInterFont()
            .LogToTrace();
}
