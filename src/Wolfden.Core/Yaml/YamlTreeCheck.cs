namespace Wolfden.Core.Yaml;

/// <summary>
/// Checks an edit's result against the tree it started from: everything
/// reads the same as before except the value it set (and the keys it added
/// on the way), so an editing bug can't silently change another value.
/// </summary>
internal static class YamlTreeCheck
{
    /// <summary>
    /// True if <paramref name="after"/> equals <paramref name="before"/>
    /// with <paramref name="path"/> set to <paramref name="value"/>: the
    /// same keys in the same order, new keys only at the end of their
    /// mapping, and every other node identical in text and style.
    /// </summary>
    public static bool SameExcept(YamlNode? before, YamlNode? after, IReadOnlyList<string> path, YamlScalarValue value) =>
        Check(before, after, path, 0, value);

    private static bool Check(YamlNode? before, YamlNode? after, IReadOnlyList<string> path, int index, YamlScalarValue value)
    {
        if (index == path.Count)
        {
            return value.Matches(after);
        }

        // Missing or empty before (no key, `key:`, `key: ~`, `key: {}`):
        // the edit builds a mapping holding only the rest of the path.
        if (before is null or YamlScalar { IsNull: true } or YamlMapping { Entries.Count: 0 })
        {
            return after is YamlMapping { Entries.Count: 1 } created
                && created.Entries[0].KeyText == path[index]
                && Check(null, created.Entries[0].Value, path, index + 1, value);
        }

        if (before is not YamlMapping old || after is not YamlMapping edited || old.IsFlow != edited.IsFlow)
        {
            return false;
        }

        var target = path[index];
        var hadKey = old.ContainsKey(target);
        if (edited.Entries.Count != old.Entries.Count + (hadKey ? 0 : 1))
        {
            return false;
        }

        for (var i = 0; i < old.Entries.Count; i++)
        {
            var (a, b) = (old.Entries[i], edited.Entries[i]);
            if (!Same(a.Key, b.Key))
            {
                return false;
            }

            var ok = a.KeyText == target
                ? Check(a.Value, b.Value, path, index + 1, value)
                : Same(a.Value, b.Value);
            if (!ok)
            {
                return false;
            }
        }

        return hadKey || (edited.Entries[^1].KeyText == target && Check(null, edited.Entries[^1].Value, path, index + 1, value));
    }

    /// <summary>True if two nodes read the same, down to scalar styles.</summary>
    public static bool Same(YamlNode? a, YamlNode? b)
    {
        // Iterative, so deep trees can't overflow the stack.
        var stack = new Stack<(YamlNode?, YamlNode?)>();
        stack.Push((a, b));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            switch (x, y)
            {
                case (null, null):
                    break;
                case (YamlScalar s, YamlScalar t) when s.Text == t.Text && s.Style == t.Style:
                    break;
                case (YamlSequence s, YamlSequence t) when s.IsFlow == t.IsFlow && s.Items.Count == t.Items.Count:
                    for (var i = 0; i < s.Items.Count; i++)
                    {
                        stack.Push((s.Items[i], t.Items[i]));
                    }

                    break;
                case (YamlMapping s, YamlMapping t) when s.IsFlow == t.IsFlow && s.Entries.Count == t.Entries.Count:
                    for (var i = 0; i < s.Entries.Count; i++)
                    {
                        stack.Push((s.Entries[i].Key, t.Entries[i].Key));
                        stack.Push((s.Entries[i].Value, t.Entries[i].Value));
                    }

                    break;
                default:
                    return false;
            }
        }

        return true;
    }
}
