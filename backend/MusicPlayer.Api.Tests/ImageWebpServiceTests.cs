using Microsoft.Extensions.Logging.Abstractions;
using MusicPlayer.Api.Contracts;
using MusicPlayer.Api.Services;
using SkiaSharp;

namespace MusicPlayer.Api.Tests;

public sealed class ImageWebpServiceTests
{
    [Fact]
    public async Task CreatesAndRepairsAllAlbumCoverVariantsWhenOneIsRequested()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-webp-{Guid.NewGuid():N}");
        var cacheDirectory = Path.Combine(testDirectory, "cache");
        var imageData = CreatePng(320, 200);

        try
        {
            var service = new ImageWebpService(
                NullLogger<ImageWebpService>.Instance,
                cacheDirectory);

            var tile = await service.ConvertAlbumCoverAsync(
                imageData,
                CoverImageVariant.Tile,
                CancellationToken.None);

            Assert.Equal("image/webp", tile.ContentType);
            Assert.Equal((184, 184), GetDimensions(tile.Data));
            AssertVariantDimensions(cacheDirectory, 42, 184, 200);

            var tileCachePath = Directory.EnumerateFiles(cacheDirectory, "*-tile.webp").Single();
            File.Delete(tileCachePath);
            var track = await service.ConvertAlbumCoverAsync(
                imageData,
                CoverImageVariant.Track,
                CancellationToken.None);

            Assert.Equal((42, 42), GetDimensions(track.Data));
            AssertVariantDimensions(cacheDirectory, 42, 184, 200);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CreatesAndRepairsBothArtistImageVariantsWhenOneIsRequested()
    {
        var testDirectory = Path.Combine(Path.GetTempPath(), $"afterhours-webp-{Guid.NewGuid():N}");
        var cacheDirectory = Path.Combine(testDirectory, "cache");
        var imageData = CreatePng(320, 200);

        try
        {
            var service = new ImageWebpService(
                NullLogger<ImageWebpService>.Instance,
                cacheDirectory);

            var pageImage = await service.ConvertArtistImageAsync(
                imageData,
                ArtistImageVariant.Page,
                CancellationToken.None);

            Assert.Equal((200, 200), GetDimensions(pageImage.Data));
            AssertVariantDimensions(cacheDirectory, 166, 200);

            var pageCachePath = Directory.EnumerateFiles(cacheDirectory, "*-artist-page.webp").Single();
            File.Delete(pageCachePath);
            var tileImage = await service.ConvertArtistImageAsync(
                imageData,
                ArtistImageVariant.Tile,
                CancellationToken.None);

            Assert.Equal((166, 166), GetDimensions(tileImage.Data));
            AssertVariantDimensions(cacheDirectory, 166, 200);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

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

            var albumCover = await service.ConvertAlbumCoverAsync(
                gif,
                CoverImageVariant.Album,
                CancellationToken.None);
            Assert.Equal((200, 200), GetDimensions(albumCover.Data));
            using var resized = SKData.CreateCopy(albumCover.Data);
            using var resizedCodec = SKCodec.Create(resized);
            Assert.NotNull(resizedCodec);
            Assert.Equal(2, resizedCodec.FrameInfo.Length);
        }
        finally
        {
            if (Directory.Exists(testDirectory))
            {
                Directory.Delete(testDirectory, recursive: true);
            }
        }
    }

    private static byte[] CreatePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Coral);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded!.ToArray();
    }

    private static (int Width, int Height) GetDimensions(byte[] image)
    {
        using var encoded = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(encoded);
        Assert.NotNull(codec);
        return (codec.Info.Width, codec.Info.Height);
    }

    private static void AssertVariantDimensions(string cacheDirectory, params int[] expectedSizes)
    {
        var actualSizes = Directory.EnumerateFiles(cacheDirectory, "*.webp")
            .Select(path => GetDimensions(File.ReadAllBytes(path)).Width)
            .Order()
            .ToArray();
        Assert.Equal(expectedSizes.Order(), actualSizes);
    }
}
