using System.Globalization;
using Microsoft.Data.Sqlite;

namespace GrowCam.Infrastructure.Persistence;

/// <summary>
/// Opens a fresh connection per operation. That keeps the repositories
/// thread-safe without a pool; SQLite handles the tiny load easily.
/// </summary>
public sealed class SqliteDatabase
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS grows (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            sow_date TEXT NOT NULL,
            plant_count INTEGER NOT NULL,
            seed_json TEXT,
            archived_on TEXT,
            summary TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS milestones (
            grow_id INTEGER NOT NULL REFERENCES grows(id),
            type TEXT NOT NULL,
            status TEXT NOT NULL,
            reached_on TEXT NOT NULL,
            note TEXT NOT NULL DEFAULT '',
            by_ai INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (grow_id, type)
        );
        CREATE TABLE IF NOT EXISTS snapshots (
            id INTEGER PRIMARY KEY,
            grow_id INTEGER NOT NULL REFERENCES grows(id),
            taken_at TEXT NOT NULL,
            path TEXT NOT NULL,
            scheduled INTEGER NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_snapshots_grow_time ON snapshots(grow_id, taken_at);
        CREATE TABLE IF NOT EXISTS analyses (
            id INTEGER PRIMARY KEY,
            grow_id INTEGER NOT NULL REFERENCES grows(id),
            snapshot_id INTEGER NOT NULL REFERENCES snapshots(id),
            created_at TEXT NOT NULL,
            health_score INTEGER NOT NULL,
            headline TEXT NOT NULL,
            findings_json TEXT NOT NULL,
            suggested_milestone TEXT,
            milestone_note TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS diary (
            id INTEGER PRIMARY KEY,
            grow_id INTEGER NOT NULL REFERENCES grows(id),
            created_at TEXT NOT NULL,
            kind TEXT NOT NULL,
            text TEXT NOT NULL DEFAULT ''
        );
        CREATE TABLE IF NOT EXISTS reports (
            id INTEGER PRIMARY KEY,
            grow_id INTEGER NOT NULL REFERENCES grows(id),
            week_start TEXT NOT NULL,
            headline TEXT NOT NULL,
            body_json TEXT NOT NULL,
            needs_attention INTEGER NOT NULL DEFAULT 0,
            UNIQUE (grow_id, week_start)
        );
        CREATE TABLE IF NOT EXISTS seed_cache (
            query TEXT PRIMARY KEY,
            seeds_json TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS kv (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    private readonly string _connectionString;

    public SqliteDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        using var connection = Open();
        connection.Execute(Schema);
    }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        connection.Execute("PRAGMA foreign_keys = ON;");
        return connection;
    }
}

/// <summary>Tiny helpers so the repositories stay readable without an ORM.</summary>
internal static class SqliteExtensions
{
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";
    public const string DateFormat = "yyyy-MM-dd";

    public static int Execute(this SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        using var command = Create(connection, sql, args);
        return command.ExecuteNonQuery();
    }

    public static long Insert(this SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        using var command = Create(connection, sql + "; SELECT last_insert_rowid();", args);
        return (long)command.ExecuteScalar()!;
    }

    public static object? Scalar(this SqliteConnection connection, string sql, params (string Name, object? Value)[] args)
    {
        using var command = Create(connection, sql, args);
        return command.ExecuteScalar();
    }

    public static List<T> Query<T>(this SqliteConnection connection, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var command = Create(connection, sql, args);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read())
            result.Add(map(reader));
        return result;
    }

    public static string Str(this SqliteDataReader reader, string column) => reader.GetString(reader.GetOrdinal(column));

    public static string? StrOrNull(this SqliteDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    public static long Long(this SqliteDataReader reader, string column) => reader.GetInt64(reader.GetOrdinal(column));

    public static string Ts(DateTime value) => value.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static DateTime ParseTs(string value) => DateTime.ParseExact(value, TimestampFormat, CultureInfo.InvariantCulture);

    public static string Day(DateOnly value) => value.ToString(DateFormat, CultureInfo.InvariantCulture);

    public static DateOnly ParseDay(string value) => DateOnly.ParseExact(value, DateFormat, CultureInfo.InvariantCulture);

    private static SqliteCommand Create(SqliteConnection connection, string sql, (string Name, object? Value)[] args)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in args)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }
}
