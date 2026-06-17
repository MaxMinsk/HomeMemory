using System.Text.Json;
using MemoryMcp.Core.Security;

namespace MemoryMcp.Core.Notes;

/// <summary>The outcome of an apply/reject review action (mapped to an HTTP status by the endpoint).</summary>
public enum SuggestionOutcome
{
    /// <summary>The suggestion was applied to its target and marked applied.</summary>
    Applied,

    /// <summary>The suggestion was marked rejected (target untouched).</summary>
    Rejected,

    /// <summary>No such suggestion is visible in scope (or it is not a suggestion note).</summary>
    NotFound,

    /// <summary>The caller may not write the suggestion's or the target's domain.</summary>
    Forbidden,

    /// <summary>The target changed since it was read — optimistic-concurrency conflict.</summary>
    Conflict,

    /// <summary>The suggestion is malformed or its target_id is unresolvable.</summary>
    BadRequest,
}

/// <summary>Result of a review action on a <c>memory_evolution_suggestion</c>.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">A human-readable detail for non-success outcomes.</param>
/// <param name="Data">Optional success payload describing what was applied.</param>
public sealed record SuggestionActionResult(SuggestionOutcome Outcome, string? Message = null, object? Data = null);

/// <summary>
/// Applies or rejects a <c>memory_evolution_suggestion</c> — the viewer's one-click review actions (MEMP-133).
/// Apply maps the suggestion's <c>proposed_patch</c> (title/body/tags/payload, mirroring <c>notes_patch</c>:
/// tags REPLACE, payload MERGES) and <c>proposed_links</c> onto the target, scope-checked and with optimistic
/// concurrency, then marks the suggestion applied. Nothing is destructive; every step is audited.
/// </summary>
public sealed class SuggestionReviewer
{
    private const string SuggestionType = "memory_evolution_suggestion";
    private const string Reviewer = "viewer-review";

    private readonly NotesRepository _notes;

    /// <summary>Creates the reviewer over the note store.</summary>
    public SuggestionReviewer(NotesRepository notes) => _notes = notes ?? throw new ArgumentNullException(nameof(notes));

    /// <summary>Marks a suggestion rejected (no change to the target).</summary>
    /// <param name="id">The suggestion note id.</param>
    /// <param name="scope">The caller's request scope.</param>
    public SuggestionActionResult Reject(string id, RequestScope scope)
    {
        var guard = new ScopeGuard(scope);
        if (Load(id, guard) is not { } suggestion)
        {
            return new(SuggestionOutcome.NotFound);
        }

        try
        {
            guard.Authorize(suggestion.Domain);
            SetStatus(id, "rejected", suggestion.UpdatedUtc);
            return new(SuggestionOutcome.Rejected);
        }
        catch (ScopeForbiddenException) { return new(SuggestionOutcome.Forbidden); }
        catch (ConcurrencyException exception) { return new(SuggestionOutcome.Conflict, exception.Message); }
    }

    /// <summary>Applies a suggestion's proposed patch + links to its target, then marks it applied.</summary>
    /// <param name="id">The suggestion note id.</param>
    /// <param name="scope">The caller's request scope.</param>
    public SuggestionActionResult Apply(string id, RequestScope scope)
    {
        var guard = new ScopeGuard(scope);
        if (Load(id, guard) is not { } suggestion)
        {
            return new(SuggestionOutcome.NotFound);
        }

        SuggestionPlan plan;
        try { plan = Parse(suggestion.PayloadJson); }
        catch (JsonException) { return new(SuggestionOutcome.BadRequest, "Suggestion payload is not valid JSON."); }

        if (plan.TargetId is null || _notes.Get(plan.TargetId) is not { } target)
        {
            return new(SuggestionOutcome.BadRequest, "Suggestion has no resolvable target_id.");
        }

        try
        {
            guard.Authorize(suggestion.Domain);
            guard.Authorize(target.Domain);
            var patched = ApplyPatch(plan, target);
            var linksAdded = ApplyLinks(plan, target.Id, guard);
            SetStatus(id, "applied", suggestion.UpdatedUtc);
            return new(SuggestionOutcome.Applied, null, new { patched, linksAdded });
        }
        catch (ScopeForbiddenException) { return new(SuggestionOutcome.Forbidden); }
        catch (ConcurrencyException exception) { return new(SuggestionOutcome.Conflict, exception.Message); }
        catch (AssembleException exception) { return new(SuggestionOutcome.BadRequest, exception.Message); }
        catch (NoteValidationException exception) { return new(SuggestionOutcome.BadRequest, exception.Message); }
    }

    // Returns the suggestion only if it exists, is the right type, and is readable in scope (else null = 404).
    private Note? Load(string id, ScopeGuard guard)
    {
        var note = _notes.Get(id);
        return note is not null && note.Type == SuggestionType && guard.IsAllowed(note.Domain) ? note : null;
    }

    private bool ApplyPatch(SuggestionPlan plan, Note target)
    {
        if (!plan.HasPatch)
        {
            return false;
        }

        // Optimistic concurrency: patch against the revision we just read (fails if it changed meanwhile).
        _notes.Patch(target.Id, plan.Title, plan.Body, plan.PayloadJson, plan.TagsJson, target.UpdatedUtc, Reviewer);
        return true;
    }

    private int ApplyLinks(SuggestionPlan plan, string targetId, ScopeGuard guard)
    {
        var added = 0;
        foreach (var (toId, rel) in plan.Links)
        {
            if (_notes.Get(toId) is not { } to)
            {
                throw new AssembleException($"Proposed link target '{toId}' does not exist.");
            }

            guard.Authorize(to.Domain); // both ends must be writable, like notes_link
            _notes.Link(targetId, toId, rel); // idempotent (unique per from/to/rel)
            added++;
        }

        return added;
    }

    private void SetStatus(string id, string status, string expectedUpdatedUtc) =>
        _notes.Patch(id, null, null, $$"""{"status":"{{status}}"}""", null, expectedUpdatedUtc, Reviewer);

    private static SuggestionPlan Parse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return new SuggestionPlan(null, null, null, null, null, Array.Empty<(string, string)>());
        }

        using var document = JsonDocument.Parse(payloadJson);
        var root = document.RootElement;

        string? title = null, body = null, tagsJson = null, payload = null;
        if (root.TryGetProperty("proposed_patch", out var patch) && patch.ValueKind == JsonValueKind.Object)
        {
            title = Str(patch, "title");
            body = Str(patch, "body");
            tagsJson = Raw(patch, "tags", JsonValueKind.Array);
            payload = Raw(patch, "payload", JsonValueKind.Object);
        }

        return new SuggestionPlan(Str(root, "target_id"), title, body, tagsJson, payload, ParseLinks(root));
    }

    private static IReadOnlyList<(string ToId, string Rel)> ParseLinks(JsonElement root)
    {
        var links = new List<(string, string)>();
        if (!root.TryGetProperty("proposed_links", out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return links;
        }

        foreach (var item in array.EnumerateArray())
        {
            var toId = Str(item, "to_id");
            var rel = Str(item, "rel");
            if (!string.IsNullOrEmpty(toId) && !string.IsNullOrEmpty(rel))
            {
                links.Add((toId, rel));
            }
        }

        return links;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Raw(JsonElement element, string name, JsonValueKind kind) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == kind ? value.GetRawText() : null;

    private sealed record SuggestionPlan(
        string? TargetId, string? Title, string? Body, string? TagsJson, string? PayloadJson,
        IReadOnlyList<(string ToId, string Rel)> Links)
    {
        public bool HasPatch => Title is not null || Body is not null || TagsJson is not null || PayloadJson is not null;
    }
}
