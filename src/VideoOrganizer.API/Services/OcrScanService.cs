using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using Xabe.FFmpeg;

namespace VideoOrganizer.API.Services;

// Background full-video OCR scanner (issue #5, "ask 2"). Samples frames on a
// fixed interval from the resume position forward, OCRs each via OcrService,
// and stores one OcrTextLine per frame that yields text. Runs until the video
// ends or the user stops it; "Scan more" resumes from
// Video.OcrScannedThroughSeconds rather than re-reading covered frames. Only
// one scan runs at a time — scans are user-initiated and CPU-heavy — guarded
// by the OcrScanProgress singleton's active flag.
public sealed class OcrScanService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly OcrService _ocr;
    private readonly OcrScanProgress _progress;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OcrScanService> _logger;
    private readonly double _intervalSeconds;
    private readonly int _frameWidth;

    // Persist (and publish a new resume point) every this many sampled frames,
    // so a stop/crash loses at most a few frames of work and the progress bar
    // and resume marker stay close to live.
    private const int CommitEvery = 8;

    public OcrScanService(
        IServiceScopeFactory scopeFactory, OcrService ocr, OcrScanProgress progress,
        IHostApplicationLifetime lifetime, IConfiguration config, ILogger<OcrScanService> logger)
    {
        _scopeFactory = scopeFactory;
        _ocr = ocr;
        _progress = progress;
        _lifetime = lifetime;
        _logger = logger;
        _intervalSeconds = config.GetValue<double?>("Ocr:ScanIntervalSeconds") is { } iv && iv > 0 ? iv : 2.0;
        _frameWidth = config.GetValue<int?>("Ocr:ScanFrameWidth") is { } fw && fw > 0 ? fw : 1280;
    }

    public enum StartResult { Started, AlreadyRunning, NotFound, FileMissing }

    // Kick off a scan on a background thread. Returns immediately; callers poll
    // OcrScanProgress for state. AlreadyRunning when a scan is in flight.
    public async Task<StartResult> TryStartAsync(Guid videoId, CancellationToken ct)
    {
        if (_progress.IsActive) return StartResult.AlreadyRunning;

        Video? video;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            video = await db.Videos.AsNoTracking().FirstOrDefaultAsync(v => v.Id == videoId, ct);
        }
        if (video is null) return StartResult.NotFound;
        if (!File.Exists(video.FilePath)) return StartResult.FileMissing;

        var duration = video.Duration.TotalSeconds;
        var scannedThrough = video.OcrScannedThroughSeconds ?? 0;
        // Resume after the last sampled point; a never-scanned video starts at 0.
        var firstSampleT = video.OcrScannedThroughSeconds is { } prev ? prev + _intervalSeconds : 0;

        _progress.Begin(videoId, duration, scannedThrough);
        _ = Task.Run(() => RunScanAsync(videoId, video.FilePath, duration, firstSampleT));
        return StartResult.Started;
    }

    private async Task RunScanAsync(Guid videoId, string filePath, double duration, double firstSampleT)
    {
        var ct = _lifetime.ApplicationStopping;
        var hits = 0;
        var scannedThrough = firstSampleT > 0 ? firstSampleT - _intervalSeconds : 0;
        var pending = new List<OcrTextLine>(CommitEvery);
        var sinceCommit = 0;

        try
        {
            for (var t = firstSampleT; t <= duration + 0.001; t += _intervalSeconds)
            {
                if (_progress.StopRequested || ct.IsCancellationRequested) break;

                var text = await ReadFrameTextAsync(filePath, t, ct);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    pending.Add(new OcrTextLine { VideoId = videoId, TimeSeconds = t, Text = text.Trim() });
                    hits++;
                }
                scannedThrough = t;
                sinceCommit++;

                if (sinceCommit >= CommitEvery)
                {
                    await CommitAsync(videoId, pending, scannedThrough, ct);
                    pending.Clear();
                    sinceCommit = 0;
                }
                _progress.Report(scannedThrough, hits);
            }

            // The video is fully covered only if we ran off the end (not stopped).
            var reached = _progress.StopRequested || ct.IsCancellationRequested
                ? scannedThrough
                : duration;
            await CommitAsync(videoId, pending, reached, ct);
            _progress.Report(reached, hits);
            _progress.End("done");
        }
        catch (OcrService.OcrUnavailableException ex)
        {
            _progress.End("error", ex.Message);
        }
        catch (OperationCanceledException)
        {
            _progress.End("done"); // app shutdown — leave the resume marker as committed
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OCR scan failed for video {VideoId} near {Seconds}s", videoId, scannedThrough);
            _progress.End("error", "The scan failed. See server logs.");
        }
    }

    // Persist this batch of hits and advance the durable resume marker in one
    // unit of work, so the two never disagree.
    private async Task CommitAsync(Guid videoId, List<OcrTextLine> pending, double scannedThrough, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
        if (pending.Count > 0) db.OcrTextLines.AddRange(pending);
        await db.SaveChangesAsync(ct);
        await db.Videos.Where(v => v.Id == videoId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.OcrScannedThroughSeconds, scannedThrough), ct);
    }

    // Snapshot the frame at time t to a temp PNG and OCR it. Fast input-seek
    // (-ss before -i) keeps per-frame cost roughly constant regardless of where
    // in the file t lands. PNG (lossless) reads better than JPEG for OCR.
    private async Task<string> ReadFrameTextAsync(string filePath, double t, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ocrscan_{Guid.NewGuid():N}.png");
        try
        {
            await RunFfmpegAsync(ct,
                "-ss", t.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", filePath,
                "-frames:v", "1",
                "-vf", $"scale='min({_frameWidth},iw)':-2",
                tmp);
            if (!File.Exists(tmp)) return string.Empty; // seek past EOF → no frame
            return await _ocr.RecognizeAsync(tmp, ct);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    private static async Task RunFfmpegAsync(CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveFfmpegPath(),
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-loglevel");
        psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        using var kill = ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }
        });

        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException($"ffmpeg failed (exit {proc.ExitCode}): {tail}");
        }
    }

    // ffmpeg lives where Xabe was pointed (Program.cs sets a bundled dir; tests
    // point at the system binaries). Fall back to PATH if unset.
    private static string ResolveFfmpegPath()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var dir = FFmpeg.ExecutablesPath;
        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, exe);
            if (File.Exists(p)) return p;
        }
        return exe;
    }
}
