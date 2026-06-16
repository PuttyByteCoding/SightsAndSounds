using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Tests.Fixtures;
using Xabe.FFmpeg;
using Xunit;

namespace VideoOrganizer.Tests.Integration;

/// <summary>
/// Full-video OCR text scan (issue #5, ask 2): a background scan samples frames,
/// OCRs them via the tesseract CLI, stores the hits, advances a resumable marker,
/// and the stored text becomes searchable. Exercised end-to-end against a real
/// synthetic clip (ffmpeg drawtext) so it covers the ffmpeg sampling + tesseract
/// recognition + DB persistence + search wiring together. Skips when Docker or
/// tesseract isn't available rather than failing.
/// </summary>
[Collection("PostgresApi")]
public sealed class OcrScanTests
{
    private readonly PostgresApiFixture _api;

    public OcrScanTests(PostgresApiFixture api) => _api = api;

    private sealed record ScanProgress(
        bool active, double scannedThroughSeconds, double durationSeconds,
        int hits, string phase, string? error);
    private sealed record OcrLine(double timeSeconds, string text);
    private sealed record SearchHit(Guid id, List<string> matchedFields);
    private sealed record SearchBody(string query, int totalCount, bool truncated, List<SearchHit> results);

    [SkippableFact]
    public async Task Scan_finds_on_screen_text_then_stores_and_makes_it_searchable()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TesseractAvailable(), "tesseract CLI not on PATH");
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        // A distinctive token so OCR + search are unambiguous; full-frame so
        // tesseract's psm-6 block mode reads it cleanly.
        const string token = "ZEBRAFISH";
        var dir = Path.Combine(Path.GetTempPath(), "sas-ocr-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clip.mp4");
        var videoId = Guid.NewGuid();

        try
        {
            await MakeTextVideoAsync(path, token, seconds: 6);

            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video
                {
                    Id = videoId,
                    FileName = "clip.mp4",
                    FilePath = path,
                    Duration = TimeSpan.FromSeconds(6),
                });
                await db.SaveChangesAsync();
            });

            // Kick off the background scan.
            var start = await _api.Client.PostAsync($"/api/videos/{videoId}/ocr-scan", null);
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            // Poll to completion (cap ~60s — a 6s clip at the default interval is
            // a handful of frames).
            ScanProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<ScanProgress>($"/api/videos/{videoId}/ocr-scan");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active, "scan should finish");
            Assert.Equal("done", prog.phase);
            Assert.True(prog.hits > 0, "scan should record at least one text hit");
            // Ran off the end → resume marker reaches the full duration.
            Assert.True(prog.scannedThroughSeconds >= prog.durationSeconds - 0.001);

            // Stored hits include the token, each tagged with a timestamp.
            var lines = await _api.Client.GetFromJsonAsync<List<OcrLine>>($"/api/videos/{videoId}/ocr-text");
            Assert.NotNull(lines);
            Assert.Contains(lines!, l => l.text.Contains(token, StringComparison.OrdinalIgnoreCase));

            // The durable resume marker persisted on the Video row.
            await _api.WithDbAsync(async db =>
            {
                var v = await db.Videos.AsNoTracking().FirstAsync(v => v.Id == videoId);
                Assert.NotNull(v.OcrScannedThroughSeconds);
                Assert.True(v.OcrScannedThroughSeconds >= 5.9);
            });

            // Global search now finds the video via on-screen text, flagged "ocrText".
            var search = await _api.Client.GetFromJsonAsync<SearchBody>($"/api/search?q={token}");
            Assert.NotNull(search);
            var hit = Assert.Single(search!.results, r => r.id == videoId);
            Assert.Contains("ocrText", hit.matchedFields);
        }
        finally
        {
            await _api.WithDbAsync(async db =>
                await db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [SkippableFact]
    public async Task Scan_resumes_from_the_saved_marker_and_does_not_re_read_covered_frames()
    {
        Skip.IfNot(_api.Available, _api.SkipReason);
        Skip.IfNot(TesseractAvailable(), "tesseract CLI not on PATH");
        Skip.IfNot(TestFfmpeg.Available, "ffmpeg not available");

        const string token = "QUOKKA";
        var dir = Path.Combine(Path.GetTempPath(), "sas-ocr-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clip.mp4");
        var videoId = Guid.NewGuid();

        try
        {
            await MakeTextVideoAsync(path, token, seconds: 6);

            // Simulate a prior partial scan that already covered the first 4s.
            await _api.WithDbAsync(async db =>
            {
                db.Videos.Add(new Video
                {
                    Id = videoId,
                    FileName = "clip.mp4",
                    FilePath = path,
                    Duration = TimeSpan.FromSeconds(6),
                    OcrScannedThroughSeconds = 4.0,
                });
                await db.SaveChangesAsync();
            });

            var start = await _api.Client.PostAsync($"/api/videos/{videoId}/ocr-scan", null);
            Assert.Equal(HttpStatusCode.Accepted, start.StatusCode);

            ScanProgress? prog = null;
            for (var i = 0; i < 120; i++)
            {
                await Task.Delay(500);
                prog = await _api.Client.GetFromJsonAsync<ScanProgress>($"/api/videos/{videoId}/ocr-scan");
                if (prog is { active: false }) break;
            }
            Assert.NotNull(prog);
            Assert.False(prog!.active);
            Assert.Equal("done", prog.phase);

            // Resume started after the marker (4 + interval), so every stored hit
            // is past the already-covered region — no frame before 4s was re-read.
            var lines = await _api.Client.GetFromJsonAsync<List<OcrLine>>($"/api/videos/{videoId}/ocr-text");
            Assert.NotNull(lines);
            Assert.NotEmpty(lines!);
            Assert.All(lines!, l => Assert.True(l.timeSeconds > 4.0,
                $"resume should not sample before the 4s marker, but found a hit at {l.timeSeconds}s"));
        }
        finally
        {
            await _api.WithDbAsync(async db =>
                await db.Videos.Where(v => v.Id == videoId).ExecuteDeleteAsync());
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Render a short H.264 clip showing `text` centered on a dark background,
    // so a sampled frame OCRs back to the token.
    private static async Task MakeTextVideoAsync(string path, string text, int seconds)
    {
        var ffmpeg = ResolveFfmpeg();
        var vf = $"drawtext=text='{text}':fontcolor=white:fontsize=72:x=(w-text_w)/2:y=(h-text_h)/2";
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"color=c=0x101820:s=1280x720:d={seconds}",
                     "-vf", vf, "-pix_fmt", "yuv420p", "-r", "5", path,
                 })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg fixture-gen failed: {stderr}");
    }

    private static string ResolveFfmpeg()
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

    private static bool TesseractAvailable()
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = "tesseract",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch { return false; }
    }
}
