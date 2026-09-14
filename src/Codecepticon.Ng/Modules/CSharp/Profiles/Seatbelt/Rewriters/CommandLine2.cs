using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Linq;
using Codecepticon.Utils;
using System.Text;
using System.Threading.Tasks;

namespace Codecepticon.Modules.CSharp.Profiles.Seatbelt.Rewriters
{
    class CommandLine2 : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            string text = node.GetFirstToken().ValueText.Trim();
            if (text == "all")
            {
                // Leave it alone if the All enum member was not renamed - either
                // --rename did not include enums, or this Seatbelt does not have
                // one. Rewriting to a name nothing else uses would desync the
                // verb; leaving it matches the un-renamed declaration.
                if (!DataCollector.Mapping.Enums.TryGetValue("All", out string renamed))
                {
                    Logger.Warning("Seatbelt: the 'All' enum member was not renamed - leaving the \"all\" verb as-is.");
                    return base.VisitLiteralExpression(node);
                }

                return SyntaxFactory.ParseExpression($"\"{renamed.ToLower()}\"");
            }
            return base.VisitLiteralExpression(node);
        }
    }
}
