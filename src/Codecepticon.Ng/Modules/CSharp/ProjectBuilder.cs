using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Codecepticon.Utils;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Builds projects by launching a build tool as a CHILD PROCESS, never by
    /// loading MSBuild into this one.
    ///
    /// Upstream called Microsoft.Build.Evaluation.Project.Build() in-process,
    /// which requires this process to pin one MSBuild version for its whole
    /// lifetime. That is what MSBuildLocator exists to arrange, and what breaks
    /// the moment the machine's MSBuild is newer than the one this binary was
    /// compiled against. A child process has no such coupling: it is whatever
    /// the machine has, and its failures arrive as an exit code and text.
    /// </summary>
    static class ProjectBuilder
    {
        private static string _toolPath;
        private static bool _isDotnetCli;
        private static bool _resolved;
        private static string _description;

        /// <summary>
        /// Resolution order. MSBuild is preferred over `dotnet build` because
        /// the tools this obfuscates are mostly LEGACY non-SDK .NET Framework
        /// projects (Seatbelt, Rubeus, SharpHound, Certify...), and the .NET SDK
        /// CLI does not build those.
        /// </summary>
        private static void Resolve()
        {
            if (_resolved)
            {
                return;
            }
            _resolved = true;

            // 1. Explicit override always wins - the escape hatch for a machine
            //    with several toolchains, or a CI agent pinning one.
            string configured = Environment.GetEnvironmentVariable("CODECEPTICON_MSBUILD");
            if (!String.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            {
                _toolPath = configured;
                _isDotnetCli = false;
                Logger.Debug($"Build tool from CODECEPTICON_MSBUILD: {_toolPath}");
                return;
            }

            // 2. Ask vswhere where MSBuild is. This runs an executable and reads
            //    its stdout - it does NOT load any MSBuild assembly.
            string fromVsWhere = QueryVsWhere();
            if (!String.IsNullOrEmpty(fromVsWhere))
            {
                _toolPath = fromVsWhere;
                _isDotnetCli = false;
                Logger.Debug($"Build tool from vswhere: {_toolPath}");
                return;
            }

            // 3. A Developer Command Prompt puts it on PATH.
            string onPath = FindOnPath(OperatingSystem.IsWindows() ? "MSBuild.exe" : "msbuild");
            if (!String.IsNullOrEmpty(onPath))
            {
                _toolPath = onPath;
                _isDotnetCli = false;
                Logger.Debug($"Build tool from PATH: {_toolPath}");
                return;
            }

            // 4. SDK-style projects only.
            string dotnet = FindOnPath(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (!String.IsNullOrEmpty(dotnet))
            {
                _toolPath = dotnet;
                _isDotnetCli = true;
                Logger.Debug($"Build tool fallback to dotnet CLI: {_toolPath}");
                return;
            }

            Logger.Debug("No build tool found (no MSBuild, no dotnet CLI)");
        }

        /// <summary>
        /// One line naming the build half of the toolchain. The Roslyn half is
        /// reported by VisualStudioManager - a mismatch between them is the bug
        /// class this whole project exists to make legible.
        /// </summary>
        public static string Describe()
        {
            // Cached, and it must stay cached: this runs the build tool with
            // -version, and Run() clears _lastStdOut/_lastStdErr. Calling it
            // while reporting a build failure would otherwise wipe the very
            // compiler output being reported.
            if (_description != null)
            {
                return _description;
            }

            Resolve();
            if (String.IsNullOrEmpty(_toolPath))
            {
                _description = "build tool: NONE FOUND - set CODECEPTICON_MSBUILD to an MSBuild.exe";
                return _description;
            }

            string version = Run(_toolPath, _isDotnetCli ? "--version" : "-version -nologo", null, out _, 20000)
                ? _lastStdOut.Trim().Split('\n').Last().Trim()
                : "unknown";

            _description = $"build tool: {(_isDotnetCli ? "dotnet CLI" : "MSBuild")} v{version} ({_toolPath})";
            return _description;
        }

        /// <summary>True once a build tool has been found. Resolves on first use.</summary>
        public static bool HasBuildTool
        {
            get
            {
                Resolve();
                return !String.IsNullOrEmpty(_toolPath);
            }
        }

        /// <summary>
        /// True when the resolved tool is `dotnet build` rather than MSBuild.
        /// Matters because the .NET SDK CLI cannot build legacy non-SDK
        /// projects, which is most of what this tool is pointed at.
        /// </summary>
        public static bool IsDotnetCli
        {
            get
            {
                Resolve();
                return _isDotnetCli;
            }
        }

        public static bool Build(IEnumerable<string> projectPaths, IDictionary<string, string> properties)
        {
            Resolve();
            if (String.IsNullOrEmpty(_toolPath))
            {
                Logger.Error("No build tool available. Install the .NET SDK or Visual Studio Build Tools, or set CODECEPTICON_MSBUILD.");
                return false;
            }

            bool allSucceeded = true;
            foreach (string projectPath in projectPaths.Where(p => !String.IsNullOrEmpty(p)).Distinct())
            {
                if (!BuildOne(projectPath, properties))
                {
                    allSucceeded = false;
                }
            }
            return allSucceeded;
        }

        private static bool BuildOne(string projectPath, IDictionary<string, string> properties)
        {
            var arguments = new StringBuilder();
            if (_isDotnetCli)
            {
                arguments.Append("build ");
            }
            arguments.Append($"\"{projectPath}\"");
            arguments.Append(" -nologo");

            if (properties != null)
            {
                foreach (KeyValuePair<string, string> property in properties)
                {
                    arguments.Append($" -p:{property.Key}=\"{property.Value}\"");
                }
            }

            Logger.Debug($"Building: {_toolPath} {arguments}");
            bool ok = Run(_toolPath, arguments.ToString(), Path.GetDirectoryName(projectPath), out int exitCode, 900000);

            if (!ok || exitCode != 0)
            {
                ReportBuildFailure(projectPath, ok, exitCode);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Says WHY the build failed, at Error level.
        ///
        /// This used to log the compiler output at Debug, so anyone not running
        /// --debug got the word "ERROR" and nothing else - the opaque failure
        /// this project exists to stop producing. The diagnostics are the whole
        /// point: "reference assemblies for v4.7.2 were not found" and "CS0103:
        /// the name X does not exist" send you to completely different fixes.
        ///
        /// The full output still goes to Debug. What surfaces here is the
        /// diagnostic lines, capped, because an MSBuild log of a large solution
        /// is thousands of lines and burying the errors again would defeat this.
        /// </summary>
        private static void ReportBuildFailure(string projectPath, bool started, int exitCode)
        {
            // Snapshot everything Run() owns BEFORE calling Describe(): on a cold
            // cache Describe() runs the tool with -version, which clears the
            // captured streams and the timeout flag.
            List<string> diagnostics = ExtractDiagnostics();
            string fullStdOut = _lastStdOut;
            string fullStdErr = _lastStdErr;
            bool timedOut = _lastTimedOut;

            string tool = Describe();

            Logger.Error("");
            if (!started)
            {
                // Never launched, or timed out - there is no compiler output to
                // show, and the cause is the tool itself rather than the code.
                Logger.Error($"BUILD FAILED - {(timedOut ? "the build tool timed out" : "could not run the build tool")}: {projectPath}");
                Logger.Error($"  {tool}");
                Logger.Error(timedOut
                    ? "  Re-run with --debug, or build the obfuscated solution by hand to see where it hangs."
                    : "  Set CODECEPTICON_MSBUILD to a working MSBuild, or check it is not blocked.");
                Logger.Error("");
                return;
            }

            Logger.Error($"BUILD FAILED (exit {exitCode}): {projectPath}");
            Logger.Error($"  {tool}");

            if (diagnostics.Count == 0)
            {
                Logger.Error("  The build tool reported no diagnostics. Re-run with --debug for its full output.");
            }
            else
            {
                foreach (string line in diagnostics)
                {
                    Logger.Error($"  {line}");
                }
            }

            Logger.Error("");
            Logger.Error("  The source HAS been obfuscated - this is the compile of the result.");
            Logger.Error("  Re-run with --debug for the build tool's full output.");
            Logger.Error("");

            // Full output stays available behind --debug - from the snapshot, not
            // the fields, which Describe() may since have cleared.
            Logger.Debug(fullStdOut);
            Logger.Debug(fullStdErr);
        }

        private const int MaxReportedDiagnostics = 25;

        /// <summary>
        /// Pulls the error and warning lines out of a build log. Matches the
        /// shape both MSBuild and the .NET SDK CLI emit:
        ///
        ///   Foo.cs(12,5): error CS0103: The name 'Bar' does not exist ...
        ///   ... : error MSB3644: The reference assemblies for ... were not found
        ///
        /// Errors are listed before warnings: a build fails because of errors,
        /// and warnings are only context when they explain one (a missing
        /// targeting pack usually shows up as both).
        /// </summary>
        private static List<string> ExtractDiagnostics()
        {
            IEnumerable<string> lines = (_lastStdOut + "\n" + _lastStdErr)
                .Split('\n')
                .Select(line => line.TrimEnd('\r').Trim())
                .Where(line => line.Length > 0);

            var errors = new List<string>();
            var warnings = new List<string>();

            foreach (string line in lines)
            {
                if (line.Contains(": error ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!errors.Contains(line))
                    {
                        errors.Add(line);
                    }
                }
                else if (line.Contains(": warning ", StringComparison.OrdinalIgnoreCase))
                {
                    if (!warnings.Contains(line))
                    {
                        warnings.Add(line);
                    }
                }
            }

            var reported = new List<string>();
            int total = errors.Count + warnings.Count;

            reported.AddRange(errors.Take(MaxReportedDiagnostics));
            if (reported.Count < MaxReportedDiagnostics)
            {
                reported.AddRange(warnings.Take(MaxReportedDiagnostics - reported.Count));
            }

            if (total > reported.Count)
            {
                reported.Add($"... and {total - reported.Count} more ({errors.Count} errors, {warnings.Count} warnings total)");
            }

            return reported;
        }

        private static string QueryVsWhere()
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            string programFiles = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (String.IsNullOrEmpty(programFiles))
            {
                programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
            }
            if (String.IsNullOrEmpty(programFiles))
            {
                return null;
            }

            string vsWhere = Path.Combine(programFiles, "Microsoft Visual Studio", "Installer", "vswhere.exe");
            if (!File.Exists(vsWhere))
            {
                return null;
            }

            const string arguments = "-latest -prerelease -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe";
            if (!Run(vsWhere, arguments, null, out int exitCode, 30000) || exitCode != 0)
            {
                return null;
            }

            string found = _lastStdOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => File.Exists(line.Trim()));

            return found == null ? null : found.Trim();
        }

        private static string FindOnPath(string executable)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (String.IsNullOrEmpty(path))
            {
                return null;
            }

            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (String.IsNullOrWhiteSpace(directory))
                {
                    continue;
                }

                try
                {
                    string candidate = Path.Combine(directory.Trim(), executable);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry is not worth aborting a build over.
                }
            }

            return null;
        }

        private static string _lastStdOut = String.Empty;
        private static string _lastStdErr = String.Empty;
        private static bool _lastTimedOut;

        private static bool Run(string fileName, string arguments, string workingDirectory, out int exitCode, int timeoutMs)
        {
            exitCode = -1;
            _lastStdOut = String.Empty;
            _lastStdErr = String.Empty;
            _lastTimedOut = false;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                if (!String.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory))
                {
                    startInfo.WorkingDirectory = workingDirectory;
                }

                using (var process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        return false;
                    }

                    // Read both streams before waiting: a build that fills the
                    // stderr pipe while we block on WaitForExit deadlocks.
                    System.Threading.Tasks.Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                    System.Threading.Tasks.Task<string> stderr = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(true); } catch { }
                        _lastTimedOut = true;
                        Logger.Debug($"Build tool timed out after {timeoutMs}ms");
                        return false;
                    }

                    _lastStdOut = stdout.Result ?? String.Empty;
                    _lastStdErr = stderr.Result ?? String.Empty;
                    exitCode = process.ExitCode;
                    return true;
                }
            }
            catch (Exception e)
            {
                Logger.Debug($"Could not run '{fileName}': {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }
}
