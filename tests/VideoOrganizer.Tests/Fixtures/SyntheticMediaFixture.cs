using System.Diagnostics;
using VideoOrganizer.Domain.Models;
using Xabe.FFmpeg;
using Xunit;
// Both Xabe.FFmpeg and the domain define a "VideoCodec"; we mean the domain's.
using VideoCodec = VideoOrganizer.Domain.Models.VideoCodec;

namespace VideoOrganizer.Tests.Fixtures;

/// <summary>
/// One synthetic clip generated for the test run, plus what the code under test
/// should derive from it. Values are known because we encoded the file ourselves.
/// </summary>
public sealed record SyntheticClip(
    string Path,
    int Width,
    int Height,
    string Codec,
    VideoCodec ExpectedCodec,
    VideoDimensionFormat ExpectedFormat,
    int AudioStreams);

public enum ClipKind { H264, Hevc, Hd1080, Uhd2160 }

/// <summary>
/// Generates a small matrix of synthetic video files with ffmpeg into a temp
/// directory, once per test run, and tears it down afterward.
///
/// Why synthetic: this project holds private home movies — real footage must
/// never reach CI. The code under test (ffprobe metadata, codec/dimension
/// helpers, thumbnail generation) only cares about container/codec headers, not
/// content, so a 2–6s testsrc clip exercises it fully at KB sizes. Nothing is
/// committed; everything is regenerated and deleted each run, keeping CI private
/// and storage-free.
///
/// If ffmpeg/ffprobe aren't on PATH (e.g. a dev box without them) the fixture
/// reports <see cref="FfmpegAvailable"/> = false and the integration tests skip
/// rather than fail. CI installs ffmpeg, so there they run for real.
/// </summary>
public sealed class SyntheticMediaFixture : IAsyncLifetime
{
    public bool FfmpegAvailable { get; private set; }
    public string? SkipReason { get; private set; }

    public string CorruptPath { get; private set; } = string.Empty;

    private string _root = string.Empty;
    private string _ffmpeg = string.Empty;
    private SyntheticClip? _h264, _hevc, _hd1080, _uhd2160;

    public SyntheticClip Clip(ClipKind kind) => kind switch
    {
        ClipKind.H264 => _h264!,
        ClipKind.Hevc => _hevc!,
        ClipKind.Hd1080 => _hd1080!,
        ClipKind.Uhd2160 => _uhd2160!,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public async Task InitializeAsync()
    {
        var ffmpegDir = LocateFfmpegDir();
        if (ffmpegDir is null)
        {
            FfmpegAvailable = false;
            SkipReason = "ffmpeg/ffprobe not found on PATH";
            return;
        }

        // Point Xabe.FFmpeg (used by the services under test) at the system
        // binaries so it finds them in place and never tries to download —
        // keeps the suite hermetic and offline.
        FFmpeg.SetExecutablesPath(ffmpegDir);
        _ffmpeg = Path.Combine(ffmpegDir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

        _root = Path.Combine(Path.GetTempPath(), "sas-test-media-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        try
        {
            // Baseline: H.264 8-bit + AAC audio, the "plays everywhere" case.
            // Longer (6s) so thumbnail generation yields several frames.
            _h264 = await MakeAsync("h264_sd.mp4",
                "-f lavfi -i testsrc=duration=6:size=640x480:rate=15 " +
                "-f lavfi -i sine=frequency=440:duration=6 " +
                "-c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 30 -c:a aac -shortest",
                640, 480, "h264", VideoCodec.H264, VideoDimensionFormat.SD480p4x3, audioStreams: 1);

            // HEVC — regression anchor for the codec-detection bug that broke
            // browser playback. hvc1 tag mirrors what Apple/VideoToolbox emits.
            _hevc = await MakeAsync("hevc_sd.mp4",
                "-f lavfi -i testsrc=duration=3:size=640x480:rate=15 " +
                "-c:v libx265 -pix_fmt yuv420p -preset ultrafast -crf 30 -tag:v hvc1",
                640, 480, "hevc", VideoCodec.HEVC, VideoDimensionFormat.SD480p4x3, audioStreams: 0);

            // Resolution coverage for VideoDimensionFormatHelper.
            _hd1080 = await MakeAsync("h264_1080.mp4",
                "-f lavfi -i testsrc=duration=2:size=1920x1080:rate=10 " +
                "-c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 32",
                1920, 1080, "h264", VideoCodec.H264, VideoDimensionFormat.HD1080p, audioStreams: 0);

            _uhd2160 = await MakeAsync("h264_2160.mp4",
                "-f lavfi -i testsrc=duration=2:size=3840x2160:rate=5 " +
                "-c:v libx264 -pix_fmt yuv420p -preset ultrafast -crf 34",
                3840, 2160, "h264", VideoCodec.H264, VideoDimensionFormat.UHD4K, audioStreams: 0);

            // Not a media file at all — ffprobe must fail and the metadata
            // service must return null (the import "skip corrupt file" path).
            CorruptPath = Path.Combine(_root, "corrupt.mp4");
            var garbage = new byte[1024];
            for (var i = 0; i < garbage.Length; i++) garbage[i] = (byte)(i % 251);
            await File.WriteAllBytesAsync(CorruptPath, garbage);

            FfmpegAvailable = true;
        }
        catch (Exception ex)
        {
            // A missing encoder (e.g. libx265) or any generation failure degrades
            // to "skip with a reason" rather than failing every test in the run.
            FfmpegAvailable = false;
            SkipReason = "synthetic media generation failed: " + ex.Message;
        }
    }

    public Task DisposeAsync()
    {
        try { if (_root.Length > 0 && Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort temp cleanup */ }
        return Task.CompletedTask;
    }

    private async Task<SyntheticClip> MakeAsync(
        string name, string args, int width, int height, string codec,
        VideoCodec expectedCodec, VideoDimensionFormat expectedFormat, int audioStreams)
    {
        var path = Path.Combine(_root, name);
        var argv = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        argv.AddRange(args.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        argv.Add(path);

        await RunFfmpegAsync(argv);

        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new InvalidOperationException($"ffmpeg produced no output for {name}");

        return new SyntheticClip(path, width, height, codec, expectedCodec, expectedFormat, audioStreams);
    }

    private async Task RunFfmpegAsync(IEnumerable<string> argv)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _ffmpeg,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (var a in argv) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start ffmpeg");
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0)
        {
            var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
            throw new InvalidOperationException(
                $"ffmpeg exited {proc.ExitCode} for [{string.Join(' ', psi.ArgumentList)}]:\n{tail}");
        }
    }

    // First PATH directory that contains both ffmpeg and ffprobe.
    private static string? LocateFfmpegDir()
    {
        var exe = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var probe = OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exe)) && File.Exists(Path.Combine(dir, probe)))
                    return dir;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return null;
    }
}

[CollectionDefinition("SyntheticMedia")]
public sealed class SyntheticMediaCollection : ICollectionFixture<SyntheticMediaFixture> { }
