using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using GrowCam.Application;

namespace GrowCam.Infrastructure.Ai;

/// <summary>Thin wrapper around the OpenAI Responses API (no SDK dependency).</summary>
public sealed class OpenAiClient(HttpClient http, string apiKey, string model)
{
    private const string ApiUrl = "https://api.openai.com/v1/responses";

    public async Task<JsonObject> CreateAsync(JsonObject payload, CancellationToken ct)
    {
        payload["model"] = model;
        payload["store"] ??= false; // nothing needs to be kept on OpenAI's side
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
                throw new AnalyzerException(ErrorMessage((int)response.StatusCode, text));
            return JsonNode.Parse(text)?.AsObject() ?? throw new AnalyzerException("Leere Antwort von der KI.");
        }
    }

    /// <summary>Turns an error response into a sentence the reader can act on.</summary>
    public static string ErrorMessage(int status, string body)
    {
        string code = "", message = "";
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            code = (string?)error?["code"] ?? "";
            message = (string?)error?["message"] ?? "";
        }
        catch (System.Text.Json.JsonException)
        {
            // not JSON – fall back to the status code
        }

        if (code == "insufficient_quota")
            return "Das Guthaben beim KI-Schlüssel ist aufgebraucht. Bitte auf platform.openai.com nachsehen.";
        if (code == "model_not_found")
            return "Das eingestellte KI-Modell gibt es für diesen Schlüssel nicht. Bitte in den Einstellungen prüfen.";
        return status switch
        {
            401 => "Der KI-Schlüssel ist ungültig. Bitte in den Einstellungen prüfen.",
            403 => "Dieser KI-Schlüssel darf das Modell nicht benutzen. Bitte in den Einstellungen prüfen.",
            429 => "Die KI ist gerade überlastet oder das Limit ist erreicht. Bitte später noch mal versuchen.",
            >= 500 => "Die KI ist gerade gestört. Bitte später noch mal versuchen.",
            _ => $"KI-Anfrage fehlgeschlagen ({status})." + (message.Length > 0 ? " " + Shorten(message) : ""),
        };
    }

    public static JsonObject ImagePart(byte[] jpeg) => new()
    {
        ["type"] = "input_image",
        ["image_url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg),
        ["detail"] = "high",
    };

    public static JsonObject TextPart(string text) => new() { ["type"] = "input_text", ["text"] = text };

    public static JsonArray UserMessage(JsonArray content) =>
        new(new JsonObject { ["role"] = "user", ["content"] = content });

    /// <summary>Converts one of the shared tool definitions (Claude format) into an OpenAI function tool.</summary>
    public static JsonObject FunctionTool(string toolJson)
    {
        var tool = JsonNode.Parse(toolJson)!.AsObject();
        return new JsonObject
        {
            ["type"] = "function",
            ["name"] = tool["name"]!.DeepClone(),
            ["description"] = tool["description"]?.DeepClone(),
            ["parameters"] = tool["input_schema"]!.DeepClone(),
        };
    }

    public static JsonObject ForceFunction(string name) => new() { ["type"] = "function", ["name"] = name };

    /// <summary>Returns the parsed arguments of the first call to the given function, if any.</summary>
    public static JsonObject? FunctionArguments(JsonObject response, string name)
    {
        var call = Output(response)
            .FirstOrDefault(o => (string?)o["type"] == "function_call" && (string?)o["name"] == name);
        if (call?["arguments"] is not JsonValue value || !value.TryGetValue<string>(out var arguments))
            return null;
        try
        {
            return JsonNode.Parse(arguments) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>All plain text the model wrote.</summary>
    public static string OutputText(JsonObject response) =>
        string.Join("\n", Output(response)
            .Where(o => (string?)o["type"] == "message")
            .SelectMany(o => o["content"]?.AsArray().OfType<JsonObject>() ?? [])
            .Where(c => (string?)c["type"] == "output_text")
            .Select(c => (string?)c["text"] ?? ""))
            .Trim();

    private static IEnumerable<JsonObject> Output(JsonObject response) =>
        response["output"]?.AsArray().OfType<JsonObject>() ?? [];

    private static string Shorten(string text) => text.Length > 160 ? text[..160] + " …" : text;
}
