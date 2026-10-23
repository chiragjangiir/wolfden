namespace Wolfden.Core.Yaml;

/// <summary>
/// Checks an edit's result against the tree it started from: everything
/// reads the same as before except what the edit changed (and the keys it
/// added on the way), so an editing bug can't silently change another value.
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
        SameExcept(before, after, path, (_, edited) => value.MatchesAsWritten(edited));

    /// <summary>
    /// True if <paramref name="after"/> equals <paramref name="before"/>
    /// everywhere except at <paramref name="path"/>, where
    /// <paramref name="changed"/> judges the old and new nodes. The path
    /// runs through mappings by key and lists by item number; keys missing
    /// before must have been added at the end of their mapping.
    /// </summary>
    public static bool SameExcept(YamlNode? before, YamlNode? after, IReadOnlyList<string> path, Func<YamlNode?, YamlNode?, bool> changed) =>
        Check(before, after, path, 0, changed);

    /// <summary>
    /// True if <paramref name="after"/> is the list at <paramref name="before"/>
    /// with <paramref name="item"/> inserted at <paramref name="index"/>, or
    /// a new one-item list where there was none.
    /// </summary>
    public static bool AddedItem(YamlNode? before, YamlNode? after, int index, YamlValue item)
    {
        if (after is not YamlSequence edited)
        {
            return false;
        }

        if (before is not YamlSequence old)
        {
            return before is null or YamlScalar { IsNull: true } && edited.Items.Count == 1 && item.MatchesAsWritten(edited.Items[0]);
        }

        if (old.IsFlow != edited.IsFlow || edited.Items.Count != old.Items.Count + 1 || !item.MatchesAsWritten(edited.Items[index]))
        {
            return false;
        }

        for (var i = 0; i < old.Items.Count; i++)
        {
            if (!Same(old.Items[i], edited.Items[i < index ? i : i + 1]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True if <paramref name="after"/> is the mapping or list at
    /// <paramref name="before"/> without its key or item
    /// <paramref name="segment"/>. A block collection that loses its last
    /// entry reads as null, and a flow one as empty.
    /// </summary>
    public static bool Removed(YamlNode? before, YamlNode? after, string segment)
    {
        switch (before)
        {
            case YamlMapping old:
            {
                var k = -1;
                for (var i = 0; i < old.Entries.Count && k < 0; i++)
                {
                    k = old.Entries[i].KeyText == segment ? i : -1;
                }

                if (k < 0)
                {
                    return false;
                }

                if (old.Entries.Count == 1)
                {
                    return old.IsFlow ? after is YamlMapping { IsFlow: true, Entries.Count: 0 } : after is null or YamlScalar { IsNull: true };
                }

                if (after is not YamlMapping edited || edited.IsFlow != old.IsFlow || edited.Entries.Count != old.Entries.Count - 1)
                {
                    return false;
                }

                for (var i = 0; i < edited.Entries.Count; i++)
                {
                    var o = old.Entries[i < k ? i : i + 1];
                    if (!Same(o.Key, edited.Entries[i].Key) || !Same(o.Value, edited.Entries[i].Value))
                    {
                        return false;
                    }
                }

                return true;
            }

            case YamlSequence old:
            {
                if (!YamlEditor.TryIndex(segment, old.Items.Count, out var k))
                {
                    return false;
                }

                if (old.Items.Count == 1)
                {
                    return old.IsFlow ? after is YamlSequence { IsFlow: true, Items.Count: 0 } : after is YamlScalar { IsNull: true };
                }

                if (after is not YamlSequence edited || edited.IsFlow != old.IsFlow || edited.Items.Count != old.Items.Count - 1)
                {
                    return false;
                }

                for (var i = 0; i < edited.Items.Count; i++)
                {
                    if (!Same(old.Items[i < k ? i : i + 1], edited.Items[i]))
                    {
                        return false;
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool Check(YamlNode? before, YamlNode? after, IReadOnlyList<string> path, int index, Func<YamlNode?, YamlNode?, bool> changed)
    {
        if (index == path.Count)
        {
            return changed(before, after);
        }

        // Missing or empty before (no key, `key:`, `key: ~`, `key: {}`):
        // the edit builds a mapping holding only the rest of the path.
        if (before is null or YamlScalar { IsNull: true } or YamlMapping { Entries.Count: 0 })
        {
            return after is YamlMapping { Entries.Count: 1 } created
                && created.Entries[0].KeyText == path[index]
                && Check(null, created.Entries[0].Value, path, index + 1, changed);
        }

        if (before is YamlSequence list)
        {
            if (after is not YamlSequence editedList || list.IsFlow != editedList.IsFlow || list.Items.Count != editedList.Items.Count
                || !YamlEditor.TryIndex(path[index], list.Items.Count, out var k))
            {
                return false;
            }

            for (var i = 0; i < list.Items.Count; i++)
            {
                var ok = i == k
                    ? Check(list.Items[i], editedList.Items[i], path, index + 1, changed)
                    : Same(list.Items[i], editedList.Items[i]);
                if (!ok)
                {
                    return false;
                }
            }

            return true;
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
                ? Check(a.Value, b.Value, path, index + 1, changed)
                : Same(a.Value, b.Value);
            if (!ok)
            {
                return false;
            }
        }

        return hadKey || (edited.Entries[^1].KeyText == target && Check(null, edited.Entries[^1].Value, path, index + 1, changed));
    }

    /// <summary>
    /// True if <paramref name="after"/> is <paramref name="before"/> with one
    /// new first entry, <paramref name="key"/> set to <paramref name="value"/>,
    /// and every other entry identical and in the same order.
    /// </summary>
    public static bool SameWithFirstKey(YamlMapping before, YamlMapping? after, string key, YamlScalarValue value)
    {
        if (after is null || after.IsFlow != before.IsFlow || after.Entries.Count != before.Entries.Count + 1
            || after.Entries[0].KeyText != key || !value.MatchesAsWritten(after.Entries[0].Value))
        {
            return false;
        }

        for (var i = 0; i < before.Entries.Count; i++)
        {
            if (!Same(before.Entries[i].Key, after.Entries[i + 1].Key) || !Same(before.Entries[i].Value, after.Entries[i + 1].Value))
            {
                return false;
            }
        }

        return true;
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
