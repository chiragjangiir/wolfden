using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wolfden.Api.Tests;

/// <summary>Compares an API answer with an MCP read tool's answer to the same question.</summary>
internal static class SameAsMcp
{
    /// <summary>The MCP tool's result in the API's naming (camelCase), with nulls left out as the tools leave them out.</summary>
    private static readonly JsonSerializerOptions Camel = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>
    /// Asserts that every member of <paramref name="mcpResult"/>, in camelCase,
    /// is in <paramref name="api"/> with the same value. The API may add
    /// members (a page's links, scope, and backlinks), never change one.
    /// </summary>
    public static void Assert(object mcpResult, JsonElement api)
    {
        var expected = JsonSerializer.SerializeToElement(mcpResult, mcpResult.GetType(), Camel);
        Compare(expected, api, "$");
    }

    private static void Compare(JsonElement expected, JsonElement actual, string at)
    {
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
                Xunit.Assert.True(actual.ValueKind == JsonValueKind.Object, $"{at}: expected an object, got {actual.ValueKind}");
                foreach (var member in expected.EnumerateObject())
                {
                    Xunit.Assert.True(actual.TryGetProperty(member.Name, out var value), $"{at}.{member.Name} is missing");
                    Compare(member.Value, value, $"{at}.{member.Name}");
                }

                break;
            case JsonValueKind.Array:
                Xunit.Assert.True(actual.ValueKind == JsonValueKind.Array, $"{at}: expected an array, got {actual.ValueKind}");
                Xunit.Assert.True(expected.GetArrayLength() == actual.GetArrayLength(), $"{at}: {expected.GetArrayLength()} items, got {actual.GetArrayLength()}");
                for (var i = 0; i < expected.GetArrayLength(); i++)
                {
                    Compare(expected[i], actual[i], $"{at}[{i}]");
                }

                break;
            default:
                Xunit.Assert.True(
                    JsonElement.DeepEquals(expected, actual),
                    $"{at}: expected {expected.GetRawText()}, got {actual.GetRawText()}");
                break;
        }
    }
}
