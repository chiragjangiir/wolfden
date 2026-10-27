using System.Text;

namespace Wolfden.Index;

/// <summary>
/// A query term: a word, a phrase in quotes, or a word joined by
/// punctuation (<c>mara-voss</c>). Its words match side by side, in order;
/// with <paramref name="Prefix"/>, the last one matches as a prefix.
/// </summary>
/// <param name="Text">The term's text, without its quotes or prefix star.</param>
/// <param name="Prefix">True if it ended in <c>*</c>.</param>
internal sealed record SearchTerm(string Text, bool Prefix);

/// <summary>
/// Reads a plain search query into terms (#185), the same way for the search
/// table (<see cref="DenIndex.Search"/>) and for field values
/// (<see cref="SearchWords"/>). Words are split at white space. A closed
/// pair of double quotes makes a phrase, whose words match side by side and
/// in order; an unclosed quote is plain text, and empty quotes are ignored.
/// Typographic quotes count too: any opening quote (<c>"</c>, <c>＂</c>,
/// <c>“</c>, <c>„</c>) is closed by any closing one (<c>"</c>, <c>＂</c>,
/// <c>“</c>, <c>”</c>). A <c>*</c> at the end of a word makes it a prefix,
/// and so does one touching the end of a phrase's last word, inside or
/// just after its closing quote (<c>"silver ke*"</c>, <c>"silver ke"*</c>).
/// Any other <c>*</c>, such as one standing alone (<c>"silver *"</c>), and
/// any other punctuation, is plain text, which splits words as it does in
/// notes. Control characters are spaces, so no input throws.
/// </summary>
internal static class SearchQuery
{
    /// <summary>The closing double quotes: ASCII, fullwidth, and typographic (“ closes a German „…“).</summary>
    private static readonly System.Buffers.SearchValues<char> Closing = System.Buffers.SearchValues.Create("\"＂“”");

    /// <summary>An opening double quote: ASCII, fullwidth, or typographic (“ or German „).</summary>
    private static bool IsOpening(char c) => c is '"' or '＂' or '“' or '„';

    /// <summary>True if <paramref name="text"/>'s last word runs to its end, so a <c>*</c> after it touches it.</summary>
    private static bool EndsInWord(string text)
    {
        var tokens = SearchWords.Tokens(text);
        return tokens.Count > 0 && tokens[^1].Start + tokens[^1].Length == text.Length;
    }

    /// <summary>The terms of <paramref name="query"/>, in order, without those that hold no words.</summary>
    public static IReadOnlyList<SearchTerm> Parse(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = string.Create(query.Length, query, (span, q) =>
        {
            for (var i = 0; i < q.Length; i++)
            {
                span[i] = char.IsControl(q[i]) ? ' ' : q[i];
            }
        });

        var terms = new List<SearchTerm>();
        var word = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            var close = IsOpening(c) ? text.AsSpan(i + 1).IndexOfAny(Closing) : -1;
            if (close >= 0)
            {
                AddWord();
                close += i + 1;
                var phrase = text[(i + 1)..close];
                var inner = phrase.TrimEnd();
                var bare = inner.TrimEnd('*');
                var prefix = false;
                if (bare.Length < inner.Length && EndsInWord(bare))
                {
                    // A star inside the quotes, touching the last word.
                    phrase = bare;
                    prefix = true;
                }

                i = close + 1;
                if (i < text.Length && text[i] == '*')
                {
                    // A star just after the quotes, touching the last word.
                    prefix |= EndsInWord(phrase);
                    while (i < text.Length && text[i] == '*')
                    {
                        i++;
                    }
                }

                Add(phrase, prefix);
                continue;
            }

            if (char.IsWhiteSpace(c))
            {
                AddWord();
            }
            else
            {
                word.Append(c);
            }

            i++;
        }

        AddWord();
        return terms;

        void AddWord()
        {
            var w = word.ToString();
            word.Clear();
            Add(w.TrimEnd('*'), w.EndsWith('*'));
        }

        void Add(string term, bool prefix)
        {
            if (SearchWords.Tokens(term).Count > 0)
            {
                terms.Add(new SearchTerm(term, prefix));
            }
        }
    }

    /// <summary>
    /// <paramref name="query"/> as an FTS5 query: each term a quoted FTS5
    /// phrase, with <c>*</c> after it if it's a prefix, and the terms joined
    /// by spaces (AND). Empty if there are no terms.
    /// </summary>
    public static string ToFts5(string query) =>
        string.Join(' ', Parse(query).Select(t => $"\"{t.Text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"{(t.Prefix ? "*" : "")}"));
}
