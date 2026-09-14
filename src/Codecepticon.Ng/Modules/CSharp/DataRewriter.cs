using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using Codecepticon.Modules.CSharp.Rewriters;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis.Text;

namespace Codecepticon.Modules.CSharp
{
    class DataRewriter
    {
        protected SyntaxTreeHelper Helper = new SyntaxTreeHelper();

        /// <summary>
        /// Empties the bodies of the methods named by --strip-method. Generic:
        /// no profile required, works on any solution.
        /// </summary>
        public async Task<Solution> StripMethods(Solution solution, string projectName, string documentName, List<string> stripped, List<string> skipped)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);
            if (document == null)
            {
                return solution;
            }

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            StripMethods rewriter = new StripMethods(CommandLineData.Global.StripMethods);
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            stripped.AddRange(rewriter.Stripped);
            skipped.AddRange(rewriter.Skipped);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RemoveComments(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            RemoveComments rewriter = new RemoveComments();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteSwitchStatements(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            SwitchStatements switchRewriter = new SwitchStatements();
            SyntaxNode newSyntaxRoot = switchRewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteStrings(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            Strings rewriter = new Strings();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            // Add required using statements.
            newSyntaxRoot = Helper.AddUsingStatement(newSyntaxRoot, "System");
            newSyntaxRoot = Helper.AddUsingStatement(newSyntaxRoot, "System.Linq");

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> RewriteAssemblies(Solution solution, string projectName, string documentName)
        {
            Document document = VisualStudioManager.GetDocumentByName(solution, projectName, documentName);

            SyntaxNode syntaxRoot = await document.GetSyntaxRootAsync();
            Assemblies rewriter = new Assemblies();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            return solution.WithDocumentSyntaxRoot(document.Id, newSyntaxRoot);
        }

        public async Task<Solution> AddStringHelperClass(Solution solution, Project project)
        {
            string code = File.ReadAllText(CommandLineData.Global.Rewrite.Template.File);
            string mapping;

            // Find all variables that look like $_NAME_%
            Regex regex = new Regex(@"(%_[A-Za-z0-9_]+_%)");
            var matches = regex.Matches(code).Cast<Match>().Select(m => m.Value).ToArray().Distinct();

            // And now replace them all. We only need to keep track of the Namespace, Class, and Function names.
            foreach (var match in matches)
            {
                string name = CommandLineData.Global.NameGenerator.Generate();
                switch (match.ToLower())
                {
                    case "%_namespace_%":
                        Logger.Debug($"StringHelperClass - Replace %_namespace_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Namespace = name;
                        break;
                    case "%_class_%":
                        Logger.Debug($"StringHelperClass - Replace %_class_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Class = name;
                        break;
                    case "%_function_%":
                        Logger.Debug($"StringHelperClass - Replace %_function_% with {name}");
                        CommandLineData.Global.Rewrite.Template.Function = name;
                        break;
                }

                code = code.Replace(match, name);
            }

            switch (CommandLineData.Global.Rewrite.EncodingMethod)
            {
                case StringEncoding.StringEncodingMethods.SingleCharacterSubstitution:
                    mapping = StringEncoding.ExportSingleCharacterMap(CommandLineData.Global.Rewrite.SingleMapping, ModuleTypes.CodecepticonModules.CSharp);
                    code = code.Replace("%MAPPING%", mapping);
                    break;
                case StringEncoding.StringEncodingMethods.GroupCharacterSubstitution:
                    mapping = StringEncoding.ExportGroupCharacterMap(CommandLineData.Global.Rewrite.GroupMapping, ModuleTypes.CodecepticonModules.CSharp);
                    code = code.Replace("%MAPPING%", mapping);
                    break;
                case StringEncoding.StringEncodingMethods.ExternalFile:
                    code = code.Replace("%MAPPING%", Path.GetFileName(CommandLineData.Global.Rewrite.ExternalFile));
                    break;
            }

            SourceText source = SourceText.From(code);
            CommandLineData.Global.Rewrite.Template.AddedFile = GenerateStringFileName(project);
            Logger.Debug($"File added into project for strings: {CommandLineData.Global.Rewrite.Template.AddedFile}");
            Document document = project.AddDocument(CommandLineData.Global.Rewrite.Template.AddedFile, source);
            return solution.AddDocument(document.Id, CommandLineData.Global.Rewrite.Template.AddedFile, source);
        }

        /// <summary>
        /// Invents a filename for the decoder that blends in with the project's
        /// own, by concatenating two existing names.
        ///
        /// The name pool is USER documents only. project.Documents also contains
        /// what the SDK generates into obj/ - AssemblyInfo.cs, GlobalUsings.g.cs
        /// and the AssemblyAttributes file - and drawing on those produced names
        /// like "DemoTool.AssemblyInfo.NETCoreApp,Version=v10.0.AssemblyAttributes.cs":
        /// a decoder masquerading as a compiler-generated file, colliding with
        /// the real one the SDK writes on the next build. Generated names are
        /// also the opposite of camouflage - they stand out in a project whose
        /// files are otherwise hand-named.
        /// </summary>
        protected string GenerateStringFileName(Project project)
        {
            List<string> allFilenames = project.Documents
                .Where(IsUserDocument)
                .Select(document => Path.GetFileNameWithoutExtension(document.Name))
                .Where(name => !String.IsNullOrEmpty(name))
                .Distinct()
                .ToList();

            // A project with no hand-written files is pathological, but the loop
            // below indexes into this list - so give it something to work with
            // rather than throwing.
            if (allFilenames.Count == 0)
            {
                allFilenames.Add(String.IsNullOrEmpty(project.Name) ? "Shared" : project.Name);
            }

            // Randomly combine 2 names at a time until we get a unique name.
            Random rnd = new Random();
            string fileName;
            do
            {
                fileName = allFilenames[rnd.Next(allFilenames.Count)] + allFilenames[rnd.Next(allFilenames.Count)];
                if (allFilenames.Contains(fileName))
                {
                    fileName = "";
                }
            } while (fileName == "");

            return fileName + ".cs";
        }

        /// <summary>
        /// A document the user actually wrote, as opposed to one the build
        /// generated into obj/ and will regenerate on the next build.
        /// </summary>
        protected static bool IsUserDocument(Document document)
        {
            if (String.IsNullOrEmpty(document.FilePath))
            {
                return false;
            }

            string path = document.FilePath.Replace('\\', '/');
            if (path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
                || path.Contains("/bin/", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string name = Path.GetFileName(path);
            return !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".AssemblyInfo.cs", StringComparison.OrdinalIgnoreCase)
                && !name.EndsWith(".AssemblyAttributes.cs", StringComparison.OrdinalIgnoreCase);
        }
    }
}
