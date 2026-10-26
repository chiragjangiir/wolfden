using System.Globalization;
using System.Text;

namespace Wolfden.Index;

/// <summary>A match in a search hit's text: where it starts, and how long it is, in UTF-16 code units.</summary>
/// <param name="Start">The 0-based offset of its first character.</param>
/// <param name="Length">Its length.</param>
public sealed record TextMatch(int Start, int Length);

/// <summary>
/// An excerpt with its search matches (#179): plain text, and where the
/// matches are in it. Unlike brackets in the text, the ranges can't be
/// mistaken for a note's own <c>[</c> and <c>]</c>.
/// </summary>
/// <param name="Text">The text, without marks.</param>
/// <param name="Matches">The matches, in order, not overlapping.</param>
public sealed record MarkedText(string Text, IReadOnlyList<TextMatch> Matches)
{
    /// <summary>The mark the search table's snippets open a match with. The table holds no control characters (<see cref="WithoutMarks"/>).</summary>
    public const char Open = '\u0001';

    /// <summary>The mark the search table's snippets close a match with.</summary>
    public const char Close = '\u0002';

    /// <summary>
    /// The text with each match in <c>[</c> and <c>]</c>, as search snippets
    /// read before #179 and as the MCP tools still give them: easy to read,
    /// though a bracket can also be the note's own.
    /// </summary>
    public string Bracketed()
    {
        var builder = new StringBuilder(Text.Length + (2 * Matches.Count));
        var at = 0;
        foreach (var m in Matches)
        {
            builder.Append(Text, at, m.Start - at).Append('[').Append(Text, m.Start, m.Length).Append(']');
            at = m.Start + m.Length;
        }

        return builder.Append(Text, at, Text.Length - at).ToString();
    }

    /// <summary>Reads text whose matches sit between <paramref name="open"/> and <paramref name="close"/>.</summary>
    public static MarkedText FromMarks(string marked, char open, char close)
    {
        ArgumentNullException.ThrowIfNull(marked);
        var text = new StringBuilder(marked.Length);
        var matches = new List<TextMatch>();
        var start = -1;
        foreach (var c in marked)
        {
            if (c == open)
            {
                start = text.Length;
            }
            else if (c == close)
            {
                if (start >= 0 && text.Length > start)
                {
                    matches.Add(new TextMatch(start, text.Length - start));
                }

                start = -1;
            }
            else
            {
                text.Append(c);
            }
        }

        return new MarkedText(text.ToString(), Merge(matches));
    }

    /// <summary>
    /// <paramref name="text"/> with every control character but line breaks
    /// and tabs as a space, so it can't hold a snippet's marks. The search
    /// tokenizer reads them as spaces anyway.
    /// </summary>
    public static string WithoutMarks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(IsMarkLike)
            ? string.Create(text.Length, text, (span, t) =>
            {
                for (var i = 0; i < t.Length; i++)
                {
                    span[i] = IsMarkLike(t[i]) ? ' ' : t[i];
                }
            })
            : text;

        static bool IsMarkLike(char c) => char.IsControl(c) && c is not ('\n' or '\r' or '\t');
    }

    /// <summary>Sorts matches and joins those that overlap or touch.</summary>
    public static IReadOnlyList<TextMatch> Merge(IEnumerable<TextMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        var merged = new List<TextMatch>();
        foreach (var m in matches.Where(m => m.Length > 0).OrderBy(m => m.Start).ThenByDescending(m => m.Length))
        {
            if (merged.Count > 0 && m.Start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = last with { Length = Math.Max(last.Start + last.Length, m.Start + m.Length) - last.Start };
            }
            else
            {
                merged.Add(m);
            }
        }

        return merged;
    }
}

/// <summary>
/// Matches search words in text the way the search table does (#179), for
/// text that isn't in it, such as field values: the text is split into words
/// of letters and digits, compared ignoring case and accents. Each query word
/// matches whole, or as a prefix if it ends in <c>*</c>, and a query word
/// that holds several (<c>mara-voss</c>) matches them side by side, as a phrase.
/// </summary>
public sealed class SearchWords
{
    private readonly List<(string[] Words, bool Prefix)> _terms;

    private SearchWords(List<(string[] Words, bool Prefix)> terms)
    {
        _terms = terms;
    }

    /// <summary>True if the query has no words to search for.</summary>
    public bool IsEmpty => _terms.Count == 0;

    /// <summary>How many query words (terms) there are.</summary>
    public int Count => _terms.Count;

    /// <summary>The words of <paramref name="query"/>, read as <see cref="DenIndex.Search"/> reads them.</summary>
    public static SearchWords Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new List<(string[] Words, bool Prefix)>();
        var clean = new string(query.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
        foreach (var part in clean.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var prefix = part.EndsWith('*');
            var words = Tokens(part.TrimEnd('*')).Select(t => t.Folded).ToArray();
            if (words.Length > 0)
            {
                terms.Add((words, prefix));
            }
        }

        return new SearchWords(terms);
    }

    /// <summary>
    /// Where each query word occurs in <paramref name="text"/>: for each
    /// term, in query order, the ranges of its occurrences (empty if none).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TextMatch>> Find(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = Tokens(text);
        var found = new List<IReadOnlyList<TextMatch>>(_terms.Count);
        foreach (var (words, prefix) in _terms)
        {
            var at = new List<TextMatch>();
            for (var i = 0; i + words.Length <= tokens.Count; i++)
            {
                var ok = true;
                for (var j = 0; j < words.Length && ok; j++)
                {
                    var token = tokens[i + j].Folded;
                    ok = prefix && j == words.Length - 1 ? token.StartsWith(words[j], StringComparison.Ordinal) : token == words[j];
                }

                if (ok)
                {
                    var first = tokens[i];
                    var last = tokens[i + words.Length - 1];
                    at.Add(new TextMatch(first.Start, last.Start + last.Length - first.Start));
                }
            }

            found.Add(at);
        }

        return found;
    }

    /// <summary>The words of <paramref name="text"/>: runs of letters, digits, and marks, each with its folded form.</summary>
    private static List<(int Start, int Length, string Folded)> Tokens(string text)
    {
        var tokens = new List<(int Start, int Length, string Folded)>();
        var start = -1;
        var i = 0;
        while (i <= text.Length)
        {
            var isWord = false;
            var width = 1;
            if (i < text.Length)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                width = char.IsSurrogatePair(text, i) ? 2 : 1;
                isWord = category is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                    or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber or UnicodeCategory.NonSpacingMark
                    or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.PrivateUse;
            }

            if (isWord && start < 0)
            {
                start = i;
            }
            else if (!isWord && start >= 0)
            {
                var folded = Fold(text[start..i]);
                if (folded.Length > 0)
                {
                    tokens.Add((start, i - start, folded));
                }

                start = -1;
            }

            i += width;
        }

        return tokens;
    }

    /// <summary>Lower case, without accents.</summary>
    private static string Fold(string word)
    {
        var builder = new StringBuilder(word.Length);
        foreach (var c in word.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}
