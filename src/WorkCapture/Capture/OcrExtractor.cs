using System.Runtime.InteropServices.WindowsRuntime;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace WorkCapture.Capture;

/// <summary>
/// On-device OCR via the built-in Windows engine (Windows.Media.Ocr). Free, offline, CPU —
/// NOT a cloud/LLM/VLM. Pulls visible text off the screenshot (RMM client-name columns,
/// hostnames, banners) to feed the server-side deterministic matcher.
///
/// Input is the in-memory ImageSharp frame, converted straight to a BGRA8 SoftwareBitmap —
/// no encode/decode, so there is no WebP/codec dependency. Fully guarded: any failure (or no
/// OCR language pack installed) returns null and the engine is marked unavailable so we don't
/// retry on every frame. Callers gate this to thin-signal frames to keep CPU cost off the loop.
/// </summary>
public static class OcrExtractor
{
    private static OcrEngine? _engine;
    private static bool _initFailed;
    private static readonly object _lock = new();

    private static OcrEngine? Engine()
    {
        if (_engine != null) return _engine;
        if (_initFailed) return null;
        lock (_lock)
        {
            if (_engine != null) return _engine;
            if (_initFailed) return null;
            try
            {
                _engine = OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch (Exception ex)
            {
                Logger.Debug($"OCR engine init failed: {ex.Message}");
            }
            if (_engine == null)
            {
                _initFailed = true;
                Logger.Info("OCR unavailable (no engine / no language pack) — skipping OCR signals");
            }
            return _engine;
        }
    }

    /// <summary>OCR an in-memory frame. Returns bounded single-line text, or null.</summary>
    public static string? Recognize(Image<Rgba32> image, int maxChars = 2000)
    {
        var engine = Engine();
        if (engine == null || image == null) return null;
        try
        {
            using var bgra = image.CloneAs<Bgra32>();
            var bytes = new byte[bgra.Width * bgra.Height * 4];
            bgra.CopyPixelDataTo(bytes);

            using var sb = SoftwareBitmap.CreateCopyFromBuffer(
                bytes.AsBuffer(), BitmapPixelFormat.Bgra8,
                bgra.Width, bgra.Height, BitmapAlphaMode.Premultiplied);

            var result = engine.RecognizeAsync(sb).AsTask().GetAwaiter().GetResult();
            var text = result?.Text;
            if (string.IsNullOrWhiteSpace(text)) return null;

            text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
        catch (Exception ex)
        {
            Logger.Debug($"OCR failed: {ex.Message}");
            return null;
        }
    }
}
