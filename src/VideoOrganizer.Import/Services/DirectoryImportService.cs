using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using VideoOrganizer.Domain.Models;
using VideoOrganizer.Infrastructure.Data;
using VideoOrganizer.Shared;
using VideoOrganizer.Shared.Dto;

namespace VideoOrganizer.Import.Services;

// Reads a directory, extracts ffprobe metadata for every new video file, and
// inserts a Video row for each. Md5 is intentionally deferred to the
// background Md5BackfillService.
//
// Tags applied at import time come from the caller as `initialTagIds`. The
// service no longer cares which group a tag belongs to or what it's named —
// it just attaches every passed tag id to every imported video.
public class DirectoryImportService
{
    private readonly VideoOrganizerDbContext _dbContext;
    private readonly ILogger<DirectoryImportService> _logger;
    private readonly IVideoMetadataService _metadataService;

    public DirectoryImportService(
        VideoOrganizerDbContext dbContext,
        ILogger<DirectoryImportService> logger,
        IVideoMetadataService metadataService)
    {
        _dbContext = dbContext;
        _logger = logger;
        _metadataService = metadataService;
    }

    public async Task ImportFromDirectoryAsync(
        string directoryPath,
        IReadOnlyList<Guid>? initialTagIds = null,
        string? notes = null,
        bool includeSubdirectories = true,
        Action<string, long, ImportFileStatus, long, long, string?>? fileStatusReporter = null,
        Guid? importJobId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
            throw new ArgumentException("Path is required.", nameof(directoryPath));

        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var searchOption = includeSubdirectories
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        var files = Directory.EnumerateFiles(directoryPath, "*.*", searchOption)
            .Where(f => VideoFileExtensions.IsVideo(f))
            .Where(f => !PathFilters.IsInExcludedFolder(f, directoryPath))
            .ToList();

        _logger.LogInformation("Found {Count} video files in {Path}", files.Count, directoryPath);

        // Pre-register every discovered file as Pending so the queue modal lists them.
        foreach (var f in files)
        {
            var size = 0L;
            try { var fi = new FileInfo(f); if (fi.Exists) size = fi.Length; } catch { }
            fileStatusReporter?.Invoke(f, size, ImportFileStatus.Pending, 0, size, null);
        }

        // Validate initial tag ids once.
        var validTagIds = new List<Guid>();
        if (initialTagIds is not null && initialTagIds.Count > 0)
        {
            var distinct = initialTagIds.Distinct().ToList();
            validTagIds = await _dbContext.Tags
                .Where(t => distinct.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);
            var missing = distinct.Except(validTagIds).ToList();
            if (missing.Count > 0)
                _logger.LogWarning("Skipping {Count} unknown initial tag id(s): {Ids}",
                    missing.Count, string.Join(", ", missing));
        }

        const int batchSize = 50;
        var added = 0;
        // Roll-up counters for the end-of-import log so operators don't have
        // to grep per-file Debug entries to answer "how many were duplicates?"
        var skippedAsDuplicate = 0;
        var failed = 0;

        for (var i = 0; i < files.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var batch = files.Skip(i).Take(batchSize);
            var batchAdds = new List<(string FilePath, long FileSize)>();

            foreach (var file in batch)
            {
                var fileInfo = new FileInfo(file);
                var fileSize = fileInfo.Exists ? fileInfo.Length : 0L;
                fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Importing, 0, fileSize, null);
                try
                {
                    var fileName = Path.GetFileName(file);
                    var filePath = PathNormalizer.Normalize(Path.GetFullPath(file));

                    _logger.LogDebug("Importing {FileName}: {FilePath}", fileName, filePath);

                    try
                    {
                        using var existsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        existsCts.CancelAfter(TimeSpan.FromSeconds(30));

                        var exists = await _dbContext.Videos
                            .AsNoTracking()
                            .AnyAsync(v => v.FilePath == filePath
                                        && v.FileName == fileName
                                        && v.ParentVideoId == null,
                                existsCts.Token);

                        if (exists)
                        {
                            _logger.LogDebug("Skipping existing file {File}", file);
                            fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Skipped, fileSize, fileSize, "already imported");
                            skippedAsDuplicate++;
                            continue;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Timed out checking existing file {File}", file);
                        fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Failed, fileSize, fileSize, "timed out checking for existing row");
                        failed++;
                        continue;
                    }

                    var info = fileInfo;

                    var video = new Video
                    {
                        FileName = fileName,
                        FilePath = filePath,
                        Md5 = null,
                        FileSize = info.Length,
                        IngestDate = DateTime.UtcNow,
                        VideoQuality = Domain.Models.VideoQuality.NotChecked,
                        CameraType = Domain.Models.CameraTypes.NotChecked,
                        ImportJobId = importJobId,
                        NeedsReview = true,  // every newly-imported video starts unreviewed
                    };

                    try
                    {
                        using var metadataCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        metadataCts.CancelAfter(TimeSpan.FromSeconds(30));
                        var metadata = await _metadataService.GetMetadataAsync(file, metadataCts.Token);

                        if (metadata != null)
                        {
                            video.Duration = metadata.Duration ?? TimeSpan.Zero;
                            video.Height = metadata.Height ?? 0;
                            video.Width = metadata.Width ?? 0;
                            video.VideoCodec = CodecHelper.GetVideoCodec(metadata.VideoCodec ?? string.Empty);
                            video.Bitrate = metadata.VideoBitrate ?? 0;
                            video.FrameRate = metadata.FrameRate ?? 0;
                            video.PixelFormat = metadata.PixelFormat ?? string.Empty;
                            video.Ratio = metadata.Ratio ?? string.Empty;
                            video.CreationTime = metadata.CreationTime;
                            video.VideoStreamCount = metadata.VideoStreamCount ?? 0;
                            video.AudioStreamCount = metadata.AudioStreamCount ?? 0;
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger.LogWarning("Timed out extracting metadata for {File}", file);
                    }

                    if (video.Duration <= TimeSpan.Zero)
                    {
                        _logger.LogWarning(
                            "Skipping import of {File} (size {FileSize} bytes): duration is 0 or unknown — file is probably corrupt or truncated, or ffprobe couldn't read it.",
                            file, fileSize);
                        fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Failed, fileSize, fileSize, "duration is 0 (probably corrupt or truncated)");
                        failed++;
                        continue;
                    }

                    video.VideoDimensionFormat = VideoDimensionFormatHelper.GetDimensionFormat(video.Height, video.Width);

                    foreach (var tid in validTagIds)
                        video.VideoTags.Add(new VideoTag { TagId = tid });

                    if (!string.IsNullOrWhiteSpace(notes))
                        video.Notes = notes;

                    _dbContext.Videos.Add(video);
                    batchAdds.Add((file, fileSize));
                    added++;
                    fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Completed, fileSize, fileSize, null);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to import file {File}", file);
                    fileStatusReporter?.Invoke(file, fileSize, ImportFileStatus.Failed, fileSize, fileSize, ex.Message);
                    failed++;
                }
            }

            try
            {
                using var saveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                saveCts.CancelAfter(TimeSpan.FromSeconds(30));
                await _dbContext.SaveChangesAsync(saveCts.Token);
                _logger.LogInformation("Imported batch up to index {Index}. Total added so far: {Added}", i + batchSize, added);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning("Timed out saving batch starting at index {Index}", i);
                foreach (var entry in batchAdds)
                    fileStatusReporter?.Invoke(entry.FilePath, entry.FileSize, ImportFileStatus.Failed, entry.FileSize, entry.FileSize, "timed out saving batch to database");
                failed += batchAdds.Count;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save batch starting at index {Index}", i);
                foreach (var entry in batchAdds)
                    fileStatusReporter?.Invoke(entry.FilePath, entry.FileSize, ImportFileStatus.Failed, entry.FileSize, entry.FileSize, ex.Message);
                failed += batchAdds.Count;
            }
        }

        _logger.LogInformation(
            "Directory import complete for {Path}: {Added} added, {SkippedAsDuplicate} skipped as duplicates, {Failed} failed of {Total} discovered files.",
            directoryPath, added, skippedAsDuplicate, failed, files.Count);
    }
}
