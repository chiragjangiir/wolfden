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
/// A <c>*</c> at the end of a word, or of a phrase's last word (inside or
/// just after its closing quote), makes that word a prefix. Any other
/// <c>*</c> or punctuation is plain text, which splits words as it does in
/// notes. Control characters are spaces, so no input throws.
/// </summary>
internal static class SearchQuery
{
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
            var close = c == '"' ? text.IndexOf('"', i + 1) : -1;
            if (close >= 0)
            {
                AddWord();
                var phrase = text[(i + 1)..close].TrimEnd();
                var prefix = phrase.EndsWith('*');
                i = close + 1;
                while (i < text.Length && text[i] == '*')
                {
                    prefix = true;
                    i++;
                }

                Add(phrase.TrimEnd('*'), prefix);
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
