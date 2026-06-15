using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using MemoryMcp.Core.Artifacts;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Integration;

public class HttpUploadTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Signed_put_uploads_artifact_then_serves_it_and_rejects_tampering()
    {
        using var temp = new TempDatabase();
        using var blobDir = new TempDir();
        const string token = "test-bearer-token";
        var port = FreePort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        using var server = StartHttpServer(temp.FilePath, blobDir.Path, token, port);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await WaitForReady(http, cts.Token);

            // Mint an upload URL with the same secret the server signs with (its bearer token).
            var signer = new ArtifactUrlSigner(token, TimeProvider.System);
            var uploadUrl = signer.BuildUploadUrl("memory-mcp", "f.bin", "application/octet-stream", null);
            var bytes = Encoding.UTF8.GetBytes("hello upload");

            // Happy path: PUT bytes via the capability URL (no bearer header).
            var put = await http.PutAsync(uploadUrl, new ByteArrayContent(bytes), cts.Token);
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            using var doc = JsonDocument.Parse(await put.Content.ReadAsStringAsync(cts.Token));
            var artifactId = doc.RootElement.GetProperty("id").GetString()!;
            Assert.False(string.IsNullOrEmpty(doc.RootElement.GetProperty("sha256").GetString()));

            // The bytes are served back (bearer-authenticated), never having passed through any model context.
            using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/artifacts/{artifactId}");
            getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var get = await http.SendAsync(getReq, cts.Token);
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("hello upload", await get.Content.ReadAsStringAsync(cts.Token));

            // A tampered signature is rejected.
            var tampered = uploadUrl.Replace("&sig=", "&sig=ff", StringComparison.Ordinal);
            var bad = await http.PutAsync(tampered, new ByteArrayContent(bytes), cts.Token);
            Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        }
        finally
        {
            TryKill(server);
        }
    }

    private static async Task WaitForReady(HttpClient http, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 120; attempt++)
        {
            try
            {
                using var r = await http.GetAsync("/ui", ct); // /ui is auth-exempt
                if (r.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // server not up yet
            }

            await Task.Delay(250, ct);
        }

        throw new InvalidOperationException("HTTP server did not become ready in time.");
    }

    private static Process StartHttpServer(string dbPath, string blobRoot, string token, int port)
    {
        var info = new ProcessStartInfo("dotnet", LocateServerDll())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["MEMORY_TRANSPORT"] = "http";
        info.Environment["MEMORY_DB_PATH"] = dbPath;
        info.Environment["MEMORY_BLOB_ROOT"] = blobRoot;
        info.Environment["MEMORY_BEARER_TOKEN"] = token;
        info.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
        return Process.Start(info) ?? throw new InvalidOperationException("Failed to start server process.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already gone
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string LocateServerDll()
    {
        var fromEnv = Environment.GetEnvironmentVariable("MEMORY_SERVER_DLL");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var embedded = typeof(HttpUploadTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "ServerDll")?.Value;
        return embedded is not null ? Path.GetFullPath(embedded) : throw new InvalidOperationException(
            "Server dll path not embedded; set MEMORY_SERVER_DLL or rebuild the test project.");
    }
}
