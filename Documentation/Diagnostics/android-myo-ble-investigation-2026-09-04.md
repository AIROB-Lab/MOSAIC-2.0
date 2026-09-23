# Android Myo BLE investigation — 4 September 2026

## Resolution — verified on the phone at 10:17

The Android backend forced `GattWriteType.NoResponse` for every control command, but this Myo's command characteristic advertises **`Write` only**, not `WriteNoResponse`. The corrected backend selects a supported write type and prefers `GattWriteType.Default` (acknowledged writes) for control commands. The serialized operation queue still waits for each matching callback.

After installing the corrected Debug APK without clearing app data, the same phone and Myo produced an actual EMG packet. The user subsequently confirmed that it runs. The EMG mode bytes, notification values, clock and plotting code were not changed in this comparison.

Evidence from `files/.config/MOSAIC/logs/mosaic-20260904-101656.log`:

| Phone-local time | Observed event |
| --- | --- |
| 10:17:49.149 | Command characteristic reports `properties=Write, writeType=Default`. |
| 10:17:49.228–49.484 | Four notification subscriptions use `CCCD=0100`; all complete successfully. |
| 10:17:49.486–49.605 | EMG-mode and no-sleep control writes complete successfully using `Default`. |
| 10:17:49.616 | Native callback receives the first **16-byte** update on `d5060105…`; handler is registered. |
| 10:17:49.616 | Myo block reports `EMG data received from Myo_17; streaming ready`. |

The old no-response callback's `Success` was not proof of remote command acceptance. Android distinguishes writes requesting remote acknowledgement from no-response writes. [Android write-type documentation](https://developer.android.com/reference/android/bluetooth/BluetoothGattCharacteristic).

Implementation adds an additive `IBleCharacteristic.WriteValueAsync` control-write entry point, preserving the legacy behavior of other platforms through its default implementation. Android overrides it with capability-aware selection; the old explicit no-response entry point also falls back to acknowledged writes if that is the only supported mode. Characteristics with no write capability fail explicitly. Logging records advertised properties, selected mode and length, not command payloads.

Verification: **1,175 tests passed**, including eight write-capability cases and an assertion that Myo setup uses the control-write entry point. Android and desktop Debug builds succeeded; the Android APK was installed successfully. Unlike the earlier fake-peripheral tests, this reproduction confirms data reception from the physical armband. Firmware reads, CCCD read-back and an HCI capture were not needed to resolve this failure and were not added.

## Earlier investigation (before the successful fix)

The sections below preserve the evidence and hypotheses recorded before the decisive phone test above. References to the "current" or "latest" behavior in these sections describe that earlier state, not the corrected implementation.

### Initial conclusion

The latest failed attempt is a **no-incoming-BLE-update problem**, before the sample buffer, EMG decoding, clock, or plotting code. The root cause is not yet proven.

The strongest remaining code-level lead is Android's unconditional use of **write without response** for control commands. Unlike the Windows backend and Android's normal characteristic defaults, it ignores the command characteristic's advertised write capabilities. The current log cannot establish whether the armband accepted the EMG-start command. Check that capability and use an acknowledged write when supported before changing the EMG protocol or plotting code.

The previously suspected notification-versus-indication mismatch is **not the explanation for this armband**: its four EMG characteristics advertise `Notify`, and the app writes the correct `01 00` subscription value. The previous subscription-mode change is defensive compatibility handling, not a demonstrated fix for this failure.

No production code was changed during this investigation. Existing changes from the earlier repair attempts were preserved.

## Evidence from the phone

Device log inspected through the app's debug sandbox:

`files/.config/MOSAIC/logs/mosaic-20260904-094254.log`

| Phone-local time | Observed event |
| --- | --- |
| 09:47:52.941–52.951 | Two control-characteristic write callbacks report `Success`. These correspond to the two vibration calls in the current connection sequence. |
| 09:47:52.961–53.182 | All four EMG characteristics advertise `Notify`; each subscription uses `CCCD=0100`; all four descriptor-write callbacks report `Success`. |
| 09:47:53.187–53.190 | Two further control-characteristic callbacks report `Success`. These correspond to EMG mode and no-sleep calls in the current source sequence; their payload bytes are not recorded in this log. |
| 09:47:58.213 | The five-second first-sample wait expires. |
| 09:47:58.214 | Android backend closes the connection and records **0 data updates**. |

Android's Bluetooth service history also recorded a later disconnect with reason 22, consistent with a local-host termination. The application log and source establish the more important fact: the app explicitly closes the session after its own first-sample timeout. A spontaneous radio disconnection is not needed to explain the visible disconnect in this attempt.

All relevant runtime permissions were granted when checked: `BLUETOOTH_SCAN`, `BLUETOOTH_CONNECT`, fine location, and coarse location. The installed application was the updated debug build targeting API 36.

The phone disconnected from USB during this investigation. Firmware/characteristic reads and a fresh packet-level capture were not performed.

## Where data stops

The receive path is:

`Android Bluetooth callback → AndroidBleCharacteristic.ValueChanged → BleDevice.HandleCharacteristicValue → SampleBuffer → Myo.OnReceive → Publish / Viz.Feed`

`GattCallbackHandler.DeliverUpdate` increments its incoming-update counter **before** invoking the application notification handler. That counter remained zero. Consequently, this observed failure cannot be explained by the handler dropping valid packets, the buffer discarding samples, signed-byte conversion, or the plots rejecting the output.

The generated Android Java callback class contains both the legacy and API-33+ `onCharacteristicChanged` methods and their native bridge registrations. A missing generated override is therefore not supported by the build evidence. This does not independently rule out a runtime or Android-stack problem; only a native packet trace can do that.

## Primary suspect: control-command write semantics

Relevant implementation:

- `MOSAIC/MOSAIC/Components/Manager/BLE/AndroidBackend.cs`, `WriteValueWithoutResponseAsync`: always selects `GattWriteType.NoResponse`.
- `MOSAIC/MOSAIC/Components/Manager/BLE/WindowsBackend.cs`, same method: checks `WriteWithoutResponse` capability and otherwise uses `WriteWithResponse`.
- `MOSAIC/MOSAIC/Components/Manager/BLE/BleManager.cs`, `SendCommandAsync`: routes every command through that platform method.

Android distinguishes a write requesting a remote acknowledgement from a write that requests no response. More importantly, the Bluetooth ATT specification permits an unprocessable Write Command to be silently ignored and provides no ATT write/error response for that command. A successful local callback is therefore not proof that the remote application entered EMG-streaming mode. [Android write types](https://developer.android.com/reference/android/bluetooth/BluetoothGattCharacteristic), [Bluetooth ATT specification, Write Command](https://www.bluetooth.com/wp-content/uploads/Files/Specification/HTML/Core-54/out/en/host/attribute-protocol--att-.html).

Android's own characteristic initialization chooses its default write type from the advertised `PROPERTY_WRITE_NO_RESPONSE` capability. MOSAIC overrides that selection unconditionally. [AOSP characteristic implementation](https://android.googlesource.com/platform/packages/modules/Bluetooth/+/refs/heads/main/framework/java/android/bluetooth/BluetoothGattCharacteristic.java).

I also read the source of `d4rken/myolib`, an independent Android Myo implementation. It serializes operations and calls `writeCharacteristic` using the characteristic's default write type; it does not force every command to no-response. Its subscriptions and EMG packet splitting agree with MOSAIC's basic structure. This is a useful comparison, not proof of compatibility with this particular phone. [Android Myo library](https://github.com/d4rken/myolib), [BaseMyo.java](https://github.com/d4rken/myolib/blob/master/myolib/src/main/java/eu/darken/myolib/BaseMyo.java).

**Still missing:** the actual write capabilities of this armband's control characteristic. The current log records only the EMG characteristics' properties. If the command characteristic supports both write types, an acknowledged-write comparison is still useful, but lack of capability cannot be asserted without reading it.

The final two callbacks occurred about three milliseconds apart. That is consistent with rapid local queuing and is a reason to inspect command delivery, not evidence by itself that a packet was lost. Arbitrary sleeps would not establish correctness.

## Protocol checks

The current constants match the published Myo protocol:

| Purpose | Current bytes / identifier | Assessment |
| --- | --- | --- |
| EMG service | `d5060005-a904-deb9-4748-2c7f4a124842` | Matches protocol. |
| Four EMG characteristics | `d5060105`, `d5060205`, `d5060305`, `d5060405`, with the same suffix | Match protocol and were discovered on the device. |
| Control characteristic | `d5060401-a904-deb9-4748-2c7f4a124842` under service `d5060001…` | Matches protocol. |
| Filtered EMG, no IMU/classifier | `01 03 02 00 00` | Valid documented mode. |
| Never sleep | `09 01 01` | Valid documented command. |
| Long vibration | `03 01 03` | Valid documented command. |
| EMG payload | Two samples, each eight signed bytes | Matches the current 16-byte packet split and signed conversion. |

Source: [Myo hardware protocol](https://github.com/thalmiclabs/myo-bluetooth/blob/master/myohw.h).

There is no source-backed reason to silently change the default to unfiltered EMG, add a different hidden characteristic, or require a gesture unlock as the next fix. Firmware compatibility and device state remain possible, but the firmware version has not been read. Raw mode can be a controlled diagnostic comparison later, not an assumed repair.

## Other BLE-manager issues found

These are genuine code issues or diagnostic gaps, but none explains the observed zero Android callback count by itself:

1. **Dispose prevents its own cleanup.** `BleManager.Dispose` sets `_disposed = true` before calling `DisconnectAllDevicesAsync`; that method immediately throws on `_disposed`. The catch then suppresses the cleanup failure. This can leave connections/resources open when the manager is disposed. No evidence shows that this disposal path ran during the recorded attempt.
2. **The device registry is keyed by name and is not synchronized.** `ConnectedDevices` is a publicly mutable `Dictionary<string, BleDevice>`, despite the manager's thread-safety claim. Same-name devices can overwrite one another, and concurrent operations are not guarded.
3. **Re-scans create new connection wrappers for the same physical device.** There is no stable per-device connection ownership keyed by device ID. Duplicate connections or subscriptions across blocks/reloads are possible. The inspected phone state showed no remaining registered GATT clients after the failed attempt, so an ongoing leaked client was not demonstrated then.
4. **The selection fallback can match an empty device name.** `selectedDeviceName.Contains(d.Name, …)` is true for an empty `d.Name`. A preceding unnamed peripheral can be selected accidentally. Successful discovery of all four Myo EMG characteristics makes this an unlikely explanation for the recorded attempt.
5. **Vibration happens before streaming is ready.** It is startup feedback, not EMG confirmation. The five-second timeout then closes the connection, producing exactly the confusing user-visible sequence: vibration, followed by “connection failed.”
6. **The view model duplicates connection state.** It updates its `IsConnected` field after commands, but does not continuously observe a later physical disconnect. This can produce stale UI after a successful connection; it is separate from the initial no-data failure.
7. **The receive buffer can grow without a running clock.** `HandleCharacteristicValue` appends indefinitely; trimming only happens on `Myo.OnReceive`. This becomes relevant after packets arrive, not before the first callback.
8. **Hardware diagnostics are incomplete.** The BLE abstraction offers no characteristic/descriptor read operation, and the current connection path does not read firmware information, command capabilities, or subscription read-back values.

## What the tests establish—and what they do not

The 1,167 passing tests establish software behavior such as operation ordering, failure propagation, subscription-value selection, packet splitting, and downstream publication when a fake characteristic emits data.

They do **not** test the Android Bluetooth controller, the armband firmware, or real notification delivery. The Myo tests inject events through fake peripherals in a `net10.0` test process. They cannot validate a hardware fix or rule out a native binding/stack defect.

## Recommended decisive next test

1. Read the armband firmware version and the control characteristic's advertised properties. Record the selected write type.
2. Read back all four CCCD values after subscribing. Expected value for this observed `Notify` device is `01 00`.
3. Keep the GATT queue, but send setup using **acknowledged writes when the characteristic supports them**. Capture the remote GATT status. Do not assume write-with-response is supported without checking.
4. In a diagnostic session, use a clearly logged sequence: stop EMG, disable sleep, then enable the existing filtered EMG mode. Keep the connection open long enough to inspect it, showing “Bluetooth connected / waiting for EMG” separately from streaming readiness.
5. If still silent, capture one short Android HCI trace and determine whether EMG notifications reach the phone's Bluetooth stack. Notifications in the native trace but absent from the managed callback point to the Android/binding path; absent notifications point back to device setup/firmware/transport.
6. Compare the same armband on desktop MOSAIC or another known-working client. A confirmed working comparison is much stronger than another protocol guess.

Android documents HCI snoop capture through Developer options and notes that full packet logging must be explicitly enabled; the always-on in-memory log omits personal data. Such a capture should be brief and limited to the reproduction, since other Bluetooth traffic may be included. It was **not enabled** during this investigation. [Android Bluetooth debugging guidance](https://source.android.com/docs/core/connect/bluetooth/verifying_debugging).

The next justified implementation step is capability-aware control writes plus the missing diagnostic reads. Another plot, permission, or notification-mode change is not supported by the latest evidence.
