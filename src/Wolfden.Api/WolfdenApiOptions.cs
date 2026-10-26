using Wolfden.Core.History;
using Wolfden.Index;

namespace Wolfden.Api;

/// <summary>What <see cref="WolfdenApi.Build"/> serves, and where.</summary>
public sealed class WolfdenApiOptions
{
    /// <summary>Options for the den at <paramref name="denRoot"/>.</summary>
    public WolfdenApiOptions(string denRoot)
    {
        ArgumentNullException.ThrowIfNull(denRoot);
        DenRoot = Path.GetFullPath(denRoot);
    }

    /// <summary>The den's folder, as a full path.</summary>
    public string DenRoot { get; }

    /// <summary>
    /// The port on 127.0.0.1 to listen on (<see cref="WolfdenApi.DefaultPort"/>
    /// by default). 0 picks a free one; the app's <c>Urls</c> say which once
    /// it has started.
    /// </summary>
    public int Port { get; init; } = WolfdenApi.DefaultPort;

    /// <summary>
    /// The universe the server is pinned to (<c>wolfden serve --universe</c>),
    /// or null to serve every universe of the den.
    /// </summary>
    public string? PinnedUniverse { get; init; }

    /// <summary>
    /// The folder of the built web UI (<c>web/dist</c>, copied next to
    /// <c>wolfden</c> as <c>wwwroot</c>), or null. Without an
    /// <c>index.html</c> there, <c>/</c> serves a page that says how to
    /// build the UI, and the API still works.
    /// </summary>
    public string? WebRoot { get; init; }

    /// <summary>Wolfden's version, which <c>/api/health</c> reports.</summary>
    public string Version { get; init; } = "unknown";

    /// <summary>Where warnings and errors are logged (standard error in <c>wolfden serve</c>), or null for nowhere.</summary>
    public TextWriter? Log { get; init; }

    /// <summary>The longest a request waits for a running watcher to catch up before it rebuilds the index itself.</summary>
    public TimeSpan WatcherWait { get; init; } = IndexBuilder.WatcherWait;

    /// <summary>
    /// Finds the den's history provider, for the history endpoints. It's
    /// asked on each request, so a den that becomes a repository while the
    /// server runs gets history without a restart. By default the den has
    /// none (<see cref="NoHistory"/>); <c>wolfden serve</c> uses git (#19).
    /// </summary>
    public Func<IHistoryProvider> History { get; init; } = () => NoHistory.Instance;
}
