using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// Joins (concatenates) several videos into one new file (issue #163), in the
// order given. Two modes:
//   · stream-copy (default): fast + lossless, works when the inputs share codec
//     / resolution / params (e.g. clips exported from the same parent).
//   · re-encode: normalizes every input to H.264/AAC at the first input's
//     resolution (synthesizing silent audio for inputs that have none), then
//     concatenates — robust across mismatched sources, but slower + lossy.
// The output is ingested as a fresh top-level video carrying the union of the
// inputs' tags. One run at a time; reuses the shared MediaExport engine.
public sealed class JoinService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly JoinProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<JoinService> _logger;

    public JoinService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        JoinProgress progress, Md5BackfillSignal md5Signal,
        ThumbnailWarmingSignal thumbSignal, IHostApplicationLifetime lifetime,
        ILogger<JoinService> logger)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _progress = progress;
        _md5Signal = md5Signal;
        _thumbSignal = thumbSignal;
        _lifetime = lifetime;
        _logger = logger;
    }

    public enum StartResult { Started, AlreadyRunning, NotEnough }

    public async Task<StartResult> TryStartAsync(
        IReadOnlyList<Guid> orderedIds, bool reencode, string? name, CancellationToken ct)
    {
        if (_progress.IsActive) return StartResult.AlreadyRunning;
        if (orderedIds.Count < 2) return StartResult.NotEnough;

        // Validate existence while preserving the requested order.
        List<Video> ordered;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            var idSet = orderedIds.ToHashSet();
            var found = await db.Videos.AsNoTracking()
                .Include(v => v.VideoTags)
                .Where(v => idSet.Contains(v.Id))
                .ToListAsync(ct);
            var byId = found.ToDictionary(v => v.Id);
            ordered = orderedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        if (ordered.Count < 2) return StartResult.NotEnough;

        _progress.Begin(reencode ? ordered.Count + 1 : 1);
        _ = Task.Run(() => RunAsync(ordered, reencode, name));
        return StartResult.Started;
    }

    private async Task RunAsync(List<Video> ordered, bool reencode, string? name)
    {
        var ct = _lifetime.ApplicationStopping;
        var jobId = Guid.NewGuid();
        var tempDir = Path.Combine(Path.GetTempPath(), "sas-join-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            foreach (var v in ordered)
            {
                if (!File.Exists(v.FilePath))
                    throw new InvalidOperationException($"Missing file: {v.FileName}");
            }

            var first = ordered[0];
            var ext = reencode ? ".mp4" : Path.GetExtension(first.FilePath);
            var outPath = string.IsNullOrWhiteSpace(name)
                ? UniquePath(Path.GetDirectoryName(first.FilePath) ?? ".",
                    Path.GetFileNameWithoutExtension(first.FilePath) + "_joined", ext)
                : UniquePath(Path.GetDirectoryName(first.FilePath) ?? ".", SanitizeName(name!), ext);

            List<string> parts;
            if (reencode)
            {
                // Normalize each input to a uniform mp4 first.
                var w = first.Width > 0 ? first.Width : 1280;
                var h = first.Height > 0 ? first.Height : 720;
                parts = new List<string>();
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (_progress.StopRequested || ct.IsCancellationRequested)
                        throw new OperationCanceledException();
                    _progress.SetCurrent($"Re-encoding {ordered[i].FileName}");
                    var seg = Path.Combine(tempDir, $"norm_{i:D4}.mp4");
                    await NormalizeAsync(ordered[i], seg, w, h, ct);
                    parts.Add(seg);
                    _progress.CompletedOne();
                }
            }
            else
            {
                parts = ordered.Select(v => v.FilePath).ToList();
            }

            _progress.SetCurrent("Joining…");
            await ConcatAsync(parts, outPath, tempDir, ct);
            if (!File.Exists(outPath))
                throw new InvalidOperationException("ffmpeg produced no output file.");
            _progress.CompletedOne();

            await IngestAsync(outPath, ordered, jobId, ct);
            _md5Signal.Signal();
            _thumbSignal.Signal();
            _progress.End("done");
        }
        catch (OperationCanceledException)
        {
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Join run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    // Re-encode one input to H.264/AAC at w×h, padding to keep aspect, and
    // synthesizing silent stereo audio when the source has none (so every
    // normalized segment has matching streams for a clean -c copy concat).
    private static async Task NormalizeAsync(Video v, string outPath, int w, int h, CancellationToken ct)
    {
        var vf = $"scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1";
        if (v.AudioStreamCount > 0)
        {
            await MediaExport.RunFfmpegAsync(ct,
                "-i", v.FilePath,
                "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-ar", "48000", "-ac", "2",
                outPath);
        }
        else
        {
            await MediaExport.RunFfmpegAsync(ct,
                "-i", v.FilePath,
                "-f", "lavfi", "-i", "anullsrc=channel_layout=stereo:sample_rate=48000",
                "-map", "0:v:0", "-map", "1:a",
                "-vf", vf,
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-shortest",
                outPath);
        }
    }

    private static async Task ConcatAsync(List<string> parts, string outPath, string tempDir, CancellationToken ct)
    {
        var sb = new StringBuilder();
        foreach (var p in parts)
            sb.Append("file '").Append(p.Replace("'", @"'\''")).Append("'\n");
        var listPath = Path.Combine(tempDir, "concat.txt");
        await File.WriteAllTextAsync(listPath, sb.ToString(), ct);
        await MediaExport.RunFfmpegAsync(ct,
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy", outPath);
    }

    private async Task IngestAsync(string outPath, List<Video> sources, Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
        var joined = await MediaExport.BuildVideoFromFileAsync(_metadata, outPath, jobId, _logger, ct);
        // Union of the inputs' tags.
        var tagIds = sources.SelectMany(s => s.VideoTags.Select(t => t.TagId)).ToHashSet();
        foreach (var tid in tagIds)
            joined.VideoTags.Add(new VideoTag { TagId = tid });
        db.Videos.Add(joined);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Joined {Count} videos -> {Path} ({NewId})", sources.Count, outPath, joined.Id);
    }

    private static string SanitizeName(string baseName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(baseName
            .Where(c => !invalid.Contains(c) && c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
            .ToArray()).Trim().Trim('.');
        return cleaned.Length == 0 ? "joined" : cleaned;
    }

    private static string UniquePath(string dir, string stem, string ext)
    {
        for (var i = 1; ; i++)
        {
            var name = i == 1 ? stem : $"{stem}-{i.ToString(CultureInfo.InvariantCulture)}";
            var candidate = Path.Combine(dir, $"{name}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
