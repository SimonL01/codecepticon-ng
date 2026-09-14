using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Codecepticon.Modules.CSharp.CommandLine;
using Codecepticon.Modules.PowerShell.CommandLine;
using Codecepticon.Modules.VB6.CommandLine;
using static Codecepticon.Modules.ModuleTypes;
using Codecepticon.Utils;
using System.Reflection;
using Codecepticon.Modules.Sign.CommandLine;

namespace Codecepticon.CommandLine
{
    class CommandLineManager
    {
        protected string[] Args;

        protected Dictionary<string, string> GlobalArguments = new Dictionary<string, string>
        {
            { "module", "" },
            { "help", "switch" },
            { "version", "switch" },
            { "config", "" },
            { "action", "" },
            { "verbose", "switch" },
            { "debug", "switch" },
            { "path", "" },
            { "save-as", "" },

            { "rename", "" },
            { "keep", "" },
            { "strip-method", "" },
            { "rename-method", "" },
            { "rename-charset", "" },
            { "rename-length", "" },
            { "rename-dictionary-file", "" },
            { "rename-dictionary", "" },

            /* These only apply for Markov generation */
            { "markov-min-length", "" },
            { "markov-max-length", "" },
            { "markov-min-words", "" },
            { "markov-max-words", "" },

            { "string-rewrite", "switch" },
            { "string-rewrite-method", "" },
            { "string-rewrite-charset", "" },
            { "string-rewrite-length", "" },
            { "string-rewrite-extfile", "" },

            { "map-file", "" },
            { "unmap-directory", "" },
            { "unmap-file", "" },
            { "unmap-recursive", "switch" },
        };

        protected Dictionary<string, string> Arguments = new Dictionary<string, string>();

        public CommandLineManager(string[] args)
        {
            Args = args;

            Init();
        }

        public bool Load()
        {
            Arguments = LoadCommandLine(Arguments);
            if (Arguments.Count == 1 && Arguments.ContainsKey("config"))
            {
                Arguments = LoadConfigFile(Arguments["config"]);
            }
            return Parse(Arguments);
        }

        /// <summary>
        /// Turns a path the user typed into an absolute one.
        ///
        /// Relative paths used to half-work: the file itself opened fine
        /// (resolved against the current directory by the OS), but anything
        /// derived from it broke. `--path good.ps1` made
        /// Path.GetDirectoryName() return "", so writing the mapping file threw
        /// "The value cannot be an empty string. (Parameter 'path')" and the run
        /// exited 1 having already rewritten the source.
        ///
        /// Resolving once, here, means every consumer downstream gets a path
        /// with a real directory component. Purely lexical - it does not require
        /// the path to exist, so it is equally valid for --save-as.
        /// </summary>
        private static string ResolveUserPath(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            try
            {
                return Path.GetFullPath(AppPaths.Normalise(value));
            }
            catch (Exception)
            {
                // Malformed path - hand it back untouched and let the module's
                // own validation report it with a better message than this.
                return value;
            }
        }

        public bool IsHelp()
        {
            Arguments = LoadCommandLine(GlobalArguments);
            return (Arguments.Count == 1 && Arguments.ContainsKey("help"));
        }

        /// <summary>
        /// --version, and nothing else. Deliberately mirrors IsHelp(): both are
        /// questions about the binary rather than instructions to it, so both
        /// answer before --module is required and neither is combinable with a
        /// real run.
        /// </summary>
        public bool IsVersion()
        {
            Arguments = LoadCommandLine(GlobalArguments);
            return (Arguments.Count == 1 && Arguments.ContainsKey("version"));
        }

        public string LoadHelp(CodecepticonModules module)
        {
            Dictionary<CodecepticonModules, string> helpMapping = new Dictionary<CodecepticonModules, string>
            {
                { CodecepticonModules.None, "Global.txt" },
                { CodecepticonModules.CSharp, "CSharp.txt" },
                { CodecepticonModules.Powershell, "PowerShell.txt" },
                { CodecepticonModules.Vb6, "VBA.txt" },
                { CodecepticonModules.Sign, "Sign.txt" }
            };

            string fileName = helpMapping.ContainsKey(module) ? helpMapping[module] : null;
            if (fileName == null)
            {
                return null;
            }

            fileName = AppPaths.InApp("Help", fileName);
            string sharedFile = AppPaths.InApp("Help", "Shared.txt");
            string headerFile = AppPaths.InApp("Help", "Header.txt");
            if (!File.Exists(fileName) || !File.Exists(sharedFile) || !File.Exists(headerFile))
            {
                return null;
            }
            return File
                .ReadAllText(fileName)
                .Replace("%%_SHARED_%%", File.ReadAllText(sharedFile))
                .Replace("%%_HEADER_%%", File.ReadAllText(headerFile))
                .Replace("%%_VERSION_%%", CommandLineData.Global.Version);
        }

        protected virtual bool Parse(Dictionary<string, string> arguments)
        {
            return false;
        }

        protected void MergeArguments()
        {
            Arguments = GlobalArguments
                .Concat(Arguments)
                .ToLookup(x => x.Key, x => x.Value).
                ToDictionary(x => x.Key, g => g.First());
        }

        public CodecepticonModules GetModule()
        {
            string name = GetArgument("--module");
            if (name == null)
            {
                // Check if there's a --config argument.
                string config = GetArgument("--config");
                if (config == null)
                {
                    return CodecepticonModules.None;
                }

                name = GetModuleFromConfigFile(config);
                if (name == null)
                {
                    Logger.Error("Config file is empty or does not exist", true, false);
                    return CodecepticonModules.None;
                }
            }

            return name switch
            {
                "csharp" or "cs" => CodecepticonModules.CSharp,
                "powershell" or "ps" => CodecepticonModules.Powershell,
                "vba" or "vb6" => CodecepticonModules.Vb6,
                "sign" => CodecepticonModules.Sign,
                _ => CodecepticonModules.Unknown
            };
        }

        protected string GetModuleFromConfigFile(string configFile)
        {
            if (!File.Exists(configFile))
            {
                return null;
            }

            // https://stackoverflow.com/a/1710942
            Dictionary<string, string> data = LoadConfigFile(configFile);
            return data.ContainsKey("module") ? data["module"] : null;
        }

        protected Dictionary<string, string> LoadConfigFile(string configFile)
        {
            XDocument document;
            try
            {
                CommandLineData.Global.RawConfigFile = File.ReadAllText(configFile);
                document = XDocument.Parse(CommandLineData.Global.RawConfigFile);
            } catch (Exception e)
            {
                Logger.Error($"Could not parse config file: {e.Message}");
                return new Dictionary<string, string>();
            }
            
            Dictionary<string, string> documentData = new Dictionary<string, string>();

            foreach (XElement element in document.Descendants().Where(p => p.HasElements == false))
            {
                documentData.Add(element.Name.LocalName, element.Value);
            }

            return documentData;
        }

        /// <summary>
        /// Turns a SPEC table (flag name -> "switch" or "") into a VALUE table
        /// (flag name -> what the user typed), dropping flags that were absent.
        ///
        /// It builds a new dictionary rather than writing back into the one it
        /// was handed, and that is load-bearing. Writing back overwrote the very
        /// "switch" markers it reads to decide whether a flag takes a value, so
        /// the call was destructive and not repeatable: a second call saw
        /// GlobalArguments["version"] == "true" instead of "switch", concluded
        /// --version takes a value, and read past the end of Args. IsHelp() and
        /// IsVersion() both run against GlobalArguments, so that second call is
        /// the normal path, not an edge case.
        ///
        /// MergeArguments() also assumes GlobalArguments is still a spec table
        /// when a module's parser concatenates its own flags onto it.
        /// </summary>
        protected Dictionary<string, string> LoadCommandLine(Dictionary<string, string> arguments)
        {
            Dictionary<string, string> values = new Dictionary<string, string>();

            foreach (KeyValuePair<string, string> parameter in arguments)
            {
                string value = GetArgument($"--{parameter.Key}", parameter.Value == "switch");

                // Absent flags are dropped, not stored as null: callers test
                // membership (ContainsKey) rather than nullness.
                if (value != null)
                {
                    values[parameter.Key] = value;
                }
            }

            return values;
        }

        protected virtual void Init()
        {
            SetRenameValue(false);

            CommandLineData.Global.RenameGenerator.Data.CharacterSet.Value = "";
            CommandLineData.Global.RenameGenerator.Data.CharacterSet.Length = -1;

            CommandLineData.Global.Rewrite.CharacterSet.Value = "";
            CommandLineData.Global.Rewrite.CharacterSet.Length = -1;

            CommandLineData.Global.Markov.MinLength = 3;
            CommandLineData.Global.Markov.MaxLength = 9;
            CommandLineData.Global.Markov.MinWords = 1;
            CommandLineData.Global.Markov.MaxWords = 4;

            CommandLineData.Global.RenameGenerator.Data.Dictionary.Words = new List<string>();

            CommandLineData.Global.Unmap.Directory = "";
            CommandLineData.Global.Unmap.File = "";

            CommandLineData.Global.Project.Guid = Guid.NewGuid();
            CommandLineData.Global.Action = CommandLineData.Action.None;

            CommandLineData.Global.RawCmdLineArgs = Args;
            CommandLineData.Global.RawConfigFile = "";
            CommandLineData.Global.Version = Assembly.GetExecutingAssembly().GetName().Version.Major.ToString() + "." + Assembly.GetExecutingAssembly().GetName().Version.Minor.ToString() + "." + Assembly.GetExecutingAssembly().GetName().Version.Build.ToString();
        }

        protected virtual void SetRenameValue(bool value)
        {
            // Placeholder.
        }

        protected string GetArgument(string name, bool isSwitch = false)
        {
            string value = null;

            for (int i = 0; i < Args.Length; i++)
            {
                if (Args[i].ToLower() == name.ToLower())
                {
                    if (isSwitch)
                    {
                        // This is a boolean switch, like --verbose, so we just return a non empty value.
                        value = "true";
                    }
                    else
                    {
                        // `<` not `<=`: at i == Args.Length - 1 the flag is the
                        // last token and has no value after it. The off-by-one
                        // here turned a typo like `--path` with nothing after it
                        // into an IndexOutOfRangeException and a core dump, which
                        // is exit 134 - not the documented exit 2 for a bad
                        // command line. Leaving it null lets validation report it.
                        if (i + 1 < Args.Length)
                        {
                            value = Args[i + 1];
                        }
                    }
                    break;
                }
            }

            return value;
        }

        public bool LoadCommandLineArguments(CodecepticonModules module)
        {
            bool result = false;
            switch (module)
            {
                case CodecepticonModules.CSharp:
                    CSharpCommandLine CSharpCommandManager = new CSharpCommandLine(Args);
                    result = CSharpCommandManager.Load();
                    break;
                case CodecepticonModules.Powershell:
                    PowerShellCommandLine PowerShellCommandManager = new PowerShellCommandLine(Args);
                    result = PowerShellCommandManager.Load();
                    break;
                case CodecepticonModules.Vb6:
                    Vb6CommandLine Vb6CommandManager = new Vb6CommandLine(Args);
                    result = Vb6CommandManager.Load();
                    break;
                case CodecepticonModules.Sign:
                    SignCommandLine SignCommandManager = new SignCommandLine(Args);
                    result = SignCommandManager.Load();
                    break;
            }

            return result;
        }

        protected List<string> ParseDictionaryFile(string filename)
        {
            List<string> words = File.ReadAllText(filename).Split('\r', '\n').ToList();
            return CleanDictionaryWords(words);
        }

        protected List<string> ParseDictionaryWords(string value)
        {
            List<string> words = value.Split(',').ToList();
            return CleanDictionaryWords(words);
        }

        protected List<string> CleanDictionaryWords(List<string> words)
        {
            // Trim all values, remove 
            return words
                .Select(s => s.Trim()) // Trim
                .Where(s => !string.IsNullOrWhiteSpace(s)) // Remove empty
                .Where(word => word.All(c => char.IsLetter(c))) // Remove items with non-alpha characters
                .Distinct() // Remove duplicates
                .ToList();
        }

        protected StringEncoding.StringEncodingMethods ParseStringRewriteMethod(string value)
        {
            return value switch
            {
                "base64" or "b64" => StringEncoding.StringEncodingMethods.Base64,
                "xor" => StringEncoding.StringEncodingMethods.XorEncrypt,
                "groupsub" or "group" => StringEncoding.StringEncodingMethods.GroupCharacterSubstitution,
                "singlesub" or "single" => StringEncoding.StringEncodingMethods.SingleCharacterSubstitution,
                "external" or "ext" or "file" => StringEncoding.StringEncodingMethods.ExternalFile,
                _ => StringEncoding.StringEncodingMethods.Unknown
            };
        }

        /// <summary>
        /// Compiles --keep once, here, so an invalid pattern is a command-line
        /// error (exit 2, nothing touched) rather than an exception thrown
        /// part-way through rewriting somebody's source.
        /// </summary>
        protected bool ParseKeepPattern(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                CommandLineData.Global.KeepPattern = null;
                CommandLineData.Global.KeepPatternIgnoreCase = null;
                return true;
            }

            try
            {
                CommandLineData.Global.KeepPattern = new Regex(value, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                CommandLineData.Global.KeepPatternIgnoreCase = new Regex(value, RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                return true;
            }
            catch (ArgumentException e)
            {
                Logger.Error($"--keep is not a valid regular expression: {value}");
                Logger.Error($"  {e.Message}");
                return false;
            }
        }

        protected NameGenerator.RandomNameGeneratorMethods ParseRenameMethod(string value)
        {
            return value switch
            {
                "random" => NameGenerator.RandomNameGeneratorMethods.RandomCombinations,
                "notwins" => NameGenerator.RandomNameGeneratorMethods.AvoidTwinCharacters,
                "dictionary" or "dict" => NameGenerator.RandomNameGeneratorMethods.DictionaryWords,
                "markov" => NameGenerator.RandomNameGeneratorMethods.Markov,
                _ => NameGenerator.RandomNameGeneratorMethods.None
            };
        }

        protected CommandLineData.Action ParseAction(string value)
        {
            return value switch
            {
                "obfuscate" => CommandLineData.Action.Obfuscate,
                "unmap" => CommandLineData.Action.Unmap,
                "cert" => CommandLineData.Action.GenerateCertificate,
                "sign" => CommandLineData.Action.Sign,
                _ => CommandLineData.Action.None
            };
        }

        protected bool ParseGlobalArguments(Dictionary<string, string> arguments)
        {
            foreach (KeyValuePair<string, string> argument in arguments)
            {
                switch (argument.Key.ToLower())
                {
                    case "help":
                        CommandLineData.Global.IsHelp = true;
                        return false;
                    case "action":
                        CommandLineData.Global.Action = ParseAction(argument.Value.ToLower());
                        break;
                    case "path":
                        CommandLineData.Global.Project.Path = ResolveUserPath(argument.Value);
                        break;
                    case "verbose":
                        if (argument.Value.ToLower() != "false")
                        {
                            CommandLineData.Global.Project.Verbose = (argument.Value.Length > 0);
                            Logger.IsVerbose = CommandLineData.Global.Project.Verbose;
                        }
                        break;
                    case "debug":
                        if (argument.Value.ToLower() != "false")
                        {
                            CommandLineData.Global.Project.Debug = (argument.Value.Length > 0);
                            Logger.IsDebug = CommandLineData.Global.Project.Debug;
                        }
                        break;
                    case "save-as":
                        CommandLineData.Global.Project.SaveAs = ResolveUserPath(argument.Value);
                        break;
                    case "rename-method":
                        CommandLineData.Global.RenameGenerator.Method = ParseRenameMethod(argument.Value.ToLower());
                        break;
                    case "keep":
                        if (!ParseKeepPattern(argument.Value))
                        {
                            return false;
                        }
                        break;
                    case "strip-method":
                        CommandLineData.Global.StripMethods = argument.Value
                            .Split(',')
                            .Select(m => m.Trim())
                            .Where(m => m.Length > 0)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        break;
                    case "rename-charset":
                        // Remove duplicate characters from string too.
                        CommandLineData.Global.RenameGenerator.Data.CharacterSet.Value = new string(argument.Value.ToCharArray().Distinct().ToArray());
                        break;
                    case "rename-length":
                        try
                        {
                            CommandLineData.Global.RenameGenerator.Data.CharacterSet.Length = (argument.Value.ToLower() == "random") ? 0 : int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.RenameGenerator.Data.CharacterSet.Length = 0;
                        }
                        break;
                    case "rename-dictionary":
                        if (argument.Value.Length > 0)
                        {
                            CommandLineData.Global.RenameGenerator.Data.Dictionary.Words = ParseDictionaryWords(argument.Value);
                        }
                        break;
                    case "rename-dictionary-file":
                        if (argument.Value.Length > 0)
                        {
                            if (!File.Exists(argument.Value))
                            {
                                Logger.Error($"Dictionary file does not exist: {argument.Value}");
                                return false;
                            }
                            CommandLineData.Global.RenameGenerator.Data.Dictionary.Words = ParseDictionaryFile(argument.Value);
                        }
                        break;
                    case "markov-min-length":
                        try
                        {
                            CommandLineData.Global.Markov.MinLength = int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.Markov.MinLength = 0;
                        }
                        break;
                    case "markov-max-length":
                        try
                        {
                            CommandLineData.Global.Markov.MaxLength = int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.Markov.MaxLength = 0;
                        }
                        break;
                    case "markov-min-words":
                        try
                        {
                            CommandLineData.Global.Markov.MinWords = int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.Markov.MinWords = 0;
                        }
                        break;
                    case "markov-max-words":
                        try
                        {
                            CommandLineData.Global.Markov.MaxWords = int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.Markov.MaxWords = 0;
                        }
                        break;
                    case "string-rewrite":
                        if (argument.Value.ToLower() != "false")
                        {
                            CommandLineData.Global.Rewrite.Strings = (argument.Value.Length > 0);
                        }   
                        break;
                    case "string-rewrite-method":
                        CommandLineData.Global.Rewrite.EncodingMethod = ParseStringRewriteMethod(argument.Value.ToLower());
                        break;
                    case "string-rewrite-charset":
                        CommandLineData.Global.Rewrite.CharacterSet.Value = new string(argument.Value.ToCharArray().Distinct().ToArray());
                        break;
                    case "string-rewrite-length":
                        try
                        {
                            CommandLineData.Global.Rewrite.CharacterSet.Length = int.Parse(argument.Value);
                        }
                        catch (FormatException)
                        {
                            CommandLineData.Global.Rewrite.CharacterSet.Length = 0;
                        }
                        break;
                    case "string-rewrite-extfile":
                        CommandLineData.Global.Rewrite.ExternalFile = argument.Value;
                        break;
                    case "map-file":
                        CommandLineData.Global.Unmap.MapFile = argument.Value;
                        break;
                    case "unmap-directory":
                        CommandLineData.Global.Unmap.Directory = argument.Value;
                        break;
                    case "unmap-file":
                        CommandLineData.Global.Unmap.File = argument.Value;
                        break;
                    case "unmap-recursive":
                        if (argument.Value.ToLower() != "false")
                        {
                            CommandLineData.Global.Unmap.Recursive = (argument.Value.Length > 0);
                        }   
                        break;
                }
            }

            // If Debug is enabled, force-enable Verbose.
            if (CommandLineData.Global.Project.Debug)
            {
                CommandLineData.Global.Project.Verbose = Logger.IsVerbose = Logger.IsDebug = true;
            }

            return true;
        }
    }
}
