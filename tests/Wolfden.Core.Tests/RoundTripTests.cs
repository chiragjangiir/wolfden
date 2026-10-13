using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// Reading a file and writing it back unchanged gives the same bytes
/// (format.md F-RT-3), for the shipped sample den, the core pack, and every
/// crafted fixture.
/// </summary>
public class RoundTripTests
{
    public static TheoryData<string> RepoMarkdownAndYaml() => new(TestFiles.RepoFiles("samples/dracula", "packs/core"));

    public static TheoryData<string> Fixtures() =>
        new(Directory.EnumerateFiles(TestFiles.FixturesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    [Fact]
    public void DiscoversTheSampleDenAndCorePack()
    {
        var files = TestFiles.RepoFiles("samples/dracula", "packs/core").ToList();

        Assert.Contains("samples/dracula/den.yaml", files);
        Assert.Contains("packs/core/pack.yaml", files);
        Assert.Contains(files, f => f.StartsWith("packs/core/templates/", StringComparison.Ordinal));
        Assert.True(files.Count > 50, $"Only {files.Count} files found.");
    }

    [Theory]
    [MemberData(nameof(RepoMarkdownAndYaml))]
    public void RepoFileRoundTrips(string path)
    {
        var bytes = TestFiles.ReadRepoFile(path);

        var written = Parse(path, bytes);

        Assert.Equal(bytes, written);
    }

    [Theory]
    [MemberData(nameof(RepoMarkdownAndYaml))]
    public void RepoFileParsesWithoutErrors(string path)
    {
        var bytes = TestFiles.ReadRepoFile(path);
        DenFile file = path.EndsWith(".yaml", StringComparison.Ordinal)
            ? YamlFile.Parse(bytes)
            : MarkdownFile.Parse(bytes, requireFrontmatter: !path.EndsWith("/README.md", StringComparison.Ordinal) && !path.EndsWith("/outline.md", StringComparison.Ordinal));

        Assert.True(file.CanEdit);
        Assert.DoesNotContain(file.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void FixtureRoundTrips(string name)
    {
        var bytes = TestFiles.Fixture(name);

        var written = Parse(name, bytes);

        Assert.Equal(bytes, written);
    }

    [Theory]
    [MemberData(nameof(RepoMarkdownAndYaml))]
    public void SettingAValueToItsCurrentTextChangesNothingElse(string path)
    {
        // Rewriting `name` with the same text keeps the file's bytes, even
        // when it has to pick a quoting style.
        if (!path.EndsWith(".md", StringComparison.Ordinal))
        {
            return;
        }

        var file = MarkdownFile.Parse(TestFiles.ReadRepoFile(path));
        if (file.GetFrontmatterValue("name") is not Yaml.YamlScalar { IsNull: false } name)
        {
            return;
        }

        var edited = file.WithFrontmatterValue("name", Yaml.YamlScalarValue.String(name.Text));

        Assert.Equal(file.ToBytes(), edited.ToBytes());
    }

    private static byte[] Parse(string path, byte[] bytes) =>
        path.EndsWith(".yaml", StringComparison.Ordinal)
            ? YamlFile.Parse(bytes).ToBytes()
            : MarkdownFile.Parse(bytes).ToBytes();
}
