using System.Net;
using MusicPlayer.Api.Services.Identification;

namespace MusicPlayer.Api.Tests;

public sealed class SafeImageFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2001:4860:4860::8888", true)]
    public void OnlyPublicIpAddressesAreAllowed(string value, bool expected)
    {
        Assert.Equal(expected, SafeImageFetcher.IsPublicAddress(IPAddress.Parse(value)));
    }
}
