using System.Diagnostics;
using System.Text.Json;

namespace MusicPlayer.Api.Services;

public sealed record AudioAnalysisResult(float[]? Embedding, string? Genre, string? Error);

public interface IAudioAnalyzer
{
    Task<AudioAnalysisResult> AnalyzeAsync(string path, double duration, CancellationToken cancellationToken);
    void Reset();
}

public sealed class ClapAudioAnalyzer(
    IHostEnvironment environment, IConfiguration configuration, ILogger<ClapAudioAnalyzer> logger) : IAudioAnalyzer
{
    private Process? process;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AudioAnalysisResult> AnalyzeAsync(string path, double duration, CancellationToken cancellationToken)
    {
        var worker = EnsureProcess();
        await worker.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { path, duration }, JsonOptions));
        await worker.StandardInput.FlushAsync(cancellationToken);
        var line = await worker.StandardOutput.ReadLineAsync(cancellationToken).AsTask()
            .WaitAsync(TimeSpan.FromMinutes(10), cancellationToken)
            ?? throw new IOException("The audio analysis process exited unexpectedly.");
        return JsonSerializer.Deserialize<AudioAnalysisResult>(line, JsonOptions)
            ?? throw new JsonException("The audio analysis response was empty.");
    }

    private Process EnsureProcess()
    {
        if (process is { HasExited: false }) return process;
        Reset();
        var python = configuration["AudioAnalysis:PythonPath"]
            ?? Path.Combine(environment.ContentRootPath, "AudioAnalysis", ".venv", "Scripts", "python.exe");
        var script = Path.Combine(AppContext.BaseDirectory, "AudioAnalysis", "analyze.py");
        var start = new ProcessStartInfo(python)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(script);
        start.Environment["HF_HUB_DISABLE_PROGRESS_BARS"] = "1";
        var cache = configuration["AudioAnalysis:ModelCachePath"]
            ?? Path.Combine(environment.ContentRootPath, "AudioAnalysis", ".venv", "model-cache");
        start.Environment["HF_HOME"] = cache;
        var worker = new Process { StartInfo = start, EnableRaisingEvents = true };
        worker.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
                logger.LogWarning("Audio analysis: {Message}", args.Data);
        };
        if (!worker.Start()) throw new InvalidOperationException("Could not start audio analysis.");
        worker.BeginErrorReadLine();
        process = worker;
        return worker;
    }

    public void Reset()
    {
        if (process is null) return;
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.Dispose();
        process = null;
    }
}
