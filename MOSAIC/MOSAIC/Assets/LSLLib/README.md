# Lab Streaming Layer runtime

The public repository retains the `liblsl-Csharp` source binding in `LSL.cs`, but
does not redistribute the native `lsl.dll`.

Download a Windows x64 build from the
[official liblsl releases](https://github.com/sccn/liblsl/releases). Then either:

1. copy `lsl.dll` to `runtimes/win-x64/lsl.dll`; or
2. pass its absolute path as `-p:LslRuntime="C:\path\to\lsl.dll"`.

Enable output bundling with `-p:EnableLsl=true`. For example, from the solution
directory:

```text
dotnet build MOSAIC.Desktop/MOSAIC.Desktop.csproj -f net10.0-windows10.0.19041.0 -p:EnableLsl=true -p:LslRuntime="C:\path\to\lsl.dll"
```

Local native binaries under `runtimes/` are ignored by Git. The C# binding is
licensed under the adjacent MIT `LICENSE` file.
