using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Codecepticon.CommandLine;
using Codecepticon.Utils;

namespace Codecepticon.Modules
{
    class ModuleManager
    {
        /// <summary>
        /// True when --keep says to leave this name alone.
        ///
        /// Applied where the MAPPING is built rather than where the rename is
        /// performed: a name with no mapping is never renamed by anything, in
        /// any module, and the mapping file then honestly records what changed.
        /// Filtering later would leave half the pipeline believing a rename was
        /// going to happen.
        ///
        /// The pattern is matched unanchored, so `--keep Command` keeps
        /// everything containing "Command"; anchor it yourself (`^Exec$`) when
        /// that is not what you want. Case-sensitive, because the identifiers
        /// it is matching are.
        /// </summary>
        protected static bool ShouldKeep(string name)
        {
            // PowerShell and VB6 are case-insensitive languages, and their
            // collectors store names lowercased for that reason - matching a
            // natural-case pattern against them case-sensitively would never
            // hit. C# is case-sensitive and matched as written.
            bool ignoreCase = CommandLineData.Global.Module == ModuleTypes.CodecepticonModules.Powershell
                           || CommandLineData.Global.Module == ModuleTypes.CodecepticonModules.Vb6;

            Regex keep = ignoreCase
                ? CommandLineData.Global.KeepPatternIgnoreCase
                : CommandLineData.Global.KeepPattern;

            if (keep == null || String.IsNullOrEmpty(name))
            {
                return false;
            }

            if (!keep.IsMatch(name))
            {
                return false;
            }

            Logger.Verbose($"--keep: leaving '{name}' alone");
            return true;
        }

        protected static async Task<string> GenerateName(NameGenerator nameGenerator, Func<string, bool> isMappingUnique)
        {
            string name;
            int duplicateAttempts = 0;
            int attemptLimit = 100;
            do
            {
                name = nameGenerator.Generate();
                if (isMappingUnique(name))
                {
                    break;
                }
            } while (++duplicateAttempts < attemptLimit);

            if (duplicateAttempts >= attemptLimit)
            {
                Logger.Error($"Failed {attemptLimit} times to generate a unique string that is not already a mapping. Increase your character set / length / dictionary, and try again.");
                return "";
            }

            return name;
        }
    }
}
