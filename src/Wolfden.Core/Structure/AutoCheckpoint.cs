using System.Globalization;
using System.Text.RegularExpressions;
using Wolfden.Core.Documents;
using Wolfden.Core.Yaml;

namespace Wolfden.Core.Structure;

/// <summary>
/// <c>auto_checkpoint</c> in <c>den.yaml</c> (format.md F-DEN-9, #30): how
/// long a running <c>wolfden serve</c> waits with no further changes before
/// it makes a checkpoint by itself. It's off unless set.
/// </summary>
public static partial class AutoCheckpoint
{
    /// <summary>The <c>den.yaml</c> key.</summary>
    public const string Key = "auto_checkpoint";

    /// <summary>The shortest wait allowed: five minutes.</summary>
    public static readonly TimeSpan Shortest = TimeSpan.FromMinutes(5);

    /// <summary>The longest wait allowed: a day.</summary>
    public static readonly TimeSpan Longest = TimeSpan.FromHours(24);

    /// <summary>
    /// Reads <c>auto_checkpoint</c> from a parsed <c>den.yaml</c>: the wait,
    /// or null when it's off. A value that isn't one is a problem, said for
    /// the writer, and the setting stays off.
    /// </summary>
    public static AutoCheckpointSetting Read(YamlFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        switch (file.GetValue(Key))
        {
            case null or YamlScalar { IsNull: true }:
                return AutoCheckpointSetting.Off;
            case YamlScalar scalar when scalar.AsBoolean() == false:
                return AutoCheckpointSetting.Off;
            case YamlScalar scalar when Parse(scalar.Text) is { } every:
                return every < Shortest || every > Longest
                    ? new AutoCheckpointSetting(
                        null,
                        $"auto_checkpoint must be from 5m to 24h, not '{scalar.Text}', so automatic checkpoints stay off.",
                        scalar.Line)
                    : new AutoCheckpointSetting(every, null, null);
            case var node:
                var shown = node switch
                {
                    YamlScalar s => s.Text,
                    YamlSequence => "a list",
                    _ => "a mapping",
                };
                return new AutoCheckpointSetting(
                    null,
                    $"auto_checkpoint must be a time such as 30m, 2h, or 1h30m, not '{shown}', so automatic checkpoints stay off.",
                    node.Line);
        }
    }

    /// <summary>
    /// Reads <c>auto_checkpoint</c> straight from <c>den.yaml</c>'s bytes,
    /// without loading the den, as a running <c>wolfden serve</c> does
    /// before each automatic checkpoint. A file that doesn't parse, or whose
    /// <c>format</c> this reader doesn't support, leaves it off with no
    /// problem of its own: <c>wolfden check</c> reports that.
    /// </summary>
    public static AutoCheckpointSetting Read(ReadOnlySpan<byte> denYaml)
    {
        var file = YamlFile.Parse(denYaml);
        return file.HasErrors || DenLoader.ReadFormat(file, out _) is null ? AutoCheckpointSetting.Off : Read(file);
    }

    /// <summary>
    /// A wait written as hours, minutes, or both, in that order, each a
    /// whole number with its unit straight after (<c>30m</c>, <c>2h</c>,
    /// <c>1h30m</c>), or null if <paramref name="text"/> isn't one.
    /// </summary>
    public static TimeSpan? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var match = DurationPattern().Match(text);
        if (!match.Success || (!match.Groups["h"].Success && !match.Groups["m"].Success))
        {
            return null;
        }

        // At most nine digits each, so the sum can't overflow.
        static long Number(Group g) => g.Success ? long.Parse(g.Value, NumberStyles.None, CultureInfo.InvariantCulture) : 0;

        return TimeSpan.FromMinutes((Number(match.Groups["h"]) * 60) + Number(match.Groups["m"]));
    }

    /// <summary>Says a wait as the setting would: <c>30m</c>, <c>2h</c>, <c>1h30m</c>.</summary>
    public static string Format(TimeSpan every)
    {
        var minutes = (long)every.TotalMinutes;
        var (h, m) = (minutes / 60, minutes % 60);
        return h == 0 ? $"{m}m" : m == 0 ? $"{h}h" : $"{h}h{m}m";
    }

    [GeneratedRegex(@"^(?:(?<h>[0-9]{1,9})h)?(?:(?<m>[0-9]{1,9})m)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}

/// <summary>What <c>auto_checkpoint</c> says.</summary>
/// <param name="Every">How long to wait with no further changes, or null when automatic checkpoints are off.</param>
/// <param name="Problem">Why the value was ignored, for the writer (an F-DEN-9 warning), or null.</param>
/// <param name="Line">The value's line in <c>den.yaml</c>, for the warning, or null.</param>
public sealed record AutoCheckpointSetting(TimeSpan? Every, string? Problem, int? Line)
{
    /// <summary>Off, with nothing wrong.</summary>
    public static AutoCheckpointSetting Off { get; } = new(null, null, null);
}
