using Wolfden.Core.Structure;
using Wolfden.Index.Reading;

namespace Wolfden.Index.Writing;

public sealed partial class DenTools
{
    /// <summary>
    /// The den's enabled types, from the built-in packs and the den's own
    /// (types.md F-PACK-7): what a new entity can be made from (#29). Types
    /// belong to the den, not to a universe, so every universe has the same
    /// ones. Containers and chapters aren't types (F-TYPE-2), so every type
    /// listed can be created in a universe, a series, or a book.
    /// </summary>
    public TypeListResult ListTypes()
    {
        DenStructure structure;
        try
        {
            structure = DenStructure.Load(Snapshot());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new DenToolException(ToolErrorCodes.DenUnreadable, $"Couldn't read the den's files: {e.Message}");
        }

        if (!structure.IsLoaded)
        {
            throw new DenToolException(
                ToolErrorCodes.DenNotLoaded,
                $"den.yaml doesn't load, so the den has no types to make notes from. {ToolWords.Cap(Words.CheckTool)} to see why.");
        }

        var types = structure.Types.Types
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Key, StringComparer.Ordinal)
            .Select(t => new TypeView(t.Key, t.Name, t.Plural, t.Description, t.Pack.Key, t.IsBuiltIn))
            .ToList();
        return new TypeListResult(DenReader.ResultVersion, types);
    }
}
