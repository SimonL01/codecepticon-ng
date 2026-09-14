# 01 - Architecture

## The Why

I used this tool <https://github.com/Accenture/Codecepticon> for static code obfuscation during my red team journey but it got some limits that I wanted to bypass. So I decided to build Codecepticon next generation, hence the "ng".

## The One Idea

Obfuscation is done through the **compiler's semantic model**, never through text. Roslyn knows that `Handler` in `MyTool.Core` and `Handler` in a using-directive three files away are the same symbol; a regex does not. C# renames go through `Renamer.RenameSymbolAsync`, which moves the declaration and every reference across the whole solution atomically.

The corollary is the design constraint: **if the compiler cannot resolve it, we do not touch it.** A symbol from a referenced assembly, a compiler-generated backing field, a name that only appears in a string, all left alone. That guarantees the output still compiles, which is the property that makes the tool usable in an unattended pipeline.

## The central decision: never host MSBuild

This is the reason the project exists, so it comes before the component tour.

Upstream is a .NET Framework 4.7.2 app that loads MSBuild **into its own
process** - `MSBuildLocator.RegisterInstance()` to pick one for the workspace, and `Microsoft.Build.Evaluation.ProjectCollection` to compile. A process that does this pins one MSBuild version for its lifetime, and dies when the machine's is newer. On a Visual Studio 2026 box that is `MissingMethodException: FrozenSet.Create`, thrown from `Microsoft.Build.Shared.XMakeElements..cctor`, mid solution-load.

The fix is not to select a better instance. It is to load no MSBuild at all:

| Concern | Upstream | Here |
| --- | --- | --- |
| Open a solution | `RegisterInstance()` then `MSBuildWorkspace.Create()` | `MSBuildWorkspace.Create()` alone. It drives MSBuild **out-of-process** through the `BuildHost` shipped in `Microsoft.CodeAnalysis.Workspaces.MSBuild` |
| Read/write project properties | `Microsoft.Build.Evaluation.Project` | `ProjectFile.cs` - raw XML over the `.csproj` |
| Build | in-process `.Build()` | `ProjectBuilder.cs` - a **child process** |
| Pass properties to the build | `SetGlobalProperty(...)` | `-p:Name=Value` on that child's command line |

Enforced at the project level: `Microsoft.Build.*` and `Microsoft.Build.Locator` are absent from the `.csproj` and **must not come back**. Only
`Microsoft.Build.Framework.dll` reaches `bin/`, pulled in and version-matched by `Workspaces.MSBuild` itself. That is the interfaces assembly, not the engine. If you think you need one of the others, you need a child process instead.

`ProjectBuilder` resolves its build tool in this order: `CODECEPTICON_MSBUILD` -> `vswhere` -> `PATH` -> `dotnet`. MSBuild is preferred over `dotnet build` because the tools this obfuscates (Seatbelt, Rubeus, SharpHound, Certify...) are mostly legacy non-SDK `net472` projects, which the .NET SDK CLI will not build. These are the one I often used during my engagements.

## Components

The shipping entry point is upstream's CLI. `StartupObject` selects it:

```text
Program.cs                        entry point (Codecepticon.Program)
  CommandLineManager              --module / --config parsing, help text
  ModuleManager                   dispatch to one of four modules
    Modules/CSharp                Roslyn. The module the port was about.
      VisualStudioManager           workspace creation, input resolution,
                                    ReportToolchain()
      ProjectFile                   .csproj as XML  (no MSBuild object model)
      ProjectBuilder                builds as a child process
      BuildPreflight                can this machine build it? - before rewriting
      SourceBackup                  copy the source aside - before rewriting
      DataCollector                 walk the solution, decide what may be renamed
      ReflectionScanner             names reached by strings - before rewriting
      DataRenamer / DataRewriter    apply renames and source rewrites
      Profiles/                     7 per-tool profiles (see below)
    Modules/PowerShell            System.Management.Automation.Language AST
    Modules/VB6                   ANTLR parse tree (VisualBasic6.g4)
    Modules/Sign                  self-signed certs + Authenticode
  Utils/AppPaths                  resolves Help/ and Templates/
  Utils/Logger                    console + codecepticon.log
```

**The seven profiles are the least replaceable thing in the codebase.** Certify, Rubeus, Seatbelt, SharpChrome, SharpDPAPI, SharpHound, SharpView. Each encodes accumulated per-tool knowledge about which string literals are commands and which help text to strip. That knowledge is not derivable from the source; it was learned. Treat the profiles as data, not as code to refactor.

## Input Resolution SLNX/SLN

`VisualStudioManager.ResolveInputPath` turns whatever `--path` names into the
file that actually gets opened:

| Input | Resolution |
| --- | --- |
| `.slnx` / `.sln` | opened as-is |
| `.csproj` | `OpenProjectAsync`, then take its `.Solution` |
| a directory | searched - `.slnx` first, then `.sln`, then a lone `.csproj` |

**`.slnx` wins over `.sln`** in a directory holding both, because such a repo is usually mid-migration and the XML file is the current source of truth. And **an ambiguous directory is refused, not guessed**: several `.csproj` files with no solution to order them is an error, because picking one arbitrarily would obfuscate the wrong thing silently.

Resolution happens during command-line validation, and the result is written back
to `Project.Path`. Everything downstream - the mapping file name most visibly -
therefore sees the resolved solution rather than the directory the user typed.

The `.csproj` branch exists because `OpenSolutionAsync` will not open a project
file; it fails with `Failed to load solution`, which tells the user nothing about
what to do instead.

## Collect, Then Re-Resolve, Then Rename

Renaming produces a *new* `Solution`. Every `ISymbol` collected from the old one
is immediately stale - using it silently targets a document that no longer
exists.

The module therefore separates collection from application. `DataCollector` walks
the solution and records what should be renamed as plain data; `SyntaxTreeHelper.RenameSymbolAsync` then re-finds the declaring node **by name in the current solution**, calls `GetDeclaredSymbol` on it, and renames that. A target that no longer resolves is skipped - it usually means the symbol was renamed away with its container, which is correct behaviour, not an error.

## Safety rails

`DataCollector` refuses to rename anything whose name is a contract with
something outside the source tree:

| Refused | Why |
| --- | --- |
| Interface implementations | the interface fixes the name |
| Overrides and virtuals | the base declaration fixes the name |
| `extern` / `[DllImport]` | the name binds to an export |
| Not defined in source | it belongs to a referenced assembly |
| Compiler-generated (`<...>`, `.ctor`) | not ours to rename |

Reflection remains the known hole: a name resolved through `Type.GetType("…")` or
`GetMethod("…")` is invisible to the semantic model, so renaming it breaks the
program silently.

## Location Independence

`Utils/AppPaths.InApp(...)` resolves `Help/` and `Templates/` against
`AppContext.BaseDirectory`, normalising separators on the way. Both halves
matter, and neither is cosmetic:

- **Separators.** Paths written as `@".\Help\Global.txt"` are not paths on Linux
  - they are single filenames containing backslashes. Help came back silently
  empty; mapping-file generation threw `FileNotFoundException` *after* the
  rewrite had already been applied to disk.
- **Anchor.** `.\` resolves against the current directory, not the executable's.
  This bites on Windows too: any caller that sets its own working directory - a
  CI job, a build step - got empty help and missing templates.

Anything that opens a file shipped next to the binary goes through `AppPaths`.

## Four Gates Around One Destructive Act

The C# module rewrites source **in place**. Everything below exists because that
single fact turns an ordinary failure into a destroyed working tree, and because
each failure mode needs a different answer:

| Gate | Runs | Catches | On failure |
| --- | --- | --- | --- |
| `BuildPreflight` | before rewriting | no build tool; SDK CLI against a legacy project; missing targeting pack | refuse, nothing touched |
| `--precompile` | before rewriting | the project did not compile to begin with | refuse, nothing touched |
| `SourceBackup` | before rewriting | - (provides the undo) | refuse, nothing touched |
| `ReflectionScanner` | before rewriting | names reached by string, which break at **run** time | warn and continue |
| `--verify` | after rewriting | the rewrite produced uncompilable code | exit `3`, tree is broken |

The ordering is the design. Three of the four run before the first write, so the
common failures cost nothing. `ReflectionScanner` warns rather than refusing
because a literal matching a class name is not proof of reflection - and it is
the only gate that catches a break which *compiles*, so `--verify` cannot
substitute for it.

## Mapping files are not optional

Every run emits a mapping of original -> obfuscated names. Without it, a crash
dump from an obfuscated build is unreadable and the tool has cost you more than
it bought. An obfuscated binary you cannot triage is a liability, not a win.

## Exit codes

| Code | Meaning |
| ---: | --- |
| `0` | completed |
| `1` | a runtime failure, or an unhandled exception |
| `2` | bad arguments - nothing was attempted |

Upstream declared `static async Task Main`, so it returned no exit code at all
and every failure path was `Logger.Error(...)` followed by `return`. A failed run
and a successful one both exited **0**, which for a tool built for unattended
pipeline use is a real defect: `codecepticon ... || handle_failure` never fired,
and a pipeline would ship an unobfuscated binary without noticing.

`Main` now returns `Task<int>`. The failure signal is `Logger.HasErrors`, set by
`Logger.Error()`. That is sound rather than a heuristic because `Error()` is
never used for anything benign - every call site reports a genuine failure, and
the modules otherwise swallow their errors and return `void`. **Keep it that
way:** anything user-facing that is not a failure belongs in `Warning()`.

Unhandled exceptions are caught at the top of `Main`, reported like any other
failure, and turned into `1` rather than a raw CLR abort with whatever code the
runtime picked.

## Deliberately Out of Scope

- **IL-level obfuscation.** ConfuserEx does it well and this is a *source* tool. They compose; they should not compete.
- **Packing / shellcode conversion.** See `donut`, `srdi`, `upx`, ...
- **Anything that cannot guarantee a compilable result.** If a transform can
  produce code that does not build, it needs `--verify` and a skip path before
  it ships.
  