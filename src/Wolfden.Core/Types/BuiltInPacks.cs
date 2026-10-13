using System.Collections.Concurrent;
using System.Text;
using Wolfden.Core.Documents;
using Wolfden.Core.Structure;

namespace Wolfden.Core.Types;

/// <summary>
/// The built-in packs (types.md F-PACK-4), embedded in this assembly at
/// build time from the repository's <c>packs/</c> folder, which stays the
/// single source of truth. Paths are relative to <c>packs/</c>, such as
/// <c>core/types/thread.yaml</c>.
/// </summary>
public sealed class BuiltInPacks : IDenSource
{
    /// <summary>The prefix of the embedded resources' logical names (see Wolfden.Core.csproj).</summary>
    internal const string ResourcePrefix = "Wolfden/packs/";

    private static readonly Lazy<BuiltInPacks> Shared = new(Load);

    private readonly Dictionary<string, byte[]> _files;

    private BuiltInPacks(Dictionary<string, byte[]> files)
    {
        _files = files;
        Keys = files.Keys.Select(p => p[..p.IndexOf('/', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Where(k => _files.ContainsKey($"{k}/pack.yaml"))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The built-in packs' files, as embedded.</summary>
    public static BuiltInPacks Instance => Shared.Value;

    /// <summary>The keys of the built-in packs, in ASCII order. In v0, only <c>core</c>.</summary>
    public IReadOnlyList<string> Keys { get; }

    /// <summary>Parsed pack files. They never change while the process runs, so each is parsed once.</summary>
    internal ConcurrentDictionary<string, DenFile> Parsed { get; } = new(StringComparer.Ordinal);

    /// <summary>Every embedded file, relative to <c>packs/</c>, in ASCII order.</summary>
    public IEnumerable<string> Files => _files.Keys.Order(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool FileExists(string path) => _files.ContainsKey(path);

    /// <inheritdoc />
    public byte[] ReadFile(string path) =>
        _files.TryGetValue(path, out var bytes) ? (byte[])bytes.Clone() : throw new FileNotFoundException($"There's no built-in pack file '{path}'.", path);

    /// <inheritdoc />
    public IEnumerable<string> GetDirectories(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('/', prefix.Length) > 0)
            .Select(f => f[prefix.Length..f.IndexOf('/', prefix.Length)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public IEnumerable<string> GetFiles(string path)
    {
        var prefix = path.Length == 0 ? "" : path + "/";
        return _files.Keys
            .Where(f => f.StartsWith(prefix, StringComparison.Ordinal) && f.IndexOf('/', prefix.Length) < 0)
            .Select(f => f[prefix.Length..])
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Built-in packs made of the given files, for tests of packs that don't ship yet.</summary>
    internal static BuiltInPacks FromFiles(IReadOnlyDictionary<string, string> files) =>
        new(files.ToDictionary(f => f.Key, f => Encoding.UTF8.GetBytes(f.Value), StringComparer.Ordinal));

    private static BuiltInPacks Load()
    {
        var assembly = typeof(BuiltInPacks).Assembly;
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal))
            {
                continue;
            }

            // %(RecursiveDir) uses the build machine's separator.
            var path = name[ResourcePrefix.Length..].Replace('\\', '/');
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException($"The embedded resource '{name}' can't be read.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            files[path] = copy.ToArray();
        }

        return new BuiltInPacks(files);
    }
}
