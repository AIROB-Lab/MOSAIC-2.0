using Foundation;
using UIKit;
using Avalonia;
using Avalonia.Controls;
using Avalonia.iOS;
using Avalonia.Media;
using MOSAIC.Components.Manager.BLE;

namespace MOSAIC.iOS;

// The UIApplicationDelegate for the application. This class is responsible for launching the 
// User Interface of the application, as well as listening (and optionally responding) to 
// application events from iOS.
[Register("AppDelegate")]
[Preserve(AllMembers = true)]
#pragma warning disable CA1711 // Identifiers should not have incorrect suffix
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711 // Identifiers should not have incorrect suffix
{
    // UIKit creates this object through Objective-C's init selector. Export the
    // concrete constructor so it does not inherit the generic base's init stub.
    [Export("init")]
    public AppDelegate()
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // CoreBluetooth must live in the iOS head, not the plain-net10.0 shared assembly.
        BleManager.ConfigurePlatformBackend(static () => new AppleBleBackend());
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
