using System.Globalization;
using MemoryMcp.Core.Notes;
using MemoryMcp.Core.Schemas;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Notes;

/// <summary>
/// MEMP-240: facts age and get contradicted, but nothing in the store noticed — recall served a note whose truth
/// had expired exactly as confidently as one written yesterday, and supersede was entirely manual. These cover the
/// three layers: a staleness hint on hits, a supersede candidate at write time, and the guarantee that a
/// superseded note actually stops being recalled.
/// </summary>
public class TemporalValidityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_hit_whose_validity_date_has_passed_carries_a_staleness_hint()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        var expired = Upsert(repo, "expired", "Deploy token rotation window", Payload(validTo: Now.AddDays(-30)));
        var live = Upsert(repo, "live", "Deploy token rotation policy", Payload(validTo: Now.AddDays(30)));

        var items = repo.Search("deploy token", domain: "development").Items;

        var hint = Assert.Single(items, item => item.Id == expired).Staleness;
        Assert.NotNull(hint);
        Assert.Equal("expired", hint!.Reason);
        Assert.Equal(30, hint.AgeDays);
        Assert.Null(Assert.Single(items, item => item.Id == live).Staleness);
    }

    [Fact]
    public void A_hit_past_its_reverification_window_carries_a_staleness_hint()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        // The note states when it was last true; updated_utc is today either way, so the payload date has to win —
        // a retag or a typo fix must not read as "re-verified".
        var stale = repo.Upsert("development", "project_state", "Rollout state", "body text",
            $$"""{ "project": "unity", "state": "in progress", "updated": "{{Iso(Now.AddDays(-200))}}" }""",
            null, "rollout-state", "tester").Id;

        var hint = Assert.Single(repo.Search("rollout", domain: "development").Items, item => item.Id == stale).Staleness;

        Assert.NotNull(hint);
        Assert.Equal("past_window", hint!.Reason);
        Assert.Equal(Staleness.DefaultProjectStateDays, hint.WindowDays);
        Assert.True(hint.AgeDays >= 200, $"age should be measured from the payload date, not updated_utc (got {hint.AgeDays})");
    }

    /// <summary>
    /// A fact that declares only when it was established has no window of its own. Ageing every such note by
    /// default would put a hint on most of the corpus, so the horizon is opt-in configuration; without it the
    /// note is reported as-is.
    /// </summary>
    [Fact]
    public void A_fact_ages_against_the_configured_horizon_only_when_one_is_set()
    {
        const string payload = """{ "statement": "the client decides", "as_of": "2025-01-01T00:00:00Z" }""";

        Assert.Null(Staleness.Evaluate("fact", payload, Iso(Now), Now));
        Assert.Null(Staleness.Evaluate("fact", payload, Iso(Now), Now, new StalenessOptions(FactHorizonDays: 0)));

        var hint = Staleness.Evaluate("fact", payload, Iso(Now), Now, new StalenessOptions(FactHorizonDays: 180));

        Assert.Equal("past_window", hint!.Reason);
        Assert.Equal(180, hint.WindowDays);
    }

    /// <summary>
    /// The hint is derived from the payload, and a default recall strips the payload from its hits before returning
    /// them — so it must be computed on the row. Recall is the surface that matters most here: it is what an agent
    /// calls to decide what is true.
    /// </summary>
    [Fact]
    public void The_hint_survives_the_lean_recall_projection_that_drops_the_payload()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        var stale = Upsert(repo, "stale", "Rollout gate", Payload(validTo: Now.AddDays(-10)));

        var hit = Assert.Single(repo.Recall("rollout gate", "development", 10, null).Hits, item => item.Id == stale);

        Assert.Null(hit.PayloadJson); // lean by default (MEMP-214)
        Assert.NotNull(hit.Staleness);
    }

    [Fact]
    public void A_note_that_makes_no_temporal_claim_is_never_hinted()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        Upsert(repo, "plain", "An ordinary reference", """{ "statement": "no dates here" }""");

        Assert.All(repo.Search("ordinary", domain: "development").Items, item => Assert.Null(item.Staleness));
    }

    [Fact]
    public void A_competing_note_of_the_same_type_and_project_is_flagged_as_a_supersede_candidate()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        var original = repo.Upsert("development", "fact", "MR reward is client authoritative",
            "the client decides the reward", """{ "statement": "client authoritative" }""", null, "reward-old", "tester", "unity").Id;
        var replacement = repo.Upsert("development", "fact", "MR reward is server authoritative",
            "the server decides the reward", """{ "statement": "server authoritative" }""", null, "reward-new", "tester", "unity").Id;

        var related = repo.Related(replacement, 5, null);

        var candidate = Assert.Single(related, note => note.Id == original);
        Assert.Contains(NotesReader.SupersedeCandidateReason, candidate.Reasons);
        Assert.Equal(NotesReader.SupersedeCandidateReason, candidate.Reasons[0]); // ranked first: it needs a decision
    }

    [Fact]
    public void A_related_note_in_another_project_is_not_a_supersede_candidate()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        repo.Upsert("development", "fact", "MR reward is client authoritative", "the client decides the reward",
            """{ "statement": "client authoritative" }""", null, "reward-other", "tester", "another-game");
        var mine = repo.Upsert("development", "fact", "MR reward is server authoritative", "the server decides the reward",
            """{ "statement": "server authoritative" }""", null, "reward-new", "tester", "unity").Id;

        var related = repo.Related(mine, 5, null);

        Assert.All(related, note => Assert.DoesNotContain(NotesReader.SupersedeCandidateReason, note.Reasons));
    }

    /// <summary>
    /// Verifies the claim the feature rests on, which had never been asserted: superseding a note has to actually
    /// remove it from what recall serves, or "supersede it instead of writing a parallel truth" is bad advice.
    /// </summary>
    [Fact]
    public void A_superseded_note_stops_being_recalled_and_keeps_a_typed_link()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        var old = Upsert(repo, "reward-old", "MR reward is client authoritative", """{ "statement": "client" }""");
        var current = Upsert(repo, "reward-new", "MR reward is server authoritative", """{ "statement": "server" }""");

        Assert.Contains(old, repo.Search("reward authoritative", domain: "development").Items.Select(item => item.Id));

        Assert.True(repo.Supersede(old, current));

        var search = repo.Search("reward authoritative", domain: "development").Items.Select(item => item.Id).ToList();
        var recall = repo.Recall("reward authoritative", "development", 10, null).Hits.Select(hit => hit.Id).ToList();
        Assert.DoesNotContain(old, search);
        Assert.DoesNotContain(old, recall);
        Assert.Contains(current, search);

        // Still reachable on purpose: the replacement records what it replaced, and an explicit status query finds it.
        Assert.Contains(repo.Links(current), link => link.Rel == "supersedes" && link.NoteId == old);
        Assert.Contains(old, repo.Search("reward authoritative", domain: "development", status: "superseded")
            .Items.Select(item => item.Id));
    }

    /// <summary>
    /// MEMP-241: the neighbour half of the guarantee above. Superseding CREATES a `supersedes` link from the new
    /// note to the old one, so recall's one-hop expansion used to drag every replaced note straight back into the
    /// context block through the very link that retired it — while the note was correctly absent from the hits.
    /// Deliberate traversal still reaches it, and now reports the status so a caller can tell.
    /// </summary>
    [Fact]
    public void A_superseded_note_is_not_offered_as_a_recall_neighbour()
    {
        var repo = NewRepo(out var temp);
        using var _ = temp;
        var old = Upsert(repo, "reward-old", "MR reward is client authoritative", """{ "statement": "client" }""");
        var current = Upsert(repo, "reward-new", "MR reward is server authoritative", """{ "statement": "server" }""");
        Assert.True(repo.Supersede(old, current));

        var recall = repo.Recall("reward authoritative", "development", 10, null);

        Assert.Contains(current, recall.Hits.Select(hit => hit.Id));
        Assert.DoesNotContain(old, recall.Neighbors.Select(neighbour => neighbour.Id));

        // notes_links is the deliberate path and still resolves it, now with the status visible.
        var link = Assert.Single(repo.Links(current), view => view.NoteId == old);
        Assert.Equal("superseded", link.Status);
    }

    private static string Iso(DateTimeOffset when) => when.ToString("O", CultureInfo.InvariantCulture);

    // `valid_to` is the field the built-in fact schema declares; the schema rejects anything it does not name.
    private static string Payload(DateTimeOffset validTo) =>
        $$"""{ "statement": "a dated claim", "valid_to": "{{Iso(validTo)}}" }""";

    private static string Upsert(NotesRepository repo, string key, string title, string payload) =>
        repo.Upsert("development", "fact", title, "body text", payload, null, key, "tester").Id;

    private static NotesRepository NewRepo(out TempDatabase temp)
    {
        temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new NotesRepository(factory, SchemaRegistry.FromEmbeddedResources(), new FakeTimeProvider(Now));
    }
}
