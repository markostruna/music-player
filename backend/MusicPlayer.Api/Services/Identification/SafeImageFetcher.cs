using System.Net;
using System.Net.Sockets;
using MusicPlayer.Api.Contracts;

namespace MusicPlayer.Api.Services.Identification;

/// <summary>Recognises supported image formats from their leading bytes.</summary>
internal static class ImageSniffer
{
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff) return "image/jpeg";
        if (data.Length >= 6 && (data[..6].SequenceEqual("GIF87a"u8) || data[..6].SequenceEqual("GIF89a"u8))) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}

/// <summary>
/// Downloads remote images on behalf of an administrator. Connections are only made to public
/// addresses, which prevents the server being used to reach internal services.
/// </summary>
public sealed class SafeImageFetcher : IDisposable
{
    public const int MaximumImageSize = 10 * 1024 * 1024;
    private const int MaximumRedirects = 3;

    private readonly HttpClient http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = ConnectToPublicAddressAsync,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public async Task<CoverImage> FetchAsync(string url, CancellationToken cancellationToken)
    {
        var current = ParseUrl(url);
        try
        {
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.UserAgent.ParseAdd(ProviderText.UserAgent);
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    current = ParseUrl(new Uri(current, location).AbsoluteUri);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ArgumentException("The image could not be downloaded from that address.");
                }

                if (response.Content.Headers.ContentLength is > MaximumImageSize)
                {
                    throw new ArgumentException("The image is larger than 10 MB.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > MaximumImageSize)
                    {
                        throw new ArgumentException("The image is larger than 10 MB.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                var data = buffer.ToArray();
                var contentType = ImageSniffer.DetectContentType(data)
                    ?? throw new ArgumentException("The address does not point to a JPEG, PNG, GIF or WebP image.");
                return new CoverImage(data, contentType);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException
            || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new ArgumentException("The image could not be downloaded from that address.", exception);
        }

        throw new ArgumentException("The address redirected too many times.");
    }

    public void Dispose() => http.Dispose();

    private static Uri ParseUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Enter a valid http or https image address.");
        }

        return uri;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = (await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken))
            .Where(IsPublicAddress)
            .ToArray();
        if (addresses.Length == 0)
        {
            throw new HttpRequestException("The address does not resolve to a public host.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(bytes[0] == 10
                || bytes[0] == 0
                || bytes[0] >= 224
                || bytes[0] == 100 && bytes[1] is >= 64 and <= 127
                || bytes[0] == 169 && bytes[1] == 254
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0
                || bytes[0] == 198 && bytes[1] is 18 or 19);
        }

        return !(address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal
            || address.IsIPv6Multicast
            || address.IsIPv6UniqueLocal
            || address.IsIPv6Teredo);
    }
}

public sealed record CoverImage(byte[] Data, string ContentType);
