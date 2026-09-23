# Getting Started

Start with a generated signal so you can learn the workbench without connecting hardware.
If MOSAIC already opens, go straight to [Your First Pipeline](first-pipeline.md).

## Platforms

MOSAIC has been run successfully on **Windows, macOS, and iOS**. macOS and iOS operation
has been confirmed by the project maintainer. Hardware availability varies with vendor
drivers, Bluetooth backends, and optional dependencies.

| Platform | Application project | Notes |
|---|---|---|
| Windows | `MOSAIC.Desktop` | Select the Windows target for the Windows Bluetooth backend. MCC DAQ and native LSL runtime bundling are optional and disabled by default. |
| macOS | `MOSAIC.Desktop` | The macOS target includes CoreBluetooth. |
| iOS | `MOSAIC.iOS` | Uses the shared mobile profile and CoreBluetooth; build and signing are managed on a Mac. |
| Android | `MOSAIC.Android` | Uses the shared mobile profile and Android Bluetooth integration. |

The browser project is outside this getting-started path. Running the application on a
platform does not establish that every device block works there.

## Build from source

Install the **.NET 10 SDK**. SDK selection is configured in `MOSAIC/global.json`.
Apple and Android targets also require their corresponding .NET workloads and platform tools.
A physical iOS device requires signing configured on the Mac.

Open a terminal at the repository root and enter the solution directory **once**:

```text
cd MOSAIC
dotnet --version
```

All commands below run from that directory. Build the application project you need;
building the entire solution involves other platforms and their workloads too.

### Windows desktop

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0
dotnet run --project MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0
```

### macOS desktop

On a Mac with the macOS workload installed:

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-macos
dotnet run --project MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-macos
```

The desktop project adds this target when building on macOS. Selecting it enables the
Apple Bluetooth backend; the generic `net10.0` target does not select that backend.

### iOS

On a Mac, open `MOSAIC.iOS/MOSAIC.iOS.csproj` in your development environment, select
your simulator or connected device, and configure your own signing team for device deployment.
The project targets `net10.0-ios` and references `MOSAIC.Mobile.csproj`.

```text
dotnet build MOSAIC.iOS/MOSAIC.iOS.csproj -f net10.0-ios
```

Device selection, signing identities, and runtime identifiers depend on the build machine
and device. Use your working iOS installation's settings rather than another developer's identity.

## Packages and optional devices

The public build disables Delsys support by default because its packages and API
licence are distributed by the vendor, not by this repository. Users who are
authorised to use the SDK can configure their private package source, set
`MOSAIC_DELSYS_API_KEY` and `MOSAIC_DELSYS_API_LICENSE`, and build with
`-p:EnableDelsys=true`. Never commit those values.

The public repository also excludes `MccDaq.dll` and the native `lsl.dll`.
Both files are ignored by Git so that a local copy cannot be committed accidentally.

### Enable MCC DAQ

Install the official MCC DAQ Software/Universal Library and configure the board with
InstaCal. The managed wrapper is normally installed at:

```text
C:\Program Files (x86)\Measurement Computing\DAQ\MccDaq.dll
```

Either copy that file to `MOSAIC/Assets/MccDaq/MccDaq.dll` or point the build to
the installed copy. From the solution directory, the latter form is:

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0 -p:EnableMccDaq=true -p:MccDaqAssembly="C:\Program Files (x86)\Measurement Computing\DAQ\MccDaq.dll"
```

The full vendor installation is still required at run time because the managed DLL
loads the native Universal Library and device drivers.

### Enable the native LSL runtime

Download a Windows x64 liblsl release from the
[official liblsl releases](https://github.com/sccn/liblsl/releases). Either copy its
`lsl.dll` to `MOSAIC/Assets/LSLLib/runtimes/win-x64/lsl.dll` or point the build to
the downloaded file:

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0 -p:EnableLsl=true -p:LslRuntime="C:\path\to\lsl.dll"
```

The build copies the supplied runtime into the application output. Do not commit it
to this repository. The retained C# binding is MIT-licensed; its notice is stored
beside the source in `MOSAIC/Assets/LSLLib/LICENSE`.

The shared build settings are in `MOSAIC/MOSAIC.Shared.targets`, relative to the
solution directory. Hardware use still requires the appropriate vendor software,
drivers, licences, and device setup.

Python is optional. Supervised UMAP, WULPUS, and the Python predictor blocks require
separate Python environments and runtime variables; see
[Python Integrations](python-integrations.md) before adding one of those blocks.

## Check a development checkout

```text
dotnet test MOSAIC.Tests/MOSAIC.Tests.csproj
```

Check the test summary for failures; the total changes as tests are added. Tests are a
developer check, not a prerequisite for opening an example.

## Next step

Follow [Your First Pipeline](first-pipeline.md) to run an example, inspect its signal,
change the graph, and save your work. See [Troubleshooting](troubleshooting.md) for setup errors.
