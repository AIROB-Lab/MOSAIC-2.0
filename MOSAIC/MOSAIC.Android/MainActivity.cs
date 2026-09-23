using System;
using System.Collections.Generic;
using Android;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using Avalonia;
using Avalonia.Android;
using Avalonia.Skia;
using MOSAIC.Components.Manager.BLE;

namespace MOSAIC.Android;

[Application]
public class AndroidApp : AvaloniaAndroidApplication<App>
{
    protected AndroidApp(IntPtr javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // The shared MOSAIC project targets plain net10.0 and therefore cannot compile Android
        // Bluetooth APIs. Register the native implementation from this Android-targeted head
        // before any BLE-backed block asks for BleManager.Instance.
        BleManager.ConfigurePlatformBackend(static () => new AndroidBleBackend());

        return base.CustomizeAppBuilder(builder)
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 96L * 1024 * 1024 }) // 96 MB
            .WithInterFont();
    }
}

[Activity(
    Label = "MOSAIC.Android",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@drawable/icon",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private const int BlePermissionRequestCode = 1001;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestBlePermissions();
    }

    private void RequestBlePermissions()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
        {
            // Android 12+ (API 31+): needs BLUETOOTH_SCAN and BLUETOOTH_CONNECT
            var needed = new List<string>();

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.BluetoothScan) != Permission.Granted)
                needed.Add(Manifest.Permission.BluetoothScan);

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.BluetoothConnect) != Permission.Granted)
                needed.Add(Manifest.Permission.BluetoothConnect);

            if (needed.Count > 0)
            {
                ActivityCompat.RequestPermissions(this, needed.ToArray(), BlePermissionRequestCode);
            }
        }
        else
        {
            // Android 6-11: needs LOCATION for BLE scanning
            var needed = new List<string>();

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.AccessFineLocation) != Permission.Granted)
                needed.Add(Manifest.Permission.AccessFineLocation);

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.Bluetooth) != Permission.Granted)
                needed.Add(Manifest.Permission.Bluetooth);

            if (ContextCompat.CheckSelfPermission(this, Manifest.Permission.BluetoothAdmin) != Permission.Granted)
                needed.Add(Manifest.Permission.BluetoothAdmin);

            if (needed.Count > 0)
            {
                ActivityCompat.RequestPermissions(this, needed.ToArray(), BlePermissionRequestCode);
            }
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);

        if (requestCode == BlePermissionRequestCode)
        {
            for (int i = 0; i < permissions.Length; i++)
            {
                var status = grantResults[i] == Permission.Granted ? "granted" : "denied";
                Console.WriteLine($"[BLE] {permissions[i]}: {status}");
            }
        }
    }
}
