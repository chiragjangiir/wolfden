using Wolfden.Core.Text;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Documents;

/// <summary>
/// A YAML file such as <c>den.yaml</c>, <c>pack.yaml</c>, or a type
/// definition, parsed as one YAML document (format.md §3). It writes back
/// byte for byte, and edits change only the value they set.
/// </summary>
/// <remarks>
/// Paths name keys in mappings and item numbers from 0 in lists, as in
/// <see cref="MarkdownFile.WithFrontmatterValue(IReadOnlyList{string}, YamlScalarValue)"/>.
/// </remarks>
public sealed class YamlFile : DenFile
{
    private readonly YamlBlock? _block;

    private YamlFile(byte[] bytes)
        : base(bytes)
    {
        if (Source is null)
        {
            return;
        }

        _block = YamlBlock.Parse(Source, 0, Source.Text.Length);
        AddDiagnostics(_block.Diagnostics);
        SortDiagnostics();
    }

    /// <summary>The top-level mapping, or null if the file is empty or isn't a valid mapping.</summary>
    public YamlMapping? Root => _block?.RootMapping;

    /// <summary>Parses a YAML file. Problems come back as <see cref="DenFile.Diagnostics"/>.</summary>
    public static YamlFile Parse(ReadOnlySpan<byte> bytes) => new(bytes.ToArray());

    /// <summary>The value at <paramref name="path"/>, or null.</summary>
    public YamlNode? GetValue(params IReadOnlyList<string> path) => Find(Root, path);

    /// <summary>Sets a top-level key, adding it after the existing keys if it's missing.</summary>
    public YamlFile WithValue(string key, YamlScalarValue value) => WithValue([key], value);

    /// <summary>Sets the scalar at <paramref name="path"/>, creating missing keys.</summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, or something on the path isn't a mapping or list.</exception>
    /// <exception cref="NotSupportedException">The edit can't be made in place without reformatting.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public YamlFile WithValue(IReadOnlyList<string> path, YamlScalarValue value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        return Edit(
            (source, block) => YamlEditor.SetScalar(source, block, path, value),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path, value),
            $"Setting '{string.Join('.', path)}'");
    }

    /// <summary>
    /// Adds <paramref name="item"/> to the list at <paramref name="path"/>,
    /// before item <paramref name="index"/> or at the end, in the list's own
    /// style. See <see cref="MarkdownFile.WithNewListItem"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is past the end of the list.</exception>
    /// <exception cref="InvalidOperationException">The file can't be edited, or the value at the path isn't a list.</exception>
    /// <exception cref="NotSupportedException">The item can't be added without moving a comment or reformatting.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public YamlFile WithNewListItem(IReadOnlyList<string> path, YamlValue item, int? index = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(item);
        var at = index ?? (GetValue(path) as YamlSequence)?.Items.Count ?? 0;
        return Edit(
            (source, block) => YamlEditor.AddItem(source, block, path, item, index),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path, (b, a) => YamlTreeCheck.AddedItem(b, a, at, item)),
            $"Adding an item to '{string.Join('.', path)}'");
    }

    /// <summary>
    /// Removes the key or list item at <paramref name="path"/>. Nothing
    /// changes if it isn't there. <paramref name="emptied"/> says what a block
    /// list under a key becomes when its last item goes: <c>key:</c> with no
    /// value (the default) or <c>key: []</c>. See <see cref="MarkdownFile.WithoutFrontmatterValue"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be edited, or the path doesn't fit the file's shape:
    /// a segment that isn't an item number where the file holds a list, or a path into a scalar. A missing key
    /// or an item number past the end of a list isn't an error: nothing changes.</exception>
    /// <exception cref="NotSupportedException">The removal would delete or move a comment that isn't part of it,
    /// or touches an explicit <c>? key</c>.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public YamlFile WithoutValue(IReadOnlyList<string> path, EmptiedList emptied = EmptiedList.Null)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Edit(
            (source, block) => YamlEditor.Remove(source, block, path, emptied, out _),
            (before, after) => YamlTreeCheck.SameExcept(before, after, path.Take(path.Count - 1).ToList(), (b, a) => YamlTreeCheck.Removed(b, a, path[^1], emptied)),
            $"Removing '{string.Join('.', path)}'");
    }

    private YamlFile Edit(Func<SourceText, YamlBlock, List<TextEdit>> edit, Func<YamlNode?, YamlNode?, bool> check, string what)
    {
        EnsureEditable();
        var edits = edit(Source!, _block!);
        if (edits.Count == 0)
        {
            return this;
        }

        var result = new YamlFile(ApplyEdits(edits));
        if (!result.CanEdit || !check(_block!.Root, result._block!.Root))
        {
            throw new EditVerificationException($"{what} didn't read back as written, or changed another value, so the edit was refused.");
        }

        return result;
    }
}
