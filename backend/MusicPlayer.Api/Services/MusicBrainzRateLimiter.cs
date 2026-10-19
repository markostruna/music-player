namespace MusicPlayer.Api.Services;

/// <summary>Keeps all MusicBrainz requests in the process at or below one per second.</summary>
internal static class MusicBrainzRateLimiter
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTimeOffset nextRequest;

    public static async Task WaitAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var delay = nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            nextRequest = DateTimeOffset.UtcNow.AddSeconds(1);
        }
        finally
        {
            Gate.Release();
        }
    }
}
