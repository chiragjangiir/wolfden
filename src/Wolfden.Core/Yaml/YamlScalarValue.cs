using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wolfden.Core.Yaml;

/// <summary>
/// A scalar value to write into frontmatter or a YAML file. Writers pick
/// how to write it: a string keeps the quoting style of the value it
/// replaces where it can (format.md F-RT-1), and is quoted whenever YAML
/// would otherwise read it as something other than that text.
/// </summary>
public sealed partial class YamlScalarValue
{
    private enum Kind
    {
        String,
        Integer,
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

    /// <summary>A boolean, written <c>true</c> or <c>false</c>.</summary>
    public static YamlScalarValue Boolean(bool value) => new(Kind.Boolean, value ? "true" : "false");

    /// <inheritdoc />
    public override string ToString() => _kind == Kind.Null ? "null" : _text;

    /// <summary>
    /// The YAML text for this value. An empty result means null.
    /// <paramref name="existing"/> is the style of the value being replaced.
    /// </summary>
    internal string Render(YamlScalarStyle? existing)
    {
        if (_kind != Kind.String)
        {
            return _text;
        }

        if (existing == YamlScalarStyle.SingleQuoted && _text.All(c => !IsSpecialChar(c) && c != '\t'))
        {
            return "'" + _text.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        if (existing != YamlScalarStyle.DoubleQuoted && IsPlainSafe(_text))
        {
            return _text;
        }

        return DoubleQuote(_text);
    }

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
            Kind.Integer => scalar.Style == YamlScalarStyle.Plain && scalar.Text == _text,
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

        return !ReservedWord().IsMatch(text);
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
    /// time: a digit first, then only digits, separators, signs, spaces, and
    /// the letters of exponents, radix prefixes, and timestamps. Cautious on
    /// purpose; <c>2291</c>, <c>014</c>, <c>1:20</c>, and <c>2026-10-13</c>
    /// are quoted, while a ULID or <c>7th-fleet</c> isn't.
    /// </summary>
    [GeneratedRegex("^[0-9][0-9_:.,+ eExXoObBtTzZ-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberLike();

    [GeneratedRegex("^(?:null|true|false|yes|no|on|off|y|n)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReservedWord();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeKey();
}
