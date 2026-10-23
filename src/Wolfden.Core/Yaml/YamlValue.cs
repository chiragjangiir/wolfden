using System.Collections;
using Wolfden.Core.Text;

namespace Wolfden.Core.Yaml;

/// <summary>
/// A value to write into frontmatter or a YAML file: a scalar
/// (<see cref="YamlScalarValue"/>), a mapping (<see cref="YamlMappingValue"/>),
/// or a list (<see cref="YamlSequenceValue"/>). Writers render it as new
/// text; they never re-serialize what's already in the file (format.md F-RT-2).
/// </summary>
public abstract class YamlValue
{
    private protected YamlValue()
    {
    }

    /// <summary>
    /// The value as flow YAML (<c>{ value: x, from: "a1:3" }</c>,
    /// <c>[0, 9, 20]</c>, or a scalar), for a flow collection or a list
    /// item. <paramref name="like"/> is a neighbor already in the file, such
    /// as the list item before it: the new value copies its quoting and its
    /// spacing inside braces where it can.
    /// </summary>
    internal abstract string RenderFlow(YamlNode? like, SourceText? source);

    /// <summary>True if <paramref name="node"/> holds this value as Wolfden
    /// wrote it (see <see cref="YamlScalarValue.MatchesAsWritten"/>).</summary>
    internal abstract bool MatchesAsWritten(YamlNode? node);
}

/// <summary>
/// A mapping to write, with its keys in the order added. A temporal entry
/// (format.md §7.3) is one:
/// <c>new YamlMappingValue { { "value", YamlScalarValue.String("Captain") }, { "from", YamlScalarValue.String("ash:30") } }</c>.
/// </summary>
public sealed class YamlMappingValue : YamlValue, IEnumerable<KeyValuePair<string, YamlValue>>
{
    private readonly List<KeyValuePair<string, YamlValue>> _entries = [];

    /// <summary>The entries, in order.</summary>
    public IReadOnlyList<KeyValuePair<string, YamlValue>> Entries => _entries;

    /// <summary>Adds an entry. Keys are plain words (<c>value</c>, <c>from</c>, <c>x-note</c>) and can't repeat.</summary>
    /// <exception cref="ArgumentException">The key can't be written as a plain key, or is already there.</exception>
    public void Add(string key, YamlValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        if (!YamlScalarValue.IsSafeKey(key))
        {
            throw new ArgumentException($"'{key}' can't be written as a plain YAML key.", nameof(key));
        }

        if (_entries.Any(e => e.Key == key))
        {
            throw new ArgumentException($"The key '{key}' is already in the mapping.", nameof(key));
        }

        _entries.Add(new(key, value));
    }

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, YamlValue>> GetEnumerator() => _entries.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override string RenderFlow(YamlNode? like, SourceText? source)
    {
        if (_entries.Count == 0)
        {
            return "{}";
        }

        var likeMapping = like as YamlMapping;
        var padded = likeMapping is not { IsFlow: true, Entries.Count: > 0 } || source is null
            || source.Text[likeMapping.Start + 1] is ' ' or '\t' or '\r' or '\n';
        var parts = _entries.Select(e => e.Key + ": " + e.Value.RenderFlow(likeMapping?[e.Key], source));
        var inner = string.Join(", ", parts);
        return padded ? "{ " + inner + " }" : "{" + inner + "}";
    }

    internal override bool MatchesAsWritten(YamlNode? node)
    {
        if (node is not YamlMapping mapping || mapping.Entries.Count != _entries.Count)
        {
            return false;
        }

        for (var i = 0; i < _entries.Count; i++)
        {
            if (mapping.Entries[i].KeyText != _entries[i].Key || !_entries[i].Value.MatchesAsWritten(mapping.Entries[i].Value))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// A list to write, such as a date's <c>sort: [0, 9, 20]</c>:
/// <c>new YamlSequenceValue { YamlScalarValue.Integer(0), YamlScalarValue.Integer(9) }</c>.
/// </summary>
public sealed class YamlSequenceValue : YamlValue, IEnumerable<YamlValue>
{
    private readonly List<YamlValue> _items = [];

    /// <summary>The items, in order.</summary>
    public IReadOnlyList<YamlValue> Items => _items;

    /// <summary>Adds an item.</summary>
    public void Add(YamlValue item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items.Add(item);
    }

    /// <inheritdoc />
    public IEnumerator<YamlValue> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    internal override string RenderFlow(YamlNode? like, SourceText? source)
    {
        var likeSequence = like as YamlSequence;
        var likeItem = likeSequence is { Items.Count: > 0 } ? likeSequence.Items[^1] : null;
        return "[" + string.Join(", ", _items.Select(i => i.RenderFlow(likeItem, source))) + "]";
    }

    internal override bool MatchesAsWritten(YamlNode? node)
    {
        if (node is not YamlSequence sequence || sequence.Items.Count != _items.Count)
        {
            return false;
        }

        for (var i = 0; i < _items.Count; i++)
        {
            if (!_items[i].MatchesAsWritten(sequence.Items[i]))
            {
                return false;
            }
        }

        return true;
    }
}
