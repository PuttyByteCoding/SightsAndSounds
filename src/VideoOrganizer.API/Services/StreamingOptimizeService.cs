using System.Buffers.Binary;
using System.Text;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// "Optimize for streaming" (issue #166). Large MP4s buffer before they start
// playing when their 'moov' atom sits at the END of the file (after 'mdat') —
// the player has to fetch the tail before it can begin. This remuxes such files
// in place with a lossless `-c copy -movflags +faststart` (moov moved to the
// front), so playback starts immediately. No re-encode → fast (seconds even for
// multi-GB files) and bit-identical media. Already-faststart and non-MP4 files
// are skipped. The remux goes to a temp file and is verified before atomically
// replacing the original; the row's FileSize is updated and Md5 cleared so the
// backfill worker re-hashes (the container changed, the frames didn't).
public sealed class StreamingOptimizeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly StreamingOptimizeProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StreamingOptimizeService> _logger;

    public StreamingOptimizeService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        StreamingOptimizeProgress progress, Md5BackfillSignal md5Signal,
        IHostApplicationLifetime lifetime, ILogger<StreamingOptimizeService> logger)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _progress = progress;
        _md5Signal = md5Signal;
        _lifetime = lifetime;
        _logger = logger;
    }

    public enum StartResult { Started, AlreadyRunning, NothingToDo }

    public async Task<StartResult> TryStartAsync(IReadOnlyList<Guid> videoIds, CancellationToken ct)
    {
        if (_progress.IsActive) return StartResult.AlreadyRunning;

        List<Guid> valid;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            valid = await db.Videos.AsNoTracking()
                .Where(v => videoIds.Contains(v.Id) && v.ParentVideoId == null)
                .Select(v => v.Id)
                .ToListAsync(ct);
        }
        if (valid.Count == 0) return StartResult.NothingToDo;

        _progress.Begin(valid.Count);
        _ = Task.Run(() => RunAsync(valid));
        return StartResult.Started;
    }

    private async Task RunAsync(List<Guid> videoIds)
    {
        var ct = _lifetime.ApplicationStopping;
        var optimizedAny = false;
        try
        {
            foreach (var id in videoIds)
            {
                if (_progress.StopRequested || ct.IsCancellationRequested) break;
                try
                {
                    if (await OptimizeOneAsync(id, ct)) optimizedAny = true;
                    else _progress.RecordSkipped();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to optimize {VideoId}", id);
                    _progress.AddError($"{id}: {ex.Message}");
                }
                finally { _progress.CompletedOne(); }
            }

            if (optimizedAny) _md5Signal.Signal();   // re-hash the changed files
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Streaming-optimize run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
    }

    // Returns true if the file was remuxed; false if skipped (already faststart
    // or not an MP4).
    private async Task<bool> OptimizeOneAsync(Guid videoId, CancellationToken ct)
    {
        string path;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            var v = await db.Videos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == videoId, ct)
                ?? throw new InvalidOperationException("Video not found.");
            path = v.FilePath;
        }
        if (!File.Exists(path)) throw new InvalidOperationException("Source file is missing on disk.");

        var faststart = await IsFaststartAsync(path, ct);
        if (faststart != false) return false;   // already faststart, or not an MP4 → nothing to do

        _progress.SetCurrent(Path.GetFileName(path));

        var dir = Path.GetDirectoryName(path) ?? ".";
        var ext = Path.GetExtension(path);
        var tmp = Path.Combine(dir, $".{Path.GetFileNameWithoutExtension(path)}.faststart.{Guid.NewGuid():N}{ext}");
        try
        {
            await MediaExport.RunFfmpegAsync(ct,
                "-i", path, "-map", "0", "-c", "copy", "-movflags", "+faststart", tmp);

            // Verify the remux before we overwrite the original.
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0)
                throw new InvalidOperationException("ffmpeg produced no output.");
            var meta = await _metadata.GetMetadataAsync(tmp, ct);
            if (meta is null || (meta.Duration ?? TimeSpan.Zero) <= TimeSpan.Zero)
                throw new InvalidOperationException("Optimized file failed verification.");

            // Atomically replace the original (same volume), then update the row.
            File.Move(tmp, path, overwrite: true);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();
            var size = new FileInfo(path).Length;
            await db.Videos.Where(v => v.Id == videoId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.FileSize, size)
                    .SetProperty(v => v.Md5, (string?)null)
                    .SetProperty(v => v.Md5Failed, false), ct);

            _progress.RecordOptimized();
            _logger.LogInformation("Optimized {VideoId} for streaming (faststart): {Path}", videoId, path);
            return true;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    // Scans top-level MP4/MOV atoms to decide faststart: true if 'moov' comes
    // before 'mdat', false if 'mdat' is first (buffers before playback), null if
    // not an MP4-family file or the layout can't be determined. Cheap — reads
    // only atom headers, seeking past each atom's payload.
    public static async Task<bool?> IsFaststartAsync(string path, CancellationToken ct)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".mp4" or ".m4v" or ".mov")) return null;

        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 64 * 1024, useAsync: true);
        var header = new byte[16];
        long pos = 0;
        var len = fs.Length;
        while (pos + 8 <= len)
        {
            fs.Position = pos;
            var got = await fs.ReadAsync(header.AsMemory(0, 8), ct);
            if (got < 8) break;
            var size32 = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = Encoding.ASCII.GetString(header, 4, 4);
            if (type == "moov") return true;
            if (type == "mdat") return false;

            long atomSize = size32;
            if (size32 == 1)
            {
                // 64-bit size follows the type.
                var got2 = await fs.ReadAsync(header.AsMemory(8, 8), ct);
                if (got2 < 8) break;
                atomSize = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
            }
            else if (size32 == 0)
            {
                // Atom runs to EOF — nothing more to scan.
                break;
            }
            if (atomSize < 8) break;   // malformed
            pos += atomSize;
        }
        return null;
    }
}
