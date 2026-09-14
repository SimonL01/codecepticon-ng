using Codecepticon.CommandLine;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Codecepticon.Modules.CSharp
{
    class CSharpManager : ModuleManager
    {
        public async Task Run()
        {
            // https://stackoverflow.com/a/57157323
            string roslynVersion = Assembly.GetAssembly(typeof(Solution)).GetName().Version.ToString();
            Logger.Debug("Identified Roslyn version: " + roslynVersion);

            // Upstream refused to run on anything but Roslyn 3.9.0.0 here, because
            // dotnet/roslyn#58463 - "RenameSymbolAsync does not rename all instances"
            // - broke renaming from 3.10 onwards whenever a `using` alias was in
            // play. It prompted Y/N on stdin to continue.
            //
            // That issue was CLOSED 2025-06-28, and the fix is verified present
            // here: on 2026-08-21 the issue's own repro (an aliased type used
            // across two files) was run against Roslyn 5.9.0 and renamed all six
            // occurrences with zero misses. The guard's premise no longer holds.
            //
            // It also could not stay as-is: Console.ReadLine() returns null when
            // stdin is not a console, so the prompt threw NullReferenceException
            // under any non-interactive caller - a CI pipeline, precisely the use
            // this project is being built for.
            //
            // The version is still reported; VisualStudioManager.ReportToolchain()
            // prints it alongside the resolved build tool.

            switch (CommandLineData.Global.Action)
            {
                case CommandLineData.Action.Obfuscate:
                    await Obfuscate();
                    break;
                case CommandLineData.Action.Unmap:
                    await Unmap();
                    break;
            }
        }

        public static async Task Obfuscate()
        {
            Dictionary<string, string> workspaceProperties = new Dictionary<string, string>
            {
                { "Configuration", "Release" }
            };

            using (var workspace = VisualStudioManager.GetWorkspace(workspaceProperties))
            {
                Logger.Info($"Loading solution: {CommandLineData.Global.Project.Path}");
                workspace.WorkspaceFailed += (o, e) => Logger.Debug(e.Diagnostic.Message);
                Solution solution = await VisualStudioManager.OpenSolutionAsync(workspace, CommandLineData.Global.Project.Path);
                if (solution == null)
                {
                    return;
                }
                Logger.Verbose("Finished loading solution");
                Logger.Verbose("");

                // If a build is going to be attempted at all, establish that it
                // CAN succeed before rewriting anything. The rewrite is in place;
                // discovering a missing targeting pack afterwards leaves the
                // caller with obfuscated source and no way to tell whose fault
                // it was.
                if (CommandLineData.CSharp.Compilation.Build || CommandLineData.CSharp.Compilation.Precompile || CommandLineData.CSharp.Compilation.Verify)
                {
                    if (!BuildPreflight.Check(solution))
                    {
                        return;
                    }
                }

                if (CommandLineData.CSharp.Compilation.Precompile)
                {
                    Logger.Info("Pre-compiling the original project to check if it is successful...", false);
                    if (!VisualStudioManager.Build(solution, new Dictionary<string, string> { { "Configuration", "Release" } } ))
                    {
                        if (!Logger.IsDebug)
                        {
                            Logger.Error("ERROR", true, false);
                        }
                        Logger.Error("Could not compile original project in 'Release' mode, make sure there are no errors and try again - or run Codecepticon with --debug.");
                        return;
                    }
                    Logger.Info("OK", true, false);
                }

                // NEVER prompt on stdin. This used to be a Y/N loop on
                // Console.ReadLine(), which returns null when stdin is not a
                // console - so .ToLower() threw NullReferenceException under any
                // non-interactive caller. That is the exact defect increment 2
                // removed from the Roslyn-version guard thirty lines above, and
                // it survived here. An opt-in flag says the same thing without
                // requiring a human to be watching.
                if (solution.Projects.Count() > 1 && !CommandLineData.CSharp.AllowMultiProject)
                {
                    Logger.Error($"This solution has {solution.Projects.Count()} projects, and Codecepticon is built for single-project solutions:");
                    foreach (Project p in solution.Projects)
                    {
                        Logger.Error($"  {p.Name} ({p.FilePath})");
                    }
                    Logger.Error("");
                    Logger.Error("Renaming across projects is not verified: a symbol renamed in one project");
                    Logger.Error("and referenced from another can be missed, and the result still compiles");
                    Logger.Error("only by luck. Point --path at a single project, or pass");
                    Logger.Error("--allow-multi-project to proceed anyway.");
                    return;
                }

                if (solution.Projects.Count() > 1)
                {
                    Logger.Warning($"Proceeding across {solution.Projects.Count()} projects because --allow-multi-project was passed. Here be dragons.");
                }

                // Last gate before anything is written. A backup that fails is
                // fatal: proceeding would destroy the only copy.
                if (CommandLineData.CSharp.Backup)
                {
                    if (SourceBackup.Create(solution, CommandLineData.CSharp.BackupPath) == null)
                    {
                        return;
                    }
                }

                foreach (Project project in solution.Projects)
                {
                    Logger.Info($"Processing project {project.FilePath}");
                    await GatherProjectData(solution, project);
                    await DataCollector.FilterCollectedData();

                    Logger.Verbose("");
                    Logger.Verbose("Elements Found:");
                    Logger.Verbose($"\tNamespaces:\t{DataCollector.AllNamespaces.Count}");
                    Logger.Verbose($"\tClasses:\t{DataCollector.AllClasses.Count}");
                    Logger.Verbose($"\tEnums:\t\t{DataCollector.AllEnums.Count}");
                    Logger.Verbose($"\tFunctions:\t{DataCollector.AllFunctions.Count}");
                    Logger.Verbose($"\tProperties:\t{DataCollector.AllProperties.Count}");
                    Logger.Verbose($"\tParameters:\t{DataCollector.AllParameters.Count}");
                    Logger.Verbose($"\tVariables:\t{DataCollector.AllVariables.Count}");
                    Logger.Verbose($"\tStructs:\t{DataCollector.AllStructs.Count}");

                    Logger.Info("Generating mappings...");
                    if (await GenerateMappings() == false)
                    {
                        Logger.Error("Could not generate mappings.");
                        return;
                    }

                    // After the mapping exists and before anything is written:
                    // this is the only point where we know both what the names
                    // are and what they will become.
                    if (!CommandLineData.CSharp.SkipReflectionScan)
                    {
                        await ReflectionScanner.Scan(project);
                    }

                    Logger.Info("Rewriting code...");
                    solution = await RewriteCode(solution, project);

                    // A profile that could not find the files it needs has
                    // already said so. Stop here rather than applying a
                    // half-profiled rewrite: nothing is on disk yet, because
                    // TryApplyChanges runs after this loop.
                    if (CommandLineData.CSharp.Profile != null && CommandLineData.CSharp.Profile.Failed)
                    {
                        Logger.Error("Aborting - nothing has been modified.");
                        return;
                    }
                }

                VisualStudioManager.SetProjectConfiguration(solution, new Dictionary<string, string> { { "Configuration", CommandLineData.CSharp.Compilation.Configuration } });
                VisualStudioManager.SetProjectConfiguration(solution, CommandLineData.CSharp.Compilation.Settings);

                Logger.Info("Applying changes to solution...");
                workspace.TryApplyChanges(solution);

                // At this point, when everything has been applied, we can do any final project-wide updates.
                if (CommandLineData.CSharp.Profile != null)
                {
                    Logger.Info("Running profile-specific final actions...", false);
                    foreach (Project project in solution.Projects)
                    {
                        solution = await CommandLineData.CSharp.Profile.Final(solution, project);
                    }
                    Logger.Info("", true, false);
                    Logger.Info("Applying changes (again) to solution...");
                    workspace.TryApplyChanges(solution);
                }
                
                // --verify implies a build: the only way to know the rewrite left
                // something compilable is to compile it.
                if (CommandLineData.CSharp.Compilation.Build || CommandLineData.CSharp.Compilation.Verify)
                {
                    Logger.Info(CommandLineData.CSharp.Compilation.Verify ? "Verifying the obfuscated solution still builds..." : "Building solution...", false);
                    if (!VisualStudioManager.Build(solution))
                    {
                        Logger.Error("ERROR", true, false);
                        if (CommandLineData.CSharp.Compilation.Verify)
                        {
                            // The distinction matters: the source on disk is
                            // obfuscated and does NOT compile. Saying so plainly
                            // is the whole point of --verify.
                            Logger.Error("VERIFY FAILED - the obfuscated code does not compile.");
                            Logger.Error("The source on disk has been rewritten and is currently broken.");
                            Logger.Error("Restore it, then narrow the scope (--rename cfv rather than all) or");
                            Logger.Error("check the mapping file for a symbol reached by reflection.");
                            RunOutcome.ObfuscatedButBuildFailed = true;
                        }
                        else
                        {
                            Logger.Error("The codebase has been obfuscated, but there was an error while building the solution.");
                            RunOutcome.ObfuscatedButBuildFailed = true;
                        }
                    }
                    else
                    {
                        Logger.Info("OK", true, false);
                        if (CommandLineData.CSharp.Compilation.Verify)
                        {
                            Logger.Verbose("Verified: the obfuscated solution compiles.");
                        }
                    }
                }
            }

            Logger.Info("Generating mapping file to: " + CommandLineData.Global.Unmap.MapFile);
            Unmapping.GenerateMapFile(CommandLineData.Global.Unmap.MapFile);

            if (CommandLineData.Global.Rewrite.EncodingMethod == StringEncoding.StringEncodingMethods.ExternalFile)
            {
                Logger.Warning($"Make sure you place {CommandLineData.Global.Rewrite.ExternalFile} somewhere where your obfsucated target can find it!");
            }

            Logger.Success("Obfuscation complete");
        }

        public static async Task Unmap()
        {
            Unmapping unmapping = new Unmapping();
            unmapping.Run(CommandLineData.Global.Unmap.MapFile, CommandLineData.Global.Unmap.File, CommandLineData.Global.Unmap.Directory, CommandLineData.Global.Unmap.Recursive);
        }

        public static async Task GatherProjectData(Solution solution, Project project)
        {
            DataCollector.Mapping.CommandLine = new Dictionary<string, DataCollector.CommandLine>();

            foreach (Document document in project.Documents)
            {
                Logger.Debug($"Gathering data for {document.FilePath}");

                await DataCollector.CollectNamespaces(solution, project.Name, document.Name);
                await DataCollector.CollectClasses(solution, project.Name, document.Name);
                await DataCollector.CollectEnums(solution, project.Name, document.Name);
                await DataCollector.CollectFunctions(solution, project.Name, document.Name);
                await DataCollector.CollectProperties(solution, project.Name, document.Name);
                await DataCollector.CollectVariables(solution, project.Name, document.Name);
                await DataCollector.CollectParameters(solution, project.Name, document.Name);
                await DataCollector.CollectStructs(solution, project.Name, document.Name);
            }
        }

        public static async Task<bool> GenerateMappings()
        {
            if (CommandLineData.CSharp.Rename.Enabled)
            {
                CommandLineData.Global.NameGenerator = new NameGenerator(
                    CommandLineData.Global.RenameGenerator.Method,
                    CommandLineData.Global.RenameGenerator.Data.CharacterSet.Value,
                    CommandLineData.Global.RenameGenerator.Data.CharacterSet.Length,
                    CommandLineData.Global.RenameGenerator.Data.Dictionary.Words
                );
            }
            else
            {
                CommandLineData.Global.NameGenerator = new NameGenerator(
                    NameGenerator.RandomNameGeneratorMethods.RandomCombinations,
                    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz",
                    0
                );
            }

            string newName;

            if (CommandLineData.CSharp.Rename.Namespaces)
            {
                Logger.Verbose("Creating mappings for namespaces");
                foreach (string name in DataCollector.AllNamespaces)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Namespaces.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Classes)
            {
                Logger.Verbose("Creating mappings for classes");
                foreach (string name in DataCollector.AllClasses)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Classes.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Functions)
            {
                Logger.Verbose("Creating mappings for functions");
                foreach (string name in DataCollector.AllFunctions)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Functions.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Enums)
            {
                Logger.Verbose("Creating mappings for enums");
                foreach (string name in DataCollector.AllEnums)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Enums.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Properties)
            {
                Logger.Verbose("Creating mappings for properties");
                foreach (string name in DataCollector.AllProperties)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Properties.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Variables)
            {
                Logger.Verbose("Creating mappings for variables");
                foreach (string name in DataCollector.AllVariables)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Variables.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Parameters)
            {
                Logger.Verbose("Creating mappings for parameters");
                foreach (string name in DataCollector.AllParameters)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Parameters.Add(name, newName);
                }
            }

            if (CommandLineData.CSharp.Rename.Structs)
            {
                Logger.Verbose("Creating mappings for structs");
                foreach (string name in DataCollector.AllStructs)
                {
                    if (ShouldKeep(name))
                    {
                        continue;
                    }

                    newName = await GenerateName(CommandLineData.Global.NameGenerator, DataCollector.IsMappingUnique);
                    if (newName.Length == 0)
                    {
                        return false;
                    }

                    DataCollector.Mapping.Structs.Add(name, newName);
                }
            }

            return true;
        }

        public static async Task<Solution> RewriteCode(Solution solution, Project project)
        {
            DataRenamer dataRenamer = new DataRenamer();
            DataRewriter dataRewriter = new DataRewriter();
            int step = 10;

            if (CommandLineData.CSharp.Profile != null)
            {
                Logger.Info("Selected profile is: " + CommandLineData.CSharp.Profile.Name);
                Logger.Info("Running profile-specific pre-process actions...", false);
                solution = await CommandLineData.CSharp.Profile.Before(solution, project);
                Logger.Info("", true, false);

                // Bail here rather than after a full rename pass that is about
                // to be thrown away - and so the mismatch is reported once,
                // adjacent to the abort, instead of scrolling off the top.
                if (CommandLineData.CSharp.Profile.Failed)
                {
                    return solution;
                }
            }

            Logger.Info("Rewriting assemblies...", CommandLineData.Global.Project.Debug);
            int c = 0;
            foreach (Document document in project.Documents)
            {
                if (CommandLineData.Global.Project.Debug)
                {
                    Logger.Debug($"Rewriting assemblies in document {document.FilePath}");
                } else if (++c % step == 0)
                {
                    Logger.Verbose(".", false, false);
                }
                solution = await dataRewriter.RewriteAssemblies(solution, project.Name, document.Name);
            }
            if (!CommandLineData.Global.Project.Debug)
            {
                Logger.Info("", true, false);
            }

            // Before renaming, so --strip-method names the methods as they appear
            // in the source rather than as markov output.
            if (CommandLineData.Global.StripMethods != null && CommandLineData.Global.StripMethods.Count > 0)
            {
                Logger.Info("Stripping method bodies...", false);
                List<string> stripped = new List<string>();
                List<string> skipped = new List<string>();

                foreach (Document document in project.Documents)
                {
                    solution = await dataRewriter.StripMethods(solution, project.Name, document.Name, stripped, skipped);
                }

                Logger.Info("", true, false);
                foreach (string name in skipped.Distinct())
                {
                    Logger.Warning($"  Not stripped, would not compile: {name}");
                }

                if (stripped.Count == 0)
                {
                    Logger.Warning($"  --strip-method matched nothing. Looked for: {String.Join(", ", CommandLineData.Global.StripMethods)}");
                }
                else
                {
                    Logger.Verbose($"  Emptied: {String.Join(", ", stripped.Distinct())}");
                }

                project = VisualStudioManager.GetProjectByName(solution, project.Name);
            }

            Logger.Info("Removing comments...", CommandLineData.Global.Project.Debug);
            c = 0;
            foreach (Document document in project.Documents)
            {
                if (CommandLineData.Global.Project.Debug)
                {
                    Logger.Debug($"Removing comments in document {document.FilePath}");
                }
                else if (++c % step == 0)
                {
                    Logger.Verbose(".", false, false);
                }
                solution = await dataRewriter.RemoveComments(solution, project.Name, document.Name);
            }
            if (!CommandLineData.Global.Project.Debug)
            {
                Logger.Info("", true, false);
            }

            if (CommandLineData.Global.Rewrite.Strings)
            {
                Logger.Info("Rewriting switch statements...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Rewriting switch statements in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRewriter.RewriteSwitchStatements(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }

                Logger.Info("Rewriting strings...", CommandLineData.Global.Project.Debug);
                switch (CommandLineData.Global.Rewrite.EncodingMethod)
                {
                    case StringEncoding.StringEncodingMethods.XorEncrypt:
                    case StringEncoding.StringEncodingMethods.SingleCharacterSubstitution:
                    case StringEncoding.StringEncodingMethods.GroupCharacterSubstitution:
                    case StringEncoding.StringEncodingMethods.ExternalFile:
                        solution = await dataRewriter.AddStringHelperClass(solution, project);
                        break;
                }

                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Rewriting strings in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRewriter.RewriteStrings(solution, project.Name, document.Name);
                }

                // Now save the mapping to file(for some methods).
                switch (CommandLineData.Global.Rewrite.EncodingMethod)
                {
                    case StringEncoding.StringEncodingMethods.ExternalFile:
                        StringEncoding.SaveExportExternalFileMapping(CommandLineData.Global.Rewrite.ExternalFile);
                        break;
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Namespaces)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Namespaces.Count} namespaces...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming namespaces in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameNamespaces(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Classes)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Classes.Count} classes...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming classes in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameClasses(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Enums)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Enums.Count} enums...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming enums in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameEnums(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Functions)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Functions.Count} functions...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming functions in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameFunctions(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Properties)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Properties.Count} properties...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming properties in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameProperties(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Parameters)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Parameters.Count} parameters...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming parameters in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameParameters(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Variables)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Variables.Count} variables...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming variables in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameVariables(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            if (CommandLineData.CSharp.Rename.Structs)
            {
                Logger.Info($"Renaming {DataCollector.Mapping.Structs.Count} structs...", CommandLineData.Global.Project.Debug);
                c = 0;
                foreach (Document document in project.Documents)
                {
                    if (CommandLineData.Global.Project.Debug)
                    {
                        Logger.Debug($"Renaming structs in document {document.FilePath}");
                    }
                    else if (++c % step == 0)
                    {
                        Logger.Verbose(".", false, false);
                    }
                    solution = await dataRenamer.RenameStructs(solution, project.Name, document.Name);
                }
                if (!CommandLineData.Global.Project.Debug)
                {
                    Logger.Info("", true, false);
                }
            }

            Logger.Debug($"Setting ProjectGuid to {{{CommandLineData.Global.Project.Guid.ToString().ToUpper()}}}");
            VisualStudioManager.SetProjectConfiguration(solution, new Dictionary<string, string>
            {
                { "ProjectGuid", $"{{{CommandLineData.Global.Project.Guid.ToString().ToUpper()}}}" },
                { "ApplicationIcon", "" },
            });

            if (CommandLineData.CSharp.Profile != null)
            {
                Logger.Info("Running profile-specific post-process actions...", false);
                solution = await CommandLineData.CSharp.Profile.After(solution, project);
                Logger.Info("", true, false);
            }

            return solution;
        }
    }
}
