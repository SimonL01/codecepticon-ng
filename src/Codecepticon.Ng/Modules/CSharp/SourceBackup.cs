using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Codecepticon.CommandLine;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Copies the source aside before the first rewrite, so there is an undo.
    ///
    /// The C# module rewrites in place. Until now the only way back was
    /// `git checkout`, which assumes the target is a clean git checkout - fine
    /// for a repo, useless for a tarball dropped on a jump box, and useless
    /// again if the tree had uncommitted work in it.
    ///
    /// What is copied is what Roslyn says belongs to the projects, plus the
    /// project files themselves (which are also rewritten). Not a whole-directory
    /// copy: a solution directory can carry gigabytes of bin/, obj/ and packages,
    /// and a backup nobody can afford to take is a backup nobody takes.
    /// </summary>
    static class SourceBackup
    {
        /// <summary>
        /// Returns the backup directory, or null if it could not be created -
        /// which callers must treat as fatal, since proceeding would be
        /// destroying the only copy.
        /// </summary>
        public static string Create(Solution solution, string requestedPath)
        {
            string root = String.IsNullOrWhiteSpace(requestedPath)
                ? DefaultPath()
                : Path.GetFullPath(AppPaths.Normalise(requestedPath));

            try
            {
                if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
                {
                    Logger.Error($"Backup directory already exists and is not empty: {root}");
                    Logger.Error("Refusing to write into it - move it aside or pass --backup-path elsewhere.");
                    return null;
                }

                Directory.CreateDirectory(root);
            }
            catch (Exception e)
            {
                Logger.Error($"Could not create backup directory {root}: {e.Message}");
                return null;
            }

            // The common root of everything being backed up, so the copy keeps
            // the original layout instead of flattening it into one directory.
            List<string> files = FilesToBackup(solution).ToList();
            if (files.Count == 0)
            {
                Logger.Error("Nothing to back up - the solution reported no source files.");
                return null;
            }

            string baseDirectory = CommonDirectory(files);
            int copied = 0;

            foreach (string file in files)
            {
                try
                {
                    string relative = Path.GetRelativePath(baseDirectory, file);
                    string destination = Path.Combine(root, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    File.Copy(file, destination, true);
                    copied++;
                }
                catch (Exception e)
                {
                    Logger.Error($"Could not back up {file}: {e.Message}");
                    return null;
                }
            }

            Logger.Info($"Backed up {copied} files to: {root}");
            return root;
        }

        private static string DefaultPath()
        {
            string next = Path.GetDirectoryName(Path.GetFullPath(CommandLineData.Global.Project.Path));
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            return Path.Combine(next ?? ".", $"codecepticon-backup-{stamp}");
        }

        /// <summary>
        /// Every document Roslyn attributes to a project, plus the project files.
        /// Anything under obj/ or bin/ is build output and regenerates.
        /// </summary>
        private static IEnumerable<string> FilesToBackup(Solution solution)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Project project in solution.Projects)
            {
                if (!String.IsNullOrEmpty(project.FilePath) && File.Exists(project.FilePath) && seen.Add(project.FilePath))
                {
                    yield return project.FilePath;
                }

                foreach (Document document in project.Documents)
                {
                    if (String.IsNullOrEmpty(document.FilePath) || !File.Exists(document.FilePath))
                    {
                        continue;
                    }

                    string normalised = document.FilePath.Replace('\\', '/');
                    if (normalised.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                        || normalised.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (seen.Add(document.FilePath))
                    {
                        yield return document.FilePath;
                    }
                }
            }
        }

        /// <summary>
        /// Deepest directory containing all the given files, so relative paths
        /// in the backup mirror the original tree.
        /// </summary>
        private static string CommonDirectory(List<string> files)
        {
            string[] first = Path.GetDirectoryName(files[0]).Split(Path.DirectorySeparatorChar);
            int shared = first.Length;

            foreach (string file in files.Skip(1))
            {
                string[] parts = Path.GetDirectoryName(file).Split(Path.DirectorySeparatorChar);
                int limit = Math.Min(shared, parts.Length);
                int i = 0;
                while (i < limit && String.Equals(first[i], parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    i++;
                }
                shared = i;
            }

            string root = String.Join(Path.DirectorySeparatorChar.ToString(), first.Take(shared));
            return String.IsNullOrEmpty(root) ? Path.DirectorySeparatorChar.ToString() : root;
        }
    }
}
