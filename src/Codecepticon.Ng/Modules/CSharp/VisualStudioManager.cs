using Codecepticon.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Roslyn workspace access and project manipulation.
    ///
    /// This class used to host MSBuild: it called
    /// MSBuildLocator.QueryVisualStudioInstances() to pick a Visual Studio
    /// install, RegisterInstance() to load THAT MSBuild into this process, and
    /// Microsoft.Build.Evaluation.ProjectCollection to evaluate and build. On a
    /// machine whose newest MSBuild is 18 (Visual Studio 2026) that combination
    /// aborts with MissingMethodException: FrozenSet.Create, thrown from
    /// Microsoft.Build.Shared.XMakeElements..cctor, mid solution-load.
    ///
    /// None of it is here any more:
    ///   - the workspace is created directly; modern MSBuildWorkspace drives
    ///     MSBuild OUT OF PROCESS through the BuildHost shipped in the
    ///     Microsoft.CodeAnalysis.Workspaces.MSBuild package
    ///   - project properties are read and written as XML (see ProjectFile)
    ///   - builds run as a child process (see ProjectBuilder)
    ///
    /// The result: no MSBuild assembly is loaded into this process at any point,
    /// so there is no version to mismatch.
    /// </summary>
    class VisualStudioManager
    {
        private static bool _reportedToolchain;

        public static MSBuildWorkspace GetWorkspace()
        {
            return GetWorkspace(new Dictionary<string, string>());
        }

        public static MSBuildWorkspace GetWorkspace(Dictionary<string, string> properties)
        {
            ReportToolchain();
            Logger.Verbose("Creating MSBuild Workspace (out-of-process build host)");
            return MSBuildWorkspace.Create(properties);
        }

        /// <summary>
        /// Print both halves of the toolchain before anything can fail because
        /// they disagree. Upstream's failures arrived as raw CLR aborts naming
        /// neither version, which cost four diagnose-and-rebuild cycles on the
        /// CI agent. One line here is what makes the next mismatch legible.
        /// </summary>
        public static void ReportToolchain()
        {
            if (_reportedToolchain)
            {
                return;
            }
            _reportedToolchain = true;

            string roslyn = typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "unknown";
            Logger.Verbose($"Toolchain: Roslyn Workspaces v{roslyn}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
            Logger.Verbose($"Toolchain: {ProjectBuilder.Describe()}");
        }

        /// <summary>
        /// Turns whatever the user pointed --path at into the file that should
        /// actually be opened: a .slnx, a .sln, or a bare .csproj. Returns null
        /// (having reported why) if that cannot be decided.
        ///
        /// A directory is searched, preferring .slnx over .sln - a repo carrying
        /// both is usually mid-migration and the XML one is the current source of
        /// truth. Falling back to a single .csproj means a one-project repo with
        /// no solution at all still works, which is common for the small tools
        /// this obfuscates.
        /// </summary>
        public static string ResolveInputPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path))
            {
                Logger.Error("Input path is empty.");
                return null;
            }

            if (File.Exists(path))
            {
                return path;
            }

            if (!Directory.Exists(path))
            {
                Logger.Error($"Input path does not exist: {path}");
                return null;
            }

            foreach (string pattern in new[] { "*.slnx", "*.sln" })
            {
                string hit = Directory
                    .EnumerateFiles(path, pattern, SearchOption.AllDirectories)
                    .OrderBy(p => p.Length)
                    .FirstOrDefault();

                if (hit != null)
                {
                    Logger.Verbose($"Resolved {path} to solution {hit}");
                    return hit;
                }
            }

            List<string> projects = Directory
                .EnumerateFiles(path, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Replace('\\', '/').Contains("/obj/", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (projects.Count == 1)
            {
                Logger.Verbose($"Resolved {path} to project {projects[0]}");
                return projects[0];
            }

            if (projects.Count == 0)
            {
                Logger.Error($"No .slnx, .sln or .csproj found under: {path}");
                return null;
            }

            Logger.Error($"{projects.Count} .csproj files under {path} and no solution to order them.");
            Logger.Error("Point --path at a solution, or at one specific project.");
            return null;
        }

        /// <summary>
        /// Opens a solution OR a bare project, turning the two exception types
        /// that signal a toolchain mismatch into a named error instead of an
        /// unhandled stack trace. Everything else is left to bubble.
        ///
        /// The .csproj branch matters because MSBuildWorkspace.OpenSolutionAsync
        /// will not open a project file - it fails with "Failed to load
        /// solution", which tells the user nothing about what to do instead.
        /// </summary>
        public static async Task<Solution> OpenSolutionAsync(MSBuildWorkspace workspace, string path)
        {
            try
            {
                if (String.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    Project project = await workspace.OpenProjectAsync(path);
                    return project.Solution;
                }

                return await workspace.OpenSolutionAsync(path);
            }
            catch (Exception e) when (e is MissingMethodException || e is TypeLoadException)
            {
                string roslyn = typeof(Workspace).Assembly.GetName().Version?.ToString() ?? "unknown";
                Logger.Error("");
                Logger.Error("TOOLCHAIN MISMATCH while loading the solution.");
                Logger.Error($"  Roslyn Workspaces : v{roslyn}");
                Logger.Error($"  {ProjectBuilder.Describe()}");
                Logger.Error($"  Underlying error  : {e.GetType().Name}: {e.Message}");
                Logger.Error("");
                Logger.Error("This is the failure class that killed upstream Codecepticon: a build");
                Logger.Error("engine newer than the Roslyn built against it. Align the two, or set");
                Logger.Error("CODECEPTICON_MSBUILD to a matching MSBuild.");
                return null;
            }
        }

        public static (Project, Document) GetProjectAndDocumentByName(Solution solution, string projectName, string documentName)
        {
            Project project = GetProjectByName(solution, projectName);
            Document document = GetDocumentByName(project, documentName);
            return (project, document);
        }

        public static Project GetProjectByName(Solution solution, string projectName)
        {
            return solution.Projects.FirstOrDefault(s => s.Name == projectName);
        }

        public static Document GetDocumentByName(Project project, string documentName)
        {
            return project.Documents.FirstOrDefault(s => s.Name == documentName);
        }

        public static Document GetDocumentByName(Solution solution, string projectName, string documentName)
        {
            Project project = GetProjectByName(solution, projectName);
            return GetDocumentByName(project, documentName);
        }

        protected static void SetConfiguration(Solution solution, Dictionary<string, string> properties, bool isGlobal)
        {
            Logger.Debug($"VisualStudio SetConfiguration - Global is {isGlobal}");
            foreach (Project project in solution.Projects)
            {
                if (String.IsNullOrEmpty(project.FilePath))
                {
                    continue;
                }
                ProjectFile buildProject = GetBuildProject(project);
                SetConfiguration(buildProject, properties, isGlobal);
            }
        }

        protected static void SetConfiguration(ProjectFile buildProject, Dictionary<string, string> properties, bool isGlobal)
        {
            foreach (KeyValuePair<string, string> property in properties)
            {
                if (isGlobal)
                {
                    buildProject.SetGlobalProperty(property.Key, property.Value);
                }
                else
                {
                    buildProject.SetProperty(property.Key, property.Value);
                }
            }

            // Global properties are not persisted - MSBuild did not write them
            // on Save() either. They travel to the build as -p: arguments.
            if (!isGlobal)
            {
                buildProject.Save();
            }
        }

        public static void SetProjectConfiguration(Solution solution, Dictionary<string, string> properties)
        {
            SetConfiguration(solution, properties, false);
        }

        public static ProjectFile GetBuildProject(Project project)
        {
            return ProjectFile.Load(project.FilePath);
        }

        public static bool Build(Solution solution)
        {
            return Build(solution, new Dictionary<string, string>());
        }

        public static bool Build(Solution solution, Dictionary<string, string> properties)
        {
            Logger.Debug("VisualStudio Build (child process)");
            try
            {
                List<string> projectPaths = solution.Projects
                    .Select(p => p.FilePath)
                    .Where(p => !String.IsNullOrEmpty(p))
                    .ToList();

                if (projectPaths.Count == 0)
                {
                    Logger.Error("No project files in the solution to build.");
                    return false;
                }

                return ProjectBuilder.Build(projectPaths, properties);
            }
            catch (Exception e)
            {
                Logger.Error("ERROR", true, false);
                Logger.Error("Could not compile solution: " + e.Message);
            }
            return false;
        }
    }
}
