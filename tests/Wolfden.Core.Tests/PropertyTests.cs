using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// Random edit sequences on every editable sample, pack, and fixture file.
/// After each edit, everything the edit didn't target must be byte-identical
/// (format.md F-RT-2). Seeds are fixed, so failures reproduce.
/// </summary>
public class PropertyTests
{
    private const int StepsPerFile = 25;

    /// <summary>Body lines that may change the outline, so a refusal is expected.</summary>
    private static readonly string[] RiskyLines =
    [
        "## A heading that should be refused", "```", "<!-- wolfden: from=ember:1 -->", "===",
    ];

    private static readonly string[] Strings =
    [
        "plain words", "", "true", "2291", "014", "a: b", "#tag", "[[velari]]", "Zoë", "it's", "say \"hi\"",
        "multi\nline", " leading space", "tab\there", "~", "-", "ember:22", "null", "back\\slash", "{ not: a map }",
    ];

    private static readonly string[] Keys = ["x-color", "x-new", "species", "rank", "born", "pov", "x_notes", "died"];

    private static readonly string[] BodyLines =
    [
        "Plain prose.", "A [[link|with alias]] and *emphasis*.", "", "- a list item", "> a quote",
        "<!-- an ordinary comment -->", "    indented code", "Trailing spaces.   ", "Zoë's line.",
        "## A heading that should be refused", "```", "<!-- wolfden: from=ember:1 -->", "===",
    ];

    public static TheoryData<string> EditableFiles()
    {
        var files = TestFiles.SampleAndPackFiles()
            .Where(f => f.EndsWith(".md", StringComparison.Ordinal))
            .Concat(Directory.EnumerateFiles(TestFiles.FixturesDir, "*.md")
                .Select(f => Path.GetRelativePath(TestFiles.RepoRoot, f).Replace('\\', '/')))
            .Order(StringComparer.Ordinal);
        return new(files);
    }

    [Theory]
    [MemberData(nameof(EditableFiles))]
    public void RandomEditsLeaveUntouchedRegionsIdentical(string path)
    {
        var file = MarkdownFile.Parse(TestFiles.ReadRepoFile(path));
        if (!file.CanEdit)
        {
            return;
        }

        var random = new Random(StableSeed(path));
        var applied = 0;
        for (var step = 0; step < StepsPerFile; step++)
        {
            var before = file;
            var choice = random.Next(10);
            file = choice switch
            {
                0 => SetTopLevel(file, random),
                1 => SetField(file, random),
                2 => ReplaceBody(file, random),
                3 => Append(file, random),
                4 => SetDeep(file, random),
                5 => AddListItem(file, random),
                6 => RemoveValue(file, random),
                7 => RenameSection(file, random),
                8 => RemoveSection(file, random),
                _ => MoveSection(file, random),
            };

            // Whatever happened, the file still round-trips.
            Assert.Equal(file.ToBytes(), MarkdownFile.Parse(file.ToBytes()).ToBytes());
            applied += ReferenceEquals(before, file) ? 0 : 1;
        }

        Assert.True(applied >= StepsPerFile / 3, $"Only {applied} of {StepsPerFile} random edits went through.");
    }

    private static MarkdownFile SetTopLevel(MarkdownFile file, Random random)
    {
        var existing = file.Frontmatter?.Entries.Where(e => e.Value is YamlScalar && e.KeyText is not null).ToList() ?? [];
        var key = existing.Count > 0 && random.Next(3) > 0 ? existing[random.Next(existing.Count)].KeyText! : Keys[random.Next(Keys.Length)];
        return SetAndCheck(file, [key], RandomValue(random));
    }

    private static MarkdownFile SetField(MarkdownFile file, Random random)
    {
        var fields = file.GetFrontmatterValue("fields") as YamlMapping;
        var key = fields is { Entries.Count: > 0 } && random.Next(2) == 0
            ? fields.Entries[random.Next(fields.Entries.Count)].KeyText ?? "pov"
            : Keys[random.Next(Keys.Length)];
        return SetAndCheck(file, ["fields", key], RandomValue(random));
    }

    private static MarkdownFile SetAndCheck(MarkdownFile file, string[] path, YamlScalarValue value)
    {
        MarkdownFile edited;
        try
        {
            edited = file.WithFrontmatterValue(path, value);
        }
        catch (NotSupportedException)
        {
            // Refused: a layout that can't be edited in place, inside a flow
            // collection or a list. Refusing is fine; changing something else isn't.
            Assert.Contains(Along(file, path), n => n is YamlMapping { IsFlow: true } or YamlSequence);
            return file;
        }
        catch (InvalidOperationException)
        {
            // Refused: a key on the path holds something other than a mapping.
            Assert.Contains(Along(file, path.SkipLast(1)), n => n is YamlSequence or YamlScalar { IsNull: false });
            return file;
        }

        Assert.True(value.Matches(edited.GetFrontmatterValue(path)));
        Assert.Equal(file.Body, edited.Body);
        if (ReferenceEquals(edited, file))
        {
            return file;
        }

        // Every other top-level entry reads the same, in the same order,
        // with new keys only at the end.
        var before = file.Frontmatter?.Entries ?? [];
        var after = edited.Frontmatter!.Entries;
        Assert.True(after.Count >= before.Count);
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].KeyText, after[i].KeyText);
            if (before[i].KeyText != path[0])
            {
                Assert.Equal(Dump(before[i].Value), Dump(after[i].Value));
            }
            else if (path.Length > 1 && before[i].Value is YamlMapping oldChild && after[i].Value is YamlMapping newChild)
            {
                foreach (var entry in oldChild.Entries.Where(e => e.KeyText != path[1]))
                {
                    Assert.Equal(Dump(entry.Value), Dump(newChild[entry.KeyText!]));
                }
            }
        }

        // The bytes that changed lie inside the edited key's lines, or are a
        // pure insertion at the end of its mapping.
        if (file.HasFrontmatter && file.Frontmatter?.Find(path[0]) is { } target)
        {
            var (start, oldEnd, _) = TestFiles.Diff(file.Text, edited.Text);
            var from = LineStart(file.Text, target.Key.Line);
            var next = file.Frontmatter.Entries.SkipWhile(e => e != target).Skip(1).FirstOrDefault();
            var to = next is null ? file.FrontmatterText.Length : LineStart(file.Text, next.Key.Line);
            Assert.True(start >= from && oldEnd <= to, $"Setting {string.Join('.', path)} changed [{start}, {oldEnd}), outside [{from}, {to}).");
        }
        else
        {
            var (start, oldEnd, _) = TestFiles.Diff(file.Text, edited.Text);
            Assert.True(oldEnd <= Math.Max(start, file.FrontmatterText.Length), "Adding a key changed existing frontmatter bytes.");
        }

        return edited;
    }

    private static MarkdownFile ReplaceBody(MarkdownFile file, Random random)
    {
        if (file.Sections.Count == 0)
        {
            return file;
        }

        var index = random.Next(file.Sections.Count);
        var body = RandomBody(random);
        MarkdownFile edited;
        try
        {
            edited = file.WithSectionBody(file.Sections[index], body);
        }
        catch (ArgumentException)
        {
            // Only text that can change the outline is refused: a heading, a
            // fence, a wolfden comment, a setext underline, or prose that runs
            // into a setext heading right after it.
            var next = index + 1 < file.Sections.Count ? file.Sections[index + 1] : null;
            Assert.True(HasRiskyLine(body) || next?.IsSetext == true, $"Refused a harmless body: {body}");
            return file;
        }

        Assert.Equal(file.FrontmatterText, edited.FrontmatterText);
        Assert.Equal(file.Lead, edited.Lead);
        Assert.Equal(file.Sections.Count, edited.Sections.Count);
        for (var i = 0; i < file.Sections.Count; i++)
        {
            var (a, b) = (file.Sections[i], edited.Sections[i]);
            AssertSameHeading(a, b, mayGainNewline: i == index);
            if (i != index)
            {
                Assert.Equal(a.Body, b.Body);
            }
        }

        var expected = body.ReplaceLineEndings("\n").TrimEnd('\n');
        Assert.Equal(TrimBlankLines(expected), TrimBlankLines(edited.Sections[index].Body.ReplaceLineEndings("\n")));
        return edited;
    }

    private static MarkdownFile Append(MarkdownFile file, Random random)
    {
        var level = random.Next(2, 7);
        var name = random.Next(4) == 0 ? "Bad name ##" : $"Section {random.Next(1000)}";
        var body = RandomBody(random);
        MarkdownFile edited;
        try
        {
            edited = file.WithNewSection(name, body, level);
        }
        catch (ArgumentException)
        {
            // Refused: a name that doesn't read back, risky text, or a file
            // that ends inside an open block from an earlier edit.
            Assert.True(
                name.EndsWith('#') || HasRiskyLine(body) || HasRiskyLine(file.Text) || (level > 2 && file.Sections.Count == 0),
                $"Refused a harmless section: {name}");
            return file;
        }

        Assert.StartsWith(file.Text, edited.Text, StringComparison.Ordinal);
        Assert.Equal(file.EndsWithNewline || file.Text.Length == 0, edited.EndsWithNewline);
        Assert.Equal(file.Sections.Count + 1, edited.Sections.Count);
        for (var i = 0; i < file.Sections.Count; i++)
        {
            var (a, b) = (file.Sections[i], edited.Sections[i]);
            AssertSameHeading(a, b, mayGainNewline: i == file.Sections.Count - 1);

            // The last old section gains only the line breaks before the new heading.
            Assert.StartsWith(a.Body, b.Body, StringComparison.Ordinal);
            Assert.True(i == file.Sections.Count - 1 || a.Body == b.Body);
        }

        Assert.Equal(name, edited.Sections[^1].Name);
        return edited;
    }

    /// <summary>Sets a scalar anywhere in the frontmatter: in a flow mapping, a list item, or a nested entry.</summary>
    private static MarkdownFile SetDeep(MarkdownFile file, Random random)
    {
        var scalars = Paths(file.Frontmatter).Where(p => p.Node is YamlScalar && p.Path.Length > 1).ToList();
        return scalars.Count == 0 ? file : SetAndCheck(file, scalars[random.Next(scalars.Count)].Path, RandomValue(random));
    }

    private static MarkdownFile AddListItem(MarkdownFile file, Random random)
    {
        var lists = Paths(file.Frontmatter).Where(p => p.Node is YamlSequence).ToList();
        string[] path = lists.Count > 0 && random.Next(4) > 0
            ? lists[random.Next(lists.Count)].Path
            : random.Next(2) == 0 ? ["aliases"] : ["fields", Keys[random.Next(Keys.Length)]];
        var list = file.GetFrontmatterValue(path) as YamlSequence;
        int? index = list is null || random.Next(2) == 0 ? null : random.Next(list.Items.Count + 1);
        YamlValue item = random.Next(3) == 0
            ? new YamlMappingValue { { "value", RandomValue(random) }, { "from", YamlScalarValue.String($"ember:{random.Next(1, 40)}") } }
            : RandomValue(random);

        MarkdownFile edited;
        try
        {
            edited = file.WithNewListItem(path, item, index);
        }
        catch (InvalidOperationException)
        {
            // Refused: the value there isn't a list.
            Assert.True(file.GetFrontmatterValue(path) is not (null or YamlSequence or YamlScalar { IsNull: true })
                || Along(file, path.SkipLast(1)).Any(n => n is YamlScalar { IsNull: false }));
            return file;
        }
        catch (NotSupportedException)
        {
            // Refused: the list's layout can't take an item without moving a comment.
            return file;
        }

        var after = Assert.IsType<YamlSequence>(edited.GetFrontmatterValue(path));
        Assert.Equal((list?.Items.Count ?? 0) + 1, after.Items.Count);
        Assert.True(item.MatchesAsWritten(after.Items[index ?? after.Items.Count - 1]));
        AssertOnlyEntryChanged(file, edited, path[0]);
        return edited;
    }

    private static MarkdownFile RemoveValue(MarkdownFile file, Random random)
    {
        var all = Paths(file.Frontmatter).ToList();
        if (all.Count == 0)
        {
            return file;
        }

        var path = all[random.Next(all.Count)].Path;
        var parent = path.Length == 1 ? file.Frontmatter : file.GetFrontmatterValue(path[..^1]);
        MarkdownFile edited;
        try
        {
            edited = file.WithoutFrontmatterValue(path);
        }
        catch (NotSupportedException)
        {
            // Refused: the removal would delete or move a comment.
            return file;
        }

        var parentAfter = path.Length == 1 ? edited.Frontmatter : edited.GetFrontmatterValue(path[..^1]);
        switch (parent)
        {
            case YamlMapping m when m.Entries.Count == 1:
                Assert.True(parentAfter is null or YamlScalar { IsNull: true } or YamlMapping { Entries.Count: 0 });
                break;
            case YamlMapping m:
                Assert.Equal(m.Entries.Count - 1, Assert.IsType<YamlMapping>(parentAfter).Entries.Count);
                Assert.False(((YamlMapping)parentAfter).ContainsKey(path[^1]) && m.Keys.Count(k => k == path[^1]) == 1);
                break;
            case YamlSequence s when s.Items.Count == 1:
                Assert.True(parentAfter is YamlScalar { IsNull: true } or YamlSequence { Items.Count: 0 });
                break;
            case YamlSequence s:
                Assert.Equal(s.Items.Count - 1, Assert.IsType<YamlSequence>(parentAfter).Items.Count);
                break;
        }

        if (path.Length > 1)
        {
            AssertOnlyEntryChanged(file, edited, path[0]);
        }
        else
        {
            Assert.Equal(file.Body, edited.Body);
        }

        return edited;
    }

    private static MarkdownFile RenameSection(MarkdownFile file, Random random)
    {
        if (file.Sections.Count == 0)
        {
            return file;
        }

        var section = file.Sections[random.Next(file.Sections.Count)];
        var name = random.Next(4) switch
        {
            0 => "Bad name ##",
            1 => section.Name + " (old)",
            2 => "Appearance",
            _ => $"Renamed {random.Next(1000)}",
        };
        MarkdownFile edited;
        try
        {
            edited = file.WithSectionName(section, name);
        }
        catch (ArgumentException)
        {
            Assert.True(name.EndsWith('#') || section.Name.Length == 0 || HasRiskyLine(file.Text), $"Refused a harmless rename to {name}");
            return file;
        }

        if (ReferenceEquals(edited, file))
        {
            Assert.Equal(name, section.Name);
            return file;
        }

        Assert.Equal(file.FrontmatterText, edited.FrontmatterText);
        Assert.Equal(file.Lead, edited.Lead);
        Assert.Equal(file.Sections.Count, edited.Sections.Count);
        for (var i = 0; i < file.Sections.Count; i++)
        {
            Assert.Equal(i == section.Index ? name : file.Sections[i].Name, edited.Sections[i].Name);
            Assert.Equal(file.Sections[i].Body, edited.Sections[i].Body);
            Assert.Equal(file.Sections[i].Comment?.Text, edited.Sections[i].Comment?.Text);
        }

        // Only the heading's own lines changed.
        var (start, oldEnd, _) = TestFiles.Diff(file.Text, edited.Text);
        var headingStart = LineStart(file.Text, section.Line);
        Assert.True(start >= headingStart && oldEnd <= headingStart + section.HeadingText.Length, $"Renaming changed [{start}, {oldEnd}), outside the heading [{headingStart}, {headingStart + section.HeadingText.Length}) '{section.HeadingText}' renamed to '{name}'.");
        return edited;
    }

    private static MarkdownFile RemoveSection(MarkdownFile file, Random random)
    {
        if (file.Sections.Count == 0)
        {
            return file;
        }

        var section = file.Sections[random.Next(file.Sections.Count)];
        MarkdownFile edited;
        try
        {
            edited = file.WithoutSection(section);
        }
        catch (NotSupportedException)
        {
            // Refused: the text around it would read differently.
            return file;
        }

        var removed = 1 + file.Sections.Skip(section.Index + 1).TakeWhile(s => s.Level > section.Level).Count();
        Assert.Equal(file.FrontmatterText, edited.FrontmatterText);
        Assert.Equal(file.Sections.Count - removed, edited.Sections.Count);
        var expected = file.Sections.Where(s => s.Index < section.Index || s.Index >= section.Index + removed).Select(s => s.Name);
        Assert.Equal(expected, edited.Sections.Select(s => s.Name));
        return edited;
    }

    private static MarkdownFile MoveSection(MarkdownFile file, Random random)
    {
        if (file.Sections.Count == 0)
        {
            return file;
        }

        var section = file.Sections[random.Next(file.Sections.Count)];
        // A target that changes the order: not the section itself or the one after it.
        var siblings = file.Sections.Where(s => ReferenceEquals(s.Parent, section.Parent)).ToList();
        var own = siblings.IndexOf(section);
        var targets = Enumerable.Range(0, siblings.Count + 1).Where(t => t != own && t != own + 1).ToList();
        if (targets.Count == 0)
        {
            return file;
        }

        var target = targets[random.Next(targets.Count)];
        var before = target == siblings.Count ? null : siblings[target];
        MarkdownFile edited;
        try
        {
            edited = file.WithSectionMovedBefore(section, before);
        }
        catch (NotSupportedException)
        {
            // Refused: in its new place, the text would read differently.
            return file;
        }

        // A move only reorders text: the same characters, frontmatter, lead,
        // and sections, with the moved one among its siblings.
        Assert.Equal(file.FrontmatterText, edited.FrontmatterText);
        Assert.Equal(file.Lead, edited.Lead);
        Assert.Equal(file.Text.Order().ToArray(), edited.Text.Order().ToArray());
        Assert.Equal(file.Sections.Select(s => s.Name).Order(StringComparer.Ordinal), edited.Sections.Select(s => s.Name).Order(StringComparer.Ordinal));
        Assert.Equal(file.EndsWithNewline, edited.EndsWithNewline);
        return edited;
    }

    /// <summary>Every top-level entry but <paramref name="key"/> reads the same, in the same order,
    /// the body is unchanged, and the changed bytes lie inside that entry's lines or at the end of the frontmatter.</summary>
    private static void AssertOnlyEntryChanged(MarkdownFile file, MarkdownFile edited, string key)
    {
        Assert.Equal(file.Body, edited.Body);
        var before = file.Frontmatter?.Entries ?? [];
        var after = edited.Frontmatter!.Entries;
        for (var i = 0; i < before.Count; i++)
        {
            Assert.Equal(before[i].KeyText, after[i].KeyText);
            if (before[i].KeyText != key)
            {
                Assert.Equal(Dump(before[i].Value), Dump(after[i].Value));
            }
        }

        if (file.HasFrontmatter && file.Frontmatter?.Find(key) is { } target)
        {
            var (start, oldEnd, _) = TestFiles.Diff(file.Text, edited.Text);
            var from = LineStart(file.Text, target.Key.Line);
            var next = file.Frontmatter.Entries.SkipWhile(e => e != target).Skip(1).FirstOrDefault();
            var to = next is null ? file.FrontmatterText.Length : LineStart(file.Text, next.Key.Line);
            Assert.True(start >= from && oldEnd <= to, $"Editing {key} changed [{start}, {oldEnd}), outside [{from}, {to}).");
        }
    }

    /// <summary>Every node under <paramref name="node"/>, with its path: keys in mappings, item numbers in lists.</summary>
    private static IEnumerable<(string[] Path, YamlNode Node)> Paths(YamlNode? node, string[]? prefix = null)
    {
        prefix ??= [];
        switch (node)
        {
            case YamlMapping mapping:
                foreach (var entry in mapping.Entries.Where(e => e.KeyText is not null))
                {
                    string[] path = [.. prefix, entry.KeyText!];
                    yield return (path, entry.Value);
                    foreach (var inner in Paths(entry.Value, path))
                    {
                        yield return inner;
                    }
                }

                break;
            case YamlSequence sequence:
                for (var i = 0; i < sequence.Items.Count; i++)
                {
                    string[] path = [.. prefix, i.ToString(System.Globalization.CultureInfo.InvariantCulture)];
                    yield return (path, sequence.Items[i]);
                    foreach (var inner in Paths(sequence.Items[i], path))
                    {
                        yield return inner;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Same heading and comment. A heading on the file's last line, with no
    /// line break after it, gains one when text is added after it.
    /// </summary>
    private static void AssertSameHeading(Section a, Section b, bool mayGainNewline)
    {
        Assert.Equal(a.Comment?.Text, b.Comment?.Text);
        if (mayGainNewline && a.HeadingText != b.HeadingText && a.Comment is null && !a.HeadingText.EndsWith('\n'))
        {
            Assert.Equal(a.HeadingText, b.HeadingText.TrimEnd('\r', '\n'));
            return;
        }

        // Clearing the last section's text in a file with no final newline
        // drops the heading's line break too, so the file still has none.
        if (mayGainNewline && a.HeadingText != b.HeadingText && a.Comment is null && !b.HeadingText.EndsWith('\n') && !b.Owner.EndsWithNewline)
        {
            Assert.Equal(a.HeadingText.TrimEnd('\r', '\n'), b.HeadingText);
            return;
        }

        Assert.Equal(a.HeadingText, b.HeadingText);
    }

    private static YamlScalarValue RandomValue(Random random) => random.Next(10) switch
    {
        0 => YamlScalarValue.Null,
        1 => YamlScalarValue.Boolean(random.Next(2) == 0),
        2 => YamlScalarValue.Integer(random.Next(-5, 3000)),
        _ => YamlScalarValue.String(Strings[random.Next(Strings.Length)]),
    };

    private static string RandomBody(Random random)
    {
        var count = random.Next(0, 4);
        var lines = Enumerable.Range(0, count).Select(_ => BodyLines[random.Next(BodyLines.Length)]);
        return string.Join(random.Next(2) == 0 ? "\n" : "\r\n", lines);
    }

    private static bool HasRiskyLine(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Any(line => RiskyLines.Contains(line) || line.StartsWith("<!--", StringComparison.Ordinal) || line.StartsWith("```", StringComparison.Ordinal));

    /// <summary>The values at each prefix of <paramref name="path"/> that exists.</summary>
    private static IEnumerable<YamlNode> Along(MarkdownFile file, IEnumerable<string> path)
    {
        YamlNode? node = file.Frontmatter;
        foreach (var key in path)
        {
            node = (node as YamlMapping)?[key];
            if (node is null)
            {
                yield break;
            }

            yield return node;
        }
    }

    private static string TrimBlankLines(string text) =>
        string.Join("\n", text.Split('\n').SkipWhile(IsBlank).Reverse().SkipWhile(IsBlank).Reverse());

    private static bool IsBlank(string line) => line.All(c => c is ' ' or '\t' or '\r');

    private static int LineStart(string text, int line)
    {
        var offset = 0;
        for (var i = 1; i < line; i++)
        {
            offset = text.IndexOf('\n', offset) + 1;
        }

        return offset;
    }

    private static string Dump(YamlNode? node) => node switch
    {
        null => "<missing>",
        YamlScalar s => $"{s.Style}:{s.Text}",
        YamlSequence q => "[" + string.Join(",", q.Items.Select(Dump)) + "]",
        YamlMapping m => "{" + string.Join(",", m.Entries.Select(e => Dump(e.Key) + "=" + Dump(e.Value))) + "}",
        _ => "?",
    };

    private static int StableSeed(string path) => path.Aggregate(17, (h, c) => unchecked((h * 31) + c));
}
