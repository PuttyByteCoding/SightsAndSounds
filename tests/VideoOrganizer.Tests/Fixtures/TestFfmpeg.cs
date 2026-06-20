using Xabe.FFmpeg;

namespace VideoOrganizer.Tests.Fixtures;

/// <summary>
/// One shared, idempotent ffmpeg setup for every ffmpeg-touching test (issue #106).
///
/// Both the synthetic-media tests and the WebApplicationFactory boot used to
/// configure ffmpeg differently — one pointed Xabe at <c>/usr/bin</c>, the other
/// at <c>&lt;BaseDir&gt;/ffmpeg</c> (where the app + Xabe's no-path downloader also
/// look). Across repeated runs <c>&lt;BaseDir&gt;/ffmpeg</c> flip-flopped between a
/// Xabe-downloaded *file* and a symlink *directory*, and the global
/// <see cref="FFmpeg.ExecutablesPath"/> raced when collections ran in parallel —
/// so the ffprobe tests would intermittently fail locally.
///
/// This makes everyone agree on a single location: <c>&lt;BaseDir&gt;/ffmpeg</c>, a
/// directory of symlinks to the system ffmpeg/ffprobe — the exact dir Program.cs
/// uses, so the app under test finds the binaries already present and never
/// downloads. Combined with disabled cross-collection parallelism
/// (see <c>AssemblyInfo.cs</c>), the global state can't race.
/// </summary>
public static class TestFfmpeg
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <summary>True once the system ffmpeg/ffprobe were located and linked in.</summary>
    public static bool Available { get; private set; }

    /// <summary>The shared executables directory (also set as FFmpeg.ExecutablesPath).</summary>
    public static string Dir { get; private set; } = string.Empty;

    public static void Ensure()
    {
        lock (Gate)
        {
            if (_initialized) return;

            var dir = Path.Combine(AppContext.BaseDirectory, "ffmpeg");
            // A prior run (or Xabe's no-path downloader) may have left a *file*
            // named "ffmpeg" here; we need it to be a directory.
            if (File.Exists(dir)) File.Delete(dir);
            Directory.CreateDirectory(dir);

            var found = true;
            foreach (var name in new[] { "ffmpeg", "ffprobe" })
            {
                var system = FindOnPath(name);
                if (system is null) { found = false; continue; }
                var link = Path.Combine(dir, Path.GetFileName(system));
                try { if (!File.Exists(link)) File.CreateSymbolicLink(link, system); }
                catch { /* symlink failed — leave it; the app would fall back to downloading */ }
            }

            FFmpeg.SetExecutablesPath(dir);
            Dir = dir;
            Available = found;
            _initialized = true;
        }
    }

    /// <summary>The ffmpeg executable inside the shared dir (a symlink to the system binary).</summary>
    public static string FfmpegExe =>
        Path.Combine(Dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    private static string? FindOnPath(string name)
    {
        var exe = OperatingSystem.IsWindows() ? name + ".exe" : name;
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir, exe); if (File.Exists(p)) return p; }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
