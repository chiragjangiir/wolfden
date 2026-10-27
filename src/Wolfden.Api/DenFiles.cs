using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;
using Wolfden.Core.Structure;

namespace Wolfden.Api;

/// <summary>
/// The den's own images, for the notes that show them (format.md F-LAY-9,
/// design.md §9, #186): <c>GET /den-files/&lt;den-relative path&gt;</c>.
/// It serves an image file in the den and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// A path is served only if all of these hold, and anything else is a 404,
/// so a request can't tell a refused file from a missing one:
/// </para>
/// <list type="bullet">
/// <item>it's a den path as <see cref="DenPaths.Problem"/> has it: relative,
/// <c>/</c> between names, no <c>..</c>, no <c>\</c> or <c>:</c> (so no drive
/// or NTFS stream), and no name that starts with <c>.</c> (F-LAY-2), so
/// nothing under <c>.wolfden/</c>, <c>.git/</c>, or <c>.obsidian/</c>;</item>
/// <item>its extension is an image's (<see cref="ContentTypes"/>), in any case;</item>
/// <item>every name on the way is there on disk as written (in any case on
/// Windows), so a Windows short name can't stand in for a hidden folder;</item>
/// <item>no folder on the way is a folder link (a symbolic link or a
/// junction), which the den never follows (F-LAY-2); a file that's a
/// symbolic link is served only if the file it leads to passes these
/// checks too, so it can't reach outside the den.</item>
/// </list>
/// <para>
/// A request a browser marks as coming from another site is a 404 as well,
/// and a response says <c>Cross-Origin-Resource-Policy: same-origin</c>, so
/// another site's page can't show the den's images or test for them.
/// </para>
/// </remarks>
internal static class DenFiles
{
    /// <summary>The path the den's files are served under; it can't be a UI route (<c>/u/...</c>) or the API's (<c>/api/...</c>).</summary>
    public const string Prefix = "/den-files";

    /// <summary>The image types served, by extension (lowercase, with the dot).</summary>
    public static readonly IReadOnlyDictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".svg"] = "image/svg+xml",
    };

    /// <summary>
    /// The Content-Security-Policy of a den file. An image loaded by a page
    /// runs nothing anyway; this is for an SVG opened at its own address,
    /// which a browser shows as a document: it may use its own inline style
    /// and nothing else, and <c>sandbox</c> gives it a unique origin and no
    /// script, so it can't act as Wolfden's pages.
    /// </summary>
    public const string FilePolicy = "default-src 'none'; style-src 'unsafe-inline'; frame-ancestors 'none'; sandbox";

    /// <summary>Maps <c>GET</c> and <c>HEAD</c> <c>/den-files/{**path}</c>.</summary>
    public static IEndpointRouteBuilder MapDenFiles(this IEndpointRouteBuilder endpoints, WolfdenApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);
        endpoints.MapMethods(Prefix + "/{**path}", [HttpMethods.Get, HttpMethods.Head], (HttpContext context, string? path) => Serve(context, options.DenRoot, path));
        return endpoints;
    }

    private static IResult Serve(HttpContext context, string denRoot, string? path)
    {
        if (WolfdenApi.IsCrossSite(context) || Find(denRoot, path) is not { } file)
        {
            return Results.NotFound();
        }

        var info = new FileInfo(file);
        var headers = context.Response.Headers;
        headers.ContentSecurityPolicy = FilePolicy;
        headers["Cross-Origin-Resource-Policy"] = "same-origin";

        // The writer changes images while the page is open, so the browser
        // asks each time and gets a 304 while the file is the same.
        headers.CacheControl = "no-cache";
        var modified = new DateTimeOffset(info.LastWriteTimeUtc.Ticks - (info.LastWriteTimeUtc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        var tag = new EntityTagHeaderValue($"\"{info.LastWriteTimeUtc.Ticks ^ info.Length:x}\"");
        return Results.File(info.FullName, ContentTypes[info.Extension], lastModified: modified, entityTag: tag);
    }

    /// <summary>
    /// The full path of the image at den-relative <paramref name="path"/>
    /// in the den at <paramref name="denRoot"/>, or null if it isn't one to
    /// serve (see <see cref="DenFiles"/>).
    /// </summary>
    internal static string? Find(string denRoot, string? path) => Find(denRoot, path, followFileLink: true);

    private static string? Find(string denRoot, string? path, bool followFileLink)
    {
        if (path is null || DenPaths.Problem(path) is not null)
        {
            return null;
        }

        var names = path.Split('/');
        if (!ContentTypes.ContainsKey(Path.GetExtension(names[^1])))
        {
            return null;
        }

        try
        {
            var folder = new DirectoryInfo(denRoot);
            for (var i = 0; i < names.Length - 1; i++)
            {
                if (Entry(folder, names[i], file: false) is not DirectoryInfo next || FileSystemDenSource.IsFolderLink(next))
                {
                    return null;
                }

                folder = next;
            }

            if (Entry(folder, names[^1], file: true) is not FileInfo found)
            {
                return null;
            }

            if (!found.Attributes.HasFlag(FileAttributes.ReparsePoint) || found.LinkTarget is null)
            {
                return found.FullName;
            }

            // A symbolic link to a file: served only if what it leads to is
            // an image in the den that passes the same checks.
            if (!followFileLink || found.ResolveLinkTarget(returnFinalTarget: true) is not FileInfo { Exists: true } target)
            {
                return null;
            }

            var relative = Path.GetRelativePath(Path.GetFullPath(denRoot), target.FullName);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return null;
            }

            return Find(denRoot, relative.Replace(Path.DirectorySeparatorChar, '/'), followFileLink: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file or folder in <paramref name="folder"/> whose name on disk
    /// is <paramref name="name"/> (in any case on Windows, whose names
    /// ignore it), or null. Only a name as written matches, not a Windows
    /// short name such as <c>WOLFDE~1</c>.
    /// </summary>
    private static FileSystemInfo? Entry(DirectoryInfo folder, string name, bool file)
    {
        var options = new EnumerationOptions
        {
            MatchType = MatchType.Simple,
            MatchCasing = MatchCasing.PlatformDefault,
            AttributesToSkip = 0,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
        };
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        IEnumerable<FileSystemInfo> entries = file ? folder.EnumerateFiles(name, options) : folder.EnumerateDirectories(name, options);
        return entries.FirstOrDefault(e => string.Equals(e.Name, name, comparison));
    }
}
