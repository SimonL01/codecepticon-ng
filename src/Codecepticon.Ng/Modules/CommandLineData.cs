using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Codecepticon.Modules;
using Codecepticon.Modules.CSharp.Profiles;
using Codecepticon.Utils;
using Codecepticon.Utils.MarkovWordGenerator;
using Microsoft.PowerShell.Commands;

namespace Codecepticon.CommandLine
{
    class CommandLineData
    {
        public enum Action
        {
            None = 0,
            Obfuscate = 1,
            Unmap = 2,
            GenerateCertificate = 3,
            Sign = 4,
        }

        public struct ProjectStruct
        {
            public string Path;
            public bool Verbose;
            public bool Debug;
            public Guid Guid;
            public string SaveAs;
        }

        public struct CharacterSetStruct
        {
            public string Value;
            public int Length;
        }

        public struct DictionaryStruct
        {
            public List<string> Words;
        }

        public struct NameGeneratorStruct
        {
            public CharacterSetStruct CharacterSet;
            public DictionaryStruct Dictionary;
        }

        public struct VSBuildStruct
        {
            public bool Build;
            public string Configuration;
            public Dictionary<string, string> Settings;
            public string OutputPath;
            public bool Precompile;

            /// <summary>
            /// Compile the result after rewriting and fail if it does not build.
            /// Implies a build - it is the only way to know.
            /// </summary>
            public bool Verify;
        }

        public struct CSharpRenamingStruct
        {
            public bool Enabled;
            public bool Namespaces;
            public bool Classes;
            public bool Functions;
            public bool Enums;
            public bool Properties;
            public bool Variables;
            public bool Parameters;
            public bool CommandLine;
            public bool Structs;
        }

        public struct RewriteTemplateStruct
        {
            public string File;
            public string Namespace;
            public string Class;
            public string Function;
            public string Mapping;
            public string AddedFile;
        }

        public struct StringRewriteStruct
        {
            public bool Strings;

            public StringEncoding.StringEncodingMethods EncodingMethod;
            public CharacterSetStruct CharacterSet;
            public RewriteTemplateStruct Template;

            public Dictionary<string, string> SingleMapping;
            public Dictionary<string, int> GroupMapping;
            public string ExternalFile;
        }

        public struct MarkovGenerationStruct
        {
            public MarkovWordGenerator Generator;
            public int MinWords;
            public int MaxWords;
            public int MinLength;
            public int MaxLength;
        }

        public struct DataUnmap
        {
            public string MapFile;
            public string Directory;
            public string File;
            public bool Recursive;
        }

        public struct CSharpSettings
        {
            public CSharpRenamingStruct Rename;
            public VSBuildStruct Compilation;
            public BaseProfile Profile;

            /// <summary>
            /// Opt in to a solution with more than one project. Off by default:
            /// cross-project renaming is unverified, and the alternative (a Y/N
            /// prompt) cannot work in the unattended pipeline this tool is for.
            /// </summary>
            public bool AllowMultiProject;

            /// <summary>Copy the source aside before the first rewrite.</summary>
            public bool Backup;

            /// <summary>Where to put it; empty means next to the solution.</summary>
            public string BackupPath;

            /// <summary>
            /// Turn off the reflection collision warning. For a codebase where
            /// the literals are known to be unrelated and the noise is not worth
            /// reading past.
            /// </summary>
            public bool SkipReflectionScan;
        }

        public struct PowerShellRenamingStruct
        {
            public bool Enabled;
            public bool Functions;
            public bool Variables;
            public bool Parameters;
        }

        public struct Vb6RenamingStruct
        {
            public bool Enabled;
            public bool Identifiers;
        }

        public struct PowerShellSettings
        {
            public PowerShellRenamingStruct Rename;
        }

        public struct Vb6Settings
        {
            public Vb6RenamingStruct Rename;
        }

        public struct SignNewCertificate
        {
            public string Subject;
            public string Issuer;
            public DateTime NotBefore;
            public DateTime NotAfter;
            public string Password;
            public bool Overwrite;
            public string PfxFile;
            public string CopyFrom;
        }

        public struct SignSettings
        {
            public SignNewCertificate NewCertificate;
            public string TimestampServer;
            public string SignatureAlgorithm;
        }

        public struct RenameGeneratorStruct
        {
            public NameGenerator.RandomNameGeneratorMethods Method;
            public NameGeneratorStruct Data;
        }

        public struct GlobalSettings
        {
            public Action Action;
            public ProjectStruct Project;
            public NameGenerator NameGenerator;
            public DataUnmap Unmap;
            public StringRewriteStruct Rewrite;
            public RenameGeneratorStruct RenameGenerator;
            public string ConfigFile;
            public bool IsHelp;
            public string[] RawCmdLineArgs;
            public string RawConfigFile;
            public string Version;
            public MarkovGenerationStruct Markov;

            /// <summary>
            /// --strip-method: method names whose bodies are emptied. The generic
            /// form of what a tool profile does to printlogo/usage. Empty when unset.
            /// </summary>
            public List<string> StripMethods;

            /// <summary>
            /// --keep: names matching this are never renamed. Null when unset.
            /// Compiled once at parse time so a bad pattern fails the command
            /// line rather than the run.
            /// </summary>
            public System.Text.RegularExpressions.Regex KeepPattern;

            /// <summary>
            /// The same pattern, case-insensitive. Used for PowerShell and VB6,
            /// which are case-insensitive languages - their collectors store
            /// names lowercased, so a natural-case --keep would never match.
            /// </summary>
            public System.Text.RegularExpressions.Regex KeepPatternIgnoreCase;

            /// <summary>Which module is running. Set once in Program.Main.</summary>
            public ModuleTypes.CodecepticonModules Module;
        }

        // Below this line are the variables that are used to access live data.
        public static GlobalSettings Global = new GlobalSettings();
        public static CSharpSettings CSharp = new CSharpSettings();
        public static PowerShellSettings PowerShell = new PowerShellSettings();
        public static Vb6Settings Vb6 = new Vb6Settings();
        public static SignSettings Sign = new SignSettings();
    }
}
