using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;

namespace Codecepticon.Modules.CSharp.Profiles
{
    class BaseProfile
    {
        public virtual string Name { get; } = "Not Set";

        /// <summary>
        /// Set when the profile could not do its job - almost always because
        /// --profile names a tool this solution is not. The caller aborts on it
        /// rather than continuing, because a half-applied profile is worse than
        /// none: the identifiers get renamed but the help text and command-line
        /// verbs do not, and nothing about the result says so.
        /// </summary>
        public bool Failed { get; protected set; }

        /// <summary>
        /// True when the document sits DIRECTLY inside a directory with this
        /// name - `Commands/Foo.cs` matches, `Commands/Windows/Foo.cs` does not.
        /// </summary>
        protected static bool IsInDirectory(Document document, string directoryName)
        {
            string directory = NormalisedDirectory(document);
            if (directory == null)
            {
                return false;
            }

            string last = directory.Split('/').LastOrDefault();
            return String.Equals(last, directoryName, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when the document is anywhere BENEATH a directory with this name
        /// - both `Commands/Foo.cs` and `Commands/Windows/Foo.cs` match.
        /// </summary>
        protected static bool IsUnderDirectory(Document document, string directoryName)
        {
            string directory = NormalisedDirectory(document);
            if (directory == null)
            {
                return false;
            }

            return $"/{directory.Trim('/')}/".IndexOf($"/{directoryName}/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// A document's directory with forward slashes, or null if it has no
        /// path at all.
        ///
        /// Profiles used to match with hardcoded backslashes - `Split('\\')` or
        /// `IndexOf(@"\Commands\")` - which on Linux never matches, so
        /// command-line rewriting SILENTLY did nothing and the run still
        /// reported success. Same bug class as the AppPaths fix, and the same
        /// reason it went unnoticed: the tools these profiles target are Windows
        /// tools, so nobody ran them anywhere else.
        ///
        /// Also guards a null FilePath, which generated documents have and which
        /// `new FileInfo(document.FilePath)` threw on.
        /// </summary>
        private static string NormalisedDirectory(Document document)
        {
            if (document == null || String.IsNullOrEmpty(document.FilePath))
            {
                return null;
            }

            string directory = Path.GetDirectoryName(document.FilePath);
            return String.IsNullOrEmpty(directory) ? null : directory.Replace('\\', '/');
        }

        /// <summary>
        /// Fetches a document the profile cannot work without, and says something
        /// useful when it is not there.
        ///
        /// Every profile used to index straight into the result of
        /// GetDocumentByName, which returns null for "no such file". Pointing
        /// --profile seatbelt at something that is not Seatbelt therefore produced
        /// a NullReferenceException from inside a rewriter - technically caught at
        /// the top of Main now, but it named neither the profile nor the file, so
        /// the one thing the user needed to know was the one thing missing.
        /// </summary>
        protected Document RequireDocument(Project project, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(project, documentName);
            if (document != null)
            {
                return document;
            }

            // A hook usually needs several files, and once the first is missing
            // the rest will be too. Explain once; list the others.
            if (Failed)
            {
                Logger.Error($"  ...also missing: {documentName}");
                return null;
            }

            Failed = true;
            Logger.Error("");
            Logger.Error($"PROFILE MISMATCH - the '{Name}' profile needs {documentName}, and this project has no such file.");
            Logger.Error($"  Project: {project.FilePath}");
            Logger.Error("");
            Logger.Error($"  Either this is not {Name}, or its layout has changed since the profile was written.");
            Logger.Error($"  Re-run without --profile to obfuscate it generically, or update the {Name} profile");
            Logger.Error("  (docs/05-profiles.md).");
            Logger.Error("");
            return null;
        }

        /// <summary>
        /// Looks up a name the profile cannot proceed without, and says which one
        /// when it is not there.
        ///
        /// Same failure shape as RequireDocument, different dictionary. Profiles
        /// indexed straight into the rename mappings - Mapping.Namespaces[rootNamespace],
        /// Mapping.Enums["All"], Mapping.Namespaces["SharpView"] - which throws
        /// KeyNotFoundException when the expected symbol is absent, naming only
        /// the key and nothing about the profile or what to do.
        ///
        /// A key is missing when the project does not contain that symbol at all
        /// (wrong tool, or upstream renamed it), or when the relevant --rename
        /// category was left off so nothing was collected for it.
        /// </summary>
        protected string RequireMapping(IDictionary<string, string> mapping, string key, string describesWhat)
        {
            if (mapping != null && key != null && mapping.TryGetValue(key, out string value))
            {
                return value;
            }

            if (Failed)
            {
                Logger.Error($"  ...also missing: {describesWhat} '{key}'");
                return null;
            }

            Failed = true;
            Logger.Error("");
            Logger.Error($"PROFILE MISMATCH - the '{Name}' profile expected {describesWhat} '{key}', and nothing of that name was renamed.");
            Logger.Error("");
            Logger.Error($"  Either this is not {Name}, or its layout has changed since the profile was written,");
            Logger.Error("  or the --rename categories in use did not collect that kind of symbol.");
            Logger.Error("  Re-run without --profile to obfuscate it generically (docs/05-profiles.md).");
            Logger.Error("");
            return null;
        }

        public virtual async Task<Solution> Before(Solution solution, Project project)
        {
            Logger.Debug("Profile does not implement Before function");
            return solution;
        }

        public virtual async Task<Solution> After(Solution solution, Project project)
        {
            ProjectFile buildProject = VisualStudioManager.GetBuildProject(project);

            if (CommandLineData.CSharp.Rename.Namespaces || CommandLineData.CSharp.Rename.Classes)
            {
                string startupObject = buildProject.GetPropertyValue("StartupObject");
                if (!String.IsNullOrEmpty(startupObject))
                {
                    Logger.Debug($"StartUp Object was: {startupObject}");
                    string[] elements = startupObject.Split('.');
                    if (elements.Length == 2)
                    {
                        string namespaceValue = CommandLineData.CSharp.Rename.Namespaces && DataCollector.Mapping.Namespaces.ContainsKey(elements[0]) ? DataCollector.Mapping.Namespaces[elements[0]] : elements[0];
                        string classValue = CommandLineData.CSharp.Rename.Classes && DataCollector.Mapping.Classes.ContainsKey(elements[1]) ? DataCollector.Mapping.Classes[elements[1]] : elements[1];
                        Logger.Debug($"StartUp Object is: {namespaceValue}.{classValue}");
                        buildProject.SetProperty("StartupObject", $"{namespaceValue}.{classValue}");
                        buildProject.Save();
                    }
                }
            }

            return solution;
        }

        public virtual async Task<Solution> Final(Solution solution, Project project)
        {
            ProjectFile buildProject = VisualStudioManager.GetBuildProject(project);
            ReconcileAddedStringsFile(buildProject);
            buildProject.Save();
            return solution;
        }

        /// <summary>
        /// Reconciles the project file with the extra source file DataRewriter
        /// adds to hold the string decoder.
        ///
        /// Roslyn's TryApplyChanges writes an explicit &lt;Compile Include&gt; for any
        /// document added to a project. That is right for one project system and
        /// wrong for the other:
        ///
        ///   SDK-style   the SDK already globs **/*.cs from the project
        ///               directory, so the entry is a duplicate and the build
        ///               dies with NETSDK1022 - "Duplicate 'Compile' items were
        ///               included". The FILE must stay; the ENTRY must go.
        ///   Legacy      there is no glob, so the entry is required - but Roslyn
        ///               can write it more than once
        ///               (https://github.com/dotnet/roslyn/issues/36781).
        ///               Keep the first, drop the rest.
        ///
        /// Upstream only ever ran against legacy projects, so it carried the
        /// duplicate-removal as a SharpHound-specific workaround. It belongs
        /// here: every project needs it, and SDK-style projects need the
        /// stronger form.
        /// </summary>
        protected static void ReconcileAddedStringsFile(ProjectFile buildProject)
        {
            if (!CommandLineData.Global.Rewrite.Strings)
            {
                return;
            }

            string addedFile = CommandLineData.Global.Rewrite.Template.AddedFile;
            if (String.IsNullOrEmpty(addedFile))
            {
                return;
            }

            List<ProjectFileItem> entries = buildProject
                .GetItems("Compile")
                .Where(item => String.Equals(item.EvaluatedInclude, addedFile, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (entries.Count == 0)
            {
                return;
            }

            List<ProjectFileItem> remove = buildProject.IsSdkStyle ? entries : entries.Skip(1).ToList();
            foreach (ProjectFileItem entry in remove)
            {
                Logger.Debug($"Removing redundant 'Compile' entry for {addedFile} ({(buildProject.IsSdkStyle ? "SDK-style: implicit glob covers it" : "legacy: duplicate")})");
                buildProject.RemoveItem(entry);
            }
        }

        public virtual bool ValidateCommandLine()
        {
            Logger.Debug("Profile does not implement ValidateCommandLine function");
            return true;
        }
    }
}
