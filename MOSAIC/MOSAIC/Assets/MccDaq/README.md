# MccDaq.dll - user-supplied Measurement Computing wrapper

`MccDaq.dll` is not redistributed in the public repository. Obtain it by installing
the official MCC DAQ Software/Universal Library. Do not commit a local copy.

The last tested vendor assembly had the following properties:

| | |
|---|---|
| Assembly | `MccDaq, Version=3.0.0.0, Culture=neutral, PublicKeyToken=a37eefcf5c6ca10a` |
| Size | 170,112 bytes |
| SHA-256 | `AFA88722246201D1CE41BF962A4847B6814A90FFD2A8F6E5374335134B51AF88` |
| Target | .NET Framework 4.x (loads unchanged on .NET 10 — it is a thin P/Invoke shim) |
| Source | MCC DAQ Software, <https://files.digilent.com/#downloads/MCCDaqCD/> |
| Installed at | `C:\Program Files (x86)\Measurement Computing\DAQ\MccDaq.dll` |
| Licence | `…\Measurement Computing\DAQ\Documents\InstaCal-EULA.pdf` |

## Enable the integration

Either copy the installed wrapper to this directory as `MccDaq.dll`, or pass its
absolute path to the build:

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0 -p:EnableMccDaq=true -p:MccDaqAssembly="C:\Program Files (x86)\Measurement Computing\DAQ\MccDaq.dll"
```

The default build uses the block's stub path and does not require the vendor assembly.

## What this does NOT remove

**You still need the MCC DAQ Software installed to acquire anything.** This file is the managed
half only. Running additionally needs, and none of it can live in a repository:

- `cbw64.dll` — the native Universal Library (~7.9 MB), which this shim loads at first use, plus
  its own siblings `daqlib64.dll`, `daqx.dll`, `mccmonitorapi.dll`
- the **`mcusb` kernel-mode driver** (`oem245.inf`) — without it Windows does not enumerate the
  board at all
- `C:\ProgramData\Measurement Computing\DAQ\CB.CFG` — InstaCal's board table, machine-specific,
  and what makes a board answer to a board *number*

Without those the block loads, appears on the palette, and reports the library as unavailable.

## Updating a local copy

Supply a newer `MccDaq.dll` and update the table above when appropriate. Check
`Models/Devices/MccDaqBoard.cs` still compiles — the `#if ENABLE_MCCDAQ` region is the only code
that touches this assembly, and the signatures worth re-checking are `ToEngUnits`/`ToEngUnits32`,
`WinBufToArray`/`WinBufToArray32` and `AInScan`.
