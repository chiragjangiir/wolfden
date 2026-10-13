using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Tests;

/// <summary>
/// Every string Wolfden writes must read back as that same string, to
/// Wolfden and to other YAML readers. Each corpus string replaces a plain,
/// a single-quoted, a double-quoted, and a null value; a plain result must
/// resolve as a string under YAML 1.2 core and under YAML 1.1. The
/// resolvers here are written independently of Wolfden's own.
/// </summary>
public partial class QuotingCorpusTests
{
    private static readonly string[] Corpus =
    [
        // Ids, keys, and ordinary words
        "01JA7Q8M4XK3V9R2D5W8HN6TQB", "01M4TF5F08GDVZVWZ4JYQDBHZG", "7th-fleet", "Mara Voss", "Kessan Reach", "ember:22",
        "a-quiet-moon:2b", "plain words", "Zoë", "it's", "say \"hi\"", "back\\slash", "C#", "100%", "x-color",
        // Integers and radix forms
        "2291", "014", "0", "-0", "+12", "-7", "0o17", "0O17", "0b1010", "0B11", "0x1F", "0xff", "0xDEADBEEF", "0xCafe",
        "0x_FF", "0X1f", "-0x1F", "+0b1", "1_000", "1,000", "08", "0_7",
        // Floats and specials
        "1e3", "1.5e+3", "1E-3", "3.14", ".5", "5.", "-.5", "1.5_0", ".inf", "-.inf", "+.Inf", ".INF", ".NaN", ".nan", ".NAN", "1.2.3",
        // Sexagesimal, dates, times
        "1:20", "190:20:30", "1:30.5", "12:30", "2026-10-13", "2026-1-3 4:05:06", "2026-10-13T10:00:00Z",
        "2026-10-13t10:00:00.5-05:00", "2026-10-13 10:00:00 +01", "20 September", "3rd", "1984",
        // Booleans in every case
        "true", "True", "TRUE", "false", "False", "FALSE", "yes", "Yes", "YES", "no", "No", "NO", "on", "On", "ON",
        "off", "Off", "OFF", "y", "Y", "n", "N",
        // Nulls and empties
        "null", "Null", "NULL", "~", "", " ", "nil",
        // Whitespace and control characters
        " leading", "trailing ", "\tTab", "tab\there", "multi\nline", "cr\rline", "a\u0085b", "a\u2028b", "a\uFFFEb",
        "a\u0001b", "a\u007Fb",
        // Indicators and YAML syntax
        "[[velari]]", "[x]", "{x}", "@at", "`tick", "!bang", "&amp", "*star", "%pct", "|pipe", ">gt", "'q", "\"q",
        "- dash", "-dash", "?q", "? q", ":colon", "a: b", "a #b", "a#b", "x:", "---", "...", "<<", "=", "#tag",
        "a, b", "key: value # comment", "-", "+", ".",
    ];

    public static TheoryData<string> CorpusData() => new(Corpus);

    [Fact]
    public void CorpusIsBigEnough() => Assert.True(Corpus.Length >= 122, $"{Corpus.Length} strings");

    [Theory]
    [MemberData(nameof(CorpusData))]
    public void StringReadsBackAsItselfEverywhere(string value)
    {
        foreach (var existing in new[] { "x", "'x'", "\"x\"", "~", "" })
        {
            var file = MarkdownFile.Parse(TestFiles.Utf8($"---\nname: {existing}\nother: 1\n---\n"));

            var result = file.WithFrontmatterValue("name", YamlScalarValue.String(value));

            var scalar = Assert.IsType<YamlScalar>(result.GetFrontmatterValue("name"));
            Assert.Equal(value, scalar.Text);
            Assert.False(scalar.IsNull && scalar.Style == YamlScalarStyle.Plain, "Written as null.");
            if (scalar.Style == YamlScalarStyle.Plain)
            {
                Assert.False(Core12Typed().IsMatch(value), $"YAML 1.2 core reads plain '{value}' as a non-string.");
                Assert.False(Yaml11Typed().IsMatch(value), $"YAML 1.1 reads plain '{value}' as a non-string.");
            }

            Assert.Equal("1", result.GetFrontmatterValue("other")!.ToString());
        }
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("0xff")]
    [InlineData("0xDEADBEEF")]
    [InlineData("0xCafe")]
    [InlineData("0x_FF")]
    [InlineData("0o17")]
    [InlineData("0b1010")]
    [InlineData("1:30")]
    [InlineData("017")]
    public void NumberLikeStringsAreQuoted(string value)
    {
        var result = MarkdownFile.Parse(TestFiles.Utf8("---\nname: x\n---\n")).WithFrontmatterValue("name", YamlScalarValue.String(value));

        Assert.Equal($"---\nname: \"{value}\"\n---\n", result.Text);
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("yes")]
    [InlineData("1:30")]
    [InlineData("2026-10-13 10:00:00")]
    [InlineData("<<")]
    public void WolfdenResolverAgreesWithTheTestResolvers(string plain)
    {
        Assert.False(PlainScalarResolver.IsStringInBothSchemas(plain));
    }

    [Theory]
    [InlineData("0x1F")]
    [InlineData("true")]
    [InlineData("~")]
    public void VerificationRejectsAPlainScalarThatIsNotAString(string plain)
    {
        // The backstop: even if the renderer wrote a typed-looking value
        // plain, the edit check refuses it.
        var node = MarkdownFile.Parse(TestFiles.Utf8($"---\nname: {plain}\n---\n")).GetFrontmatterValue("name");

        Assert.False(YamlScalarValue.String(plain).MatchesAsWritten(node));
    }

    /// <summary>YAML 1.2.2 core schema (§10.3.2) non-string plain scalars.</summary>
    [GeneratedRegex(@"^(~|null|Null|NULL|true|True|TRUE|false|False|FALSE|[-+]?\d+|0o[0-7]+|0x[\da-fA-F]+|[-+]?(\.\d+|\d+(\.\d*)?)([eE][-+]?\d+)?|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN))$")]
    private static partial Regex Core12Typed();

    /// <summary>YAML 1.1 types (bool, null, int, float, timestamp, merge, value) for plain scalars.</summary>
    [GeneratedRegex(@"^(~|null|Null|NULL|[yY]|yes|Yes|YES|[nN]|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|[-+]?0b[01_]+|[-+]?0[0-7_]+|[-+]?(0|[1-9][\d_]*)|[-+]?0x[\da-fA-F_]+|[-+]?[1-9][\d_]*(:[0-5]?\d)+|[-+]?(\d[\d_]*)?\.[\d.]*([eE][-+]\d+)?|[-+]?\d[\d_]*(:[0-5]?\d)+\.[\d_]*|[-+]?\.(inf|Inf|INF)|\.(nan|NaN|NAN)|\d{4}-\d\d-\d\d|\d{4}-\d\d?-\d\d?([Tt]|[ \t]+)\d\d?:\d\d:\d\d(\.\d*)?([ \t]*(Z|[-+]\d\d?(:\d\d)?))?|<<|=)$")]
    private static partial Regex Yaml11Typed();
}
