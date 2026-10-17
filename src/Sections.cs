namespace GapCloser
{
    // the config sections, numbered so BepInEx writes them in this order (it sorts them by name), the test and log
    // settings last
    internal static class Sections
    {
        internal const string General = "1 - General";
        internal const string Options = "2 - Options";
        internal const string Fixes = "3 - Fixes";
        internal const string RockSupport = "4 - Rock Support";
        internal const string Debug = "9 - Debug";
    }
}
