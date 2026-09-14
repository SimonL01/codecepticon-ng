# 02 - Operating guide

Every command here was run against this build and produced the output shown.
Where something does not work, or has not been run, it says so.

Verified 2026-09-12 on Linux / .NET 10.0.111.

**Jump to:** [Cheatsheet](#quick-reference-cheatsheet)

- [What it does](#what-it-does)
- [Build](#build)
- [C#](#c---the-module-the-port-was-about)
- [PowerShell](#powershell)
- [VBA/VB6](#vba--vb6)
- [Sign](#sign---windows-only)
- [Unmapping](#unmapping---reading-obfuscated-output-back)
- [Flag reference](#complete-flag-reference)
- [CI](#running-it-in-ci)
- [Troubleshooting](#troubleshooting)

---

## Quick reference cheatsheet

```bash
# Obfuscate a C# solution, safely. The four flags that matter.
Codecepticon.Ng --module csharp --action obfuscate --path App.slnx \
    --rename all --rename-method markov \
    --precompile --backup --verify

# Add string encoding
    --string-rewrite --string-rewrite-method xor

# Strip a banner / help text on ANY solution - no profile needed
    --strip-method printlogo,usage

# A known tool (one of seven) - adds tool-aware help + CLI-verb handling
    --profile seatbelt

# Exclude names that reflection reaches (the warning tells you which)
    --keep '^(PayloadRunner|Execute)$'

# PowerShell (writes App.ps1.obfuscated.ps1)
Codecepticon.Ng --module powershell --action obfuscate --path App.ps1 \
    --rename all --rename-method markov

# VBA/VB6 (writes Module1.bas.obfuscated.vba)
Codecepticon.Ng --module vba --action obfuscate --path Module1.bas \
    --rename all --rename-method markov

# Read obfuscated output back
Codecepticon.Ng --module powershell --action unmap \
    --map-file App.ps1.html --unmap-file App.ps1.obfuscated.ps1
```

| | |
| --- | --- |
| **Modules** | `csharp` · `powershell` · `vba` (=`vb6`) · `sign` |
| **Actions** | `obfuscate` · `unmap` (`sign` uses `cert` / `sign`) |
| **`--path` takes** | `.slnx` · `.sln` · `.csproj` · a directory (C#); a file (others) |
| **Rename scope** | `all`, or letters: `n`amespace `c`lass `e`num `f`unction `p`roperty p`a`rameter `v`ariable `s`truct c`o`mmandline |
| **Naming** | `markov` (default choice) · `dict` · `random` · `notwins` |
| **Exclude names** | `--keep <regex>` - unanchored; case-sensitive for C# only |
| **String methods** | `b64` · `xor` · `group` · `single` · `external` |
| **Strip banners** | `--strip-method a,b,c` - works on any solution |
| **Profiles** | `certify` `rubeus` `seatbelt` `sharpchrome` `sharpdpapi` `sharphound` `sharpview` - *optional*, for those seven tools |
| **Exit codes** | `0` ok · `1` failed · `2` bad arguments · `3` **obfuscated but broken - restore the source** |
| **Writes in place?** | C# **yes**. PowerShell and VBA write `<path>.obfuscated.*` |

**The safety flags, in the order they act:**

| Flag | When | Catches |
| --- | --- | --- |
| *(preflight, automatic)* | before rewriting | missing build tool or targeting pack |
| `--precompile` | before rewriting | the project was already broken |
| `--backup` | before rewriting | gives you an undo |
| *(reflection scan, automatic)* | before rewriting | names reached by strings - breaks at **run** time |
| `--verify` | after rewriting | the rewrite produced uncompilable code |

---

## What it does

Source-level obfuscation of C#, PowerShell and VBA/VB6, through the compiler's
own semantic model rather than text substitution.

**It works on any solution.** Every capability below is generic - point it at any
`.slnx`, `.sln`, `.csproj` or directory and it obfuscates. The seven named
profiles are an *optional* extra for seven specific tools, not a prerequisite and
not a limit: without one you lose nothing but tool-aware command-line-verb
rewriting. Verified against an arbitrary project using interfaces, enums, structs,
generics, LINQ and async, with no profile: renamed, recompiled, identical output.

Feature by feature:

- **Semantic renaming** - namespaces, classes, enums, functions, properties,
  parameters, variables, structs, each independently selectable. Renames go
  through Roslyn's symbol model, so every reference moves with the declaration.
- **String encoding** - literals replaced by calls to an injected decoder:
  base64, XOR with per-string keys, character-group or single substitution, or
  export to an external file loaded at run time.
- **Comment stripping**, and `--strip-method` to empty named method bodies -
  the generic way to remove a banner or help text from any tool.
- **Command-line argument obfuscation** (`--rename o`), which does need a
  profile to stay self-consistent.
- **Seven tool profiles** carrying per-tool knowledge for Certify, Rubeus,
  Seatbelt, SharpChrome, SharpDPAPI, SharpHound and SharpView.
- **Reversible by default** - every run writes an HTML mapping file, and `unmap`
  turns obfuscated output back into readable names.
- **Authenticode signing** and self-signed certificate generation.
- **Built for unattended use** - real exit codes, never prompts on stdin,
  resolves its own data files relative to the executable.

What it does **not** do: IL-level obfuscation (ConfuserEx does that well, and
they compose), packing, or anything that cannot guarantee a compilable result.

---

## Build

```bash
dotnet build src/Codecepticon.Ng/Codecepticon.Ng.csproj -c Release
```

The only requirement is the **.NET 10 SDK**. All five packages come from
nuget.org - no private feed, no `NuGet.config`. The binary lands in
`src/Codecepticon.Ng/bin/Release/net10.0/Codecepticon.Ng` (`.exe` on Windows).

`Help/`, `Templates/` and `CommandLineGenerator.html` are copied next to it and
resolved relative to the executable, so the tool works from any working
directory.

Warnings are visible and non-blocking by design - see the comment in the
`.csproj` for why.

## The shape of every command

```shell
Codecepticon.Ng --module [csharp|powershell|vba|sign] --action [obfuscate|unmap] --path <input> [options]
```

`--help` works globally and per module (`--module csharp --help`).
`--config <file.xml>` runs from an XML config instead of flags.
`CommandLineGenerator.html` in the repo root builds commands interactively.

### Exit codes

| Code | Meaning | What to do |
| ---: | --- | --- |
| `0` | completed | - |
| `1` | ran, but something failed | read the error |
| `2` | bad arguments; nothing was attempted | fix the command line and retry |
| `3` | **obfuscated, and the result does not build** | **restore the source first** |

`3` exists because it is the only outcome that leaves your working tree
rewritten and broken. A pipeline hitting `2` can retry; a pipeline hitting `3`
has cleanup to do.

```bash
Codecepticon.Ng --module csharp --action obfuscate --path App.slnx --rename all --verify
case $? in
  0) echo "obfuscated" ;;
  3) echo "BROKEN - restoring"; git checkout . ;;
  *) echo "failed before rewriting" ;;
esac
```

### Naming: pick `markov` unless you have a reason not to

| `--rename-method` | Produces | Use when |
| --- | --- | --- |
| `markov` | `BractockSubflowsTric`, `LaryRed` | **default choice** - English-sounding, blends into real code |
| `dict` (or `dictionary`) | words from `--rename-dictionary` / `--rename-dictionary-file` | themed or client-specific names |
| `random` | `xkqzab` from `--rename-charset` | obviously machine-generated names |
| `notwins` | as `random`, no repeated adjacent character | slightly more pronounceable |

`markov` needs `Templates/markov-english-words.txt` beside the binary; the build
puts it there. Tune with `--markov-min-length` / `--markov-max-length` (default
3 and 9) and `--markov-min-words` / `--markov-max-words`.

---

## C# - the module the port was about

**In place.** The C# module rewrites your source where it sits. Use `--backup`,
or work on a clean checkout.

```bash
Codecepticon.Ng --module csharp --action obfuscate \
  --path /src/MyTool/MyTool.slnx \
  --rename all --rename-method markov \
  --precompile --backup --verify
```

```shell
[00:03:36] Toolchain: Roslyn Workspaces v5.9.0.0, .NET 10.0.11
[00:03:37] Toolchain: build tool: MSBuild v18.9.1.35102 (...)
[00:03:37] Creating MSBuild Workspace (out-of-process build host)
[00:03:42] Finished loading solution
[00:03:44] Generating mappings...
[00:03:46] Renaming 12 namespaces... 386 classes... 119 functions...
[00:05:30] Obfuscation complete
```

Before and after:

```csharp
namespace DemoTool {                        namespace BractockSubflowsTric {
    public class CredentialHarvester {          public class LaryRed {
        private string targetHost;                  private string NonstomyVapolyzeEluship;
        public string BuildReport(int n)            public string DonersSeizoideSyndium(int GusCallent)
```

`Main` is preserved, the project still compiles, and it prints what it printed
before.

### `--path` takes any of four things

| Input | Behaviour |
| --- | --- |
| `.slnx` | opened directly - no extra flag |
| `.sln` | opened directly |
| `.csproj` | opened as a single-project solution |
| a directory | searched: `.slnx` first, then `.sln`, then a lone `.csproj` |

`.slnx` wins over `.sln` when both exist - such a repo is usually mid-migration
and the XML file is current. A directory with several projects and no solution
is refused rather than guessed at. The mapping file is named after the file
actually opened.

### `--rename` selects what gets renamed

`all`, or any combination of these letters:

| | | | | | | | | |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `n` | `c` | `e` | `f` | `p` | `a` | `v` | `s` | `o` |
| namespaces | classes | enums | functions | properties | parameters | variables | structs | command line |

`--rename cfv` renames classes, functions and variables and leaves namespaces
readable for triage. `--rename none` disables renaming (for string-only runs).

### Strings

```bash
  --string-rewrite --string-rewrite-method xor
```

Every literal becomes a call to an injected decoder with a per-string key:

```csharp
string banner = "harvest complete";
// becomes
string Deviation = KonEnansion.BroadPlastic.EnstreGluceaDialipped(
    "IAIlITMaJGkTCwwAJBUEJA==", "HcWWViPIpdapHppAxDIWERDOauqOjNco");
```

Verified: `strings -el` finds the literals in an unobfuscated build and none in
the obfuscated one.

| `--string-rewrite-method` | Effect |
| --- | --- |
| `b64` (or `base64`) | base64-encode every literal |
| `xor` | XOR with randomly generated per-string keys |
| `group` (or `groupsub`) | each character becomes a character group; `--string-rewrite-charset`, `--string-rewrite-length` |
| `single` (or `singlesub`) | substitution cipher |
| `external` (`ext`, `file`) | literals exported to `--string-rewrite-extfile`, loaded at run time |

The decoder is added as a new `.cs` file named to blend in with the project's
own. The project file is reconciled automatically: SDK-style projects get no
`<Compile Include>` entry (the implicit glob covers it, and an explicit one
fails with NETSDK1022), legacy projects get exactly one.

### Tool profiles

`--profile [certify|rubeus|seatbelt|sharpchrome|sharpdpapi|sharphound|sharpview]`

Each profile carries per-tool knowledge - which literals are that tool's command
strings, which help text to strip, which properties must move together. Use one
whenever you obfuscate that tool.

**You need one only if** you are passing `--rename o` (command line). Everything
else a profile does has a generic equivalent - use `--strip-method` for the
banner and help text. An unrecognised `--profile` name falls back to the generic
behaviour **silently**, so check the spelling if one seems to do nothing. A
profile pointed at the wrong tool now aborts with `PROFILE MISMATCH` before
anything is written.

### Stripping a banner without a profile

```bash
  --strip-method printlogo,usage
```

Empties the bodies of those methods, by name, before renaming. This matters
because **string encoding is not enough**: `--string-rewrite` hides a banner from
`strings` and ILSpy, but the program still *prints* it at run time, which on an
offensive tool is the giveaway that counts. Only removing the code stops that.

```shell
Stripping method bodies...
  Not stripped, would not compile: Version (returns string)
  Emptied: PrintLogo, Usage, LogAsync
```

Case-insensitive, comma-separated. Methods that return a value are reported and
left alone - emptying them would not compile, and inventing a return value would
change behaviour you did not ask to change. `void` and `async Task` are stripped.
Names are matched against the original source, since it runs before renaming.

**Your tool not on the list?** `docs/05-profiles.md` covers what profiles do,
when you actually need one, and how to write one - they are compiled in, not
dropped into a directory. For a single run against an unfamiliar tool,
`--rename ncefpavs` (everything except command line) with `--verify` is the
pragmatic choice.

### The safety net

Four checks, three of which run **before** anything is written.

**Preflight (automatic with `--build`, `--precompile` or `--verify`).** Confirms
the machine can build what you are about to rewrite:

```shell
PREFLIGHT FAILED - nothing has been modified.
  build tool: dotnet CLI v10.0.111 (/usr/bin/dotnet)

  [FAIL] Legacy.csproj targets .NET Framework v4.7.2, and its targeting pack is not installed.

  Install the v4.7.2 Developer Pack: https://aka.ms/msbuild/developerpacks

  Obfuscation and building are separable: re-run without --build (and without
  --precompile) to obfuscate anyway, then build elsewhere.
```

**`--precompile`** builds the *original* first, so "this was already broken"
does not masquerade as an obfuscation failure. Cheap, and recommended.

**`--backup`** copies the source aside first. `--backup-path <dir>` chooses
where (and implies `--backup`); the default is a timestamped directory beside
the solution. A non-empty target directory is refused.

```shell
Backed up 2 files to: /src/MyTool/codecepticon-backup-20260912-003816
```

**Reflection scan (automatic).** The one break neither the compiler nor
`--verify` can see:

```shell
REFLECTION WARNING - 3 string literal(s) name a symbol that is being renamed:
  Program.cs(15) GetType("App.PayloadRunner") -> 'PayloadRunner' is being renamed to 'UnpalgianCivinary'
  Program.cs(16) GetMethod("Execute") -> 'Execute' is being renamed to 'HyporessDishlike'

  These compile either way. They break at RUN time, if the lookup was real.
```

That example genuinely compiles and then throws `NullReferenceException` at run
time. A warning rather than a refusal - a literal matching a class name is not
proof of reflection. `--no-reflection-scan` silences it.

**`--keep <regex>`** is how you act on a reflection warning. It is applied where
the mapping is built, so a kept name is never renamed by anything and the mapping
file honestly records what changed:

```bash
  --keep '^(App|PayloadRunner|Execute)$'
```

Matched **unanchored** - `--keep Command` keeps every name containing "Command".
Anchor it yourself when that is not what you want, and use alternation for
several. Case-sensitive for C#; case-insensitive for PowerShell and VBA/VB6,
because those languages are. An invalid pattern fails the command line (exit `2`)
rather than the run.

Worked end to end: a project calling `Type.GetType("App.PayloadRunner")` produces
two reflection warnings and dies at run time after obfuscation. Adding
`--keep '^(App|PayloadRunner|Execute)$'` drops the warnings to zero, renames
everything else, passes `--verify`, and the program still runs.

**`--verify`** builds the result afterwards and exits `3` if it does not
compile:

```text
VERIFY FAILED - the obfuscated code does not compile.
The source on disk has been rewritten and is currently broken.
```

### Building afterwards

`--build` compiles the obfuscated solution; `--build-path <dir>` redirects
output. The build runs as a **child process**, never in-process:

```text
CODECEPTICON_MSBUILD  ->  vswhere  ->  PATH  ->  dotnet
```

```powershell
$env:CODECEPTICON_MSBUILD = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
```

MSBuild is preferred over `dotnet build` because most tools worth obfuscating -
Seatbelt, Rubeus, SharpHound, Certify - are legacy non-SDK `net472` projects,
which the .NET SDK CLI cannot build.

### Multi-project solutions

Refused by default, because cross-project renaming is not verified. Pass
`--allow-multi-project` to proceed. It never prompts - the tool is built for
unattended use.

---

## PowerShell

**Not in place.** Output goes to `<path>.obfuscated.ps1` unless `--save-as`.

```bash
Codecepticon.Ng --module powershell --action obfuscate \
  --path ./Invoke-Thing.ps1 --rename all --rename-method markov
```

```powershell
function Get-TargetInfo {                   function Phalloid {
    param([string]$ComputerName)                param([string]$Fjoricap)
    $result = "scanning $ComputerName"          $Bion = ("scanning {0}" -f $Fjoricap)
    Write-Output $result                        Write-Output $Bion
}                                           }
Get-TargetInfo -ComputerName "dc01"         Phalloid -Fjoricap "dc01"
```

Note the interpolated string: `"scanning $ComputerName"` became
`("scanning {0}" -f $Fjoricap)`, because the variable inside it was renamed.

`--rename` takes `all`, or `f` (functions) and `v` (variables *and*
parameters). String rewriting works here too.

A script that does not parse is refused rather than mangled (exit `1`):

```text
Could not parse PowerShell script due to syntax errors - ensure script is
working before trying to obfuscate it.
```

**Open risk:** parsing uses the PowerShell 7 SDK, not Windows PowerShell 5.1.
Grammar differences are untested - see `03-porting-plan.md` §5. Test 5.1-era
scripts before relying on the output.

---

## VBA / VB6

**Not in place.** Output goes to `<path>.obfuscated.vba`.

```bash
Codecepticon.Ng --module vba --action obfuscate \
  --path ./Module1.bas --rename all --rename-method markov
```

```vb
Sub RunPayload()                            Sub TriVirulousDefler()
    Dim targetHost As String                    Dim TakerHugestle As String
    targetHost = "dc01.corp.local"              TakerHugestle = "dc01.corp.local"
    MsgBox "Deploying " & payloadName           MsgBox "Deploying " & Craminess
End Sub                                     End Sub
```

`--rename` takes `all` or `i` (identifiers). `Attribute VB_Name` is left alone -
Office needs it. `--module vb6` is a synonym.

---

## Sign - Windows only

Authenticode signing and self-signed certificate generation. P/Invoke against
`crypt32`/`mssign32`: compiles anywhere, runs only on Windows. **Not verified by
execution** - this section is from `--module sign --help`.

Note `--action` takes **`cert`**, not `new`.

**`cert`** - everything below except `--overwrite` is **required**; the validator
rejects the run otherwise:

```shell
Codecepticon.Ng --module sign --action cert \
  --pfx-file <out.pfx> --password <pw> \
  --subject "CN=Contoso,C=GB" --issuer "CN=Contoso Issuer,C=GB" \
  --not-before "2026-01-01 00:00:00" --not-after "2027-01-01 00:00:00" \
  [--overwrite]
```

The dates are parsed with `DateTime.ParseExact` against exactly
`yyyy-MM-dd HH:mm:ss` - no other format is accepted and the seconds are not
optional. `--copy-from <signed.exe>` substitutes for `--subject` and `--issuer`,
but not for the dates.

**`sign`** - `--algorithm` is **required**; `--timestamp` is genuinely optional:

```shell
Codecepticon.Ng --module sign --action sign --path <file.exe> \
  --pfx-file <out.pfx> --password <pw> --algorithm SHA256 \
  [--timestamp http://timestamp.digicert.com]
```

The pfx password is verified before anything is attempted, so a wrong one fails
at exit `2` rather than part-way through signing.

---

## Unmapping - reading obfuscated output back

Every run writes a mapping file: `<path>.html` beside the input, or wherever
`--map-file` says. **Keep it.** An obfuscated crash dump you cannot read costs
more than the obfuscation bought.

```bash
Codecepticon.Ng --module powershell --action unmap \
  --map-file ./Invoke-Thing.ps1.html \
  --unmap-file ./Invoke-Thing.ps1.obfuscated.ps1
```

`--unmap-directory <dir>` with optional `--unmap-recursive` does a whole tree -
useful for a log directory full of obfuscated stack traces.

- **It rewrites in place.** The obfuscated artifact is replaced by the unmapped
  one. Copy first if you need both.
- **Case is not preserved.** `Get-TargetInfo` comes back as `get-targetinfo`.

---

## Complete flag reference

Every flag the CLI parses. A flag not listed here is silently ignored.

### Global - all modules

| Flag | Value | Meaning |
| --- | --- | --- |
| `--module` | `csharp`\|`powershell`\|`vba`\|`vb6`\|`sign` | which module |
| `--action` | `obfuscate`\|`unmap` | what to do (Sign: `cert`\|`sign`) |
| `--path` | path | input - see per-module notes |
| `--save-as` | path | write output here instead of the default |
| `--config` | file | read all settings from an XML config |
| `--help` | switch | help, global or per module |
| `--verbose` | switch | more output, including the toolchain report |
| `--debug` | switch | everything, including full build output |
| `--rename` | `all`\|`none`\|letters | what to rename |
| `--rename-method` | `markov`\|`dict`\|`random`\|`notwins` | how names are generated |
| `--rename-charset` | string | charset for `random`/`notwins` |
| `--rename-length` | int | generated name length |
| `--rename-dictionary` | `w1,w2,…` | inline wordlist for `dict` |
| `--rename-dictionary-file` | path | wordlist file for `dict` |
| `--keep` | regex | never rename names matching this |
| `--strip-method` | `a,b,c` | empty these method bodies (banners, help) |
| `--markov-min-length` | int | default 3 |
| `--markov-max-length` | int | default 9 |
| `--markov-min-words` | int | words concatenated per name |
| `--markov-max-words` | int | " |
| `--string-rewrite` | switch | enable string encoding |
| `--string-rewrite-method` | `b64`\|`xor`\|`group`\|`single`\|`external` | how |
| `--string-rewrite-charset` | string | for `group` |
| `--string-rewrite-length` | int | group length |
| `--string-rewrite-extfile` | path | for `external` |
| `--map-file` | path | where the HTML mapping goes |
| `--unmap-file` | path | file to unmap |
| `--unmap-directory` | path | directory to unmap |
| `--unmap-recursive` | switch | recurse into it |

### C# module only

| Flag | Value | Meaning |
| --- | --- | --- |
| `--profile` | name | one of the seven tool profiles |
| `--build` | switch | build the obfuscated solution afterwards |
| `--build-path` | dir | send build output here |
| `--precompile` | switch | build the **original** first; abort if broken |
| `--verify` | switch | build the **result**; exit `3` if broken |
| `--backup` | switch | copy the source aside before rewriting |
| `--backup-path` | dir | where; implies `--backup` |
| `--allow-multi-project` | switch | permit >1 project in the solution |
| `--no-reflection-scan` | switch | silence the reflection warning |

### Sign module only

| Flag | Value | Meaning |
| --- | --- | --- |
| `--pfx-file` | path | written by `cert`, read by `sign` |
| `--password` | string | pfx password |
| `--subject` | DN | certificate subject |
| `--issuer` | DN | certificate issuer |
| `--copy-from` | file | lift subject/issuer off a signed binary |
| `--not-before` | `YYYY-MM-DD HH:MM:SS` | validity start |
| `--not-after` | `YYYY-MM-DD HH:MM:SS` | validity end |
| `--overwrite` | switch | replace an existing pfx |
| `--algorithm` | `MD5`\|`SHA1`\|`SHA256`\|`SHA384`\|`SHA512` | signature algorithm |
| `--timestamp` | url | timestamp server |

### Environment

| Variable | Meaning |
| --- | --- |
| `CODECEPTICON_MSBUILD` | explicit build tool path; wins over `vswhere`, `PATH` and `dotnet` |

---

## Running it in CI

```bash
set -e
export CODECEPTICON_MSBUILD="/path/to/MSBuild.exe"

codecepticon-ng --module csharp --action obfuscate \
    --path "$WORKSPACE/src/Tool.slnx" \
    --rename all --rename-method markov \
    --string-rewrite --string-rewrite-method xor \
    --map-file "$WORKSPACE/artifacts/rename-map.html" \
    --precompile --backup --verify --build
```

Four properties make this safe unattended, all deliberate:

1. **Real exit codes** - `set -e` works, and `3` is distinguishable.
2. **Never prompts on stdin.** Every decision that used to be a Y/N prompt is a
   flag.
3. **Location-independent.** `Help/` and `Templates/` resolve against the
   executable, not the working directory.
4. **Fails before it destroys.** Preflight, `--precompile` and `--backup` all run
   before the first write.

Archive the mapping file as a build artifact. Without it the obfuscated build is
unreadable to you as well as to everyone else.

---

## Troubleshooting

**`PREFLIGHT FAILED`**
The machine cannot build what you were about to rewrite. Nothing was modified.
Install what it names, or drop `--build`/`--precompile`/`--verify` to obfuscate
anyway and build elsewhere.

**`BUILD FAILED (exit 1)` with `error MSB3644: The reference assemblies for
.NETFramework,Version=vX were not found`**
No targeting pack for that framework version. Install the Developer Pack
(<https://aka.ms/msbuild/developerpacks>); for 3.5,
`Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All`. Check the
`build tool:` line in the same output - `dotnet CLI` there is usually the real
problem, because the .NET SDK cannot build legacy non-SDK projects:

```powershell
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
& $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe
$env:CODECEPTICON_MSBUILD = "<the path it printed>"
```

**Confirm it is not the obfuscation** - revert and build the pristine source
(`git stash ; <build> ; git stash pop`). If that fails identically, the
obfuscation is not implicated.

**`TOOLCHAIN MISMATCH while loading the solution.`**
The named error this project exists to produce instead of a raw CLR abort. It
prints the Roslyn version and the resolved build tool. Point
`CODECEPTICON_MSBUILD` at a matching MSBuild.

**`VERIFY FAILED` / exit 3**
The rewrite produced uncompilable code and **your source is currently broken**.
Restore (`--backup` directory, or source control), then narrow the scope
(`--rename cfv` rather than `all`) and check the mapping file.

**Obfuscated code compiles but crashes at run time**
Almost certainly reflection. Re-read the `REFLECTION WARNING` from the run - it
names the file, line and symbol. Exclude that symbol by narrowing `--rename`.

**`This solution has N projects`**
Cross-project renaming is unverified. Point `--path` at one project, or pass
`--allow-multi-project`.

**`N .csproj files under <dir> and no solution to order them.`**
A directory holding several projects and no solution. Point `--path` at one.

**`error NETSDK1022: Duplicate 'Compile' items`**
Should not happen - the project file is reconciled after string rewriting. If it
does, delete the `<Compile Include>` line for the injected decoder file.

**Nothing renamed, no error**
`--rename` was not passed. Without it the module has nothing to do and says
`No rename or rewrite parameters set.` (exit `2`).
