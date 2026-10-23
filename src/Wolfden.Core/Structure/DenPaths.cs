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
    /// <item>doesn't end with a space or <c>.</c>, which Windows drops, so <c>mara.md.</c> would be <c>mara.md</c>;</item>
    /// <item>isn't a Windows device name such as <c>CON</c> or <c>nul.md</c>.</item>
    /// </list>
    /// These rules hold on every OS, so a den stays the same files wherever
    /// it's cloned or synced. A name that's being made is also checked for
    /// invisible characters (<see cref="NewNameProblem"/>).
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

    /// <summary>
    /// Why <paramref name="name"/> can't be the name of a new file or
    /// folder, or null if it can. On top of <see cref="Problem"/>, a new
    /// name must be valid text, and invisible characters (Unicode
    /// default-ignorable code points) mustn't hide what it is:
    /// <list type="bullet">
    /// <item>without them it isn't empty and doesn't start with <c>.</c>, so it doesn't look hidden when it isn't, or the other way round;</item>
    /// <item>it has no byte order mark, zero-width space, or text-direction control, which only disguise a name;</item>
    /// <item>it doesn't start with one, or end with one other than a variation selector or an emoji tag.</item>
    /// </list>
    /// Others are kept, as git keeps them: the zero-width joiner of an emoji
    /// sequence (a family emoji, U+200D), the zero-width non-joiner that
    /// Persian and Indic spelling needs (U+200C), and the variation selector of a red heart (U+FE0F).
    /// Only names being made are checked, so a file already named so, made
    /// outside Wolfden, can still be saved, moved, and deleted.
    /// </summary>
    public static string? NewNameProblem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var runes = new List<int>();
        for (var i = 0; i < name.Length; i++)
        {
            if (Rune.DecodeFromUtf16(name.AsSpan(i), out var rune, out var used) != OperationStatus.Done)
            {
                return $"'{Printable(name)}' isn't valid text (a lone surrogate)";
            }

            runes.Add(rune.Value);
            i += used - 1;
        }

        var visible = runes.Where(r => !IsDefaultIgnorable(r)).ToList();
        if (visible.Count == 0 || visible[0] == '.')
        {
            return $"'{Printable(name)}' looks {(visible.Count == 0 ? "empty" : "hidden")} once its invisible characters are left out";
        }

        if (runes.FirstOrDefault(IsDisguise) is var disguise and not 0)
        {
            return $"'{Printable(name)}' has an invisible character (U+{disguise:X4}) that only disguises a name";
        }

        if (IsDefaultIgnorable(runes[0]) || (IsDefaultIgnorable(runes[^1]) && !IsEmojiPart(runes[^1])))
        {
            var edge = IsDefaultIgnorable(runes[0]) ? runes[0] : runes[^1];
            return $"'{Printable(name)}' starts or ends with an invisible character (U+{edge:X4})";
        }

        return null;
    }

    /// <summary>
    /// Throws a <see cref="DenStoreException"/> (<see cref="DenStoreError.InvalidPath"/>)
    /// if <paramref name="name"/>, a name in <paramref name="path"/> that's
    /// being made, can't be (<see cref="NewNameProblem"/>).
    /// </summary>
    public static void CheckNewName(string path, string name)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (NewNameProblem(name) is { } problem)
        {
            throw new DenStoreException(DenStoreError.InvalidPath, path, $"'{Printable(path)}' can't be written: {problem}.");
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

    /// <summary>A byte order mark, a zero-width space, or a text-direction control: invisible characters with no use in a name but to disguise it.</summary>
    private static bool IsDisguise(int c) =>
        c is 0xFEFF or 0x200B or 0x200E or 0x200F or 0x061C
        || c is >= 0x202A and <= 0x202E
        || c is >= 0x2066 and <= 0x2069;

    /// <summary>A variation selector or an emoji tag character, which end emoji such as a red heart or a flag.</summary>
    private static bool IsEmojiPart(int c) =>
        c is >= 0xFE00 and <= 0xFE0F
        || c is >= 0xE0020 and <= 0xE007F
        || c is >= 0xE0100 and <= 0xE01EF;

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
