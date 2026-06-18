using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Query;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

public class NotesRepositoryTests
{
    private const string ValidPayload = """{ "key": "MEMP-100", "status": "ready" }""";

    [Fact]
    public void Upsert_creates_row_and_event_with_provenance()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        var result = repo.Upsert("memory-mcp", "backlog_item", "Title", "Body",
            ValidPayload, """["x"]""", dedupKey: "MEMP-100", sourceAgent: "tester");

        Assert.True(result.Created);
        Assert.False(string.IsNullOrEmpty(result.Id));

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM note_events WHERE op = 'create' AND actor = 'tester';"));
    }

    [Fact]
    public void Upsert_same_dedup_key_updates_in_place()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        var first = repo.Upsert("memory-mcp", "backlog_item", "First", null, ValidPayload, null, "MEMP-100", "a");
        var second = repo.Upsert("memory-mcp", "backlog_item", "Second", null,
            """{ "key": "MEMP-100", "status": "in_progress" }""", null, "MEMP-100", "b");

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Id, second.Id);

        using var c = factory.Create();
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal("Second", (string)Scalar(c, "SELECT title FROM notes;")!);
        Assert.Equal(2L, Count(c, "SELECT count(*) FROM note_events;"));
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM note_events WHERE op = 'update';"));
    }

    [Fact]
    public void Upsert_rejects_invalid_payload_without_writing()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        Assert.Throws<NoteValidationException>(() =>
            repo.Upsert("memory-mcp", "backlog_item", "X", null,
                """{ "status": "bogus" }""", null, "MEMP-100", "a"));

        using var c = factory.Create();
        Assert.Equal(0L, Count(c, "SELECT count(*) FROM notes;"));
        Assert.Equal(0L, Count(c, "SELECT count(*) FROM note_events;"));
    }

    [Fact]
    public void Upsert_with_null_dedup_always_inserts()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);

        repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-101", "status": "ready" }""", null, null, "a");
        repo.Upsert("memory-mcp", "backlog_item", "B", null, """{ "key": "MEMP-102", "status": "ready" }""", null, null, "a");

        using var c = factory.Create();
        Assert.Equal(2L, Count(c, "SELECT count(*) FROM notes;"));
    }

    [Fact]
    public void Update_preserves_created_utc_and_bumps_updated_utc()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 10, 0, 0, TimeSpan.Zero));
        var (repo, factory) = NewRepo(temp, clock);

        repo.Upsert("memory-mcp", "backlog_item", "A", null, ValidPayload, null, "MEMP-100", "a");
        clock.Advance(TimeSpan.FromMinutes(5));
        repo.Upsert("memory-mcp", "backlog_item", "B", null,
            """{ "key": "MEMP-100", "status": "next" }""", null, "MEMP-100", "a");

        using var c = factory.Create();
        Assert.StartsWith("2026-06-15T10:00:00", (string)Scalar(c, "SELECT created_utc FROM notes;")!);
        Assert.StartsWith("2026-06-15T10:05:00", (string)Scalar(c, "SELECT updated_utc FROM notes;")!);
    }

    [Fact]
    public void Search_orders_by_a_numeric_payload_field_numerically()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("memory-mcp", "memory_rule", "low", null, """{ "description": "a", "priority": 2 }""", null, "r-low", "me");
        repo.Upsert("memory-mcp", "memory_rule", "top", null, """{ "description": "b", "priority": 10 }""", null, "r-top", "me");
        repo.Upsert("memory-mcp", "memory_rule", "mid", null, """{ "description": "c", "priority": 5 }""", null, "r-mid", "me");

        var page = repo.Search(type: "memory_rule", limit: 2, sort: "payload.priority desc");

        Assert.Equal("top", page.Items[0].Title); // 10 > 5 numerically (lexicographic would put "5" first)
        Assert.Equal("mid", page.Items[1].Title);
    }

    [Fact]
    public void DomainMove_moves_notes_and_sets_project_when_no_collision()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var id = repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-990", "status": "ready" }""", null, "MEMP-990", "me").Id;

        var dry = DomainMove.Run(factory, "memory-mcp", "development", apply: false);
        Assert.Equal(1, dry.ToMove);
        Assert.Empty(dry.Collisions);

        Assert.True(DomainMove.Run(factory, "memory-mcp", "development", apply: true).Applied);
        var moved = repo.Get(id)!;
        Assert.Equal("development", moved.Domain);
        Assert.Equal("memory-mcp", moved.Project);
    }

    [Fact]
    public void DomainMove_reports_collision_and_refuses_apply()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "A", null, """{ "key": "MEMP-991", "status": "ready" }""", null, "MEMP-991", "me");
        repo.Upsert("development", "backlog_item", "dup", null, """{ "key": "MEMP-991", "status": "ready" }""", null, "MEMP-991", "me");

        Assert.Contains("backlog_item:MEMP-991", DomainMove.Run(factory, "memory-mcp", "development", apply: false).Collisions);
        Assert.False(DomainMove.Run(factory, "memory-mcp", "development", apply: true).Applied); // blocked while a clash exists
    }

    [Fact]
    public void Upsert_stores_the_content_hash_for_the_note()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var id = repo.Upsert("memory-mcp", "backlog_item", "T", "body", """{ "key": "MEMP-600", "status": "ready" }""", """["x"]""", "MEMP-600", "me").Id;
        var expected = ContentHash.Compute("backlog_item", "T", "body", """{ "key": "MEMP-600", "status": "ready" }""", """["x"]""");

        using var c = factory.Create();
        Assert.Equal(expected, (string)Scalar(c, $"SELECT content_hash FROM notes WHERE id = '{id}';")!);
    }

    [Fact]
    public void Lint_flags_duplicate_content_under_different_keys()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("memory-mcp", "backlog_item", "same", "identical body", """{ "key": "MEMP-650", "status": "ready" }""", null, "dup-a", "me");
        repo.Upsert("memory-mcp", "backlog_item", "same", "identical body", """{ "key": "MEMP-650", "status": "ready" }""", null, "dup-b", "me");

        using (var c = factory.Create())
        {
            var hashes = Count(c, "SELECT count(DISTINCT content_hash) FROM notes WHERE domain='memory-mcp';");
            var rows = Count(c, "SELECT count(*) FROM notes WHERE domain='memory-mcp';");
            Assert.Equal(2L, rows);
            Assert.Equal(1L, hashes); // both rows share one content_hash
        }

        var findings = new NotesLinter(factory).Lint("memory-mcp", null);

        Assert.Contains(findings, finding => finding.Rule == "duplicate_content"); // identical content, different dedup keys
    }

    [Fact]
    public void DuplicateMerge_supersedes_older_duplicates_into_the_newest_and_repoints_links()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (repo, factory) = NewRepo(temp, clock);
        var older = repo.Upsert("development", "backlog_item", "Dup", "same body", """{ "key": "MEMP-700", "status": "ready" }""", null, "dup-old", "me").Id;
        clock.Advance(TimeSpan.FromDays(1));
        var newer = repo.Upsert("development", "backlog_item", "Dup", "same body", """{ "key": "MEMP-700", "status": "ready" }""", null, "dup-new", "me").Id;
        var external = repo.Upsert("development", "backlog_item", "Ext", null, """{ "key": "MEMP-701", "status": "ready" }""", null, "ext", "me").Id;
        repo.Link(external, older, "references"); // an external note points at the older duplicate

        var dry = DuplicateMerge.Run(factory, "development", apply: false);
        Assert.Equal(1, dry.Groups);
        Assert.Equal(1, dry.Superseded);
        Assert.False(dry.Applied);

        Assert.True(DuplicateMerge.Run(factory, "development", apply: true).Applied);

        Assert.Equal("superseded", repo.Get(older)!.Status); // older copy retired
        Assert.Equal("active", repo.Get(newer)!.Status);     // newest kept as canonical
        var links = repo.Links(external);
        Assert.Contains(links, link => link.NoteId == newer && link.Rel == "references");      // re-pointed to canonical
        Assert.DoesNotContain(links, link => link.NoteId == older && link.Rel == "references");
    }

    [Fact]
    public void DuplicateMerge_reports_no_groups_when_content_differs()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("development", "backlog_item", "A", "body one", """{ "key": "MEMP-710", "status": "ready" }""", null, "a", "me");
        repo.Upsert("development", "backlog_item", "B", "body two", """{ "key": "MEMP-711", "status": "ready" }""", null, "b", "me");

        var report = DuplicateMerge.Run(factory, "development", apply: true);

        Assert.Equal(0, report.Groups);
        Assert.Equal(0, report.Superseded);
        Assert.False(report.Applied);
    }

    [Fact]
    public void Changes_returns_events_oldest_first_with_a_resumable_cursor()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (repo, _) = NewRepo(temp, clock);
        clock.Advance(TimeSpan.FromSeconds(1)); repo.Upsert("development", "backlog_item", "A", null, """{ "key": "MEMP-900", "status": "ready" }""", null, "chg-a", "me");
        clock.Advance(TimeSpan.FromSeconds(1)); repo.Upsert("development", "backlog_item", "B", null, """{ "key": "MEMP-901", "status": "ready" }""", null, "chg-b", "me");

        var first = repo.Changes(null, "development", null, 50, null);
        Assert.Equal(2, first.Changes.Count);
        Assert.Equal("A", first.Changes[0].Title);  // oldest first
        Assert.Equal("B", first.Changes[1].Title);
        Assert.All(first.Changes, c => Assert.Equal("create", c.Op));
        Assert.False(first.HasMore);

        clock.Advance(TimeSpan.FromSeconds(1));
        repo.Upsert("development", "backlog_item", "B", null, """{ "key": "MEMP-901", "status": "in_progress" }""", null, "chg-b", "me"); // update

        var next = repo.Changes(first.NextCursor, "development", null, 50, null);
        var change = Assert.Single(next.Changes);
        Assert.Equal("update", change.Op);
        Assert.Equal("B", change.Title);
    }

    [Fact]
    public void Changes_paginates_with_hasMore()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (repo, _) = NewRepo(temp, clock);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            repo.Upsert("development", "backlog_item", "N" + i, null, $$"""{ "key": "MEMP-90{{i}}", "status": "ready" }""", null, "chg-" + i, "me");
        }

        var page1 = repo.Changes(null, "development", null, 2, null);
        Assert.Equal(2, page1.Changes.Count);
        Assert.True(page1.HasMore);
        var page2 = repo.Changes(page1.NextCursor, "development", null, 2, null);
        Assert.Single(page2.Changes);
        Assert.False(page2.HasMore);
    }

    [Fact]
    public void Changes_excludes_out_of_scope_domains()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("development", "backlog_item", "D", null, """{ "key": "MEMP-900", "status": "ready" }""", null, "chg-d", "me");
        repo.Upsert("kitchen", "backlog_item", "K", null, """{ "key": "MEMP-901", "status": "ready" }""", null, "chg-k", "me");

        var page = repo.Changes(null, null, null, 50, new[] { "development" });

        Assert.Contains(page.Changes, c => c.Title == "D");
        Assert.DoesNotContain(page.Changes, c => c.Domain == "kitchen");
    }

    [Fact]
    public void ScopedPurge_refuses_an_unscoped_purge()
    {
        using var temp = new TempDatabase();
        var (_, factory) = NewRepo(temp);
        Assert.Throws<ArgumentException>(() => ScopedPurge.Run(factory, null, null, apply: false));
        Assert.Throws<ArgumentException>(() => ScopedPurge.Run(factory, "  ", "", apply: true)); // blank scope is still unscoped
    }

    [Fact]
    public void ScopedPurge_hard_deletes_scoped_notes_and_their_satellites()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var spam = repo.Upsert("development", "backlog_item", "S1", "spam uniquetoken", """{ "key": "MEMP-900", "status": "ready" }""", null, "MEMP-900", "spammer").Id;
        repo.Upsert("development", "backlog_item", "S2", "more spam", """{ "key": "MEMP-901", "status": "ready" }""", null, "MEMP-901", "spammer");
        var keep = repo.Upsert("development", "backlog_item", "K", "legit", """{ "key": "MEMP-902", "status": "ready" }""", null, "MEMP-902", "keeper").Id;
        using (var seed = factory.Create()) // a link + a usage row hanging off the spam note (satellite tables)
        using (var cmd = seed.CreateCommand())
        {
            cmd.CommandText =
                "INSERT INTO note_links(from_id,to_id,rel,created_utc) VALUES ($f,$t,'mentions','2026-01-01');" +
                "INSERT INTO note_usage(note_id,last_accessed_utc,retrieval_count) VALUES ($f,'2026-01-01',3);";
            cmd.Parameters.AddWithValue("$f", spam);
            cmd.Parameters.AddWithValue("$t", keep);
            cmd.ExecuteNonQuery();
        }

        var dry = ScopedPurge.Run(factory, null, "spammer", apply: false);
        Assert.Equal(2, dry.Notes);
        Assert.False(dry.Applied);
        using (var c = factory.Create())
        {
            Assert.Equal(3L, Count(c, "SELECT count(*) FROM notes;")); // dry run wrote nothing
        }

        var done = ScopedPurge.Run(factory, null, "spammer", apply: true);
        Assert.Equal(2, done.Notes);
        Assert.True(done.Applied);

        using var conn = factory.Create();
        Assert.Equal(1L, Count(conn, "SELECT count(*) FROM notes;"));                          // only the keeper survives
        Assert.Equal(0L, Count(conn, "SELECT count(*) FROM note_events WHERE actor='spammer';"));
        Assert.Equal(0L, Count(conn, "SELECT count(*) FROM note_links;"));                     // the spam->keep link is gone
        Assert.Equal(0L, Count(conn, "SELECT count(*) FROM note_usage;"));                     // usage row gone
        Assert.NotNull(repo.Get(keep));
        Assert.Equal(0, repo.Search(query: "uniquetoken").Total);                              // FTS delete trigger fired
    }

    [Fact]
    public void ScopedPurge_by_domain_leaves_other_domains_untouched()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        repo.Upsert("scratch", "backlog_item", "A", null, """{ "key": "MEMP-910", "status": "ready" }""", null, "MEMP-910", "me");
        repo.Upsert("development", "backlog_item", "B", null, """{ "key": "MEMP-911", "status": "ready" }""", null, "MEMP-911", "me");

        Assert.True(ScopedPurge.Run(factory, "scratch", null, apply: true).Applied);

        using var c = factory.Create();
        Assert.Equal(0L, Count(c, "SELECT count(*) FROM notes WHERE domain='scratch';"));
        Assert.Equal(1L, Count(c, "SELECT count(*) FROM notes WHERE domain='development';"));
    }

    [Fact]
    public void Upsert_sets_project_from_payload_even_when_the_schema_forbids_extra_fields()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        // fact@1 has additionalProperties:false and no 'project' field; payload.project must STILL set the axis (MEMP-154).
        var id = repo.Upsert("development", "fact", "F", null,
            """{ "statement": "the n150 box has a coral tpu", "project": "ha-personal-agent" }""", null, "fact-proj", "me").Id;

        Assert.Equal("ha-personal-agent", repo.Get(id)!.Project); // envelope project lifted from payload despite the strict schema
    }

    [Fact]
    public void Upsert_derives_envelope_project_from_payload_project()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        var id = repo.Upsert("development", "backlog_item", "T", null,
            """{ "key": "MEMP-980", "status": "ready", "project": "unity-solitaire" }""", null, "MEMP-980", "me").Id;

        Assert.Equal("unity-solitaire", repo.Get(id)!.Project); // envelope project auto-set from payload.project
    }

    [Fact]
    public void ProjectBackfill_sets_envelope_project_from_payload_for_legacy_notes()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = NewRepo(temp);
        var id = repo.Upsert("development", "backlog_item", "T", null,
            """{ "key": "MEMP-981", "status": "ready", "project": "unity-solitaire" }""", null, "MEMP-981", "me").Id;
        using (var c = factory.Create()) // simulate a note written before the envelope axis existed
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "UPDATE notes SET project = NULL WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
        }
        Assert.Null(repo.Get(id)!.Project);

        Assert.Equal(1, ProjectBackfill.Run(factory, apply: false).NotesUpdated); // dry run counts it
        Assert.Equal(1, ProjectBackfill.Run(factory, apply: true).NotesUpdated);
        Assert.Equal("unity-solitaire", repo.Get(id)!.Project);
    }

    [Fact]
    public void Search_filters_by_the_project_envelope_axis()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("development", "backlog_item", "U", null, """{ "key": "MEMP-970", "status": "ready" }""", null, "MEMP-970", "me", project: "unity-solitaire");
        repo.Upsert("development", "backlog_item", "M", null, """{ "key": "MEMP-971", "status": "ready" }""", null, "MEMP-971", "me", project: "memory-mcp");

        var page = repo.Search(domain: "development", filter: "project == 'unity-solitaire'", includePayload: true);

        Assert.Equal("U", Assert.Single(page.Items).Title);
    }

    [Fact]
    public void Search_recency_sort_ranks_durable_types_above_ephemeral_at_equal_age()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (repo, _) = NewRepo(temp, clock);
        repo.Upsert("memory-mcp", "backlog_item", "ephemeral", null, """{ "key": "MEMP-700", "status": "ready" }""", null, "MEMP-700", "me");
        repo.Upsert("memory-mcp", "memory_rule", "durable", null, """{ "description": "x" }""", null, "rule-700", "me");

        var page = repo.Search(domain: "memory-mcp", sort: "recency");

        Assert.Equal("durable", page.Items[0].Title);    // memory_rule's long half-life outranks the same-age backlog_item
        Assert.Equal("ephemeral", page.Items[1].Title);
    }

    [Fact]
    public void Search_recency_sort_prefers_the_fresher_note_within_a_type()
    {
        using var temp = new TempDatabase();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (repo, _) = NewRepo(temp, clock);
        repo.Upsert("memory-mcp", "backlog_item", "older", null, """{ "key": "MEMP-701", "status": "ready" }""", null, "MEMP-701", "me");
        clock.Advance(TimeSpan.FromDays(10));
        repo.Upsert("memory-mcp", "backlog_item", "newer", null, """{ "key": "MEMP-702", "status": "ready" }""", null, "MEMP-702", "me");

        var page = repo.Search(domain: "memory-mcp", type: "backlog_item", sort: "recency");

        Assert.Equal("newer", page.Items[0].Title);
        Assert.Equal("older", page.Items[1].Title);
    }

    [Fact]
    public void Search_rejects_an_unsortable_or_injected_field()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        Assert.Throws<FilterException>(() => repo.Search(sort: "evil; DROP TABLE notes"));
    }

    [Fact]
    public void CountByTypeInDomain_groups_active_notes_in_one_domain()
    {
        using var temp = new TempDatabase();
        var (repo, _) = NewRepo(temp);
        repo.Upsert("kitchen", "backlog_item", "A", null, """{ "key": "MEMP-960", "status": "ready" }""", null, "MEMP-960", "me");
        repo.Upsert("kitchen", "memory_rule", "R", null, """{ "description": "x" }""", null, "rule-1", "me");
        repo.Upsert("home", "backlog_item", "B", null, """{ "key": "MEMP-961", "status": "ready" }""", null, "MEMP-961", "me");

        var counts = repo.CountByTypeInDomain("kitchen");

        Assert.Equal(2, counts.Count);            // only kitchen's types
        Assert.Equal(1, counts["backlog_item"]);  // home's backlog_item is not counted here
        Assert.Equal(1, counts["memory_rule"]);
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) NewRepo(TempDatabase temp, TimeProvider? clock = null)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        var repo = new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), clock);
        return (repo, factory);
    }

    private static long Count(SqliteConnection connection, string sql) => Convert.ToInt64(Scalar(connection, sql));

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
