namespace Wolfden.Core.Yaml;

/// <summary>
/// What a block list under a key becomes when its last item is removed
/// (format.md F-RT-6). A flow list becomes <c>[]</c> either way.
/// </summary>
public enum EmptiedList
{
    /// <summary>
    /// The key is left with no value (<c>aliases:</c>), which reads as null:
    /// unset, the same as leaving the key out. The default, for every field
    /// but a chapter's list fields.
    /// </summary>
    Null,

    /// <summary>
    /// The key gets an empty flow list (<c>pov: []</c>). For a chapter's list
    /// fields in <c>book.md</c>, where <c>[]</c> means "none here" and null
    /// inherits from <c>chapter_defaults</c> (types.md F-CHAP-5).
    /// </summary>
    EmptyList,
}
