using System.Buffers;
using System.Text;
using Wolfden.Core.Documents;

namespace Wolfden.Core.Text;

/// <summary>Which line endings a file uses (format.md F-ENC-3).</summary>
public enum LineEndingKind
{
    /// <summary>The file has no line breaks at all.</summary>
    None,

    /// <summary>Every line break is LF.</summary>
    Lf,

    /// <summary>Every line break is CRLF.</summary>
    CrLf,

    /// <summary>The file mixes LF and CRLF ([warning] F-ENC-3).</summary>
    Mixed,
}

/// <summary>One line of a <see cref="SourceText"/>.</summary>
/// <param name="Start">Offset of the first character.</param>
/// <param name="Length">Length without the line terminator.</param>
/// <param name="TerminatorLength">0 (last line, no newline), 1 (LF or a lone CR) or 2 (CRLF).</param>
internal readonly record struct TextLine(int Start, int Length, int TerminatorLength)
{
    public int End => Start + Length;

    public int EndIncludingTerminator => Start + Length + TerminatorLength;
}

/// <summary>
/// A file's text, decoded from UTF-8, with its byte order mark split off and
/// a table of lines. It never changes the text, so encoding it back gives
/// the original bytes (format.md F-RT-3).
/// </summary>
internal sealed class SourceText
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private SourceText(string text, bool hasBom)
    {
        Text = text;
        HasBom = hasBom;
        var lines = new List<TextLine>();
        int lf = 0, crlf = 0, start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\n')
            {
                lines.Add(new TextLine(start, i - start, 1));
                lf++;
                start = i + 1;
            }
            else if (c == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    lines.Add(new TextLine(start, i - start, 2));
                    crlf++;
                    i++;
                }
                else
                {
                    // A lone CR still ends a line for CommonMark and YAML, so
                    // the line table agrees with both parsers. It's an error
                    // (F-ENC-3), reported by Diagnose().
                    lines.Add(new TextLine(start, i - start, 1));
                    LoneCrLines.Add(lines.Count);
                }

                start = i + 1;
            }
        }

        // The last line has no terminator. It's empty when the text ends
        // with a newline, and then it isn't a real line, but keeping it makes
        // offsets at the end of the text map to a line.
        lines.Add(new TextLine(start, text.Length - start, 0));
        Lines = lines;
        LfCount = lf;
        CrLfCount = crlf;
    }

    public string Text { get; }

    public bool HasBom { get; }

    public IReadOnlyList<TextLine> Lines { get; }

    public int LfCount { get; }

    public int CrLfCount { get; }

    /// <summary>1-based numbers of lines that end with a lone CR.</summary>
    public List<int> LoneCrLines { get; } = [];

    public LineEndingKind LineEndings => (LfCount, CrLfCount) switch
    {
        (0, 0) => LineEndingKind.None,
        (_, 0) => LineEndingKind.Lf,
        (0, _) => LineEndingKind.CrLf,
        _ => LineEndingKind.Mixed,
    };

    /// <summary>
    /// The line ending used for lines a writer adds: the file's own, or the
    /// more common one in a mixed file (LF on a tie), or LF in a file with no
    /// line breaks yet (F-ENC-3).
    /// </summary>
    public string Newline => CrLfCount > LfCount ? "\r\n" : "\n";

    public bool EndsWithNewline => Text.Length > 0 && Text[^1] is '\n' or '\r';

    /// <summary>
    /// Decodes <paramref name="bytes"/>. Returns null and sets
    /// <paramref name="error"/> when they aren't valid UTF-8 (F-ENC-1).
    /// </summary>
    public static SourceText? TryDecode(ReadOnlySpan<byte> bytes, out Diagnostic? error)
    {
        var hasBom = bytes.StartsWith(Bom);
        var payload = hasBom ? bytes[Bom.Length..] : bytes;
        var invalidAt = FindInvalidUtf8(payload);
        if (invalidAt >= 0)
        {
            var line = 1 + payload[..invalidAt].Count((byte)'\n');
            error = new Diagnostic("F-ENC-1", DiagnosticSeverity.Error, line,
                $"The file isn't valid UTF-8 (byte offset {invalidAt + (hasBom ? Bom.Length : 0)}).");
            return null;
        }

        error = null;
        return new SourceText(StrictUtf8.GetString(payload), hasBom);
    }

    /// <summary>Builds the text for a new version of a file, keeping its byte order mark.</summary>
    public static SourceText FromText(string text, bool hasBom) => new(text, hasBom);

    public byte[] Encode()
    {
        var count = StrictUtf8.GetByteCount(Text);
        var bytes = new byte[count + (HasBom ? Bom.Length : 0)];
        if (HasBom)
        {
            Bom.CopyTo(bytes, 0);
        }

        StrictUtf8.GetBytes(Text, 0, Text.Length, bytes, HasBom ? Bom.Length : 0);
        return bytes;
    }

    /// <summary>The 0-based index of the line that holds <paramref name="offset"/>.</summary>
    public int LineIndexOf(int offset)
    {
        int lo = 0, hi = Lines.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Lines[mid].Start <= offset)
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return lo;
    }

    public string LineText(int index) => Text.Substring(Lines[index].Start, Lines[index].Length);

    /// <summary>Errors and warnings about encoding and line endings (F-ENC-3).</summary>
    public IEnumerable<Diagnostic> Diagnose()
    {
        foreach (var line in LoneCrLines)
        {
            yield return new Diagnostic("F-ENC-3", DiagnosticSeverity.Error, line,
                "A carriage return (CR) that isn't followed by a line feed (LF).");
        }

        if (LineEndings == LineEndingKind.Mixed)
        {
            yield return new Diagnostic("F-ENC-3", DiagnosticSeverity.Warning, null,
                $"The file mixes line endings ({LfCount} LF, {CrLfCount} CRLF).");
        }
    }

    private static int FindInvalidUtf8(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var status = Rune.DecodeFromUtf8(bytes[offset..], out _, out var consumed);
            if (status != OperationStatus.Done)
            {
                return offset;
            }

            offset += consumed;
        }

        return -1;
    }
}
