using System.Linq;
using MemoryMcp.Core.Maintenance;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Notes;

// MEMP-215 (semantic/cross-field workflow lint) + MEMP-216 (dependency-representation drift) + MEMP-221 (lint scoping).
public class NotesLinterSemanticTests
{
    [Fact]
    public void Flags_semantic_workflow_contradictions_but_not_consistent_states()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        Seed(repo, "TST-100", "Open blocker", "ready");                         // an open (not-done) blocker
        var readyBlocked = Seed(repo, "TST-200", "Ready but blocked", "ready", "TST-100"); // -> inconsistent
        var dangling = Seed(repo, "TST-300", "Dangling dep", "ready", "TST-999");           // -> unresolved
        Seed(repo, "TST-500", "Done dep", "done");
        var satisfied = Seed(repo, "TST-400", "Blocked all done", "blocked", "TST-500");    // -> satisfied
        var correctlyBlocked = Seed(repo, "TST-600", "Correctly blocked", "blocked", "TST-100"); // consistent

        var findings = new NotesLinter(factory).Lint(null, null, 1000);

        Assert.Contains(findings, f => f.Rule == "inconsistent_workflow_state" && f.NoteId == readyBlocked);
        Assert.Contains(findings, f => f.Rule == "unresolved_dependency" && f.NoteId == dangling);
        Assert.Contains(findings, f => f.Rule == "satisfied_dependency" && f.NoteId == satisfied);
        // A blocked item whose blocker is genuinely open is a correct state — no semantic finding.
        Assert.DoesNotContain(findings, f => f.NoteId == correctlyBlocked &&
            f.Rule is "inconsistent_workflow_state" or "satisfied_dependency");
    }

    [Fact]
    public void Flags_dependency_dual_encoded_in_both_payload_and_graph()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        var blocker = Seed(repo, "TST-100", "Blocker", "ready");
        // Dual-encoded: dep in payload.blocked_by AND as a graph link -> the real drift (two sources).
        var dual = Seed(repo, "TST-700", "Dual encoded", "blocked", "TST-100");
        repo.Link(dual, blocker, "depends_on");
        // Graph-only: a graph dep link with NO payload.blocked_by -> a valid single form, NOT drift.
        var graphOnly = Seed(repo, "TST-800", "Graph only", "ready");
        repo.Link(graphOnly, blocker, "blocked_by");

        var findings = new NotesLinter(factory).Lint(null, null, 1000);

        Assert.Contains(findings, f => f.Rule == "dependency_representation_drift" && f.NoteId == dual);
        Assert.DoesNotContain(findings, f => f.Rule == "dependency_representation_drift" && f.NoteId == graphOnly); // single form is fine
        Assert.DoesNotContain(findings, f => f.Rule == "dependency_representation_drift" && f.NoteId == blocker);
    }

    [Fact]
    public void Scopes_by_noteIds()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        Seed(repo, "TST-100", "Open blocker", "ready");
        var readyBlocked = Seed(repo, "TST-200", "Ready but blocked", "ready", "TST-100");
        Seed(repo, "TST-300", "Dangling dep", "ready", "TST-999");

        var findings = new NotesLinter(factory).Lint(null, null, 1000, noteIds: new[] { readyBlocked });

        Assert.All(findings, f => Assert.Equal(readyBlocked, f.NoteId));                 // only the requested note scanned
        Assert.Contains(findings, f => f.Rule == "inconsistent_workflow_state");
        Assert.DoesNotContain(findings, f => f.Rule == "unresolved_dependency");         // TST-300 not scanned
    }

    [Fact]
    public void Type_filter_excluding_backlog_item_skips_semantic_rules()
    {
        using var temp = new TempDatabase();
        var (repo, factory) = New(temp);
        Seed(repo, "TST-100", "Open blocker", "ready");
        Seed(repo, "TST-200", "Ready but blocked", "ready", "TST-100");

        var findings = new NotesLinter(factory).Lint(null, null, 1000, types: new[] { "fact" });

        Assert.DoesNotContain(findings, f => f.Rule is "inconsistent_workflow_state" or "unresolved_dependency" or "dependency_representation_drift");
    }

    private static string Seed(NotesRepository repo, string key, string title, string status, string? blockedBy = null)
    {
        var deps = blockedBy is null ? "[]" : $"[\"{blockedBy}\"]";
        var payload = $$"""{ "key": "{{key}}", "status": "{{status}}", "blocked_by": {{deps}} }""";
        return repo.Upsert("development", "backlog_item", title, title, payload, """["backlog"]""", key, "me", "test-proj").Id;
    }

    private static (NotesRepository Repo, SqliteConnectionFactory Factory) New(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return (new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources()), factory);
    }
}
