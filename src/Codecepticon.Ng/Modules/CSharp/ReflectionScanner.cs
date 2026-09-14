using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Finds names that are reached by REFLECTION rather than by the compiler,
    /// and would therefore be renamed on one side only.
    ///
    /// This is the one failure mode semantic renaming cannot see. Everything
    /// else this tool rewrites goes through Roslyn's symbol model, so it is
    /// correct by construction; a name that only exists as a string -
    /// Type.GetType("Payload"), GetMethod("Run") - is invisible to it. The
    /// declaration gets renamed, the string does not, and the result COMPILES
    /// and then fails at run time. On an offensive tool that means a payload
    /// that dies on the target rather than on the build agent, which is the
    /// worst possible place to find out.
    ///
    /// Deliberately a warning, not a refusal: a string that happens to match a
    /// class name is not proof of reflection, and refusing on a guess would make
    /// the tool unusable on any codebase with descriptive string constants. The
    /// caller gets file, line, and what it collides with, and decides.
    /// </summary>
    static class ReflectionScanner
    {
        /// <summary>
        /// Method names whose string arguments are resolved by name at run time.
        /// Matched on the method name alone: the receiver is usually a Type or
        /// Assembly obtained several steps earlier, and resolving that properly
        /// costs more than the check is worth.
        /// </summary>
        private static readonly HashSet<string> ReflectionMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetType",
            "GetMethod",
            "GetMethods",
            "GetProperty",
            "GetProperties",
            "GetField",
            "GetFields",
            "GetMember",
            "GetMembers",
            "GetConstructor",
            "GetNestedType",
            "GetInterface",
            "CreateInstance",
            "CreateInstanceFrom",
            "InvokeMember",
            "GetEnumName",
        };

        /// <summary>
        /// Reports every literal that collides with a name about to change.
        /// Returns the number of hits, so the caller can summarise.
        /// </summary>
        public static async Task<int> Scan(Project project)
        {
            Dictionary<string, string> renames = AllRenames();
            if (renames.Count == 0)
            {
                return 0;
            }

            var hits = new List<string>();

            foreach (Document document in project.Documents)
            {
                if (string.IsNullOrEmpty(document.FilePath))
                {
                    continue;
                }

                SyntaxNode root = await document.GetSyntaxRootAsync();
                if (root == null)
                {
                    continue;
                }

                foreach (InvocationExpressionSyntax invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string method = MethodName(invocation);
                    if (method == null || !ReflectionMethods.Contains(method))
                    {
                        continue;
                    }

                    foreach (LiteralExpressionSyntax literal in invocation.ArgumentList.Arguments
                                 .Select(a => a.Expression)
                                 .OfType<LiteralExpressionSyntax>()
                                 .Where(l => l.IsKind(SyntaxKind.StringLiteralExpression)))
                    {
                        string value = literal.Token.ValueText;
                        foreach (string name in CollidingNames(value, renames))
                        {
                            FileLinePositionSpan position = literal.GetLocation().GetLineSpan();
                            hits.Add($"{position.Path}({position.StartLinePosition.Line + 1}) {method}(\"{value}\") -> '{name}' is being renamed to '{renames[name]}'");
                        }
                    }
                }
            }

            if (hits.Count == 0)
            {
                Logger.Verbose("Reflection scan: no string literals collide with renamed symbols.");
                return 0;
            }

            Logger.Warning("");
            Logger.Warning($"REFLECTION WARNING - {hits.Count} string literal(s) name a symbol that is being renamed:");
            foreach (string hit in hits.Distinct().Take(40))
            {
                Logger.Warning($"  {hit}");
            }
            if (hits.Distinct().Count() > 40)
            {
                Logger.Warning($"  ... and {hits.Distinct().Count() - 40} more");
            }
            Logger.Warning("");
            Logger.Warning("  These compile either way. They break at RUN time, if the lookup was real.");
            Logger.Warning("  Exclude the symbol, narrow --rename, or confirm the string is unrelated.");
            Logger.Warning("");

            return hits.Distinct().Count();
        }

        /// <summary>
        /// The identifier being invoked: Foo() -> "Foo", x.Foo() -> "Foo",
        /// x.Foo&lt;T&gt;() -> "Foo".
        /// </summary>
        private static string MethodName(InvocationExpressionSyntax invocation)
        {
            switch (invocation.Expression)
            {
                case MemberAccessExpressionSyntax member:
                    return NameOf(member.Name);
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.ValueText;
                case GenericNameSyntax generic:
                    return generic.Identifier.ValueText;
                default:
                    return null;
            }
        }

        private static string NameOf(SimpleNameSyntax name)
        {
            return name?.Identifier.ValueText;
        }

        /// <summary>
        /// Names in the literal that are being renamed. Handles a bare name and
        /// a dotted one - Type.GetType takes "Namespace.Class", and either part
        /// moving is enough to break the lookup.
        /// </summary>
        private static IEnumerable<string> CollidingNames(string literal, Dictionary<string, string> renames)
        {
            if (string.IsNullOrWhiteSpace(literal))
            {
                yield break;
            }

            if (renames.ContainsKey(literal))
            {
                yield return literal;
                yield break;
            }

            if (!literal.Contains('.'))
            {
                yield break;
            }

            // "Some.Namespace.Class, Assembly" - drop the assembly qualifier.
            string typeName = literal.Split(',')[0].Trim();
            foreach (string part in typeName.Split('.'))
            {
                if (renames.ContainsKey(part))
                {
                    yield return part;
                }
            }
        }

        /// <summary>
        /// Every rename that a string could plausibly be referring to. Locals and
        /// parameters are excluded on purpose: reflection cannot reach them, so
        /// including them would generate noise for every literal that happens to
        /// match a variable name.
        /// </summary>
        private static Dictionary<string, string> AllRenames()
        {
            var all = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Dictionary<string, string> source in new[]
                     {
                         DataCollector.Mapping.Namespaces,
                         DataCollector.Mapping.Classes,
                         DataCollector.Mapping.Functions,
                         DataCollector.Mapping.Properties,
                         DataCollector.Mapping.Enums,
                         DataCollector.Mapping.Structs,
                     })
            {
                if (source == null)
                {
                    continue;
                }

                foreach (KeyValuePair<string, string> entry in source)
                {
                    all[entry.Key] = entry.Value;
                }
            }

            return all;
        }
    }
}
