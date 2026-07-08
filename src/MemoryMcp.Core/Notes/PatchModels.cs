namespace MemoryMcp.Core.Notes;

/// <summary>One item of a <c>notes_patch_many</c> batch (MEMP-203): a partial update by id. Only the supplied
/// fields change; <see cref="PayloadJson"/> is shallow-merged into the existing payload, <see cref="TagsJson"/>
/// replaces the tags. <see cref="ExpectedUpdatedUtc"/> is an optional per-item optimistic-concurrency guard.</summary>
/// <param name="Id">The note id to patch.</param>
/// <param name="Title">New title, or null to leave unchanged.</param>
/// <param name="Body">New body, or null to leave unchanged.</param>
/// <param name="PayloadJson">Payload keys to shallow-merge, or null to leave unchanged.</param>
/// <param name="TagsJson">Tags array that REPLACES the current tags, or null to leave unchanged.</param>
/// <param name="ExpectedUpdatedUtc">Expected current updated_utc for optimistic concurrency, or null to skip the check.</param>
public sealed record PatchInput(
    string Id, string? Title = null, string? Body = null, string? PayloadJson = null, string? TagsJson = null,
    string? ExpectedUpdatedUtc = null);

/// <summary>The compact outcome of one patch in a <c>notes_patch_many</c> batch — no full body echoed back.</summary>
/// <param name="Id">The patched note id.</param>
/// <param name="UpdatedUtc">The note's new revision after the patch.</param>
public sealed record PatchResult(string Id, string UpdatedUtc);
