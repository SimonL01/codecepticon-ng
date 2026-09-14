using Codecepticon.CommandLine;
using Codecepticon.Modules;
using Codecepticon.Modules.CSharp;
using Codecepticon.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Codecepticon.Modules.PowerShell;
using Codecepticon.Modules.VB6;
using static Codecepticon.Modules.ModuleTypes;
using Newtonsoft.Json;
using Codecepticon.Modules.Sign;

namespace Codecepticon
{
    class Program
    {
        /// <summary>Obfuscation completed.</summary>
        private const int ExitSuccess = 0;

        /// <summary>Ran, but something failed. Anything reported via Logger.Error.</summary>
        private const int ExitFailure = 1;

        /// <summary>The command line was wrong - nothing was attempted.</summary>
        private const int ExitBadArguments = 2;

        /// <summary>
        /// The rewrite was applied AND the result does not build. Distinct from
        /// ExitFailure because the working tree is now obfuscated and broken:
        /// the caller has to restore it, which is not true of any other failure.
        /// </summary>
        private const int ExitObfuscatedButBroken = 3;

        /// <summary>
        /// Returns a real exit code, because this tool's whole purpose is to run
        /// unattended in a build pipeline and CI can only see the exit code.
        ///
        /// Upstream returned void, so a failed run and a successful one were
        /// indistinguishable to a caller: `codecepticon ... || echo failed` never
        /// fired, and a pipeline would happily ship an unobfuscated binary.
        ///
        ///   0  obfuscation completed
        ///   1  a runtime failure (see Logger.HasErrors), or an unhandled exception
        ///   2  bad arguments - nothing was attempted, fix the command line
        ///   3  obfuscated, but the result does not build - RESTORE THE SOURCE
        ///
        /// 3 is split out of 1 deliberately. A pipeline hitting 2 can retry with
        /// a corrected command line; a pipeline hitting 3 must restore the tree
        /// before doing anything else, because the working copy is rewritten.
        /// </summary>
        static async Task<int> Main(string[] args)
        {
            if (args.Length == 0)
            {
                Logger.Info("Run --help for more, or use the Command Line Generator HTML file to generate a command", true, false);
                return ExitBadArguments;
            }

            CommandLineManager cmdManager = new CommandLineManager(args);
            if (cmdManager.IsHelp())
            {
                Logger.Info(cmdManager.LoadHelp(CodecepticonModules.None), true, false);
                return ExitSuccess;
            }

            // Before GetModule(), because --version is a question about this
            // binary and has no module to speak of. Answering it after the check
            // below would reject it as "no module is defined" - which is the one
            // failure you most want a version number in order to diagnose.
            if (cmdManager.IsVersion())
            {
                Logger.Info(VersionInfo.Render(), true, false);
                return ExitSuccess;
            }

            CodecepticonModules module = cmdManager.GetModule();
            if (module == CodecepticonModules.None || module == CodecepticonModules.Unknown)
            {
                Logger.Error("No module is defined or module is invalid.", true, false);
                return ExitBadArguments;
            }

            // Recorded so shared code can behave per-language - ShouldKeep needs
            // to know whether identifiers are case-sensitive.
            CommandLineData.Global.Module = module;

            Logger.Info($"Codecepticon v{CommandLineData.Global.Version} is starting...");
            if (!cmdManager.LoadCommandLineArguments(module))
            {
                if (CommandLineData.Global.IsHelp)
                {
                    Logger.Info(cmdManager.LoadHelp(module), true, false);
                    return ExitSuccess;
                }
                Logger.Error("Could not parse command line.");
                return ExitBadArguments;
            }

            Logger.Debug("Global Command Line Data");
            Logger.Debug(JsonConvert.SerializeObject(CommandLineData.Global));

            Logger.Debug("C# Command Line Data");
            Logger.Debug(JsonConvert.SerializeObject(CommandLineData.CSharp));

            Logger.Debug("VB6 Command Line Data");
            Logger.Debug(JsonConvert.SerializeObject(CommandLineData.Vb6));

            Logger.Debug("PowerShell Command Line Data");
            Logger.Debug(JsonConvert.SerializeObject(CommandLineData.PowerShell));

            try
            {
                switch (module)
                {
                    case CodecepticonModules.CSharp:
                        CSharpManager CSharpManager = new CSharpManager();
                        await CSharpManager.Run();
                        break;
                    case CodecepticonModules.Powershell:
                        PowerShellManager PowerShellManager = new PowerShellManager();
                        await PowerShellManager.Run();
                        break;
                    case CodecepticonModules.Vb6:
                        Vb6Manager vb6Manager = new Vb6Manager();
                        await vb6Manager.Run();
                        break;
                    case CodecepticonModules.Sign:
                        SignManager signManager = new SignManager();
                        await signManager.Run();
                        break;
                    default:
                        Logger.Error("Code error: Module manager not implemented.");
                        return ExitBadArguments;
                }
            }
            catch (Exception e)
            {
                // An unhandled exception here used to abort the process with a
                // raw CLR stack trace and whatever exit code the runtime chose.
                // Name it, log it like every other failure, and exit 1.
                Logger.Error($"Unhandled {e.GetType().Name}: {e.Message}");
                Logger.Debug(e.ToString());
                return ExitFailure;
            }

            // The modules report failures through Logger.Error and return void,
            // so this is what "did it actually work" reduces to. The one case
            // worth distinguishing is a rewrite that landed on disk and then
            // failed to build - the caller has cleanup to do.
            if (RunOutcome.ObfuscatedButBuildFailed)
            {
                return ExitObfuscatedButBroken;
            }

            return Logger.HasErrors ? ExitFailure : ExitSuccess;
        }
    }
}
