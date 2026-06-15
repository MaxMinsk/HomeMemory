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

    [Fact]
    public void Upload_url_verifies_and_is_bound_to_all_parameters()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new ArtifactUrlSigner("secret", clock);

        var url = signer.BuildUploadUrl("kitchen", "f.jpg", "image/jpeg", "note1");
        Assert.StartsWith("/artifacts/upload?", url);
        var q = ParseQuery(url);

        Assert.True(signer.VerifyUpload(q["domain"], q["filename"], q["contentType"], q["noteId"], q["exp"], q["sig"]));
        Assert.False(signer.VerifyUpload("evil", q["filename"], q["contentType"], q["noteId"], q["exp"], q["sig"]));     // bound to domain
        Assert.False(signer.VerifyUpload(q["domain"], "evil.sh", q["contentType"], q["noteId"], q["exp"], q["sig"]));    // bound to filename
        Assert.False(signer.VerifyUpload(q["domain"], q["filename"], "text/x-sh", q["noteId"], q["exp"], q["sig"]));     // bound to contentType
        Assert.False(signer.VerifyUpload(q["domain"], q["filename"], q["contentType"], "other", q["exp"], q["sig"]));    // bound to noteId
        Assert.False(signer.VerifyUpload(q["domain"], q["filename"], q["contentType"], q["noteId"], q["exp"], q["sig"] + "ff")); // tampered
    }

    [Fact]
    public void Expired_upload_url_is_rejected()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new ArtifactUrlSigner("secret", clock);
        var q = ParseQuery(signer.BuildUploadUrl("kitchen", "f.jpg", "image/jpeg", null));

        clock.Advance(TimeSpan.FromHours(2)); // default TTL is 1h
        Assert.False(signer.VerifyUpload(q["domain"], q["filename"], q["contentType"], q["noteId"], q["exp"], q["sig"]));
    }

    [Fact]
    public void Read_signature_cannot_be_replayed_as_an_upload()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var signer = new ArtifactUrlSigner("secret", clock);
        var (exp, sig) = Parse(signer.BuildPath("kitchen")); // a read capability for id "kitchen"

        Assert.False(signer.VerifyUpload("kitchen", "f.jpg", null, null, exp, sig));
    }

    private static Dictionary<string, string> ParseQuery(string url)
    {
        var query = url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..];
        return query.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);
    }
}
