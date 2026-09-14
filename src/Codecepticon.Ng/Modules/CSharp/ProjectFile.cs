using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace Codecepticon.Modules.CSharp
{
    /// <summary>
    /// Drop-in replacement for Microsoft.Build.Evaluation.Project, backed by raw
    /// XML instead of MSBuild's object model.
    ///
    /// WHY: loading Microsoft.Build.Evaluation pulls MSBuild into this process,
    /// which is the exact failure that kills upstream Codecepticon against
    /// MSBuild 18 (MissingMethodException: FrozenSet.Create, from
    /// XMakeElements..cctor). Everything the profiles actually do with a
    /// ProjectFile is read a property, set a property, drop a
    /// Compile item, and save - none of which needs an evaluation engine.
    ///
    /// BEHAVIOUR DIFFERENCE, deliberate and worth knowing: MSBuild's
    /// GetPropertyValue *evaluates* - it follows imports, applies conditions and
    /// returns SDK defaults for properties the file never mentions. This reads
    /// literal text out of one file. For the properties Codecepticon touches
    /// (RootNamespace, AssemblyName, StartupObject, Company, Product,
    /// Configuration) the literal value is what matters, and the two defaults
    /// callers depend on are reproduced explicitly in GetPropertyValue.
    /// </summary>
    class ProjectFile
    {
        public string FullPath { get; private set; }

        private XDocument _document;
        private XNamespace _ns;
        private string _newLine = Environment.NewLine;
        private readonly Dictionary<string, string> _globalProperties = new Dictionary<string, string>();

        public IReadOnlyDictionary<string, string> GlobalProperties => _globalProperties;

        public static ProjectFile Load(string path)
        {
            string fullPath = Path.GetFullPath(path);
            XDocument document = XDocument.Load(fullPath, LoadOptions.PreserveWhitespace);

            return new ProjectFile
            {
                FullPath = fullPath,
                _document = document,
                // Legacy (non-SDK) projects live in the 2003 MSBuild namespace;
                // SDK-style projects have none. Both must work - the tools this
                // obfuscates (Seatbelt, Rubeus, SharpHound...) are mostly legacy.
                _ns = document.Root == null ? XNamespace.None : document.Root.GetDefaultNamespace(),
                _newLine = DetectNewLine(fullPath)
            };
        }

        /// <summary>
        /// Whichever line ending the project file already uses.
        ///
        /// This has to be read from the raw bytes, because by the time XML
        /// parsing is done the information is gone: the XML spec MANDATES
        /// line-end normalisation, so a CRLF file arrives in memory as LF.
        /// Saving it back then rewrote every line of a Windows-authored csproj
        /// to LF, and the lines we touched picked up Environment.NewLine on top
        /// - a one-line property change produced a whole-file diff with mixed
        /// endings. Codecepticon edits the user's real project, in a repo they
        /// have to review; it should change the lines it means to change.
        /// </summary>
        private static string DetectNewLine(string path)
        {
            try
            {
                string text = File.ReadAllText(path);
                int crlf = 0;
                int lf = 0;

                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\n')
                    {
                        continue;
                    }

                    if (i > 0 && text[i - 1] == '\r')
                    {
                        crlf++;
                    }
                    else
                    {
                        lf++;
                    }
                }

                // Ties go to CRLF: a project file with no newlines at all is
                // almost certainly Windows-authored, and so is a mixed one.
                return lf > crlf ? "\n" : "\r\n";
            }
            catch (Exception)
            {
                return Environment.NewLine;
            }
        }

        /// <summary>
        /// Last writer wins, matching MSBuild's evaluation order for properties
        /// declared more than once.
        /// </summary>
        public string GetPropertyValue(string name)
        {
            XElement element = PropertyElements(name).LastOrDefault();
            if (element != null)
            {
                return element.Value;
            }

            // The two SDK defaults callers rely on. Certify.After() indexes
            // straight into the namespace mapping with whatever comes back here,
            // so returning "" for an unset RootNamespace would turn a missing
            // property into a KeyNotFoundException.
            switch (name)
            {
                case "RootNamespace":
                case "AssemblyName":
                    return Path.GetFileNameWithoutExtension(FullPath);
                default:
                    return String.Empty;
            }
        }

        public void SetProperty(string name, string value)
        {
            XElement existing = PropertyElements(name).LastOrDefault();
            if (existing != null)
            {
                existing.Value = value;
                return;
            }

            // Prefer an unconditioned PropertyGroup; a conditioned one only
            // applies to some configurations, which is not what callers mean.
            XElement group = _document.Root
                .Elements(_ns + "PropertyGroup")
                .FirstOrDefault(g => g.Attribute("Condition") == null);

            if (group == null)
            {
                group = new XElement(_ns + "PropertyGroup");
                _document.Root.AddFirst(group);
            }

            AppendChild(group, new XElement(_ns + name, value));
        }

        /// <summary>
        /// Appends an element on its own line at the same indentation as its
        /// siblings. Codecepticon writes into the user's real project file -
        /// producing one 400-column line of jammed-together elements is valid
        /// XML and a terrible diff.
        /// </summary>
        private void AppendChild(XElement group, XElement child)
        {
            // With PreserveWhitespace the last node is the run of whitespace
            // before </PropertyGroup>, and the one before the first element is
            // the child indentation. Reuse the latter and insert ahead of the
            // former, so the closing tag stays where it was.
            //
            // The fallback uses the file's own newline, not Environment.NewLine:
            // obfuscating a Windows csproj from Linux (or the reverse) should
            // not flip line endings on the lines it writes.
            XText firstText = group.Nodes().OfType<XText>().FirstOrDefault();
            string indent = firstText == null ? "\n    " : firstText.Value;
            XText trailing = group.LastNode as XText;

            if (trailing != null)
            {
                trailing.AddBeforeSelf(new XText(indent), child);
            }
            else
            {
                group.Add(new XText(indent), child);
            }
        }

        /// <summary>
        /// Global properties are never persisted to the file - MSBuild does not
        /// write them on Save() either. They are collected here and handed to
        /// the external build as -p:Name=Value.
        /// </summary>
        public void SetGlobalProperty(string name, string value)
        {
            _globalProperties[name] = value;
        }

        /// <summary>
        /// True for SDK-style projects - &lt;Project Sdk="Microsoft.NET.Sdk"&gt; or a
        /// &lt;Sdk&gt; child element; false for legacy (non-SDK) projects.
        ///
        /// This decides whether a source file has to be listed in the project at
        /// all, and the two worlds want opposite things:
        ///
        ///   SDK-style   globs **/*.cs from the project directory implicitly, so
        ///               an explicit &lt;Compile Include&gt; for a file that lives
        ///               there is a DUPLICATE - the build fails with NETSDK1022.
        ///   Legacy      has no glob, so the same entry is MANDATORY or the file
        ///               is silently not compiled.
        ///
        /// Codecepticon adds a file when rewriting strings, so it has to know
        /// which world it is in. See BaseProfile.ReconcileAddedStringsFile.
        /// </summary>
        public bool IsSdkStyle
        {
            get
            {
                if (_document.Root == null)
                {
                    return false;
                }

                return _document.Root.Attribute("Sdk") != null
                    || _document.Root.Elements(_ns + "Sdk").Any();
            }
        }

        public IEnumerable<ProjectFileItem> GetItems(string itemType)
        {
            return _document.Root
                .Elements(_ns + "ItemGroup")
                .Elements(_ns + itemType)
                .Select(e => new ProjectFileItem(e))
                .ToList();
        }

        public void RemoveItem(ProjectFileItem item)
        {
            if (item == null || item.Element == null)
            {
                return;
            }

            XElement parent = item.Element.Parent;
            item.Element.Remove();

            // Don't leave an empty <ItemGroup/> behind. Harmless to MSBuild, but
            // it shows up as noise in the diff of an obfuscated project.
            if (parent != null && !parent.Elements().Any())
            {
                parent.Remove();
            }
        }

        public void Save()
        {
            // Preserve the declaration legacy csproj files carry; writing one
            // where there was none (or dropping it) is a spurious diff at best
            // and a parse failure for some tooling at worst.
            var settings = new System.Xml.XmlWriterSettings
            {
                OmitXmlDeclaration = _document.Declaration == null,
                Indent = false,
                Encoding = new UTF8Encoding(true),

                // XML parsing normalised every line ending to LF on load, so
                // without this a CRLF project file is rewritten entirely to LF -
                // a whole-file diff for a one-property change. Replace puts the
                // file's own convention back on the way out.
                NewLineHandling = System.Xml.NewLineHandling.Replace,
                NewLineChars = _newLine
            };

            using (var writer = System.Xml.XmlWriter.Create(FullPath, settings))
            {
                _document.Save(writer);
            }
        }

        private IEnumerable<XElement> PropertyElements(string name)
        {
            return _document.Root
                .Elements(_ns + "PropertyGroup")
                .Elements(_ns + name);
        }
    }

    class ProjectFileItem
    {
        internal XElement Element { get; private set; }

        /// <summary>
        /// The literal Include attribute. Named to match
        /// Microsoft.Build.Evaluation.ProjectItem.EvaluatedInclude so call sites
        /// port without edits - though nothing is evaluated here.
        /// </summary>
        public string EvaluatedInclude
        {
            get
            {
                XAttribute include = Element.Attribute("Include");
                return include == null ? String.Empty : include.Value;
            }
        }

        public ProjectFileItem(XElement element)
        {
            Element = element;
        }
    }
}
