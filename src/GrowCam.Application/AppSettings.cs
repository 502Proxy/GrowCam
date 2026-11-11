namespace GrowCam.Application;

/// <summary>User-editable configuration (edited in the app's settings page).</summary>
public sealed record AppSettings
{
    // Camera – empty host means demo camera.
    public string CameraHost { get; init; } = "";
    public string CameraUser { get; init; } = "";
    public string CameraPassword { get; init; } = "";
    public string FfmpegPath { get; init; } = "";

    // AI – empty key of the chosen provider means demo answers.
    public AiProvider AiProvider { get; init; } = AiProvider.OpenAi;
    public string OpenAiApiKey { get; init; } = "";
    public string OpenAiModel { get; init; } = DefaultOpenAiModel;
    public string AnthropicApiKey { get; init; } = "";
    public string AnthropicModel { get; init; } = DefaultAnthropicModel;

    // Schedule
    public TimeOnly AnalysisTime { get; init; } = new(12, 0);
    public TimeOnly ReportTime { get; init; } = new(20, 0);
    public int SnapshotIntervalMinutes { get; init; } = 60;
    public TimeOnly LightOn { get; init; } = new(6, 0);
    public TimeOnly LightOff { get; init; } = new(0, 0);

    // Updates (private GitHub repo needs a read-only token)
    public string UpdateRepoUrl { get; init; } = "https://github.com/502Proxy/GrowCam";
    public string UpdateToken { get; init; } = "";
    public bool AutoInstallUpdates { get; init; } = true;

    // UI
    public double FontScale { get; init; } = 1.0;

    public bool IsDemoCamera => string.IsNullOrWhiteSpace(CameraHost);
    public bool IsDemoAi => string.IsNullOrWhiteSpace(AiProvider == AiProvider.OpenAi ? OpenAiApiKey : AnthropicApiKey);

    public const string DefaultOpenAiModel = "gpt-5-mini";
    public const string DefaultAnthropicModel = "claude-sonnet-5-5";

    public bool IsLightOn(TimeOnly now) =>
        LightOn <= LightOff
            ? now >= LightOn && now < LightOff
            : now >= LightOn || now < LightOff; // schedule crosses midnight
}

/// <summary>Which AI service answers. Stored by name in settings.json.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<AiProvider>))]
public enum AiProvider
{
    OpenAi,
    Claude,
}

public interface ISettingsStore
{
    AppSettings Current { get; }

    void Save(AppSettings settings);

    event EventHandler<AppSettings>? Changed;
}
