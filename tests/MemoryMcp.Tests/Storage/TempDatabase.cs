using Microsoft.Data.Sqlite;

namespace MemoryMcp.Tests.Storage;

/// <summary>Creates a throwaway SQLite database in a temp directory and cleans it up on dispose.</summary>
internal sealed class TempDatabase : IDisposable
{
    private readonly string _directory;

    public TempDatabase()
    {
        _directory = Path.Combine(Path.GetTempPath(), "memorymcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "test.sqlite");
    }

    public string FilePath { get; }

    public void Dispose()
    {
        // Release pooled handles so the files can be deleted on Windows/macOS.
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
