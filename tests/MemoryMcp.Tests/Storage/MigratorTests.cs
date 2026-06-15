using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Storage;

public class MigratorTests
{
    [Fact]
    public void Migrate_clean_database_applies_all_steps()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, new IMigration[]
        {
            CreateTable(1, "a"),
            CreateTable(2, "b"),
        });

        var version = migrator.Migrate();

        Assert.Equal(2, version);
        Assert.Equal(2, migrator.GetCurrentVersion());
        Assert.True(TableExists(factory, "a"));
        Assert.True(TableExists(factory, "b"));
    }

    [Fact]
    public void Migrate_is_idempotent_on_reruns()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, new IMigration[] { CreateTable(1, "a") });

        var first = migrator.Migrate();
        var second = migrator.Migrate();

        Assert.Equal(1, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public void Migrate_applies_only_pending_steps()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        new Migrator(factory, new IMigration[] { CreateTable(1, "a") }).Migrate();

        // Re-declaring step 1 must NOT re-run it (that would fail: table already exists).
        var version = new Migrator(factory, new IMigration[]
        {
            CreateTable(1, "a"),
            CreateTable(2, "b"),
        }).Migrate();

        Assert.Equal(2, version);
        Assert.True(TableExists(factory, "b"));
    }

    [Fact]
    public void Migrate_rolls_back_a_failing_step()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        var migrator = new Migrator(factory, new IMigration[]
        {
            CreateTable(1, "a"),
            new DelegateMigration(2, "boom", (c, t) =>
            {
                Execute(c, t, "CREATE TABLE b (id INTEGER);");
                throw new InvalidOperationException("boom");
            }),
        });

        Assert.Throws<InvalidOperationException>(() => migrator.Migrate());
        Assert.Equal(1, migrator.GetCurrentVersion());
        Assert.True(TableExists(factory, "a"));
        Assert.False(TableExists(factory, "b"));
    }

    [Fact]
    public void Constructor_rejects_non_contiguous_versions()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(Path.GetTempPath(), "memorymcp-unused.sqlite"));
        Assert.Throws<InvalidOperationException>(() =>
            new Migrator(factory, new IMigration[] { CreateTable(1, "a"), CreateTable(3, "c") }));
    }

    private static IMigration CreateTable(int version, string table) =>
        new DelegateMigration(version, $"create_{table}",
            (c, t) => Execute(c, t, $"CREATE TABLE {table} (id INTEGER);"));

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static bool TableExists(ISqliteConnectionFactory factory, string table)
    {
        using var connection = factory.Create();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private sealed class DelegateMigration : IMigration
    {
        private readonly Action<SqliteConnection, SqliteTransaction> _up;

        public DelegateMigration(int version, string name, Action<SqliteConnection, SqliteTransaction> up)
        {
            Version = version;
            Name = name;
            _up = up;
        }

        public int Version { get; }

        public string Name { get; }

        public void Up(SqliteConnection connection, SqliteTransaction transaction) => _up(connection, transaction);
    }
}
