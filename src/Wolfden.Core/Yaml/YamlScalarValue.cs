using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wolfden.Core.Text;

namespace Wolfden.Core.Yaml;

/// <summary>
/// A scalar value to write into frontmatter or a YAML file. Writers pick
/// how to write it: a string keeps the quoting style of the value it
/// replaces where it can (format.md F-RT-1), and is quoted whenever YAML
/// would otherwise read it as something other than that text.
/// </summary>
public sealed partial class YamlScalarValue : YamlValue
{
    private enum Kind
    {
        String,
        Integer,
        Float,
        Boolean,
        Null,
    }

    private readonly Kind _kind;
    private readonly string _text;

    private YamlScalarValue(Kind kind, string text)
    {
        _kind = kind;
        _text = text;
    }

    /// <summary>A null value, written as nothing after the colon (<c>born:</c>, format.md F-FLD-4).</summary>
    public static YamlScalarValue Null { get; } = new(Kind.Null, "");

    /// <summary>A string. Quoted when it has to be, so <c>"2291"</c> stays a string.</summary>
    public static YamlScalarValue String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasUnpairedSurrogate(value))
        {
            throw new ArgumentException("The value has an unpaired surrogate, so it can't be written as UTF-8.", nameof(value));
        }

        return new(Kind.String, value);
    }

    /// <summary>An integer, written plain (<c>format: 1</c>).</summary>
    public static YamlScalarValue Integer(long value) => new(Kind.Integer, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// A number that isn't whole, such as <c>2.5</c> or <c>1e3</c>, written
    /// plain as given. <paramref name="text"/> must be a float under YAML
    /// 1.2's core schema (types.md F-FT-6), without <c>.inf</c> or <c>.nan</c>.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> isn't such a number.</exception>
    public static YamlScalarValue Float(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return FloatPattern().IsMatch(text)
            ? new(Kind.Float, text)
            : throw new ArgumentException($"'{text}' isn't a number YAML reads as one, such as 2.5 or 1e3.", nameof(text));
    }

    /// <summary>
    /// A plain scalar that YAML reads as something other than a string, written
    /// exactly as given: a number in any form YAML has (<c>2.50</c>, <c>0x1F</c>,
    /// <c>0o17</c>, <c>1_000</c>, <c>.inf</c>), or another such value
    /// (<c>True</c>, <c>yes</c>, <c>2291-05-14</c>). It keeps a value's written
    /// form when it's written again, as a whole or as a moved list item.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="text"/> is one line YAML reads as a string, so it would need quotes.</exception>
    public static YamlScalarValue Plain(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsPlainNonString(text)
            ? new(Kind.Float, text)
            : throw new ArgumentException($"'{text}' is text, not a number or other plain value YAML reads as one.", nameof(text));
    }

    /// <summary>
    /// True if <paramref name="text"/>, written plain, isn't a string to YAML 1.2's core schema
    /// or to YAML 1.1: a number, boolean, null, or YAML 1.1 timestamp.
    /// </summary>
    public static bool IsPlainNonString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !PlainScalarResolver.IsStringInBothSchemas(text);
    }

    /// <summary>A boolean, written <c>true</c> or <c>false</c>.</summary>
    public static YamlScalarValue Boolean(bool value) => new(Kind.Boolean, value ? "true" : "false");

    /// <inheritdoc />
    public override string ToString() => _kind == Kind.Null ? "null" : _text;

    /// <summary>
    /// The YAML text for this value. An empty result means null.
    /// <paramref name="existing"/> is the style of the value being replaced.
    /// </summary>
    internal string Render(YamlScalarStyle? existing) => Render(existing, flow: false);

    /// <summary>
    /// The YAML text for this value in block or flow context. In a flow
    /// collection a null is written <c>~</c>, since an empty value there is
    /// easy to misread, and a plain string must also avoid the flow
    /// indicators <c>, [ ] { }</c>, <c>:</c>, and <c>?</c>, which some YAML
    /// 1.1 readers (PyYAML) take as the end of a plain scalar inside
    /// braces. A new string with a <c>:</c> in it, such as a position
    /// (<c>ember:22</c>), is double-quoted as format.md §7.3 writes
    /// positions; one that replaces a plain value keeps that style.
    /// </summary>
    internal string Render(YamlScalarStyle? existing, bool flow)
    {
        if (_kind == Kind.Null && flow)
        {
            return "~";
        }

        if (_kind != Kind.String)
        {
            return _text;
        }

        if (existing == YamlScalarStyle.SingleQuoted && _text.All(c => !IsSpecialChar(c) && c != '\t'))
        {
            return "'" + _text.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        if (existing != YamlScalarStyle.DoubleQuoted && IsPlainSafe(_text)
            && !(flow && _text.AsSpan().IndexOfAny(",[]{}:?") >= 0)
            && !(existing is null && _text.Contains(':', StringComparison.Ordinal)))
        {
            return _text;
        }

        return DoubleQuote(_text);
    }

    internal override string RenderFlow(YamlNode? like, SourceText? source) => Render(StyleOf(like), flow: true);

    /// <summary>The style a new value should copy from <paramref name="node"/>:
    /// a non-null scalar's, or none. An empty or <c>~</c> value has no style to keep.</summary>
    internal static YamlScalarStyle? StyleOf(YamlNode? node) => node is YamlScalar { IsNull: false } scalar ? scalar.Style : null;

    /// <summary>
    /// True if <paramref name="node"/> holds this value as Wolfden wrote it:
    /// like <see cref="Matches"/>, but a string written plain must also be a
    /// string to other YAML readers (<see cref="PlainScalarResolver"/>), so
    /// a quoting gap can't turn it into a number, bool, or null.
    /// </summary>
    internal override bool MatchesAsWritten(YamlNode? node) =>
        Matches(node) && (_kind != Kind.String || node is not YamlScalar { Style: YamlScalarStyle.Plain } plain
            || PlainScalarResolver.IsStringInBothSchemas(plain.Text));

    /// <summary>True if <paramref name="node"/> already holds this value, read as format.md F-YAML-5 reads it.</summary>
    public bool Matches(YamlNode? node)
    {
        if (node is not YamlScalar scalar)
        {
            return false;
        }

        return _kind switch
        {
            Kind.Null => scalar.IsNull,
            Kind.Boolean => scalar.AsBoolean() == (_text == "true"),
            Kind.Integer or Kind.Float => scalar.Style == YamlScalarStyle.Plain && scalar.Text == _text,
            _ => !scalar.IsNull && scalar.Text == _text,
        };
    }

    /// <summary>
    /// True if <paramref name="text"/> can be written unquoted in block
    /// context and read back as the same string, under YAML 1.2's core schema
    /// and under YAML 1.1 (so other tools agree). Deliberately cautious:
    /// anything that looks like a number, date, boolean, or null is quoted.
    /// </summary>
    internal static bool IsPlainSafe(string text)
    {
        if (text.Length == 0 || text[0] is ' ' or '\t' || text[^1] is ' ' or '\t' or ':')
        {
            return false;
        }

        if ("-?:,[]{}#&*!|>'\"%@`<=~.+".Contains(text[0], StringComparison.Ordinal) || NumberLike().IsMatch(text))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (IsSpecialChar(c) || c == '\t')
            {
                return false;
            }
        }

        if (text.Contains(": ", StringComparison.Ordinal) || text.Contains(" #", StringComparison.Ordinal))
        {
            return false;
        }

        return !ReservedWord().IsMatch(text) && PlainScalarResolver.IsStringInBothSchemas(text);
    }

    /// <summary>A key a writer may add: a plain word such as <c>x-color</c> or <c>see_also</c>.</summary>
    internal static bool IsSafeKey(string key) => SafeKey().IsMatch(key) && IsPlainSafe(key);

    private static bool IsSpecialChar(char c) =>
        char.IsControl(c) || c is '\u2028' or '\u2029' or '\uFEFF' or '\uFFFE' or '\uFFFF';

    private static bool HasUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
            }
            else if (char.IsSurrogate(value[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string DoubleQuote(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (IsSpecialChar(c))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    /// <summary>
    /// Anything a YAML 1.1 or 1.2 reader might take for a number, date, or
    /// time: a digit first, then only digits, hex digits, separators, signs,
    /// spaces, and the letters of radix prefixes and timestamps. Cautious on
    /// purpose; <c>2291</c>, <c>014</c>, <c>0x1F</c>, <c>0x_FF</c>,
    /// <c>1:20</c>, and <c>2026-10-13</c> are quoted, while a ULID or
    /// <c>7th-fleet</c> isn't. <see cref="PlainScalarResolver"/> backs this
    /// up when an edit is verified.
    /// </summary>
    [GeneratedRegex("^[0-9][0-9a-fA-F_:.,+ xXoObBtTzZ-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    [GeneratedRegex("^(?:null|true|false|yes|no|on|off|y|n)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWord();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKey();

    /// <summary>A float under YAML 1.2's core schema, as types.md F-FT-6 reads one, without <c>.inf</c> and <c>.nan</c>.</summary>
    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();
}
