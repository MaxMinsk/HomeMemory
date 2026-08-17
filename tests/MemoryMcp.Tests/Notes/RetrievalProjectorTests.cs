using MemoryMcp.Core.Query;
using MemoryMcp.Core.Retrieval;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-251: the seam every indexer must go through. Two things are being pinned here — that the projector
/// reproduces today's extraction EXACTLY (phase 1 is a refactor, not a behaviour change), and that it now
/// carries the JSON path each piece of text came from, which is what the vector layer needs for provenance
/// and selective reindex and what the previous all-strings walk threw away.
/// </summary>
public class RetrievalProjectorTests
{
    private static readonly LegacyRetrievalProjector Projector = new();

    private static NoteContent Note(string? title = "A title", string? body = "A body",
        string? tags = null, string? payload = null) => new("fact", title, body, tags, payload);

    [Fact]
    public void Lexical_reports_the_json_path_of_every_piece_of_text()
    {
        var note = Note(
            tags: """["kitchen", "spicy"]""",
            payload: """{ "statement": "the client decides", "ingredients": [{ "name": "dried chili" }] }""");

        var paths = Projector.Lexical(note).ToDictionary(text => text.Path, text => text.Text, StringComparer.Ordinal);

        Assert.Equal("A title", paths["title"]);
        Assert.Equal("A body", paths["body"]);
        Assert.Equal("kitchen", paths["tags[0]"]);
        Assert.Equal("spicy", paths["tags[1]"]);
        Assert.Equal("the client decides", paths["payload.statement"]);
        Assert.Equal("dried chili", paths["payload.ingredients[0].name"]);
    }

    /// <summary>
    /// Order is part of the contract, not an accident: the stems sidecar is the concatenation of these, so a
    /// reordering would silently rewrite every note's indexed text on its next write.
    /// </summary>
    [Fact]
    public void Lexical_keeps_the_legacy_order_of_title_body_tags_payload()
    {
        var note = Note(tags: """["t"]""", payload: """{ "statement": "s" }""");

        var paths = Projector.Lexical(note).Select(text => text.Path).ToList();

        Assert.Equal(["title", "body", "tags[0]", "payload.statement"], paths);
    }

    [Fact]
    public void Lexical_skips_blanks_and_survives_malformed_payload()
    {
        var blank = Projector.Lexical(Note(title: "  ", body: null, payload: "not json at all"));

        Assert.Empty(blank);
    }

    /// <summary>JSON KEYS must never be indexed as text — MEMP-152 removed them because searching "status"
    /// otherwise matched every note that merely HAS a status.</summary>
    [Fact]
    public void Lexical_never_yields_a_json_key_as_text()
    {
        var texts = Projector.Lexical(Note(title: null, body: null, payload: """{ "statement": "value" }"""));

        Assert.Equal("value", Assert.Single(texts).Text);
    }

    [Fact]
    public void Stems_are_built_from_the_projection_and_are_unchanged()
    {
        var note = Note(title: "Dried chili peppers", body: "kept in the pantry",
            payload: """{ "statement": "stored in a jar" }""");

        var viaProjector = SearchStems.From(Projector.Lexical(note));
        var viaShorthand = SearchStems.For(note.Title, note.Body, note.TagsJson, note.PayloadJson);

        Assert.Equal(viaShorthand, viaProjector);
        Assert.NotNull(viaProjector);
        Assert.Contains("chili", viaProjector!, StringComparison.Ordinal);
        Assert.Contains("jar", viaProjector, StringComparison.Ordinal); // payload values still reach the sidecar
    }

    [Fact]
    public void The_title_is_its_own_passage()
    {
        var passages = Projector.Passages(Note(title: "Chili in a kazan", body: new string('x', 400)));

        var title = Assert.Single(passages, passage => passage.Name == "title");
        Assert.Equal("Chili in a kazan", title.Text);
        Assert.Equal(["title"], title.SourcePaths);
    }

    /// <summary>
    /// Each window leads with the title so a passage keeps its subject, and carries the paths it was built
    /// from so a future hit can say which fields produced it.
    /// </summary>
    [Fact]
    public void Body_passages_lead_with_the_title_and_carry_source_paths()
    {
        var note = Note(title: "Kazan", body: new string('a', 500), payload: """{ "statement": "some prose here" }""");

        var windows = Projector.Passages(note).Where(passage => passage.Name == "text").ToList();

        Assert.True(windows.Count > 1, $"a 500-char body should yield several windows (got {windows.Count})");
        Assert.All(windows, passage => Assert.StartsWith("Kazan. ", passage.Text, StringComparison.Ordinal));
        Assert.All(windows, passage => Assert.NotEmpty(passage.SourcePaths));
        Assert.Contains(windows, passage => passage.SourcePaths.Contains("body", StringComparer.Ordinal));
        Assert.Equal([0, 1], windows.Take(2).Select(passage => passage.Ordinal));
    }

    [Fact]
    public void A_note_with_nothing_to_index_yields_no_passages()
    {
        Assert.Empty(Projector.Passages(Note(title: null, body: null)));
    }

    /// <summary>
    /// The descriptor dates a note's stored passages. `IsLegacy` is what will make it visible how many types
    /// still index everything rather than declaring what matters (MEMP-252).
    /// </summary>
    [Fact]
    public void The_descriptor_marks_the_type_as_legacy_with_a_stable_hash()
    {
        var first = Projector.Describe("fact");
        var second = Projector.Describe("recipe");

        Assert.True(first.IsLegacy);
        Assert.Equal(LegacyRetrievalProjector.MappingVersion, first.MappingVersion);
        Assert.Equal(first.MappingHash, second.MappingHash); // same mapping => same hash, whatever the type
        Assert.NotEmpty(first.MappingHash);
    }
}
