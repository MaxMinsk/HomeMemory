using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class SuggestionReviewerTests
{
    [Fact]
    public void Apply_retag_replaces_target_tags_and_marks_applied()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        var target = repo.Upsert("memory-mcp", "backlog_item", "Target", null, """{ "key": "MEMP-900", "status": "ready" }""", null, "MEMP-900", "me").Id;
        var suggestion = NewSuggestion(repo, "memory-mcp",
            $$"""{ "target_id": "{{target}}", "kind": "retag", "proposed_patch": { "tags": ["maintenance", "tech-debt"] }, "rationale": "no tags", "status": "open" }""");

        var result = reviewer.Apply(suggestion, RequestScope.Unrestricted);

        Assert.Equal(SuggestionOutcome.Applied, result.Outcome);
        var tags = repo.Get(target)!.TagsJson!;
        Assert.Contains("maintenance", tags, StringComparison.Ordinal);
        Assert.Contains("tech-debt", tags, StringComparison.Ordinal);
        Assert.Contains("\"status\":\"applied\"", repo.Get(suggestion)!.PayloadJson!.Replace(" ", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_adds_proposed_links()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        var target = repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-901", "status": "ready" }""", null, "MEMP-901", "me").Id;
        var other = repo.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-902", "status": "ready" }""", null, "MEMP-902", "me").Id;
        var suggestion = NewSuggestion(repo, "memory-mcp",
            $$"""{ "target_id": "{{target}}", "kind": "link", "proposed_links": [ { "to_id": "{{other}}", "rel": "relates_to" } ], "rationale": "related", "status": "open" }""");

        var result = reviewer.Apply(suggestion, RequestScope.Unrestricted);

        Assert.Equal(SuggestionOutcome.Applied, result.Outcome);
        Assert.Contains(repo.Links(target), l => l.NoteId == other && l.Rel == "relates_to" && l.Direction == "out");
    }

    [Fact]
    public void Reject_marks_rejected_and_leaves_target_untouched()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        var target = repo.Upsert("memory-mcp", "backlog_item", "Target", null, """{ "key": "MEMP-903", "status": "ready" }""", """["keep"]""", "MEMP-903", "me").Id;
        var suggestion = NewSuggestion(repo, "memory-mcp",
            $$"""{ "target_id": "{{target}}", "kind": "retag", "proposed_patch": { "tags": ["nope"] }, "rationale": "x", "status": "open" }""");

        var result = reviewer.Reject(suggestion, RequestScope.Unrestricted);

        Assert.Equal(SuggestionOutcome.Rejected, result.Outcome);
        Assert.Contains("keep", repo.Get(target)!.TagsJson!, StringComparison.Ordinal);     // target tags unchanged
        Assert.DoesNotContain("nope", repo.Get(target)!.TagsJson!, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_on_a_non_suggestion_is_not_found()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        var note = repo.Upsert("memory-mcp", "backlog_item", "X", null, """{ "key": "MEMP-904", "status": "ready" }""", null, "MEMP-904", "me").Id;

        Assert.Equal(SuggestionOutcome.NotFound, reviewer.Apply(note, RequestScope.Unrestricted).Outcome);
        Assert.Equal(SuggestionOutcome.NotFound, reviewer.Apply("ghost", RequestScope.Unrestricted).Outcome);
    }

    [Fact]
    public void Apply_with_unresolvable_target_is_bad_request()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        var suggestion = NewSuggestion(repo, "memory-mcp",
            """{ "target_id": "does-not-exist", "kind": "correct", "rationale": "x", "status": "open" }""");

        Assert.Equal(SuggestionOutcome.BadRequest, reviewer.Apply(suggestion, RequestScope.Unrestricted).Outcome);
    }

    [Fact]
    public void Apply_out_of_write_scope_is_forbidden()
    {
        using var temp = new TempDatabase();
        var (repo, reviewer) = New(temp);
        // commons is world-READable but not auto-writable, so a kitchen-scoped caller can see it yet not apply.
        var target = repo.Upsert("commons", "backlog_item", "T", null, """{ "key": "MEMP-905", "status": "ready" }""", null, "MEMP-905", "me").Id;
        var suggestion = NewSuggestion(repo, "commons",
            $$"""{ "target_id": "{{target}}", "kind": "retag", "proposed_patch": { "tags": ["x"] }, "rationale": "x", "status": "open" }""");

        var result = reviewer.Apply(suggestion, RequestScope.ForDomains(new[] { "kitchen" }));

        Assert.Equal(SuggestionOutcome.Forbidden, result.Outcome);
        Assert.Null(repo.Get(target)!.TagsJson); // nothing applied
    }

    private static string NewSuggestion(NotesRepository repo, string domain, string payloadJson) =>
        repo.Upsert(domain, "memory_evolution_suggestion", "suggestion", null, payloadJson, """["curation"]""", null, "memory-curator").Id;

    private static (NotesRepository Repo, SuggestionReviewer Reviewer) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources());
        return (repo, new SuggestionReviewer(repo));
    }
}
