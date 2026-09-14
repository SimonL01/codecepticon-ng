using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Answers "can this machine actually build what we are about to obfuscate?"
    /// BEFORE a single file is rewritten.
    ///
    /// The C# module rewrites source IN PLACE. A build failure discovered
    /// afterwards leaves the caller with obfuscated source and a broken build,
    /// and nothing to tell them whether the obfuscation or the toolchain was at
    /// fault. Every check here is one we have actually been burned by:
    ///
    ///   no build tool          --build silently degrades to nothing useful
    ///   dotnet CLI + legacy    the .NET SDK cannot build non-SDK projects, and
    ///                          most tools worth obfuscating are non-SDK net472
    ///   missing targeting pack MSB3644 - observed on a VS 2026 VM against
    ///                          Seatbelt, which targets v3.5. Cost two rounds of
    ///                          diagnosis to establish it was not a tool bug.
    ///
    /// Deliberately refuses rather than warns: a warning scrolled past costs a
    /// destroyed working tree. And it reports EVERY failing project in one go,
    /// rather than one per run.
    /// </summary>
    static class BuildPreflight
    {
        /// <summary>
        /// Returns true when a build may be attempted. On false it has already
        /// explained what is wrong and nothing should be rewritten.
        /// </summary>
        public static bool Check(Solution solution)
        {
            var problems = new List<string>();
            var advice = new List<string>();

            if (!ProjectBuilder.HasBuildTool)
            {
                problems.Add("No build tool found at all.");
                advice.Add("Install the .NET SDK or Visual Studio Build Tools, or set CODECEPTICON_MSBUILD.");
                Report(problems, advice);
                return false;
            }

            foreach (Project project in solution.Projects)
            {
                if (String.IsNullOrEmpty(project.FilePath) || !File.Exists(project.FilePath))
                {
                    continue;
                }

                CheckProject(project, problems, advice);
            }

            if (problems.Count == 0)
            {
                Logger.Verbose($"Preflight OK - {ProjectBuilder.Describe()}");
                return true;
            }

            Report(problems, advice);
            return false;
        }

        private static void CheckProject(Project project, List<string> problems, List<string> advice)
        {
            ProjectFile file;
            try
            {
                file = ProjectFile.Load(project.FilePath);
            }
            catch (Exception e)
            {
                // Not fatal to the preflight: the build will report it properly.
                Logger.Debug($"Preflight could not read {project.FilePath}: {e.Message}");
                return;
            }

            string name = Path.GetFileName(project.FilePath);

            // 1. The .NET SDK CLI cannot build legacy non-SDK projects. This is a
            //    guaranteed failure, not a maybe, so it is worth its own message.
            if (!file.IsSdkStyle && ProjectBuilder.IsDotnetCli)
            {
                problems.Add($"{name} is a legacy (non-SDK) project, and the only build tool found is the .NET SDK CLI, which cannot build one.");
                advice.Add("Point CODECEPTICON_MSBUILD at a real MSBuild.exe. On Windows:");
                advice.Add("  & \"${env:ProgramFiles(x86)}\\Microsoft Visual Studio\\Installer\\vswhere.exe\" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\\**\\Bin\\MSBuild.exe");
            }

            // 2. .NET Framework targets need their reference assemblies present.
            foreach (string version in FrameworkVersions(file))
            {
                if (HasTargetingPack(version))
                {
                    continue;
                }

                problems.Add($"{name} targets .NET Framework {version}, and its targeting pack is not installed.");
                advice.Add($"Install the {version} Developer Pack: https://aka.ms/msbuild/developerpacks");
                if (version == "v3.5")
                {
                    advice.Add("  For 3.5 specifically: Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All");
                }
            }
        }

        /// <summary>
        /// The .NET Framework versions a project targets, as "v4.7.2" style
        /// strings. Empty for projects that target .NET (Core) only - those need
        /// no separate targeting pack.
        /// </summary>
        private static IEnumerable<string> FrameworkVersions(ProjectFile file)
        {
            // Legacy projects say it outright.
            string legacy = file.GetPropertyValue("TargetFrameworkVersion");
            if (!String.IsNullOrWhiteSpace(legacy))
            {
                yield return legacy.Trim();
                yield break;
            }

            // SDK-style: TargetFramework, or TargetFrameworks for multi-target.
            string single = file.GetPropertyValue("TargetFramework");
            string multiple = file.GetPropertyValue("TargetFrameworks");
            string combined = String.IsNullOrWhiteSpace(multiple) ? single : multiple;

            if (String.IsNullOrWhiteSpace(combined))
            {
                yield break;
            }

            foreach (string moniker in combined.Split(';'))
            {
                string version = FrameworkVersionFromMoniker(moniker.Trim());
                if (version != null)
                {
                    yield return version;
                }
            }
        }

        /// <summary>
        /// "net472" -> "v4.7.2". Returns null for anything that is not .NET
        /// Framework: net10.0 and netstandard2.0 carry a dot, netcoreapp is
        /// named outright, and only .NET Framework monikers are bare digits.
        /// </summary>
        public static string FrameworkVersionFromMoniker(string moniker)
        {
            if (String.IsNullOrWhiteSpace(moniker))
            {
                return null;
            }

            moniker = moniker.Trim().ToLowerInvariant();
            if (!moniker.StartsWith("net") || moniker.Contains('.') || moniker.StartsWith("netstandard") || moniker.StartsWith("netcoreapp"))
            {
                return null;
            }

            string digits = moniker.Substring(3);
            if (digits.Length < 2 || digits.Length > 3 || !digits.All(Char.IsDigit))
            {
                return null;
            }

            return "v" + String.Join(".", digits.Select(c => c.ToString()));
        }

        /// <summary>
        /// Whether the reference assemblies for a .NET Framework version are on
        /// this machine. Off Windows the answer is always no, and saying so is
        /// more useful than pretending the check does not apply.
        /// </summary>
        private static bool HasTargetingPack(string version)
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            foreach (string root in new[] { "ProgramFiles(x86)", "ProgramFiles" })
            {
                string programFiles = Environment.GetEnvironmentVariable(root);
                if (String.IsNullOrEmpty(programFiles))
                {
                    continue;
                }

                string path = Path.Combine(programFiles, "Reference Assemblies", "Microsoft", "Framework", ".NETFramework", version);
                if (Directory.Exists(path))
                {
                    return true;
                }
            }

            return false;
        }

        private static void Report(List<string> problems, List<string> advice)
        {
            Logger.Error("");
            Logger.Error("PREFLIGHT FAILED - nothing has been modified.");
            Logger.Error($"  {ProjectBuilder.Describe()}");
            Logger.Error("");

            foreach (string problem in problems)
            {
                Logger.Error($"  [FAIL] {problem}");
            }

            if (advice.Count > 0)
            {
                Logger.Error("");
                foreach (string line in advice.Distinct())
                {
                    Logger.Error($"  {line}");
                }
            }

            Logger.Error("");
            Logger.Error("  Obfuscation and building are separable: re-run without --build (and without");
            Logger.Error("  --precompile) to obfuscate anyway, then build elsewhere.");
            Logger.Error("");
        }
    }
}
