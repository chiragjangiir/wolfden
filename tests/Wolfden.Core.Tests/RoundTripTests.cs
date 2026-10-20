using System.Text;
using Wolfden.Core.Documents;

namespace Wolfden.Core.Tests;

/// <summary>
/// Reading a file and writing it back unchanged gives the same bytes
/// (format.md F-RT-3, #18), for every sample den under <c>samples/</c>,
/// every shipped pack under <c>packs/</c>, and every crafted fixture. Dens
/// and packs are found at test time, so a new sample is covered without
/// touching this class, and each file is its own theory case, so a failure
/// names the file.
/// </summary>
/// <remarks>
/// Line endings and git: <c>.gitattributes</c> checks every sample out with
/// LF (<c>* text=auto eol=lf</c>), whatever <c>core.autocrlf</c> says, so
/// the samples only ever give LF bytes. The round-trip tests compare the
/// file's bytes with themselves, so they'd hold for any checkout anyway. To
/// cover CRLF, a byte order mark, and a missing final newline on real
/// notes without depending on how git checked them out,
/// <see cref="SampleFileRoundTripsWithOtherEncodings"/> builds those
/// variants in memory from each sample. The crafted fixtures that need
/// exact bytes on disk are marked <c>-text</c> and guarded by
/// <see cref="GitAttributesGuardTests"/>.
/// </remarks>
public class RoundTripTests
{
    public static TheoryData<string> RepoMarkdownAndYaml() => new(TestFiles.SampleAndPackFiles());

    public static TheoryData<string> Fixtures() =>
        new(Directory.EnumerateFiles(TestFiles.FixturesDir).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);

    /// <summary>Every sample file crossed with each in-memory encoding variant.</summary>
    public static TheoryData<string, string> SampleFilesWithVariants()
    {
        var data = new TheoryData<string, string>();
        foreach (var path in TestFiles.SampleFiles())
        {
            foreach (var variant in Variants)
            {
                data.Add(path, variant);
            }
        }

        return data;
    }

    private static readonly string[] Variants = ["crlf", "bom", "bom-crlf", "no-final-newline", "crlf-no-final-newline"];

    [Fact]
    public void DiscoversEverySampleDenAndPack()
    {
        var dens = TestFiles.SampleDens().ToList();
        var files = TestFiles.SampleAndPackFiles().ToList();

        Assert.Contains("samples/dracula", dens);
        Assert.Contains("packs/core", TestFiles.ShippedPacks());
        Assert.All(dens, den => Assert.Contains($"{den}/den.yaml", files));
        Assert.Contains("packs/core/pack.yaml", files);
        Assert.Contains(files, f => f.StartsWith("packs/core/templates/", StringComparison.Ordinal));
        Assert.True(files.Count > 50, $"Only {files.Count} files found.");
    }

    [Fact]
    public void LeavesOutLocalStateUnderDotFolders()
    {
        // A den's .wolfden/ folder (F-LAY-2) is gitignored local state. If
        // someone has indexed a sample, its files mustn't become test cases.
        Assert.DoesNotContain(TestFiles.SampleAndPackFiles(), f => f.Split('/').Any(segment => segment.StartsWith('.')));
    }

    [Theory]
    [Trait("Scenario", "SC-RT-1")]
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
    [Trait("Scenario", "SC-RT-1")]
    [MemberData(nameof(SampleFilesWithVariants))]
    public void SampleFileRoundTripsWithOtherEncodings(string path, string variant)
    {
        var bytes = Variant(TestFiles.ReadRepoFile(path), variant);

        var written = Parse(path, bytes);

        Assert.Equal(bytes, written);
    }

    [Theory]
    [Trait("Scenario", "SC-RT-1")]
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

    [Theory]
    [InlineData("crlf")]
    [InlineData("bom-crlf")]
    [InlineData("crlf-no-final-newline")]
    public void VariantsReallyHaveTheirEncoding(string variant)
    {
        var bytes = Variant(TestFiles.ReadRepoFile("samples/dracula/den.yaml"), variant);
        var text = Encoding.UTF8.GetString(bytes);

        Assert.Equal(variant.StartsWith("bom", StringComparison.Ordinal), bytes.AsSpan().StartsWith((byte[])[0xEF, 0xBB, 0xBF]));
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Equal(!variant.EndsWith("no-final-newline", StringComparison.Ordinal), text.EndsWith('\n'));
    }

    /// <summary>
    /// The sample's bytes re-encoded as <paramref name="variant"/>. Line
    /// endings are made LF first, so the result is the same whatever line
    /// endings the checkout gave.
    /// </summary>
    internal static byte[] Variant(byte[] bytes, string variant)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.StartsWith('﻿'))
        {
            text = text[1..];
        }

        if (variant.Contains("no-final-newline", StringComparison.Ordinal))
        {
            text = text.TrimEnd('\n');
        }

        if (variant.Contains("crlf", StringComparison.Ordinal))
        {
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        var encoded = Encoding.UTF8.GetBytes(text);
        return variant.StartsWith("bom", StringComparison.Ordinal) ? [0xEF, 0xBB, 0xBF, .. encoded] : encoded;
    }

    private static byte[] Parse(string path, byte[] bytes) =>
        path.EndsWith(".yaml", StringComparison.Ordinal)
            ? YamlFile.Parse(bytes).ToBytes()
            : MarkdownFile.Parse(bytes).ToBytes();
}
