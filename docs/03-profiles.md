# 03 - Tool profiles

`--profile seatbelt` and its six siblings. What they are, when you need one, and
how to write one for a tool that isn't on the list.

---

## The problem they solve

Renaming is correct by construction - it goes through Roslyn's symbol model, so
every reference moves with its declaration. But a real tool contains knowledge
the compiler cannot see:

- **Its own command-line verbs are string literals.** Seatbelt's `Runtime.cs`
  compares against `"all"`. Rename the surrounding code and that string still
  says `"all"` - which is either fine or catastrophic depending on whether you
  also asked to obfuscate the command line (`--rename o`). The compiler has no
  opinion; it is just a string.
- **Help text is a liability.** A banner reading `Seatbelt v1.2.1` in an
  otherwise obfuscated binary defeats the point. It lives in an ordinary method,
  indistinguishable from real logic to a generic tool.
- **Which files matter is tool-specific.** Seatbelt's verbs live under
  `Commands/` and in `SeatbeltArgumentParser.cs`. Certify's live under
  `Commands/` and its help in `Info.cs`. No rule derives that.
- **Some flag combinations break a given tool.** SharpHound cannot have its
  command line obfuscated without also obfuscating properties. Nothing in the
  source says so; someone found out.

A profile is where that per-tool knowledge lives. **It is the least replaceable
thing in this codebase** - not derivable from the source, only learned.

## Do you actually need one?

**Usually not.** Codecepticon-ng is universal: any `.slnx`, `.sln`, `.csproj` or
directory obfuscates without one. A profile is an optional extra for seven
specific tools.

| You are… | Use a profile? |
| --- | --- |
| Renaming identifiers only (`--rename ncefpavs`) | **No.** The generic path handles it |
| Obfuscating strings | **No.** Generic |
| Wanting the help/banner stripped | **No** - use `--strip-method printlogo,usage` |
| Using `--rename o` (command line) | **Yes**, or expect the tool's own argument parsing to break |
| Working on Rubeus, Seatbelt, SharpHound, SharpView, Certify, SharpDPAPI, SharpChrome | **Yes** - it already exists, use it |

`--rename o` is the only capability with no generic substitute: rewriting a
tool's command-line verbs requires knowing which literals *are* verbs, and
nothing derives that.

Without `--profile`, you get `BaseProfile`, which is not nothing: it fixes up
`StartupObject` after renaming and reconciles the injected strings file with the
project. Most runs are fine on it.

**Why `seatbelt` for Seatbelt:** because that profile knows help lives in
`printlogo` and `usage` inside `Seatbelt.cs`, that the CLI verbs live under
`Commands/` and in `SeatbeltArgumentParser.cs`, and that `Runtime.cs` holds a
final reference to the `"all"` verb that has to move with the rest.

## The lifecycle

A profile is a class with four optional hooks. They run at fixed points, and the
log tells you which is which:

```text
Selected profile is: Seatbelt
Running profile-specific pre-process actions...     <- Before()
Rewriting assemblies...
Removing comments...
Rewriting switch statements...
Rewriting strings...
Renaming 12 namespaces... 386 classes... ...
Running profile-specific post-process actions...    <- After()
Applying changes to solution...                     <- written to disk
Running profile-specific final actions...           <- Final()
Applying changes (again) to solution...             <- written again
```

| Hook | Runs | Use it for | `BaseProfile` default |
| --- | --- | --- | --- |
| `Before` | before any rewriting or renaming | anything that must see ORIGINAL names - help stripping, CLI verb rewriting | nothing |
| `After` | after renaming, before the first write | project properties that depend on the new names | fixes `StartupObject` |
| `Final` | after changes are on disk | project-file surgery | reconciles the injected strings file |
| `ValidateCommandLine` | at argument parsing | reject flag combinations this tool cannot survive | allows everything |

If a hook calls `RequireDocument` and the file is missing, the profile is marked
`Failed` and the run **aborts before anything is written** - a half-applied
profile (identifiers renamed, help text left in place) is worse than none,
because nothing about the result says so.

**`Before` is where most profile work belongs.** It is the only hook that still
sees the original identifiers, which is what you need to find `usage()` or match
a literal `"all"`.

If you override `After` or `Final`, call the base behaviour or reproduce it -
`BaseProfile.Final` is what keeps string rewriting from breaking the build, and
a profile that silently drops it will produce a project that does not compile.

## What the shipped profiles do

| Profile | Strips help from | Rewrites CLI verbs in | Extra |
| --- | --- | --- | --- |
| `seatbelt` | `printlogo`, `usage` in `Seatbelt.cs` | `Commands/`, `SeatbeltArgumentParser.cs`, `Runtime.cs` | - |
| `certify` | `Info.cs` | `Commands/` | renames `RootNamespace`, `AssemblyName`, `Company`, `Product`; rewrites switch expressions |
| `rubeus` | yes | yes | also rewrites a word list |
| `sharphound` | yes | yes | removes the strings-file `<Compile>` entry; **requires** properties + command line together |
| `sharpview` | yes | method names, switch cases | **requires** functions + command line together |
| `sharpdpapi` | yes | yes | - |
| `sharpchrome` | yes | yes | - |

## Writing one

Profiles are **compiled in, not dropped in a directory.** There is no plugin
folder - adding one means editing the source and rebuilding. Four steps.

### 1. Create the folder and class

`src/Codecepticon.Ng/Modules/CSharp/Profiles/MyTool/MyTool.cs`

```csharp
using System.IO;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using Codecepticon.Utils;
using Microsoft.CodeAnalysis;

namespace Codecepticon.Modules.CSharp.Profiles.MyTool
{
    class MyTool : BaseProfile
    {
        public override string Name { get; } = "MyTool";

        public override async Task<Solution> Before(Solution solution, Project project)
        {
            if (CommandLineData.CSharp.Rename.CommandLine)
            {
                Logger.Debug("MyTool: Rewriting command line");
                solution = await RewriteCommandLine(solution, project);
            }

            Logger.Debug("MyTool: Removing help text");
            solution = await RemoveHelpText(solution, project);
            return solution;
        }

        protected async Task<Solution> RemoveHelpText(Solution solution, Project project)
        {
            project = VisualStudioManager.GetProjectByName(solution, project.Name);
            Document doc = RequireDocument(project, "Help.cs");
            if (doc == null)
            {
                return solution;   // RequireDocument has reported it; the run aborts
            }

            SyntaxNode root = await doc.GetSyntaxRootAsync();
            root = new Rewriters.RemoveHelpText().Visit(root);
            return solution.WithDocumentSyntaxRoot(doc.Id, root);
        }

        protected async Task<Solution> RewriteCommandLine(Solution solution, Project project)
        {
            var rewriter = new Rewriters.CommandLine();
            project = VisualStudioManager.GetProjectByName(solution, project.Name);

            foreach (Document document in project.Documents)
            {
                // Match on the directory the tool keeps its verbs in.
                // Normalise separators - see the gotcha below.
                string dir = Path.GetDirectoryName(document.FilePath).Replace('\\', '/');
                if (!dir.EndsWith("/Commands"))
                {
                    continue;
                }

                SyntaxNode root = await document.GetSyntaxRootAsync();
                solution = solution.WithDocumentSyntaxRoot(document.Id, rewriter.Visit(root));
            }

            return solution;
        }
    }
}
```

### 2. Write the rewriters

`Profiles/MyTool/Rewriters/RemoveHelpText.cs` - a `CSharpSyntaxRewriter` that
replaces the body of the named functions with an empty block. The shipped ones
are 40 lines each and are the best template:

```csharp
class RemoveHelpText : CSharpSyntaxRewriter
{
    protected SyntaxTreeHelper Helper = new SyntaxTreeHelper();
    protected List<string> HelpFunctions = new List<string> { "printlogo", "usage" };

    public override SyntaxNode VisitBlock(BlockSyntax node)
    {
        SyntaxNode method = Helper.FindParentOfType(node, SyntaxKind.MethodDeclaration);
        if (method == null) return base.VisitBlock(node);

        string name = method.ChildTokens().LastOrDefault().ValueText.ToLower();
        return HelpFunctions.Contains(name) ? SyntaxFactory.Block(null) : base.VisitBlock(node);
    }
}
```

Copy `Profiles/Seatbelt/Rewriters/` and adapt - it is the shortest working pair.

### 3. Register it

`Modules/CSharp/CommandLine/CSharpCommandLine.cs`, in `ParseProfile`:

```csharp
CommandLineData.CSharp.Profile = name switch {
    "rubeus" => new Rubeus(),
    "seatbelt" => new Seatbelt(),
    "mytool" => new MyTool.MyTool(),      // <- add, and a using at the top
    _ => new BaseProfile()
};
```

**An unrecognised `--profile` silently falls through to `BaseProfile`** - it does
not error. If your profile seems to do nothing, check the spelling here first.

### 4. Document it

Add the name to the `--profile` list in `src/Codecepticon.Ng/Help/CSharp.txt`,
then rebuild. `--module csharp --help` should show it.

## Gotchas

**Hardcoded path separators.** Two shipped profiles match files with Windows
separators - `Seatbelt.cs` uses `IndexOf(@"\Commands\")` and `Certify.cs` uses
`Split('\\').Last()`. On Linux those never match, so command-line rewriting
**silently does nothing** and the run still reports success. On Windows they work,
which is why it has not bitten. Do not copy the pattern: normalise with
`.Replace('\\', '/')` first, as the skeleton above does.

**Guard flag combinations you know are unsafe** rather than letting them produce
a broken tool:

```csharp
public override bool ValidateCommandLine()
{
    if (CommandLineData.CSharp.Rename.CommandLine && !CommandLineData.CSharp.Rename.Properties)
    {
        Logger.Error("MyTool: obfuscating the command line also requires --rename p.");
        return false;
    }
    return true;
}
```

That is how `sharphound` and `sharpview` protect themselves, and it turns a
silent runtime break into an exit `2` before anything is touched.

**Use `RequireDocument`, not `GetDocumentByName`.** A profile that cannot find a
file it needs must say so rather than throwing:

```csharp
Document doc = RequireDocument(project, "Help.cs");
if (doc == null)
{
    return solution;
}
```

`RequireDocument` reports a `PROFILE MISMATCH` naming the profile, the file and
the project, sets `Failed`, and the run aborts before anything is written. Using
`GetDocumentByName` directly and indexing into the result gives a
`NullReferenceException` that names none of those things - which is how every
shipped profile behaved until 2026-09-13.

**Verify your profile.** A profile is source-rewriting code with no tests behind
it. Run with `--precompile --backup --verify` while developing it - `--verify`
catches a rewriter that produced uncompilable code, and `--backup` means you can
retry without re-cloning the target.

## If your tool has no profile

Run without `--profile`. Renaming, string encoding and comment stripping all
work. Two things you give up:

- **`--rename o`** (command-line obfuscation) is unsafe - the tool's argument
  parsing and its verb strings will not move together. Leave `o` out.
- **Help text and banners survive.** Strip them by hand afterwards, or write a
  profile.

A profile is worth writing when you will obfuscate the same tool repeatedly
**and** need its command line obfuscated. For anything else, this gets you the
same result with no code:

```bash
--rename ncefpavs --strip-method printlogo,usage,banner --verify
```

Everything except command-line verbs, the banner removed, and the result checked
to still compile.
