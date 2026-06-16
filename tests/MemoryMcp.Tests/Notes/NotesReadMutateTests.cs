using System.Text.Json;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesReadMutateTests
{
    [Fact]
    public void Get_returns_full_note()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", title: "Title", body: "Body", tags: """["x"]""");

        var note = repo.Get(id);

        Assert.NotNull(note);
        Assert.Equal("memory-mcp", note!.Domain);
        Assert.Equal("backlog_item", note.Type);
        Assert.Equal("Title", note.Title);
        Assert.Equal("active", note.Status);
        Assert.Contains("MEMP-200", note.PayloadJson!);
    }

    [Fact]
    public void Get_returns_null_when_missing()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);

        Assert.Null(repo.Get("nope"));
    }

    [Fact]
    public void GetView_returns_full_body_and_size_metadata_by_default()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "Hello world");

        var view = repo.GetView(id);

        Assert.NotNull(view);
        Assert.Equal("Hello world", view!.Body);   // full body by default
        Assert.Equal(11, view.BodyChars);
        Assert.False(view.Truncated);
        Assert.Equal(0, view.AttachmentCount);
        Assert.Equal(0, view.LinkCount);
    }

    [Fact]
    public void GetView_includeBody_false_omits_body_but_reports_size()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "Hello world");

        var view = repo.GetView(id, includeBody: false);

        Assert.Null(view!.Body);            // peek: no body
        Assert.Equal(11, view.BodyChars);   // but the size is still known
        Assert.True(view.Truncated);
    }

    [Fact]
    public void GetView_bodyMaxChars_caps_body_and_flags_truncation()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "Hello world");

        var view = repo.GetView(id, bodyMaxChars: 5);

        Assert.Equal("Hello", view!.Body);
        Assert.Equal(11, view.BodyChars);
        Assert.True(view.Truncated);

        // A cap at or above the length does not truncate.
        Assert.False(repo.GetView(id, bodyMaxChars: 11)!.Truncated);
    }

    [Fact]
    public void GetView_counts_links_in_both_directions()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");
        repo.Link(a, b, "depends_on");

        Assert.Equal(1, repo.GetView(a)!.LinkCount);
        Assert.Equal(1, repo.GetView(b)!.LinkCount);
    }

    [Fact]
    public void Search_by_text_returns_snippet_without_body()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "findme uniqueword");

        var hits = repo.Search(query: "uniqueword");

        var hit = Assert.Single(hits.Items);
        Assert.Equal(id, hit.Id);
        Assert.NotNull(hit.Snippet);
        Assert.Contains("uniqueword", hit.Snippet!);
    }

    [Fact]
    public void Search_handles_fts_special_characters_without_error()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", body: "alpha beta");

        // Hyphen/colon/quote would break a raw FTS5 MATCH; the sanitizer turns them into phrases.
        Assert.Single(repo.Search(query: "alpha-beta").Items);
        Assert.Empty(repo.Search(query: "\"missing\":").Items);
    }

    [Fact]
    public void Search_finds_text_in_payload()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        // The searchable text lives only in the payload (no title/body); FTS now covers payload (MEMP-120).
        repo.Upsert("memory-mcp", "fact", null, null, """{ "statement": "uniquepayloadword about a pipe" }""", null, "F-1", "me");

        Assert.Single(repo.Search(query: "uniquepayloadword").Items);
    }

    [Fact]
    public void Search_finds_note_by_its_dedup_key()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        // Title deliberately omits the key; the key lives only in dedup_key/payload.
        repo.Upsert("memory-mcp", "backlog_item", "A structured-inputs task", null,
            """{ "key": "MEMP-072", "status": "next" }""", null, "MEMP-072", "me");

        Assert.Single(repo.Search(query: "072").Items);        // key token, via the FTS dedup_key column
        Assert.Single(repo.Search(query: "MEMP-072").Items);
    }

    [Fact]
    public void Search_filters_by_domain_and_type()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", domain: "memory-mcp");
        Seed(repo, "WORK-200", "ready", domain: "work");

        Assert.Single(repo.Search(domain: "memory-mcp").Items);
        Assert.Single(repo.Search(domain: "work").Items);
        Assert.Empty(repo.Search(type: "recipe").Items);
    }

    [Fact]
    public void Search_filters_by_tag()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready", tags: """["urgent"]""");
        Seed(repo, "MEMP-201", "ready", tags: """["later"]""");

        Assert.Single(repo.Search(tags: new[] { "urgent" }).Items);
        Assert.Empty(repo.Search(tags: new[] { "missing" }).Items);
    }

    [Fact]
    public void Search_without_query_returns_structured_results()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Seed(repo, "MEMP-200", "ready");
        Seed(repo, "MEMP-201", "ready");

        var hits = repo.Search();

        Assert.Equal(2, hits.Total);
        Assert.Equal(2, hits.Items.Count);
        Assert.All(hits.Items, h => Assert.Null(h.Snippet));
    }

    [Fact]
    public void Search_paginates_with_total_and_hasMore()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        for (var i = 0; i < 5; i++)
        {
            Seed(repo, $"MEMP-30{i}", "ready");
        }

        var first = repo.Search(limit: 2, offset: 0);
        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.True(first.HasMore);

        var last = repo.Search(limit: 2, offset: 4);
        Assert.Single(last.Items);
        Assert.False(last.HasMore);

        var clamped = repo.Search(limit: 9999);
        Assert.Equal(NotesReader.MaxLimit, clamped.Limit); // page size clamped to 100
        Assert.Equal(5, clamped.Items.Count);
    }

    [Fact]
    public void Archive_hides_from_default_search_but_get_and_archived_search_work()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "findme");

        Assert.True(repo.Archive(id));

        Assert.Empty(repo.Search(query: "findme").Items);                    // default status = active
        Assert.Single(repo.Search(query: "findme", status: "archived").Items);
        Assert.Equal("archived", repo.Get(id)!.Status);               // still retrievable by id
        Assert.False(repo.Archive(id));                               // already archived -> no-op
    }

    [Fact]
    public void Link_inserts_link_and_event()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");

        repo.Link(a, b, "depends_on");

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_links WHERE from_id = '{a}' AND to_id = '{b}' AND rel = 'depends_on';"));
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_events WHERE note_id = '{a}' AND op = 'link';"));
    }

    [Fact]
    public void Link_is_idempotent_and_validates_endpoints()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");

        repo.Link(a, b, "depends_on");
        repo.Link(a, b, "depends_on"); // duplicate -> ignored (unique index)

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_links WHERE from_id = '{a}' AND to_id = '{b}' AND rel = 'depends_on';"));
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_events WHERE note_id = '{a}' AND op = 'link';")); // one audit, not two

        Assert.Throws<AssembleException>(() => repo.Link(a, "ghost", "depends_on")); // missing endpoint rejected
    }

    [Fact]
    public void Supersede_marks_old_and_links_new()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var oldId = Seed(repo, "MEMP-200", "ready");
        var newId = Seed(repo, "MEMP-201", "ready");

        Assert.True(repo.Supersede(oldId, newId));

        Assert.Equal("superseded", repo.Get(oldId)!.Status);
        using var c = factory.Create();
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_links WHERE from_id = '{newId}' AND to_id = '{oldId}' AND rel = 'supersedes';"));
        Assert.Equal(1L, Count(c, $"SELECT count(*) FROM note_events WHERE note_id = '{oldId}' AND op = 'supersede';"));
    }

    [Fact]
    public void Search_filter_dsl_matches_payload_field()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        SeedSprint(repo, "MEMP-700", "S1");
        SeedSprint(repo, "MEMP-701", "S1");
        SeedSprint(repo, "MEMP-702", "S2");

        var s1 = repo.Search(type: "backlog_item", filter: "payload.sprint == 'S1'");
        Assert.Equal(2, s1.Total);

        var combined = repo.Search(filter: "payload.sprint == 'S1' AND payload.status == 'ready'");
        Assert.Equal(2, combined.Total);

        Assert.Empty(repo.Search(filter: "payload.sprint == 'S9'").Items);
    }

    [Fact]
    public void Search_filter_is_null_finds_items_without_a_sprint()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        SeedSprint(repo, "MEMP-700", "S1");
        Seed(repo, "MEMP-800", "later");   // no sprint in payload

        Assert.Equal(1, repo.Search(type: "backlog_item", filter: "payload.sprint is null").Total);
        Assert.Equal(1, repo.Search(type: "backlog_item", filter: "payload.sprint is not null").Total);
    }

    [Fact]
    public void Search_includePayload_returns_status_and_payload_only_when_requested()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        SeedSprint(repo, "MEMP-700", "S1");

        var lean = repo.Search(type: "backlog_item");
        var leanHit = Assert.Single(lean.Items);
        Assert.Null(leanHit.Status);
        Assert.Null(leanHit.PayloadJson);

        var rich = repo.Search(type: "backlog_item", includePayload: true);
        var richHit = Assert.Single(rich.Items);
        Assert.Equal("active", richHit.Status);          // envelope status
        Assert.Contains("\"sprint\":\"S1\"", richHit.PayloadJson!.Replace(" ", string.Empty));
    }

    [Fact]
    public void Search_includeLinks_attaches_links_only_when_requested()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");
        repo.Link(a, b, "depends_on");

        var lean = repo.Search(domain: "memory-mcp");
        Assert.All(lean.Items, hit => Assert.Null(hit.Links));

        var rich = repo.Search(domain: "memory-mcp", includeLinks: true);
        var hitA = Assert.Single(rich.Items, hit => hit.Id == a);
        var link = Assert.Single(hitA.Links!);
        Assert.Equal("depends_on", link.Rel);
        Assert.Equal(b, link.NoteId);
    }

    private static string SeedSprint(NotesRepository repo, string key, string sprint) =>
        repo.Upsert("memory-mcp", "backlog_item", key, null,
            $$"""{ "key": "{{key}}", "status": "ready", "sprint": "{{sprint}}" }""", null, key, "tester").Id;

    [Fact]
    public void Links_returns_both_directions_and_unlink_removes()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready");
        var b = Seed(repo, "MEMP-201", "ready");
        repo.Link(a, b, "depends_on");

        var fromA = Assert.Single(repo.Links(a));
        Assert.Equal("out", fromA.Direction);
        Assert.Equal("depends_on", fromA.Rel);
        Assert.Equal(b, fromA.NoteId);
        Assert.Equal("in", Assert.Single(repo.Links(b)).Direction);

        Assert.Equal(1, repo.Unlink(a, b, "depends_on"));
        Assert.Empty(repo.Links(a));
        Assert.Equal(0, repo.Unlink(a, b, "depends_on")); // already gone
    }

    [Fact]
    public void Related_ranks_candidates_by_shared_tag_count()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready", tags: """["topic:x", "topic:y"]""");
        var b = Seed(repo, "MEMP-201", "ready", tags: """["topic:x", "topic:y"]""");   // 2 shared
        var c = Seed(repo, "MEMP-202", "ready", tags: """["topic:x"]""");               // 1 shared
        Seed(repo, "MEMP-203", "ready", tags: """["topic:z"]""");                       // 0 shared

        var related = repo.Related(a, 10, restrictToDomains: null);

        Assert.Equal(2, related.Count);          // self and the zero-overlap note excluded
        Assert.Equal(b, related[0].Id);          // most shared tags first
        Assert.Equal(c, related[1].Id);
        Assert.Equal(2, related[0].SharedTags.Count);
        Assert.Contains("topic:x", related[0].SharedTags);
    }

    [Fact]
    public void Related_is_empty_when_the_note_has_no_tags()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready");

        Assert.Empty(repo.Related(id, 10, restrictToDomains: null));
    }

    [Fact]
    public void Recall_returns_hits_and_one_hop_neighbors()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready", body: "findme alpha");
        var b = Seed(repo, "MEMP-201", "ready", title: "Neighbor");
        repo.Link(a, b, "depends_on");

        var recall = repo.Recall("findme", "memory-mcp", 10, restrictToDomains: null);

        Assert.Equal(a, Assert.Single(recall.Hits).Id);
        var neighbor = Assert.Single(recall.Neighbors);
        Assert.Equal(b, neighbor.Id);
        Assert.Equal("depends_on", neighbor.Rel);
        Assert.Equal(a, neighbor.FromId);
    }

    [Fact]
    public void Recall_excludes_out_of_scope_neighbors()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var a = Seed(repo, "MEMP-200", "ready", body: "findme");
        var work = repo.Upsert("work", "backlog_item", "W", null, """{ "key": "WORK-200", "status": "ready" }""", null, "WORK-200", "t").Id;
        repo.Link(a, work, "depends_on");

        var recall = repo.Recall("findme", null, 10, restrictToDomains: new[] { "memory-mcp" });

        Assert.Equal(a, Assert.Single(recall.Hits).Id); // only the in-scope hit
        Assert.Empty(recall.Neighbors);                 // the 'work' neighbor is filtered out
    }

    [Fact]
    public void Patch_merges_payload_keeps_other_fields_and_bumps_revision()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", body: "Body");
        var rev = repo.Get(id)!.UpdatedUtc;

        var patched = repo.Patch(id, "New title", null, """{ "status": "done" }""", null, rev, "me");

        Assert.NotNull(patched);
        Assert.Equal("New title", patched!.Title);
        Assert.Equal("Body", patched.Body);                         // untouched
        using var doc = JsonDocument.Parse(patched.PayloadJson!);
        Assert.Equal("done", doc.RootElement.GetProperty("status").GetString());   // overwritten
        Assert.Equal("MEMP-200", doc.RootElement.GetProperty("key").GetString());  // merged, kept
        Assert.NotEqual(rev, patched.UpdatedUtc);                   // revision bumped
    }

    [Fact]
    public void Patch_conflicts_on_stale_revision()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready");

        Assert.Throws<ConcurrencyException>(() =>
            repo.Patch(id, "x", null, null, null, "1999-01-01T00:00:00.0000000Z", "me"));
    }

    [Fact]
    public void Patch_missing_returns_null()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Assert.Null(repo.Patch("nope", "x", null, null, null, null, "me"));
    }

    [Fact]
    public void Patch_rejects_invalid_merged_payload()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready");

        Assert.Throws<NoteValidationException>(() =>
            repo.Patch(id, null, null, """{ "status": "bogus" }""", null, null, "me"));
    }

    [Fact]
    public void Restore_reactivates_archived_note()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready");

        Assert.True(repo.Archive(id));
        Assert.Equal("archived", repo.Get(id)!.Status);
        Assert.True(repo.Restore(id));
        Assert.Equal("active", repo.Get(id)!.Status);
        Assert.False(repo.Restore(id)); // already active -> no-op
    }

    [Fact]
    public void Events_returns_history()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready");
        repo.Archive(id);

        var events = repo.Events(id);
        Assert.Contains(events, e => e.Op == "create");
        Assert.Contains(events, e => e.Op == "archive");
    }

    [Fact]
    public void History_is_compact_with_changed_fields_and_detail_on_demand()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", title: "Old", body: "Body");
        var rev = repo.Get(id)!.UpdatedUtc;
        repo.Patch(id, "New title", null, null, null, rev, "me");
        repo.Archive(id);

        var events = repo.Events(id);

        var patch = Assert.Single(events, e => e.Op == "patch");
        Assert.Contains("title", patch.ChangedFields);            // only the title changed
        Assert.DoesNotContain("body", patch.ChangedFields);
        Assert.True(patch.DiffBytes > 0);

        var archive = Assert.Single(events, e => e.Op == "archive");
        Assert.Empty(archive.ChangedFields);                     // no snapshot fields for archive

        // The heavy before/after is only fetched on demand by event id.
        var detail = repo.Event(id, patch.EventId);
        Assert.NotNull(detail);
        Assert.Contains("New title", detail!.DiffJson!);
        Assert.Contains("before", detail.DiffJson!);
        Assert.Null(repo.Event(id, "no-such-event"));
    }

    [Fact]
    public void HistoryEvent_caps_and_projects_the_diff()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = Seed(repo, "MEMP-200", "ready", title: "Old", body: "Body");
        var rev = repo.Get(id)!.UpdatedUtc;
        repo.Patch(id, "New title", null, null, null, rev, "me");
        var patch = Assert.Single(repo.Events(id), e => e.Op == "patch");

        var full = repo.Event(id, patch.EventId)!;
        Assert.False(full.Truncated);
        Assert.True(full.DiffChars > 0);
        Assert.Contains("before", full.DiffJson!, StringComparison.Ordinal);

        var capped = repo.Event(id, patch.EventId, maxChars: 10)!;
        Assert.True(capped.Truncated);
        Assert.Equal(10, capped.DiffJson!.Length);
        Assert.Equal(full.DiffChars, capped.DiffChars); // still reports the true size

        var changed = repo.Event(id, patch.EventId, fields: "changed")!;
        Assert.Contains("title", changed.DiffJson!, StringComparison.Ordinal);
        Assert.Contains("New title", changed.DiffJson!, StringComparison.Ordinal);
        Assert.Contains("Old", changed.DiffJson!, StringComparison.Ordinal);
        Assert.DoesNotContain("\"body\"", changed.DiffJson!, StringComparison.Ordinal); // unchanged field omitted
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }

    private static string Seed(NotesRepository repo, string key, string status,
        string domain = "memory-mcp", string? title = null, string? body = null, string? tags = null)
    {
        var payload = $$"""{ "key": "{{key}}", "status": "{{status}}" }""";
        return repo.Upsert(domain, "backlog_item", title ?? key, body, payload, tags, key, "tester").Id;
    }

    private static long Count(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
