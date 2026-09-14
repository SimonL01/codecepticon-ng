using Microsoft.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using System.IO;
using Codecepticon.Modules.CSharp.Profiles.Seatbelt.Rewriters;
using Codecepticon.Utils;

namespace Codecepticon.Modules.CSharp.Profiles.Seatbelt
{
    class Seatbelt : BaseProfile
    {
        public override string Name { get; } = "Seatbelt";
        public override async Task<Solution> Before(Solution solution, Project project)
        {
            if (CommandLineData.CSharp.Rename.CommandLine)
            {
                Logger.Debug("Seatbelt: Rewriting command line");
                solution = await RewriteCommandLine(solution, project);
            }

            Logger.Debug("Seatbelt: Removing help text");
            solution = await RemoveHelpText(solution, project);
            return solution;
        }

        protected async Task<Solution> RewriteCommandLine(Solution solution, Project project)
        {
            Rewriters.CommandLine rewriteCommandLine = new Rewriters.CommandLine();

            project = VisualStudioManager.GetProjectByName(solution, project.Name);
            SyntaxNode syntaxRoot;
            foreach (Document document in project.Documents)
            {
                syntaxRoot = await document.GetSyntaxRootAsync();

                // Under, not in: Seatbelt nests its commands (Commands/Windows,
                // Commands/Browser, ...).
                if (IsUnderDirectory(document, "Commands"))
                {
                    syntaxRoot = rewriteCommandLine.Visit(syntaxRoot);
                }
                else if (document.Name == "SeatbeltArgumentParser.cs")
                {
                    syntaxRoot = rewriteCommandLine.Visit(syntaxRoot);
                }

                solution = solution.WithDocumentSyntaxRoot(document.Id, syntaxRoot);
            }

            // Now we need to update the final references to the "all" command argument.
            Document csRuntime = RequireDocument(project, "Runtime.cs");
            if (csRuntime == null)
            {
                return solution;
            }

            syntaxRoot = await csRuntime.GetSyntaxRootAsync();
            CommandLine2 rewriteCommandLine2 = new CommandLine2();
            syntaxRoot = rewriteCommandLine2.Visit(syntaxRoot);
            solution = solution.WithDocumentSyntaxRoot(csRuntime.Id, syntaxRoot);

            return solution;
        }

        protected async Task<Solution> RemoveHelpText(Solution solution, Project project)
        {
            project = VisualStudioManager.GetProjectByName(solution, project.Name);
            Document csInfo = RequireDocument(project, "Seatbelt.cs");
            if (csInfo == null)
            {
                return solution;
            }


            SyntaxNode syntaxRoot = await csInfo.GetSyntaxRootAsync();
            RemoveHelpText rewriter = new RemoveHelpText();
            SyntaxNode newSyntaxRoot = rewriter.Visit(syntaxRoot);

            solution = solution.WithDocumentSyntaxRoot(csInfo.Id, newSyntaxRoot);

            return solution;
        }
    }
}
