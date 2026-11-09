using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using GrowCam.Application;

namespace GrowCam.Infrastructure.Ai;

/// <summary>Thin wrapper around the Claude Messages API (no SDK dependency).</summary>
public sealed class ClaudeClient(HttpClient http, string apiKey, string model)
{
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    public async Task<JsonObject> CreateAsync(JsonObject payload, CancellationToken ct)
    {
        payload["model"] = model;
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AnalyzerException("Die KI ist gerade nicht erreichbar. Gibt es Internet?", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var message = (int)response.StatusCode switch
                {
                    401 => "Der KI-Schlüssel ist ungültig. Bitte in den Einstellungen prüfen.",
                    429 or 529 => "Die KI ist gerade überlastet. Bitte später noch mal versuchen.",
                    _ => $"KI-Anfrage fehlgeschlagen ({(int)response.StatusCode}).",
                };
                throw new AnalyzerException(message);
            }
            return JsonNode.Parse(text)?.AsObject() ?? throw new AnalyzerException("Leere Antwort von der KI.");
        }
    }

    public static JsonObject ImageBlock(byte[] jpeg) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = "image/jpeg",
            ["data"] = Convert.ToBase64String(jpeg),
        },
    };

    public static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    /// <summary>Returns the input of the first call to the given tool, if any.</summary>
    public static JsonObject? ToolInput(JsonObject response, string toolName) =>
        response["content"]?.AsArray()
            .OfType<JsonObject>()
            .FirstOrDefault(b => (string?)b["type"] == "tool_use" && (string?)b["name"] == toolName)?["input"]?.AsObject();
}
