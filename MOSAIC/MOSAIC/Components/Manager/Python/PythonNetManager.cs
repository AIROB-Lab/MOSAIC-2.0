using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json;
using Python.Runtime;

namespace MOSAIC.Components.Manager.Python
{
    /// <summary>
    /// Singleton manager for the PythonNet runtime lifecycle.
    /// Must be initialized once at application startup before any prediction
    /// blocks attempt to acquire the GIL. Call <see cref="Shutdown"/> on
    /// application exit to allow PythonEngine.Shutdown() to run cleanly.
    /// Thread-safe: GIL acquisition is the caller's responsibility per-call.
    /// </summary>
    public sealed class PythonNetManager : IDisposable
    {
        private static PythonNetManager? _instance;
        private static readonly object _lock = new();

        public static PythonNetManager Instance
        {
            get
            {
                if (_instance is null)
                    throw new InvalidOperationException(
                        "PythonNetManager has not been initialized. Call PythonNetManager.Initialize() at startup.");
                return _instance;
            }
        }

        public string PythonDllPath        { get; private set; }
        public string SitePackagesPath     { get; private set; }
        public string ScriptsPath          { get; private set; }

        /// <summary>Config file the live runtime was built from. Used to explain a second, conflicting request.</summary>
        public string ConfigPath           { get; private set; }

        /// <summary>Folder a Python config is deployed to beside the executable.</summary>
        /// <remarks>
        /// A published build has no source tree to search, so the configs are copied here and this is
        /// the first place <see cref="ResolveConfigPath"/> looks.
        /// </remarks>
        public const string DeployedConfigFolder = "Python";

        private bool _disposed;
        private IntPtr _threadState;   // stores the thread state from BeginAllowThreads

        /// <summary>
        /// Initializes the Python runtime from the given config file path.
        /// Safe to call only once; subsequent calls are no-ops if already initialized.
        /// </summary>
        public static PythonNetManager Initialize(string configPath)
        {
            lock (_lock)
            {
                if (_instance is not null)
                {
                    // One process gets one Python. A second block asking for a different config is
                    // not an error we can fix here - the runtime is already up and its interpreter
                    // cannot be swapped - but it is worth saying out loud, because the symptom
                    // otherwise is an unrelated ImportError from whichever block lost the race.
                    var requested = Path.GetFullPath(configPath);
                    if (!string.Equals(_instance.ConfigPath, requested, StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine(
                            $"[PythonNetManager] Already running '{_instance.ConfigPath}'; ignoring the " +
                            $"request for '{requested}'. Blocks needing different Python versions " +
                            "cannot share one pipeline.");

                    return _instance;   // idempotent
                }

                _instance = new PythonNetManager(configPath);
                return _instance;
            }
        }

        /// <summary>
        /// Locates a Python config file, looking beside the executable before the source tree.
        /// </summary>
        /// <param name="fileName">Config file name, e.g. <c>config.json</c> or <c>configWulpus.json</c>.</param>
        /// <returns>Absolute path to the config file.</returns>
        /// <exception cref="FileNotFoundException">Neither location holds the file.</exception>
        /// <remarks>
        /// Both orders matter. A published build only has the copy beside the executable, and looking
        /// there first also lets a developer test the deployed layout. The walk up to the solution
        /// root is the fallback that keeps <c>dotnet run</c> working from the source tree, where
        /// nothing has been copied to the output yet.
        /// </remarks>
        public static string ResolveConfigPath(string fileName)
        {
            var deployed = Path.Combine(AppContext.BaseDirectory, DeployedConfigFolder, fileName);
            if (File.Exists(deployed)) return deployed;

            var searched = new List<string> { deployed };

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (dir.GetFiles("*.sln").Length > 0)
                {
                    var candidate = Path.Combine(
                        dir.FullName, "MOSAIC", "Components", "Manager", "Python", fileName);
                    searched.Add(candidate);
                    if (File.Exists(candidate)) return candidate;
                }
                dir = dir.Parent;
            }

            throw new FileNotFoundException(
                $"Python configuration '{fileName}' was not found. Looked in:{Environment.NewLine}" +
                string.Join(Environment.NewLine, searched.Select(s => "  " + s)));
        }

        private PythonNetManager(string configPath)
        {
            ConfigPath = Path.GetFullPath(configPath);
            var raw = File.ReadAllText(configPath);
            dynamic cfg = JsonConvert.DeserializeObject(raw)
                          ?? throw new InvalidDataException("Python config.json is empty or invalid.");

            string osKey = OperatingSystem.IsWindows() ? "windows"
                : OperatingSystem.IsLinux()   ? "linux"
                : OperatingSystem.IsMacOS()   ? "osx"
                : throw new PlatformNotSupportedException("Unsupported OS for Python runtime.");

            string rawDllPath = (string)cfg.pythonDllByOS[osKey]
                                ?? throw new InvalidDataException($"No pythonDllByOS entry for '{osKey}'.");

            string rawPackagesPath = (string)cfg.sitePackagesByOS[osKey]
                                     ?? throw new InvalidDataException($"No sitePackagesByOS entry for '{osKey}'.");

            // Resolve relative paths against the config file's directory. This is what lets a build
            // ship its own Python next to the executable: the config says "runtime\python39.dll"
            // and it resolves wherever the folder was copied or unzipped to, on any machine.
            string configDir = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
            PythonDllPath    = Resolve(configDir, rawDllPath);
            SitePackagesPath = Resolve(configDir, rawPackagesPath);
            ScriptsPath      = Resolve(configDir, (string)cfg.loadScriptsPath);

            if (!File.Exists(PythonDllPath))
                throw new FileNotFoundException(
                    $"Python runtime not found at '{PythonDllPath}' (from '{configPath}'). " +
                    "Install the Python version that config names, or point pythonDllByOS at one " +
                    "that exists. Without this the block cannot run.", PythonDllPath);

            if (!Directory.Exists(SitePackagesPath))
                throw new DirectoryNotFoundException(
                    $"Python site-packages directory not found at '{SitePackagesPath}' " +
                    $"(from '{configPath}'). Install the declared environment and update the " +
                    "corresponding MOSAIC environment variable.");

            if (!Directory.Exists(ScriptsPath))
                throw new DirectoryNotFoundException(
                    $"Python scripts directory not found at '{ScriptsPath}' (from '{configPath}'). " +
                    "Set the corresponding MOSAIC environment variable to the public " +
                    "PythonIntegrations/scripts directory.");

            Runtime.PythonDLL = PythonDllPath;

            // A Python that ships with the app is not in the registry and cannot find its own
            // standard library, so say where it lives. Detected rather than configured: the marker
            // is a Lib folder beside the DLL, which a system install does not have.
            var runtimeDir = Path.GetDirectoryName(PythonDllPath);
            if (runtimeDir is not null && Directory.Exists(Path.Combine(runtimeDir, "Lib")))
            {
                PythonEngine.PythonHome = runtimeDir;
                Console.WriteLine($"[PythonNetManager] Using the bundled runtime at {runtimeDir}.");
            }

            PythonEngine.Initialize();

            // Release the GIL so background threads can acquire it freely.
            // Store thread state so Shutdown() can re-acquire before calling
            // PythonEngine.Shutdown(), which requires the GIL held.
            _threadState = PythonEngine.BeginAllowThreads();

            // --- Extend sys.path ----------------------------------------
            // Must be done inside a GIL acquire AFTER BeginAllowThreads.
            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                // Insert at index 0 so our packages take priority
                sys.path.insert(0, SitePackagesPath);
                sys.path.insert(1, ScriptsPath);
            }

            Console.WriteLine(
                $"[PythonNetManager] Ready.\n" +
                $"  DLL:      {PythonDllPath}\n" +
                $"  Packages: {SitePackagesPath}\n" +
                $"  Scripts:  {ScriptsPath}");
        }

        /// <summary>
        /// Call from each prediction block's constructor or Init() to confirm
        /// the runtime is live. Throws if manager was never initialized.
        /// </summary>
        /// <summary>
        /// Expands environment variables, then resolves a relative path against the config's folder.
        /// </summary>
        /// <param name="configDir">Folder holding the config file.</param>
        /// <param name="value">Raw value from the config. May be absolute, relative, or empty.</param>
        /// <returns>An absolute path.</returns>
        private static string Resolve(string configDir, string? value)
        {
            var expanded = Environment.ExpandEnvironmentVariables(value ?? string.Empty);
            var unresolved = Regex.Match(expanded, "%[A-Za-z_][A-Za-z0-9_]*%");
            if (unresolved.Success)
                throw new InvalidDataException(
                    $"Required environment variable '{unresolved.Value.Trim('%')}' is not set.");

            return Path.GetFullPath(
                Path.IsPathRooted(expanded) ? expanded : Path.Combine(configDir, expanded));
        }

        public void AssertReady()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // If we got here the runtime is up — nothing else needed.
        }

        /// <summary>
        /// Convenience wrapper: runs <paramref name="action"/> under the GIL.
        /// Prefer this over raw Py.GIL() in block code to keep GIL usage central.
        /// </summary>
        public void RunWithGIL(Action<dynamic> action)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using (Py.GIL())
            {
                dynamic builtins = Py.Import("builtins");
                action(builtins);
            }
        }

        /// <summary>
        /// Shuts down the Python runtime cleanly. Call once on application exit,
        /// e.g. from App.OnExit() or a hosted service StopAsync().
        /// </summary>
        public void Shutdown()
        {
            lock (_lock)
            {
                if (_disposed) return;
                Dispose();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Re-acquire GIL ownership before shutdown
            PythonEngine.EndAllowThreads(_threadState);
            PythonEngine.Shutdown();

            _instance = null;
            Console.WriteLine("[PythonNetManager] Python runtime shut down.");
        }
    }
}
