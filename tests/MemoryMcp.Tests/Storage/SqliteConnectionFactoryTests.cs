using MemoryMcp.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MemoryMcp.Tests.Storage;

public class SqliteConnectionFactoryTests
{
    [Fact]
    public void Create_enables_wal_busy_timeout_and_foreign_keys()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        using var connection = factory.Create();

        Assert.Equal("wal", ReadString(connection, "PRAGMA journal_mode;"), ignoreCase: true);
        Assert.Equal(1L, ReadLong(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(5000L, ReadLong(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void Create_writes_wal_sidecar_on_first_write()
    {
        using var temp = new TempDatabase();
        var factory = new SqliteConnectionFactory(temp.FilePath);
        using var connection = factory.Create();

        Execute(connection, "CREATE TABLE probe (id INTEGER);");
        Execute(connection, "INSERT INTO probe (id) VALUES (1);");

        Assert.True(File.Exists(temp.FilePath + "-wal"));
    }

    [Fact]
    public void Constructor_creates_missing_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "memorymcp-tests", Guid.NewGuid().ToString("N"), "nested");
        var dbPath = Path.Combine(dir, "x.sqlite");
        try
        {
            _ = new SqliteConnectionFactory(dbPath);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void Constructor_rejects_empty_path()
    {
        Assert.Throws<ArgumentException>(() => new SqliteConnectionFactory("  "));
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ReadString(SqliteConnection connection, string sql) =>
        Convert.ToString(ReadScalar(connection, sql)) ?? string.Empty;

    private static long ReadLong(SqliteConnection connection, string sql) =>
        Convert.ToInt64(ReadScalar(connection, sql));

    private static object? ReadScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}
