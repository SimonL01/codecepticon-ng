<!-- markdownlint-disable MD033 -->
<!-- markdownlint-disable MD041 -->
<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="assets/logo-dark.svg">
  <source media="(prefers-color-scheme: light)" srcset="assets/logo-light.svg">
  <img src="assets/logo-light.svg" alt="codecepticon-ng" width="480">
</picture>

<p>
  Identifiers, strings and banners rewritten through the compiler's own symbol model -<br>
  never with a regular expression, and never leaving a solution that does not build.
</p>

<p>
  <img alt=".NET 10.0" src="https://img.shields.io/badge/.NET-10.0-30363d?style=flat-square&labelColor=0d1117">
  <img alt="Roslyn 5.9" src="https://img.shields.io/badge/Roslyn-5.9-30363d?style=flat-square&labelColor=0d1117">
  <img alt="C# - PowerShell - VBA" src="https://img.shields.io/badge/C%23%20%C2%B7%20PowerShell%20%C2%B7%20VBA-30363d?style=flat-square&labelColor=0d1117">
  <img alt="MIT" src="https://img.shields.io/badge/license-MIT-30363d?style=flat-square&labelColor=0d1117">
</p>

</div>
<!-- markdownlint-enable MD033 -->
<!-- markdownlint-enable MD041 -->

---

`codecepticon-ng` obfuscates a project's **source** before it is compiled. Identifiers are
renamed through Roslyn's semantic model, string literals are replaced by calls to an injected
decoder, and banners are emptied out of their method bodies. Because every rename moves through
the compiler's symbol table rather than a pattern match, the declaration and all its references
move together - and the result still compiles. That guarantee is what makes it usable unattended.

It is a port of [Codecepticon](https://github.com/sadreck/Codecepticon) to a current toolchain:
`net10.0`, Roslyn 5.9, MSBuild 18.9. Upstream cannot run on a Visual Studio 2026 machine.
See [Why this exists](#why-this-exists).

## Quick start

```bash
dotnet build src/Codecepticon.Ng/Codecepticon.Ng.csproj -c Release
```

The .NET 10 SDK is the only prerequisite. All packages come from nuget.org; there is no private
feed and no `NuGet.config`.

```bash
Codecepticon.Ng --module csharp --action obfuscate --path App.slnx \
    --rename all --rename-method markov \
    --precompile --backup --verify
```

Back up the source, prove it built *before* it was touched, rename every identifier with
English-sounding names, then prove it still builds. Exit `0` is done. Exit `3` is the one outcome
that leaves your tree rewritten and broken.

## Capabilities

| | |
| --- | --- |
| **Modules** | `csharp` - `powershell` - `vba` (=`vb6`) - `sign` |
| **Input** | `.slnx` - `.sln` - `.csproj` - a directory (C#); a single file (PowerShell, VBA) |
| **Renaming** | namespaces, classes, enums, methods, properties, parameters, variables, structs and command-line verbs - each independently selectable |
| **Name generation** | `markov` (default) - `dict` - `random` - `notwins` |
| **String encoding** | `b64` - `xor` - `group` - `single` - `external` |
| **Stripping** | comments, and `--strip-method` to empty named method bodies - the generic way to remove a banner from any tool |
| **Reversibility** | every run writes an HTML mapping file; `--action unmap` reads obfuscated output back |
| **Profiles** | optional per-tool knowledge for Certify, Rubeus, Seatbelt, SharpChrome, SharpDPAPI, SharpHound and SharpView |
| **Signing** | Authenticode signing and self-signed certificate generation *(Windows)* |
| **Automation** | real exit codes, never prompts on stdin, resolves its data files relative to the executable |

**Universal, not tool-specific.** Point it at any `.slnx`, `.sln`, `.csproj` or directory and it
obfuscates with no profile. The seven profiles are an optional extra, not a prerequisite; the only
capability with no generic substitute is `--rename o`, which rewrites a tool's own command-line
verbs and therefore has to know which string literals *are* verbs.

## Safety

An obfuscator that hands back uncompilable source has cost you more than it bought. Five checks
run in this order:

| Check | When | Catches |
| --- | --- | --- |
| *preflight* - automatic | before rewriting | a missing build tool or targeting pack |
| `--precompile` | before rewriting | a project that was already broken |
| `--backup` | before rewriting | *nothing - it is the undo, for when one of the others fires* |
| *reflection scan* - automatic | before rewriting | a string literal naming a renamed symbol - the break that compiles, then fails at run time |
| `--verify` | after rewriting | a rewrite that produced uncompilable code |

Exit codes: `0` completed - `1` ran, something failed - `2` bad arguments, nothing attempted -
`3` **obfuscated, and the result does not build - restore the source.**

## Why this exists

Upstream's approach is right; its toolchain is stranded. Three problems, in order of cost:

1. **It cannot run on a VS-2026-only machine.** Upstream is a .NET Framework 4.7.2 app that hosts
   MSBuild *in its own process*. Against MSBuild 18 that is a `MissingMethodException` from
   `XMakeElements..cctor`, mid solution-load. Keeping it alive means keeping a Visual Studio 2022
   on the box purely to feed it an MSBuild 17.
2. **`.slnx`.** Upstream globs for `*.sln` and cannot open an XML-solution repo. New solutions
   increasingly are one.
3. **All-or-nothing transforms.** Renaming methods while leaving class names readable for triage
   requires transforms to be independently selectable.

The fix for the first is not to select a better MSBuild - it is to **host none at all**. Solutions
load through a modern `MSBuildWorkspace`, which drives MSBuild out-of-process via its own
`BuildHost`; builds shell out to a child process. `Microsoft.Build.*` is absent from the `.csproj`
and must not come back. Problems 2 and 3 fell out of that work.

## Documentation

| | |
| --- | --- |
| [**01 - Architecture**](docs/01-architecture.md) | the MSBuild decision, component tour, and the invariants that hold it together |
| [**02 - Operating guide**](docs/02-operating-guide.md) | every flag, every module, verified command output, CI recipes, troubleshooting |
| [**03 - Profiles**](docs/03-profiles.md) | what the seven tool profiles carry, and how to write one |
| [`CommandLineGenerator.html`](CommandLineGenerator.html) | builds a command line interactively, in the browser |

## Attribution

**This is a derivative work, not a clean-room reimplementation.** It carries MIT-licensed code from
three separately-authored projects - [Codecepticon](https://github.com/sadreck/Codecepticon)
(Pavel Tsakalidis; originally [Accenture](https://github.com/Accenture/Codecepticon), unmaintained
since 2024-02), the [ProLeap VB6 ANTLR grammar](https://github.com/uwol/proleap-vb6-parser), and
[SigningServer](https://github.com/Danielku15/SigningServer).

[`NOTICE.md`](NOTICE.md) records which code came from where; [`LICENSE`](LICENSE) carries all four
copyright lines. Do not remove either.

## Use

Intended for authorised offensive-security work - red-team engagements, research, and detection
development against tooling you own or have written permission to test. You are responsible for
having that authorisation.
