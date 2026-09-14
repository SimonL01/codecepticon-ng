using System;
using System.Collections.Generic;
using System.Linq;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Codecepticon.Modules.CSharp.Rewriters
{
    /// <summary>
    /// Empties the body of named methods - the generic form of what the tool
    /// profiles do to `printlogo` and `usage`.
    ///
    /// Why this exists as a flag: string encoding hides a banner from `strings`
    /// and ILSpy, but the program still PRINTS it at run time, which on an
    /// offensive tool is the giveaway that matters. Removing the code is the only
    /// thing that stops that, and until now the only way to remove it was to be
    /// one of the seven tools with a profile. Any solution can now say
    /// `--strip-method printlogo,usage`.
    ///
    /// Runs before renaming, so the names given are the ones in the source.
    /// </summary>
    class StripMethods : CSharpSyntaxRewriter
    {
        private readonly HashSet<string> _names;

        /// <summary>Methods actually emptied, for the caller to report.</summary>
        public List<string> Stripped { get; } = new List<string>();

        /// <summary>
        /// Methods that matched by name but were left alone because emptying them
        /// would not compile.
        /// </summary>
        public List<string> Skipped { get; } = new List<string>();

        public StripMethods(IEnumerable<string> names)
        {
            _names = new HashSet<string>(names ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        }

        public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            if (!_names.Contains(node.Identifier.ValueText))
            {
                return base.VisitMethodDeclaration(node);
            }

            // Only a method that returns nothing can have its body removed and
            // still compile. Anything else needs a return value, and inventing
            // one would change behaviour in a way the caller did not ask for -
            // so say so and leave it, rather than producing a project that does
            // not build. --verify would catch it; a warning is cheaper.
            if (!ReturnsNothing(node))
            {
                Skipped.Add($"{node.Identifier.ValueText} (returns {node.ReturnType})");
                return base.VisitMethodDeclaration(node);
            }

            // An expression body (=> Foo()) has no block to empty; give it one.
            Stripped.Add(node.Identifier.ValueText);
            return node
                .WithExpressionBody(null)
                .WithSemicolonToken(default)
                .WithBody(SyntaxFactory.Block());
        }

        /// <summary>
        /// void, or an awaitable with no result - Task and ValueTask. A
        /// Task-returning method with an empty body still compiles: async ones
        /// warn CS1998, non-async ones fail, so only `async` qualifies.
        /// </summary>
        private static bool ReturnsNothing(MethodDeclarationSyntax node)
        {
            if (node.ReturnType is PredefinedTypeSyntax predefined && predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
            {
                return true;
            }

            if (!node.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)))
            {
                return false;
            }

            string returnType = node.ReturnType.ToString();
            return returnType == "Task"
                || returnType == "ValueTask"
                || returnType.EndsWith(".Task", StringComparison.Ordinal)
                || returnType.EndsWith(".ValueTask", StringComparison.Ordinal);
        }
    }
}
