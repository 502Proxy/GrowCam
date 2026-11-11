using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GrowCam.Application;
using GrowCam.Domain;
using GrowCam.Infrastructure.Ai;

namespace GrowCam.Tests;

public class OpenAiAdapterTests
{
    private static readonly GrowContext Context = new("Test", 5, Phase.Germination, null, [MilestoneType.Sprouted], []);

    private static string FunctionCall(string name, string argumentsJson) =>
        new JsonObject
        {
            ["output"] = new JsonArray(
                new JsonObject { ["type"] = "reasoning", ["summary"] = new JsonArray() },
                new JsonObject { ["type"] = "function_call", ["name"] = name, ["call_id"] = "c1", ["arguments"] = argumentsJson }),
        }.ToJsonString();

    private static OpenAiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler), "sk-test", "gpt-test");

    [Fact]
    public async Task AnalyzerSendsBothImagesAndForcesFunction()
    {
        var handler = new ScriptedHandler(FunctionCall("report_analysis",
            """{"health_score":77,"headline":"Gut","findings":[{"severity":"watch","title":"A","advice":"B"}],"suggested_milestone":"sprouted"}"""));

        var result = await new OpenAiPlantAnalyzer(Client(handler)).AnalyzeAsync([1, 2], [3, 4], Context);

        var request = handler.Requests[0];
        Assert.Equal("gpt-test", (string?)request["model"]);
        Assert.False((bool)request["store"]!);
        Assert.Equal("report_analysis", (string?)request["tool_choice"]?["name"]);
        Assert.Equal("function", (string?)request["tools"]![0]!["type"]);
        Assert.NotNull(request["tools"]![0]!["parameters"]?["properties"]?["health_score"]);
        var images = request["input"]![0]!["content"]!.AsArray()
            .Where(p => (string?)p?["type"] == "input_image")
            .Select(p => (string)p!["image_url"]!)
            .ToList();
        Assert.Equal(2, images.Count);
        Assert.StartsWith("data:image/jpeg;base64,", images[0]);
        Assert.Equal(77, result.HealthScore);
        Assert.Equal(Severity.Watch, result.Findings[0].Severity);
        Assert.Equal(MilestoneType.Sprouted, result.SuggestedMilestone);
    }

    [Fact]
    public async Task UploadedPhotoIsDescribedAsSuch()
    {
        var handler = new ScriptedHandler(FunctionCall("report_analysis", """{"health_score":50,"headline":"x","findings":[]}"""));

        await new OpenAiPlantAnalyzer(Client(handler)).AnalyzeAsync([1], null, Context with { IsUploadedPhoto = true, UserNote = "Flecken?" });

        var text = string.Join(" ", handler.Requests[0]["input"]![0]!["content"]!.AsArray().Select(p => (string?)p?["text"]));
        Assert.Contains("uploaded", text);
        Assert.Contains("Flecken?", text);
        Assert.DoesNotContain("compare with the previous photo", text);
    }

    [Fact]
    public async Task WeeklyReportIsParsed()
    {
        var handler = new ScriptedHandler(FunctionCall("report_week",
            """{"headline":"Ruhig","happened":["wuchs"],"next_steps":["gießen"],"needs_attention":true}"""));

        var draft = await new OpenAiPlantAnalyzer(Client(handler)).WeeklyReportAsync([[1], [2]], Context, []);

        Assert.Equal("Ruhig", draft.Headline);
        Assert.True(draft.NeedsAttention);
        Assert.Equal(2, handler.Requests[0]["input"]![0]!["content"]!.AsArray().Count(p => (string?)p?["type"] == "input_image"));
    }

    [Fact]
    public async Task SeedSearchUsesWebSearchAndFallsBackToForcedCall()
    {
        var textOnly = """{"output":[{"type":"web_search_call","status":"completed"},{"type":"message","content":[{"type":"output_text","text":"Northern Lights Auto: 10 Wochen"}]}]}""";
        var handler = new ScriptedHandler(textOnly, FunctionCall("report_seeds",
            """{"seeds":[{"name":"Northern Lights Auto","seed_type":"autoflower","weeks_to_harvest":10}]}"""));

        var seeds = await new OpenAiSeedCatalog(Client(handler)).SearchAsync("northern lights");

        Assert.Equal("web_search", (string?)handler.Requests[0]["tools"]![0]!["type"]);
        Assert.Equal("report_seeds", (string?)handler.Requests[1]["tool_choice"]?["name"]);
        Assert.Contains("10 Wochen", handler.Requests[1].ToJsonString());
        Assert.Equal(SeedType.Autoflower, seeds[0].Type);
        Assert.Equal(10, seeds[0].WeeksToHarvest);
    }

    [Fact]
    public async Task QuotaErrorBecomesReadableMessage()
    {
        var handler = new FixedHandler(HttpStatusCode.TooManyRequests,
            """{"error":{"message":"You exceeded your current quota","type":"insufficient_quota","code":"insufficient_quota"}}""");

        var error = await Assert.ThrowsAsync<AnalyzerException>(() =>
            new OpenAiPlantAnalyzer(Client(handler)).AnalyzeAsync([1], null, Context));

        Assert.Contains("Guthaben", error.Message);
    }

    [Fact]
    public async Task InvalidKeyBecomesReadableMessage()
    {
        var handler = new FixedHandler(HttpStatusCode.Unauthorized, """{"error":{"code":"invalid_api_key"}}""");

        var error = await Assert.ThrowsAsync<AnalyzerException>(() =>
            new OpenAiPlantAnalyzer(Client(handler)).AnalyzeAsync([1], null, Context));

        Assert.Contains("ungültig", error.Message);
    }

    private sealed class FixedHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
