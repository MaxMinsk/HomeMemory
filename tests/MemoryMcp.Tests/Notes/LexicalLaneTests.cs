using MemoryMcp.Core.Retrieval;
using MemoryMcp.Core.Schemas;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-262: which full-text lane a field lands in is the TYPE's decision, declared by its schema.
/// <para>The lanes ship weighted exactly as the columns they replaced were, so introducing them cannot move any
/// ranking. What they buy is the ability to re-weight later as a query-time change — no reindex, and revertible
/// — which is the separation Elasticsearch spent years undoing its index-time boost to reach.</para>
/// </summary>
public class LexicalLaneTests
{
    private static readonly SchemaRegistry Schemas = SchemaRegistry.FromEmbeddedResources();

    /// <summary>
    /// A ticket is ABOUT its acceptance criteria. Its status and sprint are how it is filtered, and indexing
    /// them as ordinary text is why searching "ready" used to return every ticket that has a status.
    /// </summary>
    [Fact]
    public void An_annotated_type_sorts_its_fields_by_what_they_are_for()
    {
        var payload = """
            { "key": "MEMP-262", "status": "ready", "sprint": "S67", "estimate": "M",
              "acceptance": "Replace the fixed columns with universal lanes." }
            """;

        var lanes = new SchemaRetrievalProjector(Schemas)
            .Lanes(new NoteContent("backlog_item", "FTS lanes", "the body", null, payload));

        var everything = lanes.Primary + " " + lanes.Secondary;

        // PRIMARY: what the ticket is about, and the key you would search for it by.
        Assert.Contains("the body", lanes.Primary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("universal lanes", lanes.Primary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("MEMP-262", lanes.Primary ?? string.Empty, StringComparison.Ordinal);

        // SECONDARY: findable, but never what the note is about.
        Assert.Contains("S67", lanes.Secondary ?? string.Empty, StringComparison.Ordinal);

        // NEITHER: declared noise. This is why searching "ready" used to return every ticket that HAS a status.
        Assert.DoesNotContain("ready", everything, StringComparison.Ordinal);
    }

    /// <summary>
    /// A type that declares nothing keeps every string it had. The conservative reading of "we were not told" is
    /// what makes this rollout safe one type at a time.
    /// </summary>
    [Fact]
    public void An_unannotated_type_keeps_every_string_it_had()
    {
        var payload = """{ "statement": "borscht is beetroot soup", "anything": "else" }""";

        var lanes = new LegacyRetrievalProjector()
            .Lanes(new NoteContent("journal", "Borscht", "beet cabbage broth", null, payload));

        Assert.Equal("beet cabbage broth", lanes.Primary);
        Assert.Contains("borscht is beetroot soup", lanes.Secondary ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("else", lanes.Secondary ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>The lane weights must stay as the columns they replaced, or the migration moves ranking by itself.</summary>
    [Fact]
    public void The_lane_weights_are_the_ones_the_replaced_columns_carried()
    {
        var expression = typeof(SchemaRegistry).Assembly
            .GetType("MemoryMcp.Core.Notes.Bm25Weights")!
            .GetField("Expression")!.GetValue(null) as string;

        // identity, title, primary_text, secondary_text, tags, stems — the title is the only weighted lane,
        // exactly as before (MEMP-237 set it to 5 and nothing here changes that).
        Assert.Equal("bm25(notes_fts, 1.0, 5.0, 1.0, 1.0, 1.0, 1.0)", expression);
    }
}
