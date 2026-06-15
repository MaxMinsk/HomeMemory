using MemoryMcp.Core.Artifacts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace MemoryMcp.Tests.Artifacts;

public class ArtifactUrlSignerTests
{
    private static (string Exp, string Sig) Parse(string path)
    {
        var query = path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&');
        return (query[0].Split('=')[1], query[1].Split('=')[1]);
    }

    [Fact]
    public void Signed_path_verifies_and_is_bound_to_id_and_signature()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new ArtifactUrlSigner("secret", clock);

        var path = signer.BuildPath("abc");
        Assert.StartsWith("/artifacts/abc?exp=", path);
        var (exp, sig) = Parse(path);

        Assert.True(signer.Verify("abc", exp, sig));
        Assert.False(signer.Verify("other", exp, sig));   // bound to id
        Assert.False(signer.Verify("abc", exp, sig + "ff")); // tampered signature
        Assert.False(signer.Verify("abc", null, sig));    // missing exp
    }

    [Fact]
    public void Signature_from_a_different_secret_is_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (exp, sig) = Parse(new ArtifactUrlSigner("secret-A", clock).BuildPath("abc"));

        Assert.False(new ArtifactUrlSigner("secret-B", clock).Verify("abc", exp, sig));
    }

    [Fact]
    public void Expired_signature_is_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new ArtifactUrlSigner("secret", clock);
        var (exp, sig) = Parse(signer.BuildPath("abc"));

        clock.Advance(TimeSpan.FromHours(2)); // TTL is 1h
        Assert.False(signer.Verify("abc", exp, sig));
    }
}
