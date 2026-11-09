using System.Text.Json;
using System.Text.Json.Serialization;
using GrowCam.Application;
using GrowCam.Domain;
using Microsoft.Data.Sqlite;
using static GrowCam.Infrastructure.Persistence.SqliteExtensions;

namespace GrowCam.Infrastructure.Persistence;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}

public sealed class SqliteGrowRepository(SqliteDatabase db) : IGrowRepository
{
    public Grow? Active()
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM grows WHERE archived_on IS NULL ORDER BY id DESC LIMIT 1", r => Load(con, r)).FirstOrDefault();
    }

    public Grow? Get(long id)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM grows WHERE id = $id", r => Load(con, r), ("$id", id)).FirstOrDefault();
    }

    public IReadOnlyList<Grow> Archived()
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM grows WHERE archived_on IS NOT NULL ORDER BY sow_date DESC", r => Load(con, r));
    }

    public Grow Add(Grow grow)
    {
        using var con = db.Open();
        var id = con.Insert(
            "INSERT INTO grows (name, sow_date, plant_count, seed_json) VALUES ($name, $sow, $plants, $seed)",
            ("$name", grow.Name),
            ("$sow", Day(grow.SowDate)),
            ("$plants", grow.PlantCount),
            ("$seed", grow.Seed is null ? null : Json.Write(grow.Seed)));
        return Get(id)!;
    }

    public void Archive(long growId, DateOnly on, string summary)
    {
        using var con = db.Open();
        con.Execute("UPDATE grows SET archived_on = $on, summary = $summary WHERE id = $id",
            ("$on", Day(on)), ("$summary", summary), ("$id", growId));
    }

    public void SetMilestone(long growId, Milestone milestone)
    {
        using var con = db.Open();
        con.Execute(
            """
            INSERT INTO milestones (grow_id, type, status, reached_on, note, by_ai)
            VALUES ($grow, $type, $status, $on, $note, $ai)
            ON CONFLICT (grow_id, type) DO UPDATE SET
              status = excluded.status, reached_on = excluded.reached_on,
              note = excluded.note, by_ai = excluded.by_ai
            """,
            ("$grow", growId),
            ("$type", milestone.Type.ToString()),
            ("$status", milestone.Status.ToString()),
            ("$on", Day(milestone.ReachedOn)),
            ("$note", milestone.Note),
            ("$ai", milestone.ByAi ? 1 : 0));
    }

    public void RemoveMilestone(long growId, MilestoneType type)
    {
        using var con = db.Open();
        con.Execute("DELETE FROM milestones WHERE grow_id = $grow AND type = $type", ("$grow", growId), ("$type", type.ToString()));
    }

    private static Grow Load(SqliteConnection con, SqliteDataReader row)
    {
        var id = row.Long("id");
        var milestones = con.Query(
                "SELECT * FROM milestones WHERE grow_id = $id",
                m => new Milestone(
                    Enum.Parse<MilestoneType>(m.Str("type")),
                    Enum.Parse<MilestoneStatus>(m.Str("status")),
                    ParseDay(m.Str("reached_on")),
                    m.Str("note"),
                    m.Long("by_ai") == 1),
                ("$id", id))
            .OrderBy(m => m.Type)
            .ToList();
        var seedJson = row.StrOrNull("seed_json");
        var archivedOn = row.StrOrNull("archived_on");
        return new Grow
        {
            Id = id,
            Name = row.Str("name"),
            SowDate = ParseDay(row.Str("sow_date")),
            PlantCount = (int)row.Long("plant_count"),
            Seed = seedJson is null ? null : Json.Read<SeedInfo>(seedJson),
            ArchivedOn = archivedOn is null ? null : ParseDay(archivedOn),
            Summary = row.Str("summary"),
            Milestones = milestones,
        };
    }
}

public sealed class SqliteSnapshotRepository(SqliteDatabase db) : ISnapshotRepository
{
    public Snapshot Add(Snapshot snapshot)
    {
        using var con = db.Open();
        var id = con.Insert(
            "INSERT INTO snapshots (grow_id, taken_at, path, scheduled) VALUES ($grow, $at, $path, $scheduled)",
            ("$grow", snapshot.GrowId), ("$at", Ts(snapshot.TakenAt)), ("$path", snapshot.Path), ("$scheduled", snapshot.Scheduled ? 1 : 0));
        return snapshot with { Id = id };
    }

    public Snapshot? Get(long id)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM snapshots WHERE id = $id", Map, ("$id", id)).FirstOrDefault();
    }

    public Snapshot? Latest(long growId)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM snapshots WHERE grow_id = $grow ORDER BY taken_at DESC, id DESC LIMIT 1", Map, ("$grow", growId)).FirstOrDefault();
    }

    public IReadOnlyList<Snapshot> ScheduledBetween(long growId, DateTime start, DateTime end)
    {
        using var con = db.Open();
        return con.Query(
            "SELECT * FROM snapshots WHERE grow_id = $grow AND scheduled = 1 AND taken_at >= $start AND taken_at < $end ORDER BY taken_at",
            Map, ("$grow", growId), ("$start", Ts(start)), ("$end", Ts(end)));
    }

    private static Snapshot Map(SqliteDataReader r) =>
        new(r.Long("id"), r.Long("grow_id"), ParseTs(r.Str("taken_at")), r.Str("path"), r.Long("scheduled") == 1);
}

public sealed class SqliteAnalysisRepository(SqliteDatabase db) : IAnalysisRepository
{
    public Analysis Add(Analysis analysis)
    {
        using var con = db.Open();
        var id = con.Insert(
            """
            INSERT INTO analyses (grow_id, snapshot_id, created_at, health_score, headline, findings_json, suggested_milestone, milestone_note)
            VALUES ($grow, $snap, $at, $score, $headline, $findings, $milestone, $note)
            """,
            ("$grow", analysis.GrowId),
            ("$snap", analysis.SnapshotId),
            ("$at", Ts(analysis.CreatedAt)),
            ("$score", analysis.HealthScore),
            ("$headline", analysis.Headline),
            ("$findings", Json.Write(analysis.Findings)),
            ("$milestone", analysis.SuggestedMilestone?.ToString()),
            ("$note", analysis.MilestoneNote));
        return analysis with { Id = id };
    }

    public Analysis? Latest(long growId)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM analyses WHERE grow_id = $grow ORDER BY created_at DESC, id DESC LIMIT 1", Map, ("$grow", growId)).FirstOrDefault();
    }

    public IReadOnlyList<Analysis> Between(long growId, DateTime start, DateTime end)
    {
        using var con = db.Open();
        return con.Query(
            "SELECT * FROM analyses WHERE grow_id = $grow AND created_at >= $start AND created_at < $end ORDER BY created_at",
            Map, ("$grow", growId), ("$start", Ts(start)), ("$end", Ts(end)));
    }

    private static Analysis Map(SqliteDataReader r)
    {
        var milestone = r.StrOrNull("suggested_milestone");
        return new Analysis(
            r.Long("id"),
            r.Long("grow_id"),
            r.Long("snapshot_id"),
            ParseTs(r.Str("created_at")),
            (int)r.Long("health_score"),
            r.Str("headline"),
            Json.Read<List<Finding>>(r.Str("findings_json")),
            milestone is null ? null : Enum.Parse<MilestoneType>(milestone),
            r.Str("milestone_note"));
    }
}

public sealed class SqliteDiaryRepository(SqliteDatabase db) : IDiaryRepository
{
    public DiaryEntry Add(DiaryEntry entry)
    {
        using var con = db.Open();
        var id = con.Insert(
            "INSERT INTO diary (grow_id, created_at, kind, text) VALUES ($grow, $at, $kind, $text)",
            ("$grow", entry.GrowId), ("$at", Ts(entry.CreatedAt)), ("$kind", entry.Kind.ToString()), ("$text", entry.Text));
        return entry with { Id = id };
    }

    public IReadOnlyList<DiaryEntry> Recent(long growId, int limit)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM diary WHERE grow_id = $grow ORDER BY created_at DESC, id DESC LIMIT $limit",
            Map, ("$grow", growId), ("$limit", limit));
    }

    public IReadOnlyList<DiaryEntry> Between(long growId, DateTime start, DateTime end)
    {
        using var con = db.Open();
        return con.Query(
            "SELECT * FROM diary WHERE grow_id = $grow AND created_at >= $start AND created_at < $end ORDER BY created_at",
            Map, ("$grow", growId), ("$start", Ts(start)), ("$end", Ts(end)));
    }

    public void Delete(long id)
    {
        using var con = db.Open();
        con.Execute("DELETE FROM diary WHERE id = $id", ("$id", id));
    }

    private static DiaryEntry Map(SqliteDataReader r) =>
        new(r.Long("id"), r.Long("grow_id"), ParseTs(r.Str("created_at")), Enum.Parse<DiaryKind>(r.Str("kind")), r.Str("text"));
}

public sealed class SqliteReportRepository(SqliteDatabase db) : IReportRepository
{
    private sealed record Body(IReadOnlyList<string> Happened, IReadOnlyList<string> Done, IReadOnlyList<string> NextSteps, IReadOnlyList<long> SnapshotIds);

    public WeeklyReport Add(WeeklyReport report)
    {
        using var con = db.Open();
        var id = con.Insert(
            "INSERT INTO reports (grow_id, week_start, headline, body_json, needs_attention) VALUES ($grow, $week, $headline, $body, $attention)",
            ("$grow", report.GrowId),
            ("$week", Day(report.WeekStart)),
            ("$headline", report.Headline),
            ("$body", Json.Write(new Body(report.Happened, report.Done, report.NextSteps, report.SnapshotIds))),
            ("$attention", report.NeedsAttention ? 1 : 0));
        return report with { Id = id };
    }

    public IReadOnlyList<WeeklyReport> ForGrow(long growId)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM reports WHERE grow_id = $grow ORDER BY week_start DESC", Map, ("$grow", growId));
    }

    public WeeklyReport? Get(long id)
    {
        using var con = db.Open();
        return con.Query("SELECT * FROM reports WHERE id = $id", Map, ("$id", id)).FirstOrDefault();
    }

    public bool Exists(long growId, DateOnly weekStart)
    {
        using var con = db.Open();
        return con.Scalar("SELECT 1 FROM reports WHERE grow_id = $grow AND week_start = $week",
            ("$grow", growId), ("$week", Day(weekStart))) is not null;
    }

    private static WeeklyReport Map(SqliteDataReader r)
    {
        var body = Json.Read<Body>(r.Str("body_json"));
        return new WeeklyReport(
            r.Long("id"), r.Long("grow_id"), ParseDay(r.Str("week_start")), r.Str("headline"),
            body.Happened, body.Done, body.NextSteps, r.Long("needs_attention") == 1, body.SnapshotIds);
    }
}

public sealed class SqliteSeedCache(SqliteDatabase db) : ISeedCache
{
    public IReadOnlyList<SeedInfo>? Get(string key)
    {
        using var con = db.Open();
        return con.Scalar("SELECT seeds_json FROM seed_cache WHERE query = $q", ("$q", key)) is string json
            ? Json.Read<List<SeedInfo>>(json)
            : null;
    }

    public void Put(string key, IReadOnlyList<SeedInfo> seeds)
    {
        using var con = db.Open();
        con.Execute("INSERT OR REPLACE INTO seed_cache (query, seeds_json) VALUES ($q, $json)", ("$q", key), ("$json", Json.Write(seeds)));
    }
}

public sealed class SqliteKeyValueStore(SqliteDatabase db) : IKeyValueStore
{
    public string? Get(string key)
    {
        using var con = db.Open();
        return con.Scalar("SELECT value FROM kv WHERE key = $k", ("$k", key)) as string;
    }

    public void Set(string key, string value)
    {
        using var con = db.Open();
        con.Execute("INSERT OR REPLACE INTO kv (key, value) VALUES ($k, $v)", ("$k", key), ("$v", value));
    }
}
