namespace Codecepticon.Modules
{
    /// <summary>
    /// What actually happened, so Program.Main can pick an exit code that says
    /// something useful.
    ///
    /// "It failed" is not enough for a caller that has to decide what to do
    /// next. The case that matters most is the one this tool can uniquely cause:
    /// the source on disk has been rewritten AND does not compile. A pipeline
    /// hitting that must restore the tree; a pipeline hitting a bad command line
    /// must not, because nothing was touched.
    /// </summary>
    static class RunOutcome
    {
        /// <summary>
        /// The rewrite was applied to disk, but building the result failed.
        /// Distinct from every other failure because the working tree is now
        /// obfuscated and broken, and somebody has to restore it.
        /// </summary>
        public static bool ObfuscatedButBuildFailed;
    }
}
