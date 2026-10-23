using System.Buffers;
using System.Text;

namespace Wolfden.Core.Structure;

/// <summary>
/// The rules for a path a den store writes (<see cref="IDenStore"/>). Reads
/// take any path and find nothing at a bad one; a write must name a file
/// in the den, the same one on every OS, so a den written on Linux loads on
/// Windows too.
/// </summary>
public static class DenPaths
{
    // Names Windows keeps for devices, with or without an extension.
    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
         "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

    // Characters Windows doesn't allow in a name. '/' separates names, and '\' is refused separately.
    private const string WindowsForbidden = "<>:\"|?*";

    /// <summary>
    /// Why <paramref name="path"/> can't be written, or null if it can. A
    /// writable path is den-relative, uses <c>/</c> between names, and has
    /// at least one name. Each name:
    /// <list type="bullet">
    /// <item>isn't empty, <c>.</c>, or <c>..</c>, so the path can't leave the den;</item>
    /// <item>doesn't start with <c>.</c>: hidden files aren't part of the den (F-LAY-2), and <c>.git/</c> and <c>.wolfden/</c> are never written through the store;</item>
    /// <item>has no <c>\</c>, which is a separator on Windows but a letter on Linux, and none of <c>&lt; &gt; : " | ? *</c> or control characters, which Windows doesn't allow (a <c>:</c> would also name a drive or an NTFS stream);</item>
    /// <item>is valid text with no invisible characters (Unicode default-ignorable code points such as a zero-width space or a byte order mark), so names that look alike are never different files;</item>
    /// <item>doesn't end with a space or <c>.</c>, which Windows drops, so <c>mara.md.</c> would be <c>mara.md</c>;</item>
    /// <item>isn't a Windows device name such as <c>CON</c> or <c>nul.md</c>.</item>
    /// </list>
    /// These rules hold on every OS, so a den stays the same files wherever
    /// it's cloned or synced.
    /// </summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "the path is empty; name a file or folder inside the den";
        }

        if (path.Contains('\\', StringComparison.Ordinal))
        {
            return "use '/' between names, not '\\'";
        }

        if (path.StartsWith('/'))
        {
            return "the path must be relative to the den, not start with '/'";
        }

        foreach (var name in path.Split('/'))
        {
            if (NameProblem(name) is { } problem)
            {
                return problem;
            }
        }

        return null;
    }

    /// <summary>Throws a <see cref="DenStoreException"/> (<see cref="DenStoreError.InvalidPath"/>) if <paramref name="path"/> can't be written (<see cref="Problem"/>).</summary>
    public static void CheckWritable(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Problem(path) is { } problem)
        {
            throw new DenStoreException(DenStoreError.InvalidPath, path, $"'{path}' can't be written: {problem}.");
        }
    }

    /// <summary>The folder that holds <paramref name="path"/>, or the empty string for a name at the den root.</summary>
    public static string Parent(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var slash = path.LastIndexOf('/');
        return slash < 0 ? "" : path[..slash];
    }

    /// <summary>The last name in <paramref name="path"/>.</summary>
    public static string Name(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path[(path.LastIndexOf('/') + 1)..];
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="folder"/> or inside it, comparing names without case, as Windows and macOS do.</summary>
    public static bool IsAtOrInside(string path, string folder)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folder);
        return string.Equals(path, folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NameProblem(string name)
    {
        if (name.Length == 0)
        {
            return "a name in the path is empty ('//', or '/' at the end)";
        }

        if (name is "." or "..")
        {
            return $"'{name}' isn't allowed in a den path, so nothing is written outside the den";
        }

        if (name.StartsWith('.'))
        {
            return $"'{name}' is hidden, and hidden files and folders aren't part of the den (F-LAY-2)";
        }

        foreach (var c in name)
        {
            if (c < ' ' || c == '\u007f')
            {
                return $"'{Printable(name)}' has a control character";
            }

            if (WindowsForbidden.Contains(c, StringComparison.Ordinal))
            {
                return $"'{name}' has '{c}', which Windows doesn't allow in a name";
            }
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done)
            {
                return $"'{Printable(name)}' isn't valid text (a lone surrogate)";
            }

            if (IsDefaultIgnorable(rune.Value))
            {
                return $"'{Printable(name)}' has an invisible character (U+{rune.Value:X4}), so two names that look the same would be different files";
            }

            i += used - 1;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return $"'{name}' ends with a space or '.', which Windows drops from a name";
        }

        var stem = name.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? name[..dot] : name;
        if (DeviceNames.Contains(stem.TrimEnd(' ')))
        {
            return $"'{name}' is a device name on Windows";
        }

        return null;
    }

    private static string Printable(string name) =>
        string.Concat(name.Select(c => c < ' ' || c == '\u007f' || char.IsSurrogate(c) || IsDefaultIgnorable(c) ? $"\\u{(int)c:x4}" : c.ToString()));

    /// <summary>
    /// True for a Unicode default-ignorable code point (DerivedCoreProperties,
    /// Unicode 15): characters that show as nothing, such as a zero-width
    /// space, a soft hyphen, a byte order mark, or a direction mark.
    /// </summary>
    private static bool IsDefaultIgnorable(int c) =>
        c is 0x00AD or 0x034F or 0x061C or 0x3164 or 0xFEFF or 0xFFA0
        || c is >= 0x115F and <= 0x1160
        || c is >= 0x17B4 and <= 0x17B5
        || c is >= 0x180B and <= 0x180F
        || c is >= 0x200B and <= 0x200F
        || c is >= 0x202A and <= 0x202E
        || c is >= 0x2060 and <= 0x206F
        || c is >= 0xFE00 and <= 0xFE0F
        || c is >= 0xFFF0 and <= 0xFFF8
        || c is >= 0x1BCA0 and <= 0x1BCA3
        || c is >= 0x1D173 and <= 0x1D17A
        || c is >= 0xE0000 and <= 0xE0FFF;
}
