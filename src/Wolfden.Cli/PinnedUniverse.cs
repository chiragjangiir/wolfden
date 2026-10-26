using Wolfden.Core.Structure;

namespace Wolfden.Cli;

/// <summary>
/// <c>--universe</c> for a command that pins a long-running session to one
/// universe (<c>mcp</c>, <c>serve</c>): the key must be one the den has,
/// and <c>*</c> (every universe) can't be pinned.
/// </summary>
internal static class PinnedUniverse
{
    /// <summary>True if <paramref name="pinned"/> is null or a universe of the den at <paramref name="root"/>; otherwise false after reporting why.</summary>
    public static bool Check(string command, string root, string? pinned, TextWriter error)
    {
        if (pinned is null)
        {
            return true;
        }

        if (pinned == Mcp.DenTools.AllUniverses)
        {
            error.WriteLine($"wolfden {command}: '*' (every universe) can't be pinned: --universe pins one universe. Leave it out to serve them all.");
            return false;
        }

        var structure = DenStructure.Load(root);
        if (!structure.IsLoaded)
        {
            error.WriteLine($"wolfden {command}: den.yaml doesn't load, so universe '{pinned}' can't be found. Run 'wolfden check' to see why.");
            return false;
        }

        if (structure.FindUniverse(pinned) is null)
        {
            var keys = structure.Universes.Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
            error.WriteLine(
                $"wolfden {command}: the den has no universe '{pinned}'." + (keys.Count == 0 ? " It has no universes yet." : $" Its universes: {string.Join(", ", keys)}."));
            return false;
        }

        return true;
    }
}
