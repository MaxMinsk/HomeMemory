namespace MemoryMcp.Core.Confirmation;

/// <summary>A destructive operation awaiting confirmation. The caller passes <paramref name="Token"/>
/// back to confirm (execute) or cancel it.</summary>
/// <param name="Token">Opaque one-time token identifying this pending action.</param>
/// <param name="Action">The destructive action, e.g. <c>archive</c> or <c>supersede</c>.</param>
/// <param name="TargetId">The note the action applies to.</param>
/// <param name="SecondId">Secondary note id where the action needs one (e.g. supersede's replacement).</param>
/// <param name="Summary">Human-readable description of what confirming will do.</param>
/// <param name="Status">Lifecycle status: <c>pending</c> until resolved.</param>
/// <param name="CreatedUtc">When the request was recorded (ISO-8601 UTC).</param>
/// <param name="TargetDomain">The domain the action touches, used to scope confirm/cancel/list (may be null for legacy rows).</param>
public sealed record PendingAction(
    string Token, string Action, string TargetId, string? SecondId, string? Summary, string Status, string CreatedUtc, string? TargetDomain = null);
