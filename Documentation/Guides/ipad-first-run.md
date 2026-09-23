# First MOSAIC test on iPad

Keep editing and testing shared code on Windows. Use the Mac for the native iOS build,
signing and installation. A successful C# compile on Windows does **not** establish
that the native iPad application builds or runs.

The initial target is a foreground **Debug** app with a sine plot, then Myo BLE EMG.
Desktop USB/serial drivers, Delsys native drivers, Python and FFTW workflows are
not part of this iPad test. Background streaming, Release/AOT and App Store packaging
are separate work; no background Bluetooth entitlement has been enabled.

## 1. Inspect the Mac first

Copy/sync the updated source, including uncommitted changes, to the Mac. A fresh clone
will not contain local Windows edits until those edits are transferred or committed
and pushed. Do not copy Windows `bin` or `obj` directories. No push is performed by
the helper.

In Terminal, from the repository root:

```bash
bash tools/ipad.sh doctor
```

This is read-only: it reports Mac architecture/macOS, selected Xcode, the .NET SDK,
workloads, connected devices and signing identities. It neither installs tools nor
changes certificates or phone data. Redact account names and device identifiers
before sharing its output publicly.

You need **Apple Xcode**, not just Visual Studio Code. Open Xcode once and complete
its first-run components. Also install a stable **.NET 10 SDK** and its **iOS workload**.
Run .NET commands from inside `MOSAIC/`, where this repository's `global.json` excludes
preview SDKs. Build the iOS project, not `MOSAIC.sln`.

Do not blindly install a new iOS workload over a working Xcode setup: each .NET Apple
workload requires a particular Xcode version. Match the versions shown by `doctor`
against the official [.NET Apple release notes](https://github.com/dotnet/macios/releases).
The usual workload command is `dotnet workload install ios`; use the release's
`--version` argument when selecting a specific compatible workload set. Only use
`sudo` if the installation location requires it. Do not upgrade macOS or Xcode solely
on the basis of this checklist before checking that pairing.

## 2. One-time signing setup in Xcode

1. Connect the unlocked iPad by USB and accept **Trust This Computer** on the iPad.
2. Add your Apple account in Xcode Settings → Accounts.
3. Create a temporary iOS App project. Choose a unique bundle identifier, for example
   `com.yourname.mosaic.dev` (replace `yourname`), and select your development team with
   automatic signing. Run that small app on the physical iPad once.
4. Enable Developer Mode on the iPad when prompted (Settings → Privacy & Security →
   Developer Mode), including its restart/confirmation. Do not disable other security
   protections.

The temporary app establishes the certificate/profile/device combination; MOSAIC
must use that **exact bundle ID**. A Personal Team can be used for local device testing;
distribution has additional requirements. See [Avalonia's device provisioning guide](https://docs.avaloniaui.net/docs/platform-specific-guides/ios)
and [Apple's Developer Mode guide](https://developer.apple.com/documentation/xcode/enabling-developer-mode-on-a-device).

Do not put signing identities or provisioning profiles into source control. The
project's fallback `companyName.MOSAIC` is a placeholder, not a provisioned identifier.

## 3. Build and run from Terminal

From the repository root, replace these example values with the actual bundle ID and
the iPad name or UDID reported by `doctor`:

```bash
export MOSAIC_IOS_BUNDLE_ID='com.yourname.mosaic.dev'
export MOSAIC_IOS_DEVICE='your iPad name or UDID'
bash tools/ipad.sh run
```

For build/sign only, without installing, use `bash tools/ipad.sh build`.
Both use `Debug`, `net10.0-ios`, and `ios-arm64` even on an Apple Silicon Mac:
`iossimulator-arm64` is a simulator, not the physical iPad.

If automatic signing selection is ambiguous, supply the exact identity and profile
already created on the Mac, then repeat the command:

```bash
export MOSAIC_IOS_SIGNING_KEY='Apple Development: your actual certificate identity'
export MOSAIC_IOS_PROFILE='your actual provisioning profile name or UUID'
```

The helper uses .NET's `CodesignKey`, `CodesignProvision`, `ApplicationId` and `Device`
properties. It never uninstalls the app, clears app data, or selects a device for you.
Installing an update can restart MOSAIC and end its current BLE session.
See the [.NET iOS build-property reference](https://learn.microsoft.com/en-us/dotnet/ios/building-apps/build-properties).

## 4. Test in two stages

Copy the two JSON files from
`MOSAIC/MOSAIC/Assets/Examples/MobileSmokeTests/` to a location accessible through the
iPad's Files picker, such as iCloud Drive. They contain no personal paths or device IDs.

1. Open `01-sine.json` using MOSAIC's Open button. Start **Clock** and open the Signal
   block's plot/monitor. Verify an eight-channel moving signal and usable portrait /
   landscape touch layout. This isolates UI/rendering from Bluetooth.
2. Disconnect Myo from Android/desktop MOSAIC first. Keep the armband awake and open
   `02-myo.json`. Scan inside MOSAIC, allow the Bluetooth permission prompt, select Myo
   and connect. Wait for **“EMG data received”**, then start Clock and inspect the Myo
   plot while contracting muscles. Vibration alone is not stream confirmation.

Use the physical iPad for BLE validation; a simulator UI test is not a Myo radio test.
For this first run, keep MOSAIC foreground and the iPad awake. Repeat disconnect /
reconnect and check that data resumes before calling the port verified.

## What has been prepared in the source

- iOS now compiles the native Apple BLE backend in its platform head and registers it
  before a Myo block can create the shared BLE manager. Merely building the shared
  plain `net10.0` assembly on a Mac no longer wrongly enables CoreBluetooth imports.
- The Bluetooth purpose string is present in `Info.plist`; CoreBluetooth requires it.
  See [Apple's Bluetooth permission documentation](https://developer.apple.com/documentation/bundleresources/information-property-list/nsbluetoothalwaysusagedescription).
- Control writes prefer acknowledgement when the characteristic supports it, using
  the same tested capability-selection helper as the Android Myo fix.
- The touch shell is used on iOS as well as Android. The old iOS-only 65% render-scale
  workaround is removed; desktop retains its own window.
- Desktop native dependencies are excluded from the iOS head. Debug uses the managed
  interpreter and `TrimMode=copy` to preserve reflection-based block/view discovery.

## Verification from the original integration and remaining boundaries

The modified iOS head and shared code were C#-compiled successfully on Windows with
the .NET 10.0.303 SDK and installed iOS reference assemblies. This did not invoke a
Mac native build, code signing, launch, or CoreBluetooth hardware test.
The Windows desktop Debug build succeeded and the shared regression suite passed all
**1,175 tests**. The helper passed Bash syntax
and help-output checks, and both sample pipelines passed JSON validation. These are
local checks, not a replacement for the signed iPad run.

If a build fails, share the **first actual error**, not only the final failed-build
summary, plus the Xcode/.NET workload versions from `doctor`. A NuGet vulnerability
lookup warning was observed during local restore; it is not an iPad runtime result.
If the app launches but fails, collect the app log through its Log panel or the
connected-device console in Xcode. Relevant tags are `AppleBLE`, `Myo`, and `Crash`.

Do not copy passwords, private signing keys, or full provisioning profiles into chat.
