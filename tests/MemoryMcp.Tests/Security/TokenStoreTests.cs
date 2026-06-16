using MemoryMcp.Core.Security;
using MemoryMcp.Core.Storage;
using MemoryMcp.Tests.Storage;
using Xunit;

namespace MemoryMcp.Tests.Security;

public class TokenStoreTests
{
    [Fact]
    public void Add_resolve_revoke_round_trip()
    {
        using var temp = new TempDatabase();
        var store = NewStore(temp);

        var record = store.Add("home-agent", "Home, Kitchen", "raw-secret-123");
        Assert.Equal("home,kitchen", record.Domains); // normalized, lowercase

        var resolved = store.Resolve("raw-secret-123");
        Assert.Equal(record.Id, resolved!.Id);
        Assert.Null(store.Resolve("wrong-token"));      // unknown -> null

        Assert.True(store.Revoke(record.Id));
        Assert.Null(store.Resolve("raw-secret-123"));   // revoked -> null
        Assert.False(store.Revoke(record.Id));          // already revoked
    }

    [Fact]
    public void Stores_only_the_hash_never_the_raw_token()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        new TokenStore(factory).Add(null, "*", "supersecret");

        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT token_hash FROM tokens LIMIT 1;";
        var stored = (string)command.ExecuteScalar()!;

        Assert.Equal(TokenStore.Hash("supersecret"), stored);
        Assert.NotEqual("supersecret", stored);
    }

    [Fact]
    public void AllowedDomains_maps_star_and_list()
    {
        Assert.Null(TokenStore.AllowedDomains("*"));
        Assert.Null(TokenStore.AllowedDomains(""));
        Assert.Equal(new[] { "home", "kitchen" }, TokenStore.AllowedDomains("Home, Kitchen"));
    }

    [Fact]
    public void List_includes_active_and_revoked()
    {
        using var temp = new TempDatabase();
        var store = NewStore(temp);
        var a = store.Add("a", "home", "tok-a");
        store.Add("b", "*", "tok-b");
        store.Revoke(a.Id);

        Assert.Equal(2, store.List().Count);
    }

    private static TokenStore NewStore(TempDatabase temp)
    {
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, SchemaMigrations.All).Migrate();
        return new TokenStore(factory);
    }
}
