using System.ComponentModel;
using System.Diagnostics;

namespace VideoOrganizer.API.Services;

// Thin wrapper around the Tesseract CLI (issue #5). Shelling out to the
// `tesseract` binary — rather than the native-interop NuGet — keeps OCR
// portable: a Mac/Linux deployment just needs `tesseract` on PATH (brew /
// apt) instead of matching libtesseract/libleptonica binaries against the
// managed binding. It also mirrors how the app already invokes ffmpeg/ffprobe.
//
// The executable and language are configurable via Ocr:TesseractPath and
// Ocr:Language (defaults: "tesseract" on PATH, "eng").
public sealed class OcrService
{
    private readonly string _exe;
    private readonly string _lang;
    private readonly ILogger<OcrService> _logger;

    public OcrService(IConfiguration config, ILogger<OcrService> logger)
    {
        _exe = config["Ocr:TesseractPath"] is { Length: > 0 } p ? p : "tesseract";
        _lang = config["Ocr:Language"] is { Length: > 0 } l ? l : "eng";
        _logger = logger;
    }

    // Thrown when the tesseract binary can't be launched, so callers can map
    // it to a 503 with an actionable install hint instead of a generic 500.
    public sealed class OcrUnavailableException(string message) : Exception(message);

    // Run tesseract over an image file and return the recognized text.
    // psm 6 ("assume a uniform block of text") reads on-screen captions /
    // title cards better than the default page-segmentation mode.
    public async Task<string> RecognizeAsync(string imagePath, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(imagePath);
        psi.ArgumentList.Add("stdout");
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add(_lang);
        psi.ArgumentList.Add("--psm");
        psi.ArgumentList.Add("6");

        Process proc;
        try
        {
            proc = Process.Start(psi)
                ?? throw new OcrUnavailableException("Failed to start the tesseract process.");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new OcrUnavailableException(
                $"Tesseract OCR ('{_exe}') was not found. Install it " +
                "(macOS: brew install tesseract, Debian/Ubuntu: apt install tesseract-ocr, " +
                "Windows: choco install tesseract) or set Ocr:TesseractPath.");
        }

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0)
        {
            _logger.LogWarning("tesseract exited {Code}: {Stderr}", proc.ExitCode, stderr.Trim());
            throw new InvalidOperationException($"OCR failed (tesseract exit {proc.ExitCode}).");
        }
        return stdout.Trim();
    }
}
