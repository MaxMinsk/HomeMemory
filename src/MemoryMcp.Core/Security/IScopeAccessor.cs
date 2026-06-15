namespace MemoryMcp.Core.Security;

/// <summary>Supplies the <see cref="RequestScope"/> for the current call.</summary>
public interface IScopeAccessor
{
    /// <summary>The active scope.</summary>
    RequestScope Current { get; }
}

/// <summary>A trusted accessor that always reports an unrestricted scope (used for local stdio).</summary>
public sealed class TrustedScopeAccessor : IScopeAccessor
{
    /// <inheritdoc />
    public RequestScope Current => RequestScope.Unrestricted;
}
