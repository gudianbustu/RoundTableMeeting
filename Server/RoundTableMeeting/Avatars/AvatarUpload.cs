using SkiaSharp;

namespace RoundTableMeeting.Avatars;

internal static class AvatarUpload
{
    internal const int MaxBytes = 2 * 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxBytes) return null;
        using var stream = file.OpenReadStream();
        var bytes = new byte[(int)file.Length];
        await stream.ReadExactlyAsync(bytes, ct);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
            return null;

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width is <= 0 or > 4096 || height is <= 0 or > 4096) return null;

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success) return null;
        ct.ThrowIfCancellationRequested();

        var scale = 256d / Math.Max(width, height);
        var size = new SKSizeI(Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
        using var resized = bitmap.Resize(size, new SKSamplingOptions(SKCubicResampler.CatmullRom));
        using var png = resized?.Encode(SKEncodedImageFormat.Png, 100);
        return png?.ToArray();
    }
}
