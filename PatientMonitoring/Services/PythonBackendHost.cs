using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace PatientMonitoring.Services
{
    public static class PythonBackendHost
    {
        private static readonly object _lock = new();
        private static Process? _proc;
        private static WindowsJob? _job; // Ensures child is killed when app ends

        public static void Start()
        {
            lock (_lock)
            {
                if (_proc is { HasExited: false }) return;

                var script = ResolveScriptPath();
                var exe = ResolvePythonExe();
                var venvRoot = TryGetVenvRootFromExe(exe);

                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = $"\"{script}\"",
                    WorkingDirectory = Path.GetDirectoryName(script)!,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.Environment["PYTHONUNBUFFERED"] = "1";

                // If running from a venv, propagate VIRTUAL_ENV and ensure Scripts is first in PATH
                if (!string.IsNullOrWhiteSpace(venvRoot) && Directory.Exists(venvRoot))
                {
                    var scriptsDir = Path.Combine(venvRoot, "Scripts");
                    psi.Environment["VIRTUAL_ENV"] = venvRoot;
                    var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
                    psi.Environment["PATH"] = Directory.Exists(scriptsDir)
                        ? scriptsDir + ";" + currentPath
                        : currentPath;
                }

                _proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Python backend.");
                _proc.EnableRaisingEvents = true;
                _proc.OutputDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine("[py] " + e.Data); };
                _proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) Debug.WriteLine("[py-err] " + e.Data); };
                _proc.BeginOutputReadLine();
                _proc.BeginErrorReadLine();

                // Attach backend process to a Job so it dies when the parent is terminated (e.g., Stop Debugging)
                _job ??= new WindowsJob(killOnClose: true);
                try { _job.AddProcess(_proc); }
                catch (Exception ex) { Debug.WriteLine("Failed to add process to job: " + ex.Message); }
            }
        }

        public static async Task StopAsync()
        {
            Process? p;
            lock (_lock) { p = _proc; }
            if (p == null) return;

            try
            {
                await PythonBackendControl.RequestShutdownAsync();
                if (!p.HasExited && !p.WaitForExit(1500))
                {
                    p.Kill(entireProcessTree: true);
                    p.WaitForExit(3000);
                }
            }
            catch
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                try { p.Dispose(); } catch { }
                lock (_lock) { _proc = null; }
            }
        }

        private static string ResolvePythonExe()
        {
            // 1) Explicit override
            var fromEnv = Environment.GetEnvironmentVariable("PYTHON");
            if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv))
                return fromEnv;

            // 2) VIRTUAL_ENV points to a venv root
            var ve = Environment.GetEnvironmentVariable("VIRTUAL_ENV");
            if (TryResolveFromVenv(ve, out var fromVenv)) return fromVenv;

            // 3) Look for a nearby venv next to the script or its parent
            var scriptDir = Path.GetDirectoryName(ResolveScriptPath())!;
            foreach (var dir in new[]
            {
                Path.Combine(scriptDir, ".venv"),
                Path.Combine(scriptDir, "venv"),
                Path.Combine(scriptDir, "env"),
                Path.Combine(Directory.GetParent(scriptDir)?.FullName ?? scriptDir, ".venv"),
                Path.Combine(Directory.GetParent(scriptDir)?.FullName ?? scriptDir, "venv"),
                Path.Combine(Directory.GetParent(scriptDir)?.FullName ?? scriptDir, "env"),
            })
            {
                if (TryResolveFromVenv(dir, out var exe)) return exe;
            }

            // 4) Fallback to pythonw (no console)
            return "pythonw.exe";
        }

        private static bool TryResolveFromVenv(string? venvRoot, out string exePath)
        {
            exePath = string.Empty;
            if (string.IsNullOrWhiteSpace(venvRoot) || !Directory.Exists(venvRoot)) return false;

            try
            {
                var scripts = Path.Combine(venvRoot, "Scripts");
                var pyw = Path.Combine(scripts, "pythonw.exe");
                var py = Path.Combine(scripts, "python.exe");

                if (File.Exists(pyw)) { exePath = pyw; return true; }
                if (File.Exists(py)) { exePath = py; return true; }
            }
            catch { /* ignore */ }

            return false;
        }

        private static string? TryGetVenvRootFromExe(string exePath)
        {
            try
            {
                var scriptsDir = Path.GetDirectoryName(exePath);
                if (string.IsNullOrWhiteSpace(scriptsDir)) return null;

                var venvRoot = Directory.GetParent(scriptsDir)?.FullName;
                if (string.IsNullOrWhiteSpace(venvRoot)) return null;

                var cfg = Path.Combine(venvRoot, "pyvenv.cfg");
                return File.Exists(cfg) ? venvRoot : null;
            }
            catch { return null; }
        }

        private static string ResolveScriptPath()
        {
            // prefer .pyw if available
            var baseDir = AppContext.BaseDirectory;
            var pyw = Path.Combine(baseDir, "blebackend", "blebackend.pyw");
            if (File.Exists(pyw)) return pyw;

            var py = Path.Combine(baseDir, "blebackend", "blebackend.py");
            if (File.Exists(py)) return py;

            string TryUp(params string[] parts) => Path.GetFullPath(Path.Combine(baseDir, Path.Combine(parts)));

            foreach (var c in new[]
            {
                TryUp("..","..","..","..","blebackend","blebackend.pyw"),
                TryUp("..","..","..","..","blebackend","blebackend.py"),
                TryUp("..","..","..","blebackend","blebackend.pyw"),
                TryUp("..","..","..","blebackend","blebackend.py")
            })
            {
                if (File.Exists(c)) return c;
            }

            throw new FileNotFoundException("Cannot locate blebackend.py/pyw. Copy it to the app output or set Copy to Output Directory.");
        }
    }
}