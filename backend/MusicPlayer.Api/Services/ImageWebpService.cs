using System.Collections.Concurrent;
using System.Security.Cryptography;
using MusicPlayer.Api.Contracts;
using SkiaSharp;

namespace MusicPlayer.Api.Services;

public sealed class ImageWebpService
{
    private const int Quality = 82;
    private const int MaximumPixels = 16_000_000;
    private const int MaximumAnimationFrames = 200;
    private const long MaximumAnimationPixels = 24_000_000;
    private const long MaximumCacheFileSize = 64 * 1024 * 1024;
    private static readonly (string Variant, int Size)[] CoverVariants =
    [
        ("tile", 184),
        ("track", 42),
        ("album", 200),
    ];
    private static readonly (string Variant, int Size)[] ArtistVariants =
    [
        ("artist-tile", 166),
        ("artist-page", 200),
    ];
    private static readonly SKWebpEncoderOptions EncoderOptions =
        new(SKWebpEncoderCompression.Lossy, Quality);

    private readonly ILogger<ImageWebpService> logger;
    private readonly string cacheDirectory;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> conversionLocks = new(StringComparer.Ordinal);

    public ImageWebpService(ILogger<ImageWebpService> logger)
        : this(
            logger,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Afterhours",
                "webp-cache"))
    {
    }

    internal ImageWebpService(ILogger<ImageWebpService> logger, string cacheDirectory)
    {
        this.logger = logger;
        this.cacheDirectory = cacheDirectory;
    }

    public async Task<CoverImage> ConvertAsync(
        byte[] image,
        string contentType,
        CancellationToken cancellationToken)
    {
        if (contentType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            return new CoverImage(image, "image/webp");
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cachePath = Path.Combine(cacheDirectory, $"{hash}.webp");
        var cachedImage = await ReadCacheAsync(cachePath, cancellationToken);
        if (cachedImage is not null)
        {
            return new CoverImage(cachedImage, "image/webp");
        }

        var conversionLock = conversionLocks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await conversionLock.WaitAsync(cancellationToken);
        try
        {
            cachedImage = await ReadCacheAsync(cachePath, cancellationToken);
            if (cachedImage is not null)
            {
                return new CoverImage(cachedImage, "image/webp");
            }

            var webp = EncodeToWebp(image);
            Directory.CreateDirectory(cacheDirectory);
            var temporaryPath = Path.Combine(cacheDirectory, $"{hash}.{Guid.NewGuid():N}.tmp");
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, webp, cancellationToken);
                File.Move(temporaryPath, cachePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            return new CoverImage(webp, "image/webp");
        }
        finally
        {
            conversionLock.Release();
        }
    }

    public async Task<CoverImage> ConvertOrOriginalAsync(
        byte[] image,
        string contentType,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ConvertAsync(image, contentType, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Could not convert a {ContentType} image to WebP; serving the original.", contentType);
            return new CoverImage(image, contentType);
        }
    }

    public async Task<CoverImage> ConvertAlbumCoverAsync(
        byte[] image,
        CoverImageVariant requestedVariant,
        CancellationToken cancellationToken) =>
        await ConvertVariantSetAsync(
            image,
            requestedVariant.ToString().ToLowerInvariant(),
            CoverVariants,
            cancellationToken);

    public async Task<CoverImage> ConvertArtistImageAsync(
        byte[] image,
        ArtistImageVariant requestedVariant,
        CancellationToken cancellationToken) =>
        await ConvertVariantSetAsync(
            image,
            $"artist-{requestedVariant.ToString().ToLowerInvariant()}",
            ArtistVariants,
            cancellationToken);

    private async Task<CoverImage> ConvertVariantSetAsync(
        byte[] image,
        string requestedVariant,
        (string Variant, int Size)[] variantsToEnsure,
        CancellationToken cancellationToken)
    {
        if (!variantsToEnsure.Any(item => item.Variant == requestedVariant))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedVariant), requestedVariant, "Unsupported image variant.");
        }

        var hash = Convert.ToHexString(SHA256.HashData(image));
        var cachedVariants = await ReadVariantSetCacheAsync(hash, variantsToEnsure, cancellationToken);
        if (cachedVariants.Count == variantsToEnsure.Length)
        {
            return new CoverImage(cachedVariants[requestedVariant], "image/webp");
        }

        var conversionLock = conversionLocks.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await conversionLock.WaitAsync(cancellationToken);
        try
        {
            cachedVariants = await ReadVariantSetCacheAsync(hash, variantsToEnsure, cancellationToken);
            if (cachedVariants.Count == variantsToEnsure.Length)
            {
                return new CoverImage(cachedVariants[requestedVariant], "image/webp");
            }

            var resizedVariants = EncodeImageVariants(image, variantsToEnsure);
            Directory.CreateDirectory(cacheDirectory);
            foreach (var (variant, _) in variantsToEnsure)
            {
                var cachePath = GetVariantCachePath(hash, variant);
                var temporaryPath = Path.Combine(cacheDirectory, $"{hash}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, resizedVariants[variant], cancellationToken);
                    File.Move(temporaryPath, cachePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath))
                    {
                        File.Delete(temporaryPath);
                    }
                }
            }

            return new CoverImage(resizedVariants[requestedVariant], "image/webp");
        }
        finally
        {
            conversionLock.Release();
        }
    }

    private async Task<Dictionary<string, byte[]>> ReadVariantSetCacheAsync(
        string hash,
        (string Variant, int Size)[] variantsToEnsure,
        CancellationToken cancellationToken)
    {
        var cachedVariants = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (variant, size) in variantsToEnsure)
        {
            var cachedImage = await ReadVariantCacheAsync(
                GetVariantCachePath(hash, variant),
                size,
                cancellationToken);
            if (cachedImage is not null)
            {
                cachedVariants.Add(variant, cachedImage);
            }
        }

        return cachedVariants;
    }

    private async Task<byte[]?> ReadVariantCacheAsync(
        string path,
        int size,
        CancellationToken cancellationToken)
    {
        try
        {
            var image = await ReadCacheAsync(path, cancellationToken);
            if (image is null)
            {
                return null;
            }

            using var encodedImage = SKData.CreateCopy(image);
            using var codec = SKCodec.Create(encodedImage);
            if (codec is not null && codec.Info.Width == size && codec.Info.Height == size)
            {
                return image;
            }

            logger.LogWarning("Cached image variant {CachePath} has incorrect dimensions.", path);
            return null;
        }
        catch (InvalidDataException exception)
        {
            logger.LogWarning(exception, "Cached image variant {CachePath} is invalid.", path);
            return null;
        }
    }

    private string GetVariantCachePath(string hash, string variant) =>
        Path.Combine(cacheDirectory, $"{hash}-{variant}.webp");

    private static Dictionary<string, byte[]> EncodeImageVariants(
        byte[] data,
        (string Variant, int Size)[] variantsToEncode)
    {
        using var encodedImage = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encodedImage);
        if (codec is null)
        {
            throw new InvalidDataException("The image could not be decoded.");
        }

        var info = codec.Info;
        var pixelCount = (long)info.Width * info.Height;
        if (info.Width <= 0 || info.Height <= 0 || pixelCount > MaximumPixels)
        {
            throw new InvalidDataException("The image dimensions are invalid or too large to resize.");
        }

        var frameInfo = codec.FrameInfo;
        if (frameInfo.Length <= 1)
        {
            using var bitmap = DecodeFrame(codec, info, 0);
            var variants = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (variant, size) in variantsToEncode)
            {
                using var resizedBitmap = ResizeFrame(bitmap, size);
                variants.Add(variant, EncodeFrame(resizedBitmap));
            }

            return variants;
        }

        if (frameInfo.Length > MaximumAnimationFrames ||
            pixelCount * frameInfo.Length > MaximumAnimationPixels)
        {
            throw new InvalidDataException("The animated image exceeds the supported conversion limits.");
        }

        var resizedFrames = variantsToEncode.ToDictionary(
            item => item.Variant,
            _ => new List<SKBitmap>(frameInfo.Length));
        try
        {
            for (var index = 0; index < frameInfo.Length; index++)
            {
                using var bitmap = DecodeFrame(codec, info, index);
                foreach (var (variant, size) in variantsToEncode)
                {
                    resizedFrames[variant].Add(ResizeFrame(bitmap, size));
                }
            }

            var variants = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var (variant, _) in variantsToEncode)
            {
                var resizedVariantFrames = resizedFrames[variant];
                var webpFrames = new SKWebpEncoderFrame[frameInfo.Length];
                for (var index = 0; index < frameInfo.Length; index++)
                {
                    var duration = TimeSpan.FromMilliseconds(Math.Max(10, frameInfo[index].Duration));
                    webpFrames[index] = new SKWebpEncoderFrame(resizedVariantFrames[index], duration);
                }

                using var encoded = SKWebpEncoder.EncodeAnimated(webpFrames, EncoderOptions);
                variants.Add(
                    variant,
                    encoded?.ToArray()
                        ?? throw new InvalidDataException($"The {variant} image could not be encoded as WebP."));
            }

            return variants;
        }
        finally
        {
            foreach (var frames in resizedFrames.Values)
            {
                foreach (var bitmap in frames)
                {
                    bitmap.Dispose();
                }
            }
        }
    }

    private static SKBitmap ResizeFrame(SKBitmap source, int size)
    {
        var side = Math.Min(source.Width, source.Height);
        var left = (source.Width - side) / 2f;
        var top = (source.Height - side) / 2f;
        var sourceRect = new SKRect(left, top, left + side, top + side);
        var target = new SKBitmap(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(target);
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(source, sourceRect, new SKRect(0, 0, size, size), sampling, paint);
        canvas.Flush();
        return target;
    }

    private static async Task<byte[]?> ReadCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var fileInfo = new FileInfo(path);
        if (fileInfo.Length is <= 0 or > MaximumCacheFileSize)
        {
            throw new InvalidDataException($"The cached WebP image at '{path}' has an invalid size.");
        }

        var image = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!IsWebp(image))
        {
            throw new InvalidDataException($"The cached image at '{path}' is not a valid WebP file.");
        }

        return image;
    }

    private static byte[] EncodeToWebp(byte[] data)
    {
        using var encodedImage = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(encodedImage);
        if (codec is null)
        {
            throw new InvalidDataException("The image could not be decoded.");
        }

        var info = codec.Info;
        var pixelCount = (long)info.Width * info.Height;
        if (info.Width <= 0 || info.Height <= 0 || pixelCount > MaximumPixels)
        {
            throw new InvalidDataException("The image dimensions are invalid or too large to convert.");
        }

        var frameInfo = codec.FrameInfo;
        if (frameInfo.Length <= 1)
        {
            using var bitmap = DecodeFrame(codec, info, 0);
            return EncodeFrame(bitmap);
        }

        if (frameInfo.Length > MaximumAnimationFrames ||
            pixelCount * frameInfo.Length > MaximumAnimationPixels)
        {
            throw new InvalidDataException("The animated image exceeds the supported conversion limits.");
        }

        var bitmaps = new List<SKBitmap>(frameInfo.Length);
        try
        {
            var frames = new SKWebpEncoderFrame[frameInfo.Length];
            for (var index = 0; index < frameInfo.Length; index++)
            {
                var bitmap = DecodeFrame(codec, info, index);
                bitmaps.Add(bitmap);
                var duration = TimeSpan.FromMilliseconds(Math.Max(10, frameInfo[index].Duration));
                frames[index] = new SKWebpEncoderFrame(bitmap, duration);
            }

            using var encoded = SKWebpEncoder.EncodeAnimated(frames, EncoderOptions);
            return encoded?.ToArray()
                ?? throw new InvalidDataException("The animated image could not be encoded as WebP.");
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    private static SKBitmap DecodeFrame(SKCodec codec, SKImageInfo info, int frameIndex)
    {
        var bitmap = new SKBitmap(info);
        var result = frameIndex == 0
            ? codec.GetPixels(info, bitmap.GetPixels())
            : codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(frameIndex));
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidDataException($"The image frame {frameIndex} could not be decoded: {result}.");
        }

        return bitmap;
    }

    private static byte[] EncodeFrame(SKBitmap bitmap)
    {
        using var pixmap = bitmap.PeekPixels();
        if (pixmap is null)
        {
            throw new InvalidDataException("The decoded image pixels could not be read.");
        }

        using var encoded = SKWebpEncoder.Encode(pixmap, EncoderOptions);
        return encoded?.ToArray()
            ?? throw new InvalidDataException("The image could not be encoded as WebP.");
    }

    private static bool IsWebp(ReadOnlySpan<byte> image) =>
        image.Length >= 12 &&
        image[..4].SequenceEqual("RIFF"u8) &&
        image.Slice(8, 4).SequenceEqual("WEBP"u8);
}
