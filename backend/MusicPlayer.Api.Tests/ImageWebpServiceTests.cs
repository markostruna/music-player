using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Services;
using SkiaSharp;

namespace MusicPlayer.Api.Tests;

public sealed class ImageWebpServiceTests
{
    [Fact]
    public async Task ConvertsAnimatedGifToAnimatedWebpAndReusesTheCachedResult()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-webp-{Guid.NewGuid():N}");
        var gif = Convert.FromHexString(
            "47494638396101000100800000FF00000000FF21FF0B4E45545343415045322E300301000000" +
            "21F904000A0000002C0000000001000100000202440100" +
            "21F904000A0000002C00000000010001000002024C01003B");

        try
        {
            var service = new ImageWebpService(
                NullLogger<ImageWebpService>.Instance,
                Path.Combine(testDirectory, "cache"));

            var image = await service.ConvertAsync(gif, "image/gif", CancellationToken.None);
            var cachedImage = await service.ConvertAsync(gif, "image/gif", CancellationToken.None);

            Assert.Equal("image/webp", image.ContentType);
            Assert.Equal(image.Data, cachedImage.Data);
            using var encoded = SKData.CreateCopy(image.Data);
            using var codec = SKCodec.Create(encoded);
            Assert.NotNull(codec);
            Assert.Equal(2, codec.FrameInfo.Length);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }
}
