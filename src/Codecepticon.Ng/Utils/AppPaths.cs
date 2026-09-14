using System;
using System.IO;

namespace Codecepticon.Utils
{
    /// <summary>
    /// Resolves the data files that ship next to the executable - Help/ and
    /// Templates/.
    ///
    /// Fixes two bugs that upstream carried, one of which bites on Windows too:
    ///
    /// 1. SEPARATORS. Paths were written as @".\Help\Global.txt". On Linux that
    ///    is not a path at all - it is one filename that happens to contain
    ///    backslashes - so help output came back silently empty and
    ///    mapping-file generation threw FileNotFoundException.
    ///
    /// 2. ANCHOR, and this one is not platform-specific. ".\" resolves against
    ///    the CURRENT DIRECTORY, not the executable's. Codecepticon therefore
    ///    only found its own templates when invoked from its install directory.
    ///    Any caller that sets its own working directory - a Jenkins job, a
    ///    build step, `cd` anywhere else - got the same empty help and the same
    ///    missing templates. Anchoring to AppContext.BaseDirectory makes the
    ///    tool location-independent, which is what a CLI invoked by a pipeline
    ///    has to be.
    /// </summary>
    static class AppPaths
    {
        /// <summary>Directory the executable lives in, with a trailing separator.</summary>
        public static string BaseDirectory => AppContext.BaseDirectory;

        /// <summary>
        /// Combines path segments against the executable's directory. Segments
        /// may themselves contain either separator; both are normalised.
        /// </summary>
        public static string InApp(params string[] segments)
        {
            string combined = BaseDirectory;
            foreach (string segment in segments)
            {
                combined = Path.Combine(combined, Normalise(segment));
            }
            return Path.GetFullPath(combined);
        }

        /// <summary>
        /// Swaps whichever separator the string was written with for the one
        /// this platform uses. Windows accepts '/' natively, so this is a no-op
        /// there in practice - it exists so the same source runs on Linux.
        /// </summary>
        public static string Normalise(string path)
        {
            if (String.IsNullOrEmpty(path))
            {
                return path;
            }

            return path
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
