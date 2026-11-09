using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GrowCam.Application;
using GrowCam.Domain;
using GrowCam.Infrastructure.Ai;

namespace GrowCam.Tests;

/// <summary>Replays prepared API responses and records the requests.</summary>
public sealed class ScriptedHandler(params string[] responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);

    public List<JsonObject> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json"),
        };
    }
}

public class ClaudeAdapterTests
{
    private static readonly GrowContext Context = new("Test", 5, Phase.Germination, null, [MilestoneType.Sprouted], []);

    private static string ToolUse(string name, string inputJson) =>
        "{\"stop_reason\":\"tool_use\",\"content\":[{\"type\":\"tool_use\",\"name\":\"" + name + "\",\"input\":" + inputJson + "}]}";

    [Fact]
    public async Task AnalyzerSendsBothImagesAndForcesTool()
    {
        var handler = new ScriptedHandler(ToolUse("report_analysis",
            """{"health_score":90,"headline":"Gut","findings":[{"severity":"bogus","title":"A","advice":"B"}],"suggested_milestone":"sprouted"}"""));
        var analyzer = new ClaudePlantAnalyzer(new ClaudeClient(new HttpClient(handler), "key", "model"));

        var result = await analyzer.AnalyzeAsync([1, 2], [3, 4], Context);

        var request = handler.Requests[0];
        Assert.Equal("report_analysis", (string?)request["tool_choice"]?["name"]);
        var images = request["messages"]![0]!["content"]!.AsArray().Count(b => (string?)b?["type"] == "image");
        Assert.Equal(2, images);
        Assert.Equal(MilestoneType.Sprouted, result.SuggestedMilestone);
        Assert.Equal(Severity.Minor, result.Findings[0].Severity); // unknown severity falls back
    }

    [Fact]
    public async Task SeedSearchContinuesAfterPauseAndTextAnswer()
    {
        var handler = new ScriptedHandler(
            """{"stop_reason":"pause_turn","content":[{"type":"server_tool_use","name":"web_search","input":{}}]}""",
            """{"stop_reason":"end_turn","content":[{"type":"text","text":"Gefunden!"}]}""",
            ToolUse("report_seeds", """{"seeds":[{"name":"Northern Lights Auto","seed_type":"autoflower","weeks_to_harvest":10}]}"""));
        var catalog = new ClaudeSeedCatalog(new ClaudeClient(new HttpClient(handler), "key", "model"));

        var seeds = await catalog.SearchAsync("northern lights");

        Assert.Equal(SeedType.Autoflower, seeds[0].Type);
        Assert.Equal(10, seeds[0].WeeksToHarvest);
        // paused turn is sent back unchanged, without an extra user message
        Assert.Equal("assistant", (string?)handler.Requests[1]["messages"]!.AsArray().Last()!["role"]);
        Assert.Equal("report_seeds", (string?)handler.Requests[2]["tool_choice"]?["name"]);
    }

    [Fact]
    public void ParsingIsTolerant()
    {
        var result = Parsing.Analysis(new JsonObject { ["suggested_milestone"] = "none" });
        Assert.Null(result.SuggestedMilestone);
        Assert.Equal(0, result.HealthScore);
        Assert.Empty(result.Findings);
    }
}
