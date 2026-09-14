using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Codecepticon.Modules.CSharp;

namespace Codecepticon.Utils
{
    /// <summary>
    /// What `--version` prints.
    ///
    /// ReportToolchain() already prints both halves of the toolchain, but only
    /// under --verbose, and the mismatch handler in VisualStudioManager only
    /// prints them once the run has already failed. Neither is reachable by
    /// someone who just wants to answer "what have I actually got here?" before
    /// pointing this at a solution - so every version that can disagree with
    /// another is gathered in one place here, and printed on request.
    ///
    /// The point is a block that can be pasted into a bug report whole. A
    /// mismatch between the Roslyn line and the build tool line IS the bug class
    /// this port exists to eliminate; reading them off a user's paste is how you
    /// identify it in one round trip instead of four.
    /// </summary>
    static class VersionInfo
    {
        public static string Render()
        {
            string informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // Falls back to AssemblyVersion for a build that set neither - the
            // release workflow passes -p:Version, and .NET appends the commit
            // SHA to InformationalVersion on its own, so a released binary
            // always names the commit it came from.
            if (String.IsNullOrWhiteSpace(informational))
            {
                informational = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
            }

            return String.Join(Environment.NewLine, new[]
            {
                $"codecepticon-ng {informational}",
                "",
                $"  Roslyn Workspaces : {AssemblyVersionOf(typeof(Microsoft.CodeAnalysis.Workspace))}",
                $"  Build tool        : {BuildTool()}",
                $"  PowerShell SDK    : {AssemblyVersionOf(typeof(System.Management.Automation.Language.Parser))}",
                $"  ANTLR runtime     : {AssemblyVersionOf(typeof(Antlr4.Runtime.Parser))}",
                "",
                $"  Runtime           : {RuntimeInformation.FrameworkDescription}",
                $"  OS                : {RuntimeInformation.OSDescription.Trim()} ({RuntimeInformation.OSArchitecture})",
                $"  Install directory : {AppPaths.BaseDirectory}",
                "",
                RenderRuntimeAssets(),
            });
        }

        /// <summary>
        /// ProjectBuilder.Describe() returns a whole sentence ("build tool: ..."),
        /// which is right where it is used - inside the toolchain-mismatch error,
        /// as prose. Here it is one row of an aligned table, so the label is
        /// dropped and the value keeps its column. Describe() stays the single
        /// place that resolves the tool, so the two reports cannot disagree.
        /// </summary>
        private static string BuildTool()
        {
            const string prefix = "build tool: ";
            string described = ProjectBuilder.Describe();

            return described.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? described.Substring(prefix.Length)
                : described;
        }

        /// <summary>
        /// The four directories that must sit beside the executable, and whether
        /// they actually do.
        ///
        /// This is not padding. The BuildHosts are separate executables that
        /// MSBuildWorkspace launches out-of-process, and Templates/ and Help/ are
        /// read from disk at run time - so an unpacked-wrong install fails with
        /// FileNotFoundException on the first workspace open, or silently lacks
        /// the templates it is supposed to inject. Naming the missing directory
        /// here turns "it crashed" into "you unzipped only part of the archive".
        /// </summary>
        private static string RenderRuntimeAssets()
        {
            string[] required = { "BuildHost-netcore", "BuildHost-net472", "Templates", "Help" };
            string report = "  Runtime assets    :";

            foreach (string name in required)
            {
                bool present = Directory.Exists(AppPaths.InApp(name));
                report += $"{Environment.NewLine}    {(present ? "ok     " : "MISSING")} {name}{Path.DirectorySeparatorChar}";
            }

            return report;
        }

        /// <summary>
        /// Version of the assembly a type came from. Reported per-package rather
        /// than read off the csproj, because what matters at run time is what
        /// actually loaded - a binding redirect or a stray DLL beside the
        /// executable can make those two disagree, and only this one is true.
        /// </summary>
        private static string AssemblyVersionOf(Type type)
        {
            try
            {
                return type.Assembly.GetName().Version?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
