using GrowCam.Application;
using SkiaSharp;

namespace GrowCam.App.Services;

/// <summary>
/// Prepares a photo the user picked (phone photo, screenshot …) for storage and the AI:
/// upright according to its EXIF orientation, at most <see cref="MaxEdge"/> pixels, saved as JPEG.
/// </summary>
public static class PhotoImport
{
    public const int MaxEdge = 2048;

    public static readonly string[] Patterns = ["*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif"];

    public static byte[] ToJpeg(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
                          ?? throw new UserFacingException("Dieses Bild kann GrowCam nicht öffnen. Bitte ein JPG- oder PNG-Foto wählen.");
        using var decoded = SKBitmap.Decode(codec)
                            ?? throw new UserFacingException("Dieses Bild kann GrowCam nicht öffnen. Bitte ein JPG- oder PNG-Foto wählen.");
        using var upright = ApplyOrientation(decoded, codec.EncodedOrigin);

        var scale = Math.Min(1.0, (double)MaxEdge / Math.Max(upright.Width, upright.Height));
        var width = Math.Max(1, (int)Math.Round(upright.Width * scale));
        var height = Math.Max(1, (int)Math.Round(upright.Height * scale));

        // Draw onto an opaque white canvas: JPEG has no transparency (PNG screenshots may have).
        using var target = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.White);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawBitmap(upright, new SKRect(0, 0, width, height), paint);
        }

        using var jpeg = target.Encode(SKEncodedImageFormat.Jpeg, 88);
        return jpeg?.ToArray() ?? throw new UserFacingException("Das Foto konnte nicht umgewandelt werden.");
    }

    private static SKBitmap ApplyOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        var (degrees, mirror) = origin switch
        {
            SKEncodedOrigin.TopRight => (0, true),
            SKEncodedOrigin.BottomRight => (180, false),
            SKEncodedOrigin.BottomLeft => (180, true),
            SKEncodedOrigin.LeftTop => (90, true),
            SKEncodedOrigin.RightTop => (90, false),
            SKEncodedOrigin.RightBottom => (270, true),
            SKEncodedOrigin.LeftBottom => (270, false),
            _ => (0, false),
        };
        if (degrees == 0 && !mirror)
            return source.Copy();

        var swap = degrees is 90 or 270;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height);
        using var canvas = new SKCanvas(result);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(degrees);
        if (mirror)
            canvas.Scale(-1, 1);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0);
        return result;
    }
}
