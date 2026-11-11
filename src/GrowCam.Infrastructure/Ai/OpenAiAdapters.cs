using System.Text.Json.Nodes;
using GrowCam.Application;
using GrowCam.Domain;

namespace GrowCam.Infrastructure.Ai;

/// <summary>Plant check and weekly report with ChatGPT. Same prompts and schemas as the Claude adapter.</summary>
public sealed class OpenAiPlantAnalyzer(OpenAiClient client) : IPlantAnalyzer
{
    // Reasoning models spend part of this budget on thinking, so leave room.
    private const int MaxOutputTokens = 6000;

    public async Task<AnalysisResult> AnalyzeAsync(byte[] image, byte[]? previousImage, GrowContext context, CancellationToken ct = default)
    {
        var content = new JsonArray();
        if (previousImage is not null)
        {
            content.Add(OpenAiClient.TextPart("Photo from the previous check:"));
            content.Add(OpenAiClient.ImagePart(previousImage));
        }
        content.Add(OpenAiClient.TextPart(context.IsUploadedPhoto ? "Photo from the grower:" : "Photo from now:"));
        content.Add(OpenAiClient.ImagePart(image));
        content.Add(OpenAiClient.TextPart(Prompts.AnalysisRequest(context, previousImage is not null)));

        var response = await client.CreateAsync(new JsonObject
        {
            ["instructions"] = Prompts.Style,
            ["input"] = OpenAiClient.UserMessage(content),
            ["tools"] = new JsonArray(OpenAiClient.FunctionTool(Prompts.AnalysisTool)),
            ["tool_choice"] = OpenAiClient.ForceFunction("report_analysis"),
            ["max_output_tokens"] = MaxOutputTokens,
        }, ct);

        var input = OpenAiClient.FunctionArguments(response, "report_analysis")
                    ?? throw new AnalyzerException("Die KI hat keine Auswertung geliefert.");
        return Parsing.Analysis(input);
    }

    public async Task<ReportDraft> WeeklyReportAsync(IReadOnlyList<byte[]> images, GrowContext context, IReadOnlyList<string> analyses, CancellationToken ct = default)
    {
        var content = new JsonArray();
        for (var i = 0; i < images.Count; i++)
        {
            content.Add(OpenAiClient.TextPart($"Day {i + 1}:"));
            content.Add(OpenAiClient.ImagePart(images[i]));
        }
        content.Add(OpenAiClient.TextPart(Prompts.ReportRequest(context, analyses)));

        var response = await client.CreateAsync(new JsonObject
        {
            ["instructions"] = Prompts.Style,
            ["input"] = OpenAiClient.UserMessage(content),
            ["tools"] = new JsonArray(OpenAiClient.FunctionTool(Prompts.ReportTool)),
            ["tool_choice"] = OpenAiClient.ForceFunction("report_week"),
            ["max_output_tokens"] = MaxOutputTokens,
        }, ct);

        var input = OpenAiClient.FunctionArguments(response, "report_week")
                    ?? throw new AnalyzerException("Die KI hat keinen Wochenbericht geliefert.");
        return Parsing.Report(input);
    }
}

/// <summary>Looks up strain data using OpenAI's built-in web search.</summary>
public sealed class OpenAiSeedCatalog(OpenAiClient client) : ISeedCatalog
{
    public async Task<IReadOnlyList<SeedInfo>> SearchAsync(string query, CancellationToken ct = default)
    {
        // Round 1: search the web, ideally ending with a call to report_seeds.
        var response = await client.CreateAsync(new JsonObject
        {
            ["instructions"] = Prompts.Style,
            ["input"] = OpenAiClient.UserMessage(new JsonArray(OpenAiClient.TextPart(Prompts.SeedRequest(query)))),
            ["tools"] = new JsonArray(
                new JsonObject { ["type"] = "web_search" },
                OpenAiClient.FunctionTool(Prompts.SeedTool)),
            ["tool_choice"] = "auto",
            ["max_output_tokens"] = 8000,
        }, ct);

        var input = OpenAiClient.FunctionArguments(response, "report_seeds");
        if (input is null)
        {
            // Round 2: the model only wrote text – let it turn its findings into the structured answer.
            var found = OpenAiClient.OutputText(response);
            response = await client.CreateAsync(new JsonObject
            {
                ["instructions"] = Prompts.Style,
                ["input"] = OpenAiClient.UserMessage(new JsonArray(OpenAiClient.TextPart(
                    Prompts.SeedRequest(query) + "\n\nWhat a web search found:\n" + (found.Length > 0 ? found : "nothing")
                    + "\n\nCall report_seeds now."))),
                ["tools"] = new JsonArray(OpenAiClient.FunctionTool(Prompts.SeedTool)),
                ["tool_choice"] = OpenAiClient.ForceFunction("report_seeds"),
                ["max_output_tokens"] = 4000,
            }, ct);
            input = OpenAiClient.FunctionArguments(response, "report_seeds")
                    ?? throw new AnalyzerException("Die Sorten-Suche hat nichts geliefert.");
        }

        return Parsing.Seeds(input);
    }
}
