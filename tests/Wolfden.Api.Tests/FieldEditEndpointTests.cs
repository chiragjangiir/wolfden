using System.Net;
using System.Text;
using System.Text.Json;

namespace Wolfden.Api.Tests;

/// <summary>
/// Editing a note's fields in the web UI (#28, part b): the field types and
/// raw values the source gives a field editor, and <c>/api/edit/field</c>,
/// which makes one field's changes through the MCP <c>update_fields</c>
/// code. Over a copy of the Dracula sample with an invented den-local pack
/// whose type has a field of every field type, and an invented note of it.
/// </summary>
public class FieldEditEndpointTests
{
    private const string BoxPath = "universes/dracula/books/dracula/entities/specimens/the-box.md";

    private const string Specimen =
        "name: Specimen\n" +
        "plural: specimens\n" +
        "fields:\n" +
        "  label: string\n" +
        "  notes: text\n" +
        "  weight: number\n" +
        "  preserved: bool\n" +
        "  found: date\n" +
        "  state: { type: enum, values: [fresh, dried, lost] }\n" +
        "  finder: { type: ref, to: character }\n" +
        "  seen: { type: position, list: true }\n" +
        "  tags: { type: string, list: true }\n" +
        "  keeper: { type: ref, to: character, temporal: true }\n" +
        "sections:\n" +
        "  - Overview\n";

    private const string Box =
        "---\n" +
        "id: 01M4TF5F0EKGGWD5FNVBAEY8Z5\n" +
        "type: specimen\n" +
        "name: The Box\n" +
        "status: canon\n" +
        "fields:\n" +
        "  label: Earth box   # the label on the lid\n" +
        "  weight: 12.5\n" +
        "  preserved: false\n" +
        "  found: \"1893-08-08\"\n" +
        "  state: Dried\n" +
        "  tags:\n" +
        "    - soil\n" +
        "    - \"box\"\n" +
        "    - chapel\n" +
        "  seen: [\"dracula:1\", \"dracula:7\"]\n" +
        "  keeper:\n" +
        "    - { value: \"[[count-dracula]]\", from: \"dracula:1\" }\n" +
        "    - { value: \"[[jonathan-harker]]\", from: \"dracula:7\", pin: true }\n" +
        "  x_extra: kept\n" +
        "---\n" +
        "\n" +
        "# The Box\n" +
        "\n" +
        "## Overview\n" +
        "Fifty boxes of earth.\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static TempDen NewDen()
    {
        var den = TempDen.Dracula();
        den.Write("den.yaml", den.Read("den.yaml") + "packs: [museum]\n");
        den.Write("packs/museum/pack.yaml", "name: Museum\ndescription: An invented pack for the field editing tests.\n");
        den.Write("packs/museum/types/specimen.yaml", Specimen);
        den.Write(BoxPath, Box);
        return den;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Json)> EditAsync(Server server, string hash, string field, object changes)
    {
        var body = JsonSerializer.Serialize(new { entity = BoxPath, expectedHash = hash, field, changes });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await server.Client.PostAsync("/api/edit/field", content, Token);
        var text = await response.Content.ReadAsStringAsync(Token);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static async Task<JsonElement> SourceAsync(Server server) =>
        await server.GetJsonAsync($"/api/entity/source?entity={Uri.EscapeDataString(BoxPath)}", HttpStatusCode.OK);

    private static async Task<string> HashAsync(Server server) => (await SourceAsync(server)).GetProperty("hash").GetString()!;

    /// <summary>Makes one field's changes and checks they were saved; returns the result.</summary>
    private static async Task<JsonElement> SavedAsync(Server server, string field, object changes)
    {
        var (status, json) = await EditAsync(server, await HashAsync(server), field, changes);
        Assert.True(status == HttpStatusCode.OK, json.ToString());
        return json;
    }

    private static string[] Rules(JsonElement result) => result.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("rule").GetString()!).ToArray();

    private static string[] Strings(JsonElement json, string name) => json.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    [Trait("Scenario", "SC-WEB-60")]
    public async Task TheSourceGivesTheTypesFieldsAndEachValueAsWritten()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var source = await SourceAsync(server);

        var fields = source.GetProperty("fields").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        Assert.Equal(["label", "weight", "preserved", "found", "state", "tags", "seen", "keeper", "x_extra"], fields.Keys);
        Assert.Equal(7, fields["label"].GetProperty("line").GetInt32());
        Assert.Equal("Earth box", fields["label"].GetProperty("value").GetString());
        Assert.Equal(12.5, fields["weight"].GetProperty("value").GetDouble());
        Assert.Equal(JsonValueKind.False, fields["preserved"].GetProperty("value").ValueKind);
        Assert.Equal("1893-08-08", fields["found"].GetProperty("value").GetString());
        Assert.Equal(["soil", "box", "chapel"], fields["tags"].GetProperty("value").EnumerateArray().Select(e => e.GetString()));
        var keeper = fields["keeper"].GetProperty("value");
        Assert.Equal("[[jonathan-harker]]", keeper[1].GetProperty("value").GetString());
        Assert.True(keeper[1].GetProperty("pin").GetBoolean());

        var types = source.GetProperty("fieldTypes").EnumerateArray().ToDictionary(f => f.GetProperty("name").GetString()!, f => f);
        Assert.Equal(["label", "notes", "weight", "preserved", "found", "state", "finder", "seen", "tags", "keeper", "placeholder"], types.Keys);
        Assert.Equal(["fresh", "dried", "lost"], Strings(types["state"], "values"));
        Assert.Equal(["character"], Strings(types["finder"], "to"));
        Assert.True(types["keeper"].GetProperty("temporal").GetBoolean());
        Assert.True(types["seen"].GetProperty("list").GetBoolean());
        Assert.Equal("position", types["seen"].GetProperty("type").GetString());
        Assert.Equal("bool", types["placeholder"].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null, types["label"].GetProperty("to").ValueKind);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-60")]
    public async Task EachFieldTypeIsSetAndOnlyItsValueChanges()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        await SavedAsync(server, "label", new[] { new { op = "set", value = "Box of earth" } });
        await SavedAsync(server, "weight", new[] { new { op = "set", value = 3.25 } });
        await SavedAsync(server, "preserved", new[] { new { op = "set", value = true } });
        await SavedAsync(server, "found", new[] { new { op = "set", value = (object)new { text = "early August", sort = new[] { 1893, 8 } } } });
        await SavedAsync(server, "state", new[] { new { op = "set", value = "lost" } });
        var finder = await SavedAsync(server, "finder", new[] { new { op = "set", value = "[[mina-harker]]" } });
        await SavedAsync(server, "notes", new[] { new { op = "set", value = "Taken from Carfax.\nSome went to Piccadilly." } });

        var text = den.Read(BoxPath);
        Assert.Equal(
            Box.Replace("  label: Earth box   # the label on the lid\n", "  label: Box of earth   # the label on the lid\n", StringComparison.Ordinal)
                .Replace("  weight: 12.5\n", "  weight: 3.25\n", StringComparison.Ordinal)
                .Replace("  preserved: false\n", "  preserved: true\n", StringComparison.Ordinal)
                .Replace("  found: \"1893-08-08\"\n", "  found: { text: early August, sort: [1893, 8] }\n", StringComparison.Ordinal)
                .Replace("  state: Dried\n", "  state: lost\n", StringComparison.Ordinal)
                .Replace("  x_extra: kept\n", "  x_extra: kept\n  finder: \"[[mina-harker]]\"\n  notes: \"Taken from Carfax.\\nSome went to Piccadilly.\"\n", StringComparison.Ordinal),
            text);
        Assert.Equal(["F-FLD-2"], Rules(finder)); // x_extra only
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-61")]
    public async Task ListItemsAreAddedRemovedAndReorderedInTheListsOwnStyle()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // Add at the end, then move 'chapel' (now item 2) to the front: a removal and an add, saved together.
        await SavedAsync(server, "tags", new[] { new { op = "add", value = "Carfax" } });
        Assert.Contains("  tags:\n    - soil\n    - \"box\"\n    - chapel\n    - Carfax\n  seen:", den.Read(BoxPath), StringComparison.Ordinal);
        await SavedAsync(server, "tags", new object[] { new { op = "remove", path = new object[] { 2 } }, new { op = "add", value = "chapel", index = 0 } });
        Assert.Contains("  tags:\n    - chapel\n    - soil\n    - \"box\"\n    - Carfax\n  seen:", den.Read(BoxPath), StringComparison.Ordinal);

        // A flow list keeps its brackets; an item is set where it is.
        await SavedAsync(server, "seen", new[] { new { op = "set", path = new object[] { 1 }, value = "dracula:8" } });
        Assert.Contains("  seen: [\"dracula:1\", \"dracula:8\"]\n", den.Read(BoxPath), StringComparison.Ordinal);

        // The last item of a flow list leaves [], of a block list the bare key (format.md F-RT-6): unset either way.
        await SavedAsync(server, "seen", new object[] { new { op = "remove", path = new object[] { 1 } }, new { op = "remove", path = new object[] { 0 } } });
        Assert.Contains("  seen: []\n", den.Read(BoxPath), StringComparison.Ordinal);
        var removed = await SavedAsync(server, "tags", Enumerable.Range(0, 4).Select(_ => new { op = "remove", path = new object[] { 0 } }).ToArray());
        Assert.Contains("  tags:\n  seen: []\n", den.Read(BoxPath), StringComparison.Ordinal);
        Assert.Equal(["F-FLD-2"], Rules(removed)); // x_extra only

        // Nothing there to remove is refused, and nothing is written.
        var before = den.Read(BoxPath);
        var (status, json) = await EditAsync(server, await HashAsync(server), "tags", new[] { new { op = "remove", path = new object[] { 0 } } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad_argument", json.GetProperty("code").GetString());
        Assert.Equal(before, den.Read(BoxPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-62")]
    public async Task DatedEntriesAreAddedEndedAndRemovedAndPinnedCanonIsReported()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        // End the first entry, and add one after the second.
        await SavedAsync(server, "keeper", new[] { new { op = "set", path = new object[] { 0, "until" }, value = "dracula:5" } });
        await SavedAsync(server, "keeper", new[] { new { op = "add", value = (object)new { value = "[[abraham-van-helsing]]", from = "dracula:20", status = "candidate" } } });
        Assert.Contains(
            "  keeper:\n" +
            "    - { value: \"[[count-dracula]]\", from: \"dracula:1\", until: \"dracula:5\" }\n" +
            "    - { value: \"[[jonathan-harker]]\", from: \"dracula:7\", pin: true }\n" +
            "    - { value: \"[[abraham-van-helsing]]\", from: \"dracula:20\", status: candidate }\n" +
            "  x_extra: kept\n",
            den.Read(BoxPath),
            StringComparison.Ordinal);

        // Unpinning, and removing, the pinned entry are saved and reported.
        var unpinned = await SavedAsync(server, "keeper", new[] { new { op = "remove", path = new object[] { 1, "pin" } } });
        Assert.NotEmpty(unpinned.GetProperty("pinnedCanon").EnumerateArray());
        Assert.Contains("    - { value: \"[[jonathan-harker]]\", from: \"dracula:7\" }\n", den.Read(BoxPath), StringComparison.Ordinal);
        var pinned = await SavedAsync(server, "keeper", new[] { new { op = "set", path = new object[] { 1, "pin" }, value = true } });
        Assert.NotEmpty(pinned.GetProperty("pinnedCanon").EnumerateArray());
        var removed = await SavedAsync(server, "keeper", new[] { new { op = "remove", path = new object[] { 1 } } });
        Assert.Contains(Strings(removed, "pinnedCanon"), p => p.Contains("fields.keeper", StringComparison.Ordinal));
        Assert.DoesNotContain("jonathan-harker", den.Read(BoxPath), StringComparison.Ordinal);

        // A plain value becomes a dated list, as a whole.
        await SavedAsync(server, "label", new[]
        {
            new { op = "set", value = (object)new object[] { new { value = "Earth box" }, new { value = "Empty box", from = "dracula:27" } } },
        });
        Assert.Contains("  label:   # the label on the lid\n    - { value: Earth box }\n    - { value: Empty box, from: \"dracula:27\" }\n  weight:", den.Read(BoxPath), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-63")]
    public async Task ClearingAFieldRemovesItsLinesAndAddingOneGoesAfterTheOthers()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        await SavedAsync(server, "label", new[] { new { op = "remove" } });
        await SavedAsync(server, "keeper", new[] { new { op = "remove" } });
        await SavedAsync(server, "placeholder", new[] { new { op = "set", value = true } });

        Assert.Equal(
            Box.Replace("  label: Earth box   # the label on the lid\n", "", StringComparison.Ordinal)
                .Replace(
                    "  keeper:\n    - { value: \"[[count-dracula]]\", from: \"dracula:1\" }\n    - { value: \"[[jonathan-harker]]\", from: \"dracula:7\", pin: true }\n",
                    "",
                    StringComparison.Ordinal)
                .Replace("  x_extra: kept\n", "  x_extra: kept\n  placeholder: true\n", StringComparison.Ordinal),
            den.Read(BoxPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-64")]
    public async Task SettingAFieldBackToWhatItWasGivesBackTheSameBytes()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var bytes = File.ReadAllBytes(Path.Combine(den.Root, BoxPath));

        // The same value writes nothing.
        var same = await SavedAsync(server, "state", new[] { new { op = "set", value = "Dried" } });
        Assert.False(same.GetProperty("changed").GetBoolean());

        await SavedAsync(server, "label", new[] { new { op = "set", value = "Something else" } });
        await SavedAsync(server, "label", new[] { new { op = "set", value = "Earth box" } });
        await SavedAsync(server, "tags", new[] { new { op = "set", path = new object[] { 1 }, value = "crate" } });
        await SavedAsync(server, "tags", new[] { new { op = "set", path = new object[] { 1 }, value = "box" } });
        await SavedAsync(server, "keeper", new[] { new { op = "set", path = new object[] { 0, "from" }, value = "dracula:2" } });
        await SavedAsync(server, "keeper", new[] { new { op = "set", path = new object[] { 0, "from" }, value = "dracula:1" } });

        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(den.Root, BoxPath)));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-65")]
    public async Task AStaleFrozenOrInvalidFieldEditIsRefusedAndNothingIsWritten()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));
        var hash = await HashAsync(server);
        await SavedAsync(server, "label", new[] { new { op = "set", value = "Box" } });
        var before = den.Read(BoxPath);

        // Stale: the error holds the file as it is now.
        var (status, json) = await EditAsync(server, hash, "label", new[] { new { op = "set", value = "Crate" } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("stale_file", json.GetProperty("code").GetString());
        Assert.Contains("  label: Box   #", json.GetProperty("current").GetProperty("text").GetString(), StringComparison.Ordinal);

        // Invalid: a 'from' that isn't a position is an error (F-POS-3), so it's refused with the errors.
        hash = await HashAsync(server);
        (status, json) = await EditAsync(server, hash, "keeper", new[] { new { op = "set", path = new object[] { 0, "from" }, value = "chapter seven" } });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("invalid_edit", json.GetProperty("code").GetString());

        // Only one field's frontmatter can be reached, by a field name and a path under it.
        (status, json) = await EditAsync(server, hash, "Status", new[] { new { op = "set", value = "idea" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("F-FLD-1", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        (status, _) = await EditAsync(server, hash, "label", new[] { new { op = "set", path = "..status", value = "idea" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await EditAsync(server, hash, "label", new[] { new { op = "set", path = new object[] { "a.b" }, value = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await EditAsync(server, hash, "label", new[] { new { op = "rename", value = "x" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, _) = await EditAsync(server, hash, "label", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.BadRequest, status);
        (status, json) = await EditAsync(server, hash, "label", new[] { new { op = "set", value = "x", field = "status" } });
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'field'", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Equal(before, den.Read(BoxPath));

        // Frozen: a note in a final book can't be edited.
        var book = "universes/dracula/books/dracula/book.md";
        den.Write(book, den.Read(book).Replace("status: draft", "status: final", StringComparison.Ordinal));
        (status, json) = await EditAsync(server, hash, "label", new[] { new { op = "set", value = "Crate" } });
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("frozen", json.GetProperty("code").GetString());
        Assert.Equal(before, den.Read(BoxPath));
    }

    [Fact]
    [Trait("Scenario", "SC-WEB-60")]
    public async Task AValueThatDoesntFitItsTypeIsSavedWithTheWarning()
    {
        using var den = NewDen();
        await using var server = await Server.StartAsync(new WolfdenApiOptions(den.Root));

        var json = await SavedAsync(server, "state", new[] { new { op = "set", value = "misplaced" } });

        Assert.Contains(json.GetProperty("warnings").EnumerateArray(), w => w.GetProperty("rule").GetString() == "F-FT-8");
        Assert.Contains("  state: misplaced\n", den.Read(BoxPath), StringComparison.Ordinal);
    }
}
