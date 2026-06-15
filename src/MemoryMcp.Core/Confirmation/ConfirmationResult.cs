namespace MemoryMcp.Core.Confirmation;

/// <summary>The outcome of confirming or cancelling a pending action.</summary>
/// <param name="Token">The pending action's token.</param>
/// <param name="Action">The action that was resolved.</param>
/// <param name="Executed">True if the underlying operation ran and changed state.</param>
/// <param name="Detail">Human-readable description of what happened.</param>
public sealed record ConfirmationResult(string Token, string Action, bool Executed, string Detail);
