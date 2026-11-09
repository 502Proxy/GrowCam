using Avalonia.Media;
using GrowCam.Domain;

namespace GrowCam.App.ViewModels;

/// <summary>Semantic colour tones used for status boxes and chips.</summary>
public enum Tone
{
    Ok,
    Info,
    Warn,
    Muted,
}

/// <summary>High-contrast colours (all text/background pairs exceed WCAG AA).</summary>
public static class Palette
{
    public static IBrush Background(Tone tone) => Brush(tone switch
    {
        Tone.Ok => "#1E3324",
        Tone.Warn => "#3A2C15",
        Tone.Info => "#1C2A38",
        _ => "#243029",
    });

    public static IBrush Border(Tone tone) => Brush(tone switch
    {
        Tone.Ok => "#3E6B44",
        Tone.Warn => "#7A5A22",
        Tone.Info => "#3A5A78",
        _ => "#3A4A40",
    });

    public static IBrush Foreground(Tone tone) => Brush(tone switch
    {
        Tone.Ok => "#D6F2D8",
        Tone.Warn => "#FFE2A8",
        Tone.Info => "#DCEBFA",
        _ => "#E3E9E5",
    });

    public static Tone Of(Severity severity) => severity switch
    {
        Severity.Ok => Tone.Ok,
        Severity.Watch => Tone.Warn,
        _ => Tone.Info,
    };

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

/// <summary>A coloured label ("Alles gut", "Im Auge behalten" …).</summary>
public sealed record Chip(string Text, Tone Tone)
{
    public IBrush Background => Palette.Background(Tone);
    public IBrush BorderBrush => Palette.Border(Tone);
    public IBrush Foreground => Palette.Foreground(Tone);
}
