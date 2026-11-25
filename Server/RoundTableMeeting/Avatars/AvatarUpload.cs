using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Processing;

namespace RoundTableMeeting.Avatars;

internal static class AvatarUpload
{
    internal const int MaxBytes = 2 * 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(IFormFile file, CancellationToken ct)
    {
        if (file.Length is <= 0 or > MaxBytes) return null;
        using var stream = file.OpenReadStream();
        try
        {
            var options = new DecoderOptions { SkipMetadata = true, MaxFrames = 1 };
            var info = await Image.IdentifyAsync(options, stream, ct);
            if (info.Metadata.DecodedImageFormat?.Name is not ("PNG" or "JPEG" or "Webp" or "WEBP")
                || info.Width > 4096 || info.Height > 4096) return null;
            stream.Position = 0;
            using var image = await Image.LoadAsync(options, stream, ct);
            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(256, 256), Mode = ResizeMode.Max }));
            using var output = new MemoryStream();
            await image.SaveAsync(output, new PngEncoder { SkipMetadata = true }, ct);
            return output.ToArray();
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException) { return null; }
    }
}
