using System.Text.Encodings.Web;
using System.Text.Json;

namespace Wolfden.Cli;

/// <summary>
/// The <c>--json</c> output every command shares: one indented JSON
/// document with camelCase names and a <c>version</c> for its shape, LF
/// line endings on every OS. Within a version, fields may be added, but a
/// field is never renamed, removed, or given a new meaning.
/// </summary>
internal static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Keep names and quotes readable ("Zoë", 'x'). The JSON never goes into HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        NewLine = "\n",
    };

    /// <summary>Writes <paramref name="value"/> as the command's whole output, ending with a newline.</summary>
    public static void Write<T>(TextWriter output, T value) =>
        // LF throughout, on every OS, so the bytes don't depend on where it runs.
        output.Write(JsonSerializer.Serialize(value, Options) + "\n");
}
