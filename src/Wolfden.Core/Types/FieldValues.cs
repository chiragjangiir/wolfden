using System.Globalization;
using System.Text.RegularExpressions;
using Wolfden.Core.Links;
using Wolfden.Core.Positions;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Types;

/// <summary>A value that doesn't match its field type (types.md §5.2), as a warning.</summary>
/// <param name="RuleId">The rule, such as <c>F-FT-8</c>.</param>
/// <param name="Line">The 1-based line of the value or item.</param>
/// <param name="Message">A message for the writer.</param>
/// <param name="Node">The value or list item that doesn't match.</param>
/// <param name="IsAdvisory">
/// True for a warning that doesn't make the value a mismatch, such as a
/// date's <c>calendar</c> key (types.md F-FT-11): the value is still read.
/// </param>
public sealed record ValueProblem(string RuleId, int Line, string Message, YamlNode Node, bool IsAdvisory = false);

/// <summary>The result of checking a plain value against a field definition (types.md §5.2, §5.3).</summary>
public sealed class ValueCheck
{
    internal ValueCheck(IReadOnlyList<ValueProblem> problems, IReadOnlyList<YamlNode> items, bool isNull, bool isEmptyList)
    {
        Problems = problems;
        Items = items;
        IsNull = isNull;
        IsEmptyList = isEmptyList;
    }

    /// <summary>The problems, in file order, including advisory ones, which don't stop the value matching.</summary>
    public IReadOnlyList<ValueProblem> Problems { get; }

    /// <summary>True if the whole value matches its field type.</summary>
    public bool Matches => Problems.All(p => p.IsAdvisory);

    /// <summary>
    /// The items that match: for a list field, each matching list item (a
    /// value that isn't a sequence is one item, F-FT-12); otherwise the value
    /// itself if it matches. Empty for a null value.
    /// </summary>
    public IReadOnlyList<YamlNode> Items { get; }

    /// <summary>True for a null value (F-FLD-4), which matches every type.</summary>
    public bool IsNull { get; }

    /// <summary>True for an empty list on a list field: unset for an entity field (F-FT-12), "none here" for a chapter field (F-CHAP-5).</summary>
    public bool IsEmptyList { get; }
}

/// <summary>An in-world date (types.md F-FT-11): its text, its sort key, or both.</summary>
/// <param name="Text">The date's text, or null for a mapping with only <c>sort</c>.</param>
/// <param name="SortKey">The numbers it sorts by, or null if it has no sort key.</param>
public sealed record InWorldDate(string? Text, IReadOnlyList<long>? SortKey);

/// <summary>
/// How a value must look for each field type (types.md §5.2, the check
/// format.md F-FLD-3 refers to). Every problem is a warning: a value that
/// doesn't match is kept and indexed as written.
/// </summary>
public static partial class FieldValues
{
    /// <summary>
    /// Checks a plain value (not an entry, F-TMP-1) against
    /// <paramref name="field"/>. For an entry or a temporal list, check each
    /// entry's <c>value</c>.
    /// </summary>
    public static ValueCheck Check(FieldDefinition field, YamlNode value)
    {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(value);
        var problems = new List<ValueProblem>();
        var items = new List<YamlNode>();
        if (value is YamlScalar { IsNull: true })
        {
            return new ValueCheck(problems, items, isNull: true, isEmptyList: false);
        }

        if (!field.IsList)
        {
            if (CheckItem(field, value, problems))
            {
                items.Add(value);
            }

            return new ValueCheck(problems, items, false, false);
        }

        // F-FT-12: a value that isn't a sequence is a one-item list.
        var list = value as YamlSequence;
        foreach (var item in list?.Items ?? [value])
        {
            if (item is YamlScalar { IsNull: true })
            {
                problems.Add(new ValueProblem("F-FT-12", item.Line, $"{field.Name} has an empty item. Remove it, or give it a value.", item));
            }
            else if (item is YamlSequence)
            {
                problems.Add(new ValueProblem("F-FT-12", item.Line,
                    $"{field.Name} has a list inside its list, and there are no lists of lists.{ItemQuoteHint(field, item)}", item));
            }
            else if (CheckItem(field, item, problems))
            {
                items.Add(item);
            }
        }

        return new ValueCheck(problems, items, false, list is { Items.Count: 0 });
    }

    /// <summary>Reads an in-world date (F-FT-11), or returns null if the value isn't one.</summary>
    public static InWorldDate? ReadDate(YamlNode value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var problems = new List<ValueProblem>();
        return ReadDate("date", value, problems);
    }

    /// <summary>
    /// The sort key of a date's text (F-FT-11), such as 2291, 5, 14 for
    /// <c>2291-05-14</c>, or null if the whole text isn't a <c>date-key</c>.
    /// </summary>
    public static IReadOnlyList<long>? SortKey(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = DateKey().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var negative = text.StartsWith('-');
        var numbers = new List<long>();
        foreach (Match number in Digits().Matches(text))
        {
            if (!long.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                return null;
            }

            numbers.Add(numbers.Count == 0 && negative ? -n : n);
        }

        return numbers;
    }

    /// <summary>True if <paramref name="text"/> is a ref value: exactly one link and nothing else, after trimming (F-FT-9, F-LINK-11).</summary>
    public static bool IsSingleLink(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        var links = LinkSyntax.FindLinks(trimmed);
        return links.Count == 1 && links[0].Text.Length == trimmed.Length;
    }

    private static bool CheckItem(FieldDefinition field, YamlNode node, List<ValueProblem> problems)
    {
        var before = problems.Count(p => !p.IsAdvisory);
        var name = field.Name;
        void Problem(string rule, string message) => problems.Add(new ValueProblem(rule, node.Line, message, node));

        switch (field.Kind)
        {
            case FieldKind.String:
                if (node is not YamlScalar s)
                {
                    Problem("F-FT-4", $"{name} is a string, not {Shape(node)}.");
                }
                else if (s.Text.TrimEnd('\r', '\n').Contains('\n', StringComparison.Ordinal))
                {
                    Problem("F-FT-4", $"{name} is a one-line string, but this value has a line break. Use a text field for text over several lines.");
                }

                break;
            case FieldKind.Text:
                if (node is not YamlScalar)
                {
                    Problem("F-FT-5", $"{name} is text, not {Shape(node)}.");
                }

                break;
            case FieldKind.Number:
                if (node is not YamlScalar { Style: YamlScalarStyle.Plain } number || !IsNumber(number.Text))
                {
                    Problem("F-FT-6", $"{name} is a number, such as 140 or 2.5, not '{Describe(node)}'. Don't quote it.");
                }

                break;
            case FieldKind.Bool:
                if (node is not YamlScalar b || b.AsBoolean() is null)
                {
                    Problem("F-FT-7", $"{name} is true or false, not '{Describe(node)}'.");
                }

                break;
            case FieldKind.Date:
                ReadDate(name, node, problems);
                break;
            case FieldKind.Enum:
                if (node is not YamlScalar e || field.MatchEnum(e.Text) is null)
                {
                    var suggestion = node is YamlScalar written ? Suggest(field, written.Text) : null;
                    Problem("F-FT-8", $"{name} is one of {string.Join(", ", field.Values)}, not '{Describe(node)}'."
                        + (suggestion is null ? "" : $" Did you mean '{suggestion}'?"));
                }

                break;
            case FieldKind.Ref:
                if (node is not YamlScalar r || !IsSingleLink(r.Text))
                {
                    Problem("F-FT-9", $"{name} is a link to one entity, such as \"[[mara-voss]]\", in quotes, with nothing else in the string, not '{Describe(node)}'.{QuoteHint(field, node)}");
                }

                break;
            case FieldKind.Position:
                if (node is not YamlScalar p)
                {
                    Problem("F-FT-10", $"{name} holds a story position, such as \"ember:14\", not {Shape(node)}: {StoryPosition.InvalidMessage}.");
                }
                else if (!StoryPosition.TryParse(p.Text, out _, out var error))
                {
                    Problem("F-FT-10", error.Message);
                }

                break;
        }

        return problems.Count(p => !p.IsAdvisory) == before;
    }

    private static InWorldDate? ReadDate(string name, YamlNode node, List<ValueProblem> problems)
    {
        void Problem(YamlNode at, string message) => problems.Add(new ValueProblem("F-FT-11", at.Line, message, at));

        // F-FT-11: calendar and unknown keys are advisory; the date is still read.
        void Advise(YamlNode at, string message) => problems.Add(new ValueProblem("F-FT-11", at.Line, message, at, IsAdvisory: true));

        if (node is YamlScalar { IsNull: false } scalar)
        {
            return new InWorldDate(scalar.Text, SortKey(scalar.Text));
        }

        if (node is not YamlMapping map)
        {
            Problem(node, $"{name} is a date: a string such as \"2291-05-14\", or a mapping with text and sort, not {Shape(node)}.");
            return null;
        }

        string? text = null;
        List<long>? sort = null;
        var ok = true;
        foreach (var entry in map.Entries)
        {
            switch (entry.KeyText)
            {
                case "text":
                    if (entry.Value is YamlScalar { IsNull: false } t)
                    {
                        text = t.Text;
                    }
                    else if (entry.Value is not YamlScalar { IsNull: true })
                    {
                        Problem(entry.Value, $"{name}'s text must be a string.");
                        ok = false;
                    }

                    break;
                case "sort":
                    sort = ReadSort(entry.Value);
                    if (sort is null && entry.Value is not YamlScalar { IsNull: true })
                    {
                        Problem(entry.Value, $"{name}'s sort must be an integer or a non-empty list of integers, such as [2291, 5].");
                        ok = false;
                    }

                    break;
                case "calendar":
                    Advise(entry.Key, "Custom calendars aren't supported yet (#37), so calendar is kept and otherwise ignored.");
                    break;
                default:
                    Advise(entry.Key, $"A date has text and sort, not '{entry.KeyText}'. The key is kept and ignored.");
                    break;
            }
        }

        if (text is null && sort is null)
        {
            if (ok)
            {
                Problem(node, $"{name} needs text, sort, or both.");
            }

            return null;
        }

        return ok ? new InWorldDate(text, sort) : null;
    }

    private static List<long>? ReadSort(YamlNode node)
    {
        if (node is YamlScalar one)
        {
            return TryInteger(one, out var n) ? [n] : null;
        }

        if (node is not YamlSequence { Items.Count: > 0 } list)
        {
            return null;
        }

        var result = new List<long>();
        foreach (var item in list.Items)
        {
            if (item is not YamlScalar s || !TryInteger(s, out var n))
            {
                return null;
            }

            result.Add(n);
        }

        return result;
    }

    /// <summary>A YAML 1.2 core-schema integer, written plain.</summary>
    private static bool TryInteger(YamlScalar scalar, out long value)
    {
        value = 0;
        if (scalar.Style != YamlScalarStyle.Plain)
        {
            return false;
        }

        var text = scalar.Text;
        try
        {
            if (text.StartsWith("0o", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(c => c is >= '0' and <= '7'))
            {
                value = Convert.ToInt64(text[2..], 8);
                return true;
            }

            if (text.StartsWith("0x", StringComparison.Ordinal) && text.Length > 2 && text[2..].All(char.IsAsciiHexDigit))
            {
                value = Convert.ToInt64(text[2..], 16);
                return true;
            }
        }
        catch (OverflowException)
        {
            return false;
        }

        return IntegerPattern().IsMatch(text)
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>A YAML 1.2 core-schema integer or finite float (F-FT-6).</summary>
    private static bool IsNumber(string text) =>
        IntegerPattern().IsMatch(text) || FloatPattern().IsMatch(text)
        || (text.Length > 2 && text.StartsWith("0o", StringComparison.Ordinal) && text[2..].All(c => c is >= '0' and <= '7'))
        || (text.Length > 2 && text.StartsWith("0x", StringComparison.Ordinal) && text[2..].All(char.IsAsciiHexDigit));

    /// <summary>A defined key close to what was written, such as <c>third-limited</c> for <c>third_limited</c>.</summary>
    private static string? Suggest(FieldDefinition field, string written)
    {
        var folded = LinkText.CaseFold(written.Trim());
        var dashed = SeparatorRun().Replace(folded, "-");
        var exact = field.Values.FirstOrDefault(v => v == dashed);
        if (exact is not null)
        {
            return exact;
        }

        var best = field.Values
            .Select(v => (Value: v, Distance: Distance(v, dashed)))
            .OrderBy(x => x.Distance)
            .FirstOrDefault();
        return best.Value is not null && best.Distance <= Math.Max(1, best.Value.Length / 3) ? best.Value : null;
    }

    private static int Distance(string a, string b)
    {
        var previous = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var current = new int[b.Length + 1];
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            previous = current;
        }

        return previous[b.Length];
    }

    /// <summary>F-FLD-6: an unquoted <c>[[x]]</c> is a list inside a list.</summary>
    private static string QuoteHint(FieldDefinition field, YamlNode node) =>
        field.Kind == FieldKind.Ref && node is YamlSequence { Items: [YamlSequence { Items: [YamlScalar inner] }] }
            ? $" Without quotes, YAML reads [[{inner.Text}]] as a list inside a list: write \"[[{inner.Text}]]\" (F-FLD-6)."
            : "";

    /// <summary>
    /// F-FLD-6 for an item of a list ref field: in <c>pov: [[pike]]</c>, the
    /// value's one item is a list holding one string. (In
    /// <c>pov: ["[[voss]]", [[pike]]]</c> the item is a list inside a list,
    /// which gets no hint.)
    /// </summary>
    private static string ItemQuoteHint(FieldDefinition field, YamlNode item) =>
        field.Kind == FieldKind.Ref && item is YamlSequence { Items: [YamlScalar inner] }
            ? $" Without quotes, YAML reads [[{inner.Text}]] as a list inside a list: write \"[[{inner.Text}]]\" (F-FLD-6)."
            : "";

    private static string Shape(YamlNode node) => node switch
    {
        YamlSequence => "a list",
        YamlMapping => "a mapping",
        _ => "a scalar",
    };

    private static string Describe(YamlNode node) => node switch
    {
        YamlScalar s => s.Text,
        YamlSequence => "a list",
        _ => "a mapping",
    };

    [GeneratedRegex(@"^[-+]?[0-9]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex IntegerPattern();

    [GeneratedRegex(@"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex FloatPattern();

    [GeneratedRegex(@"^-?[0-9]+(?:(?:-[0-9]+)*|-[0-9]+-[0-9]+[ T][0-9]{1,2}:[0-9]{2}(?::[0-9]{2})?)\z", RegexOptions.CultureInvariant)]
    private static partial Regex DateKey();

    [GeneratedRegex("[0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"[\s_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRun();
}
