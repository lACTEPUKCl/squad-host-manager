using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SquadHostManager.Service;

public sealed class Store : IDisposable
{
    public string DirectoryPath { get; }
    private readonly FileStream processLease;
    private readonly string connectionString;
    public Store(string directory)
    {
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        // One coordinator process per durable pool, including across containers.
        processLease = new FileStream(Path.Combine(directory, "coordinator.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "pool.db"), DefaultTimeout = 30, Pooling = false }.ToString();
        using var db = Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS documents (id TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS users (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, salt BLOB NOT NULL, hash BLOB NOT NULL, admin INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS invites (hash TEXT PRIMARY KEY, uses INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS sessions (hash TEXT PRIMARY KEY, user_id TEXT NOT NULL, expires TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS access_keys (user_id TEXT PRIMARY KEY, hash TEXT NOT NULL UNIQUE, created TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS disabled_users (id TEXT PRIMARY KEY);
            """;
        cmd.ExecuteNonQuery();
        using var migrationTransaction=db.BeginTransaction();using var migration=db.CreateCommand();migration.Transaction=migrationTransaction;
        migration.CommandText="DELETE FROM sessions WHERE expires<=$now AND NOT EXISTS(SELECT 1 FROM documents WHERE id='permanent-sessions-v1'); UPDATE sessions SET expires=$forever WHERE NOT EXISTS(SELECT 1 FROM documents WHERE id='permanent-sessions-v1'); INSERT OR IGNORE INTO documents VALUES ('permanent-sessions-v1','true')";
        migration.Parameters.AddWithValue("$now",DateTimeOffset.UtcNow.ToString("O"));migration.Parameters.AddWithValue("$forever",DateTimeOffset.MaxValue.ToString("O"));migration.ExecuteNonQuery();migrationTransaction.Commit();
    }
    public SqliteConnection Open() { var db = new SqliteConnection(connectionString); db.Open(); return db; }
    public T? Load<T>(string id)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT value FROM documents WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is string value ? JsonSerializer.Deserialize<T>(value) : default;
    }
    public void Save<T>(string id, T value)
    {
        using var db = Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO documents VALUES ($id,$value) ON CONFLICT(id) DO UPDATE SET value=excluded.value";
        cmd.Parameters.AddWithValue("$id", id); cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value)); cmd.ExecuteNonQuery();
    }
    public void Delete(string id){using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="DELETE FROM documents WHERE id=$id";cmd.Parameters.AddWithValue("$id",id);cmd.ExecuteNonQuery();}
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public void Dispose() => processLease.Dispose();
}
