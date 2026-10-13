using Wolfden.Core.Yaml;

namespace Wolfden.Core.Documents;

/// <summary>
/// A YAML file such as <c>den.yaml</c>, <c>pack.yaml</c>, or a type
/// definition, parsed as one YAML document (format.md §3). It writes back
/// byte for byte, and edits change only the value they set.
/// </summary>
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
    /// <exception cref="InvalidOperationException">The file can't be edited, or a key on the path isn't a mapping.</exception>
    /// <exception cref="NotSupportedException">The edit would have to change a flow mapping.</exception>
    /// <exception cref="EditVerificationException">The edit didn't read back as intended (a bug).</exception>
    public YamlFile WithValue(IReadOnlyList<string> path, YamlScalarValue value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        EnsureEditable();
        var edits = YamlEditor.SetScalar(Source!, _block!, path, value);
        if (edits.Count == 0)
        {
            return this;
        }

        var result = new YamlFile(ApplyEdits(edits));
        if (!result.CanEdit || !YamlTreeCheck.SameExcept(_block!.Root, result._block!.Root, path, value))
        {
            throw new EditVerificationException($"Setting '{string.Join('.', path)}' didn't read back as written, or changed another value, so the edit was refused.");
        }

        return result;
    }
}
