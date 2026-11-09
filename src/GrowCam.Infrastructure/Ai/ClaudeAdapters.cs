using System.Text.Json.Nodes;
using GrowCam.Application;
using GrowCam.Domain;

namespace GrowCam.Infrastructure.Ai;

/// <summary>Prompts and tool schemas. Structured output via forced tool calls.</summary>
internal static class Prompts
{
    public const string Style = """
        You are the plant assistant of a small home grow-tent monitor.
        Everything you write is shown to an older German reader with poor eyesight and
        no gardening jargon: write short, plain German sentences (du-Form), no
        abbreviations, no Latin names. Be calm and honest – only report a problem
        when you actually see one. Never invent details you cannot see in the photo.
        """;

    public const string AnalysisTool = """
        {
          "name": "report_analysis",
          "description": "Report the result of looking at the plant photo.",
          "input_schema": {
            "type": "object",
            "properties": {
              "health_score": { "type": "integer", "minimum": 0, "maximum": 100 },
              "headline": { "type": "string", "description": "One short sentence: how are the plants doing overall?" },
              "findings": {
                "type": "array", "maxItems": 3,
                "items": {
                  "type": "object",
                  "properties": {
                    "severity": { "type": "string", "enum": ["ok", "minor", "watch"] },
                    "title": { "type": "string", "description": "What you see, max. 8 words." },
                    "advice": { "type": "string", "description": "What to do, one or two short sentences." }
                  },
                  "required": ["severity", "title", "advice"]
                }
              },
              "suggested_milestone": {
                "type": "string",
                "enum": ["none", "sprouted", "vegetative", "flowering", "harvest"],
                "description": "Only if the photo clearly shows a stage that is listed as still open."
              },
              "milestone_note": { "type": "string", "description": "What you saw that indicates the stage." }
            },
            "required": ["health_score", "headline", "findings", "suggested_milestone"]
          }
        }
        """;

    public const string ReportTool = """
        {
          "name": "report_week",
          "description": "Report the weekly summary.",
          "input_schema": {
            "type": "object",
            "properties": {
              "headline": { "type": "string", "description": "One sentence: how was the week?" },
              "happened": { "type": "array", "maxItems": 4, "items": { "type": "string" } },
              "next_steps": { "type": "array", "maxItems": 3, "items": { "type": "string" } },
              "needs_attention": { "type": "boolean" }
            },
            "required": ["headline", "happened", "next_steps", "needs_attention"]
          }
        }
        """;

    public const string SeedTool = """
        {
          "name": "report_seeds",
          "description": "Report the strains that match the search, best match first.",
          "input_schema": {
            "type": "object",
            "properties": {
              "seeds": {
                "type": "array", "maxItems": 3,
                "items": {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Strain name incl. breeder if known" },
                    "seed_type": { "type": "string", "enum": ["autoflower", "photoperiod", "unknown"] },
                    "weeks_to_harvest": { "type": ["integer", "null"], "description": "Autoflower: sowing to harvest" },
                    "flowering_weeks": { "type": ["integer", "null"], "description": "Photoperiod: flowering time" },
                    "height": { "type": "string", "description": "Indoor height, e.g. 'ca. 60–100 cm'" },
                    "light_schedule": { "type": "string", "description": "e.g. '18/6 die ganze Zeit'" },
                    "difficulty": { "type": "string", "description": "leicht / mittel / schwer" }
                  },
                  "required": ["name", "seed_type"]
                }
              }
            },
            "required": ["seeds"]
          }
        }
        """;

    public static string MilestoneHint(MilestoneType type) => type switch
    {
        MilestoneType.Sprouted => "sprouted = a seedling has broken through the soil",
        MilestoneType.Vegetative => "vegetative = several true (serrated) leaf pairs, plant grows steadily",
        MilestoneType.Flowering => "flowering = first white pistils/hairs at the nodes or top",
        MilestoneType.Harvest => "harvest = buds look mature, most pistils brown/curled",
        _ => type.ToString(),
    };

    public static string Describe(GrowContext c)
    {
        var lines = new List<string>
        {
            $"Grow: {c.GrowName}, day {c.DayCount} since sowing, current phase: {c.Phase}.",
        };
        if (c.Seed is { } seed)
            lines.Add($"Strain: {seed.Name} ({seed.Type}); weeks to harvest: {seed.WeeksToHarvest?.ToString() ?? "?"}, flowering weeks: {seed.FloweringWeeks?.ToString() ?? "?"}.");
        if (c.OpenMilestones.Count > 0)
            lines.Add("Stages not reached yet: " + string.Join("; ", c.OpenMilestones.Select(MilestoneHint)));
        if (c.RecentDiary.Count > 0)
            lines.Add("Recent diary: " + string.Join(" | ", c.RecentDiary));
        if (c.PreviousHeadline.Length > 0)
            lines.Add($"Previous assessment: {c.PreviousHeadline}");
        if (c.UserNote.Length > 0)
            lines.Add($"Note from the grower: {c.UserNote}");
        return string.Join("\n", lines);
    }

    public static JsonNode Tool(string json) => JsonNode.Parse(json)!;

    public static JsonObject ForceTool(string name) => new() { ["type"] = "tool", ["name"] = name };
}

public sealed class ClaudePlantAnalyzer(ClaudeClient client) : IPlantAnalyzer
{
    public async Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default)
    {
        var content = new JsonArray();
        if (previousImage is not null)
        {
            content.Add(ClaudeClient.TextBlock("Photo from the previous check:"));
            content.Add(ClaudeClient.ImageBlock(previousImage));
        }
        content.Add(ClaudeClient.TextBlock("Photo from now:"));
        content.Add(ClaudeClient.ImageBlock(image));
        content.Add(ClaudeClient.TextBlock(Prompts.Describe(context)
            + "\n\nLook for deficiencies, pests, mould, light/heat stress, over- or underwatering, "
            + "and compare with the previous photo if there is one. Then call report_analysis."));

        var response = await client.CreateAsync(new JsonObject
        {
            ["max_tokens"] = 1200,
            ["system"] = Prompts.Style,
            ["tools"] = new JsonArray(Prompts.Tool(Prompts.AnalysisTool)),
            ["tool_choice"] = Prompts.ForceTool("report_analysis"),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        }, ct);

        var input = ClaudeClient.ToolInput(response, "report_analysis")
                    ?? throw new AnalyzerException("Die KI hat keine Auswertung geliefert.");
        return Parsing.Analysis(input);
    }

    public async Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default)
    {
        var content = new JsonArray();
        for (var i = 0; i < images.Count; i++)
        {
            content.Add(ClaudeClient.TextBlock($"Day {i + 1}:"));
            content.Add(ClaudeClient.ImageBlock(images[i]));
        }
        content.Add(ClaudeClient.TextBlock(Prompts.Describe(context)
            + "\nDaily assessments this week: " + (analyses.Count > 0 ? string.Join(" | ", analyses) : "none")
            + "\n\nSummarize the week for the grower and call report_week."));

        var response = await client.CreateAsync(new JsonObject
        {
            ["max_tokens"] = 1200,
            ["system"] = Prompts.Style,
            ["tools"] = new JsonArray(Prompts.Tool(Prompts.ReportTool)),
            ["tool_choice"] = Prompts.ForceTool("report_week"),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = content }),
        }, ct);

        var input = ClaudeClient.ToolInput(response, "report_week")
                    ?? throw new AnalyzerException("Die KI hat keinen Wochenbericht geliefert.");
        return new ReportDraft(
            Parsing.Str(input, "headline"),
            Parsing.StrList(input, "happened"),
            Parsing.StrList(input, "next_steps"),
            input["needs_attention"]?.GetValueKind() == System.Text.Json.JsonValueKind.True);
    }
}

/// <summary>Looks up strain data using Claude's server-side web search tool.</summary>
public sealed class ClaudeSeedCatalog(ClaudeClient client) : ISeedCatalog
{
    private const int MaxRounds = 4;

    public async Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        var messages = new JsonArray(new JsonObject
        {
            ["role"] = "user",
            ["content"] = $"Find grow data for the cannabis seed strain '{query}' (indoor grow tent). "
                          + "Search the web (breeder or seed shop pages), then call report_seeds with up to three "
                          + "matching strains. Write height, light schedule and difficulty in short German.",
        });
        JsonObject toolChoice = new() { ["type"] = "auto" };

        for (var round = 0; round < MaxRounds; round++)
        {
            var response = await client.CreateAsync(new JsonObject
            {
                ["max_tokens"] = 2000,
                ["system"] = Prompts.Style,
                ["tools"] = new JsonArray(
                    new JsonObject { ["type"] = "web_search_20250305", ["name"] = "web_search", ["max_uses"] = 3 },
                    Prompts.Tool(Prompts.SeedTool)),
                ["tool_choice"] = toolChoice.DeepClone(),
                ["messages"] = messages.DeepClone(),
            }, ct);

            if (ClaudeClient.ToolInput(response, "report_seeds") is { } input)
            {
                return (input["seeds"]?.AsArray() ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(Parsing.Seed)
                    .Where(s => s.Name.Length > 0)
                    .ToList();
            }

            // A paused server-side search continues by sending the turn back unchanged;
            // a plain text answer gets a nudge to call the tool.
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = response["content"]?.DeepClone() });
            if ((string?)response["stop_reason"] != "pause_turn")
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Bitte jetzt report_seeds aufrufen." });
                toolChoice = Prompts.ForceTool("report_seeds");
            }
        }
        throw new AnalyzerException("Die Sorten-Suche hat nichts geliefert.");
    }
}

/// <summary>Tolerant parsing of tool inputs – models occasionally omit optional fields.</summary>
public static class Parsing
{
    public static AnalysisResult Analysis(JsonObject input)
    {
        var findings = (input["findings"]?.AsArray() ?? new JsonArray())
            .OfType<JsonObject>()
            .Take(3)
            .Select(f => new Finding(ParseSeverity(Str(f, "severity")), Str(f, "title"), Str(f, "advice")))
            .ToList();
        return new AnalysisResult(
            Int(input, "health_score") ?? 0,
            Str(input, "headline"),
            findings,
            ParseMilestone(Str(input, "suggested_milestone")),
            Str(input, "milestone_note"));
    }

    public static SeedInfo Seed(JsonObject s) => new(
        Str(s, "name"),
        Str(s, "seed_type") switch
        {
            "autoflower" => SeedType.Autoflower,
            "photoperiod" => SeedType.Photoperiod,
            _ => SeedType.Unknown,
        },
        Int(s, "weeks_to_harvest"),
        Int(s, "flowering_weeks"),
        Str(s, "height"),
        Str(s, "light_schedule"),
        Str(s, "difficulty"));

    public static Severity ParseSeverity(string value) => value switch
    {
        "ok" => Severity.Ok,
        "watch" => Severity.Watch,
        _ => Severity.Minor,
    };

    public static MilestoneType? ParseMilestone(string value) => value switch
    {
        "sprouted" => MilestoneType.Sprouted,
        "vegetative" => MilestoneType.Vegetative,
        "flowering" => MilestoneType.Flowering,
        "harvest" => MilestoneType.Harvest,
        _ => null,
    };

    public static string Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    public static int? Int(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static IReadOnlyList<string> StrList(JsonObject o, string key) =>
        (o[key]?.AsArray() ?? new JsonArray())
            .OfType<JsonValue>()
            .Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : "")
            .Where(s => s.Length > 0)
            .ToList();
}
