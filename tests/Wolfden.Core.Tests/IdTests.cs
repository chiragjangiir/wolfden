using Wolfden.Core.Checks;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Tests;

/// <summary>
/// New ids (format.md F-ID-1, F-ID-5) and adding a missing id to a file
/// (F-ID-6): only the id line changes, and it goes first, after any
/// opening comment lines.
/// </summary>
public class IdTests
{
    private const string Id = "01JC5CK6000000000000000001";

    [Fact]
    public void ANewIdIsAValidId()
    {
        for (var i = 0; i < 100; i++)
        {
            Assert.True(Keys.IsId(Ids.New()));
        }
    }

    [Fact]
    public void AnIdEncodesItsTimeAndRandomBitsAsTheUlidSpecSays()
    {
        // The ULID spec's example timestamp, 1469918176385 ms, is 01ARYZ6S41.
        var time = DateTimeOffset.FromUnixTimeMilliseconds(1469918176385);

        Assert.Equal("01ARYZ6S41" + "0000000000000000", Ids.New(time, new byte[10]));
        Assert.Equal("01ARYZ6S41" + "ZZZZZZZZZZZZZZZZ", Ids.New(time, Enumerable.Repeat((byte)0xFF, 10).ToArray()));
    }

    [Fact]
    public void LaterIdsSortLater()
    {
        var random = new byte[10];
        var early = Ids.New(DateTimeOffset.FromUnixTimeMilliseconds(1_000), random);
        var late = Ids.New(DateTimeOffset.FromUnixTimeMilliseconds(2_000), random);

        Assert.True(string.CompareOrdinal(early, late) < 0);
    }

    [Fact]
    [Trait("Scenario", "SC-CLI-4")]
    public void TheIdGoesFirstAfterOpeningCommentLines()
    {
        var result = WithId("---\n# Notes on Orrin\n\ntype: character   # kept\nname: Orrin\n---\n\nBody.\n");

        Assert.Equal($"---\n# Notes on Orrin\n\nid: {Id}\ntype: character   # kept\nname: Orrin\n---\n\nBody.\n", result);
    }

    [Fact]
    public void TheIdTakesTheIndentOfTheOtherKeys()
    {
        var result = WithId("---\n  type: character\n  name: Orrin\n---\n");

        Assert.Equal($"---\n  id: {Id}\n  type: character\n  name: Orrin\n---\n", result);
    }

    [Fact]
    public void AnEmptyIdIsFilledInPlace()
    {
        Assert.Equal($"---\ntype: note\nid: {Id}\nname: N\n---\n", WithId("---\ntype: note\nid:\nname: N\n---\n"));
        Assert.Equal($"---\ntype: note\nid: {Id}\n---\n", WithId("---\ntype: note\nid: ~\n---\n"));
    }

    [Fact]
    public void EmptyFrontmatterGetsJustTheId()
    {
        Assert.Equal($"---\n# only a comment\nid: {Id}\n---\nText.\n", WithId("---\n# only a comment\n---\nText.\n"));
    }

    [Fact]
    public void CrlfAndAByteOrderMarkAreKept()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(TestFiles.Utf8("---\r\nname: Vael\r\n---\r\n")).ToArray();

        var result = MarkdownFile.Parse(bytes, requireFrontmatter: true).WithId(Id).ToBytes();

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(TestFiles.Utf8($"---\r\nid: {Id}\r\nname: Vael\r\n---\r\n")), result);
    }

    [Fact]
    public void AFileThatHasAnIdIsRefused()
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8($"---\nid: {Id}\nname: N\n---\n"));

        Assert.Throws<InvalidOperationException>(() => file.WithId("01JC5CK6000000000000000002"));
    }

    [Fact]
    public void AFlowMappingIsRefused()
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8("---\n{ type: note, name: N }\n---\n"));

        Assert.Throws<NotSupportedException>(() => file.WithId(Id));
    }

    [Fact]
    public void AnInvalidIdIsRefused()
    {
        var file = MarkdownFile.Parse(TestFiles.Utf8("---\nname: N\n---\n"));

        Assert.Throws<ArgumentException>(() => file.WithId("01jc5ck6000000000000000001"));
    }

    [Fact]
    public void FixGivesIdsToContainersAndEntitiesOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolfden-core-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Write(root, "den.yaml", "format: 1\n");
            Write(root, "universes/vael/universe.md", "---\nname: Vael\n---\n");
            Write(root, "universes/vael/books/a1/book.md", "---\nid: 01JC5CK6000000000000000009\nname: Book 1\n---\n");
            Write(root, "universes/vael/books/a1/outline.md", "| row | beat |\n|---|---|\n| 1 | Start. |\n");
            Write(root, "universes/vael/books/a1/entities/notes/flow.md", "---\n{ type: note, name: Flow }\n---\n");
            var ids = new Queue<string>(["01JC5CK6000000000000000001", "01JC5CK6000000000000000002"]);

            var results = IdFix.Run(root, ids.Dequeue);

            Assert.Equal(
                [
                    new IdFixResult("universes/vael/books/a1/entities/notes/flow.md", IdFixOutcome.Skipped, null,
                        "The frontmatter is a flow mapping, so an id can't be added as its first key."),
                    new IdFixResult("universes/vael/universe.md", IdFixOutcome.Added, "01JC5CK6000000000000000002", null),
                ],
                results);
            Assert.Equal("---\nid: 01JC5CK6000000000000000002\nname: Vael\n---\n", File.ReadAllText(Path.Combine(root, "universes", "vael", "universe.md")));
            Assert.Equal("| row | beat |\n|---|---|\n| 1 | Start. |\n", File.ReadAllText(Path.Combine(root, "universes", "vael", "books", "a1", "outline.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FixDoesNothingWhenDenYamlDoesntLoad()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolfden-core-tests", Guid.NewGuid().ToString("N"));
        try
        {
            Write(root, "den.yaml", "format: 2\n");
            Write(root, "universes/vael/universe.md", "---\nname: Vael\n---\n");

            Assert.Empty(IdFix.Run(root));
            Assert.Equal("---\nname: Vael\n---\n", File.ReadAllText(Path.Combine(root, "universes", "vael", "universe.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string WithId(string text) => MarkdownFile.Parse(TestFiles.Utf8(text)).WithId(Id).Text;

    private static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }
}
