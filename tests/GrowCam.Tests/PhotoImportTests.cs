using GrowCam.App.Services;
using GrowCam.Application;
using SkiaSharp;

namespace GrowCam.Tests;

public class PhotoImportTests
{
    [Fact]
    public void LargeTransparentPngBecomesSmallerJpeg()
    {
        using var source = new SKBitmap(4000, 1000, SKColorType.Rgba8888, SKAlphaType.Premul);
        source.Erase(SKColors.Transparent);
        using var png = source.Encode(SKEncodedImageFormat.Png, 100);

        var jpeg = PhotoImport.ToJpeg(png.ToArray());

        using var codec = SKCodec.Create(new SKMemoryStream(jpeg));
        Assert.Equal(SKEncodedImageFormat.Jpeg, codec.EncodedFormat);
        Assert.Equal(new SKSizeI(PhotoImport.MaxEdge, 512), codec.Info.Size);
        using var result = SKBitmap.Decode(jpeg);
        Assert.Equal(SKColors.White, result.GetPixel(10, 10)); // transparency → white
    }

    [Fact]
    public void SmallPhotoKeepsItsSize()
    {
        using var source = new SKBitmap(640, 480);
        source.Erase(SKColors.Green);
        using var jpg = source.Encode(SKEncodedImageFormat.Jpeg, 90);

        using var result = SKBitmap.Decode(PhotoImport.ToJpeg(jpg.ToArray()));

        Assert.Equal(640, result.Width);
        Assert.Equal(480, result.Height);
    }

    [Fact]
    public void NonImageFileGivesReadableError()
    {
        var error = Assert.Throws<UserFacingException>(() => PhotoImport.ToJpeg("hello"u8.ToArray()));
        Assert.Contains("JPG", error.Message);
    }
}
