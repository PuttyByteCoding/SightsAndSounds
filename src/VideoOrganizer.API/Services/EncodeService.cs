using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Import.Services;
using VideoOrganizer.Infrastructure.Data;

namespace VideoOrganizer.API.Services;

// Encodes/converts videos to a configurable profile (issue #164). Two backends,
// selected by the "Encode" config section:
//   · ffmpeg (default): a raw ffmpeg argument string (Encode:FfmpegArgs).
//   · handbrake: HandBrakeCLI with an imported preset (Encode:HandBrakePresetFile
//     + PresetName). Optional — if HandBrakeCLI isn't installed the run fails
//     with a clear message (the same "failure detection" the issue asks for).
// Output is "<stem>_encoded.mp4", ingested as a fresh top-level video carrying
// the source's tags. Works on anything ffmpeg/HandBrake can read, including
// animated GIFs. One run at a time; reuses the shared MediaExport engine.
public sealed class EncodeService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IVideoMetadataService _metadata;
    private readonly EncodeProgress _progress;
    private readonly Md5BackfillSignal _md5Signal;
    private readonly ThumbnailWarmingSignal _thumbSignal;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConfiguration _config;
    private readonly ILogger<EncodeService> _logger;

    // Sensible browser-friendly default when Encode:FfmpegArgs isn't configured.
    private const string DefaultFfmpegArgs =
        "-c:v libx264 -preset medium -crf 20 -pix_fmt yuv420p -c:a aac -b:a 192k -movflags +faststart";

    public EncodeService(
        IServiceScopeFactory scopeFactory, IVideoMetadataService metadata,
        EncodeProgress progress, Md5BackfillSignal md5Signal,
        ThumbnailWarmingSignal thumbSignal, IHostApplicationLifetime lifetime,
        IConfiguration config, ILogger<EncodeService> logger)
    {
        _scopeFactory = scopeFactory;
        _metadata = metadata;
        _progress = progress;
        _md5Signal = md5Signal;
        _thumbSignal = thumbSignal;
        _lifetime = lifetime;
        _config = config;
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
        var jobId = Guid.NewGuid();
        var producedAny = false;
        try
        {
            foreach (var id in videoIds)
            {
                if (_progress.StopRequested || ct.IsCancellationRequested) break;
                try
                {
                    await EncodeOneAsync(id, jobId, ct);
                    producedAny = true;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to encode {VideoId}", id);
                    _progress.AddError($"{id}: {ex.Message}");
                }
                finally { _progress.CompletedOne(); }
            }

            if (producedAny) { _md5Signal.Signal(); _thumbSignal.Signal(); }
            _progress.End("done");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Encode run failed");
            _progress.AddError(ex.Message);
            _progress.End("error");
        }
    }

    private async Task EncodeOneAsync(Guid videoId, Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<VideoOrganizerDbContext>();

        var video = await db.Videos.Include(v => v.VideoTags)
            .FirstOrDefaultAsync(v => v.Id == videoId, ct)
            ?? throw new InvalidOperationException("Video not found.");
        if (!File.Exists(video.FilePath))
            throw new InvalidOperationException("Source file is missing on disk.");

        _progress.SetCurrent(video.FileName);
        var outPath = EncodedOutputPath(video.FilePath);

        var backend = (_config["Encode:Backend"] ?? "ffmpeg").Trim().ToLowerInvariant();
        if (backend == "handbrake")
            await RunHandBrakeAsync(video.FilePath, outPath, ct);
        else
            await RunFfmpegProfileAsync(video.FilePath, outPath, ct);

        if (!File.Exists(outPath))
            throw new InvalidOperationException("The encoder produced no output file.");

        var encoded = await MediaExport.BuildVideoFromFileAsync(_metadata, outPath, jobId, _logger, ct);
        if (encoded.Duration <= TimeSpan.Zero)
        {
            try { File.Delete(outPath); } catch { /* best-effort */ }
            throw new InvalidOperationException("Encode produced an unreadable file.");
        }
        foreach (var t in video.VideoTags)
            encoded.VideoTags.Add(new VideoTag { TagId = t.TagId });
        db.Videos.Add(encoded);
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Encoded {VideoId} -> {Path} ({NewId})", videoId, outPath, encoded.Id);
    }

    private Task RunFfmpegProfileAsync(string input, string output, CancellationToken ct)
    {
        var args = (_config["Encode:FfmpegArgs"] is { Length: > 0 } a ? a : DefaultFfmpegArgs)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var argv = new List<string> { "-i", input };
        argv.AddRange(args);
        argv.Add(output);
        return MediaExport.RunFfmpegAsync(ct, argv.ToArray());
    }

    // HandBrakeCLI --preset-import-file <file> --preset <name> -i <in> -o <out>.
    // Throws a clear message when HandBrakeCLI isn't on PATH (failure detection).
    private async Task RunHandBrakeAsync(string input, string output, CancellationToken ct)
    {
        var presetFile = _config["Encode:HandBrakePresetFile"];
        var presetName = _config["Encode:HandBrakePresetName"];

        var psi = new ProcessStartInfo
        {
            FileName = "HandBrakeCLI",
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        if (!string.IsNullOrWhiteSpace(presetFile))
        {
            psi.ArgumentList.Add("--preset-import-file");
            psi.ArgumentList.Add(presetFile);
        }
        if (!string.IsNullOrWhiteSpace(presetName))
        {
            psi.ArgumentList.Add("--preset");
            psi.ArgumentList.Add(presetName);
        }
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(input);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start HandBrakeCLI.");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                "HandBrakeCLI is not installed (Encode:Backend is 'handbrake'). Install it or switch to the ffmpeg backend.");
        }
        using var kill = ct.Register(() => { try { proc.Kill(entireProcessTree: true); } catch { } });
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException($"HandBrakeCLI failed (exit {proc.ExitCode}): {tail}");
        }
    }

    private static string EncodedOutputPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        for (var i = 1; ; i++)
        {
            var suffix = i == 1 ? "_encoded" : $"_encoded-{i.ToString(CultureInfo.InvariantCulture)}";
            var candidate = Path.Combine(dir, $"{stem}{suffix}.mp4");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
