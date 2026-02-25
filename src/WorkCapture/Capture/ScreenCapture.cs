using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Webp;

namespace WorkCapture.Capture;

/// <summary>
/// Handles screenshot capture and processing
/// </summary>
public class ScreenCapture : IDisposable
{
    private readonly string _outputDir;
    private readonly string _format;
    private readonly int _quality;
    private readonly int _maxWidth;

    public ScreenCapture(string outputDir, string format = "webp", int quality = 85, int maxWidth = 1920)
    {
        _outputDir = outputDir;
        _format = format.ToLowerInvariant();
        _quality = quality;
        _maxWidth = maxWidth;

        Directory.CreateDirectory(_outputDir);
    }

    /// <summary>
    /// Capture the primary screen (capture + save in one step)
    /// </summary>
    /// <returns>Tuple of (filepath, hash) or null if failed</returns>
    public (string Path, string Hash)? Capture()
    {
        var memCapture = CaptureToMemory();
        if (memCapture == null)
            return null;

        return SaveFromMemory(memCapture);
    }

    /// <summary>
    /// Capture screenshot to memory and compute perceptual hash WITHOUT saving to disk.
    /// Use this to get the hash for change detection before deciding whether to save.
    /// Call SaveFromMemory() to persist if the capture should be kept.
    /// </summary>
    /// <returns>In-memory capture with hash, or null if failed</returns>
    public MemoryCapture? CaptureToMemory()
    {
        try
        {
            // Get screen bounds
            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds
                ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

            // Capture using GDI+
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Location, System.Drawing.Point.Empty, bounds.Size);
            }

            // Convert to ImageSharp for processing
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            ms.Position = 0;

            var image = SixLabors.ImageSharp.Image.Load<Rgba32>(ms);

            // Resize if needed
            if (image.Width > _maxWidth)
            {
                var ratio = (float)_maxWidth / image.Width;
                var newHeight = (int)(image.Height * ratio);
                image.Mutate(x => x.Resize(_maxWidth, newHeight));
            }

            // Calculate perceptual hash
            var hash = CalculatePerceptualHash(image);

            return new MemoryCapture(image, hash);
        }
        catch (Exception ex)
        {
            Logger.Error($"Screenshot capture to memory failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Save a previously captured in-memory screenshot to disk.
    /// Disposes the MemoryCapture after saving.
    /// </summary>
    public (string Path, string Hash)? SaveFromMemory(MemoryCapture memCapture)
    {
        try
        {
            // Generate filename
            var now = DateTime.Now;
            var dateDir = Path.Combine(_outputDir, now.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(dateDir);

            var filename = $"{now:HHmmss_fff}.{_format}";
            var filepath = Path.Combine(dateDir, filename);

            // Save image
            SaveImage(memCapture.Image, filepath);
            var hash = memCapture.Hash;

            // Dispose the in-memory image
            memCapture.Dispose();

            Logger.Debug($"Captured screenshot: {filepath}");
            return (filepath, hash);
        }
        catch (Exception ex)
        {
            Logger.Error($"Screenshot save failed: {ex.Message}");
            memCapture.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Calculate a perceptual hash for change detection using 16x16 grid (256 bits).
    /// 16x16 provides 4x finer spatial resolution than 8x8, enabling detection of
    /// within-app navigation changes (e.g. Server Manager section changes).
    /// </summary>
    private static string CalculatePerceptualHash(Image<Rgba32> image)
    {
        const int hashSize = 16; // 16x16 = 256 bits (was 8x8 = 64 bits)
        using var small = image.Clone(x => x.Resize(hashSize, hashSize).Grayscale());

        int totalPixels = hashSize * hashSize; // 256
        double total = 0;
        var pixels = new double[totalPixels];
        int i = 0;

        for (int y = 0; y < hashSize; y++)
        {
            for (int x = 0; x < hashSize; x++)
            {
                var pixel = small[x, y];
                pixels[i] = pixel.R; // Already grayscale
                total += pixel.R;
                i++;
            }
        }

        var average = total / totalPixels;

        // Build hash as byte array (256 bits = 32 bytes → 64-char hex string)
        var hashBytes = new byte[totalPixels / 8]; // 32 bytes
        for (int j = 0; j < totalPixels; j++)
        {
            if (pixels[j] > average)
            {
                hashBytes[j / 8] |= (byte)(1 << (j % 8));
            }
        }

        return Convert.ToHexString(hashBytes); // 64-char uppercase hex
    }

    /// <summary>
    /// Calculate Hamming distance between two perceptual hashes.
    /// Supports variable-length byte arrays (handles 16-char legacy and 64-char new hashes).
    /// Mismatched lengths return 64 — triggers one extra capture on first run after update.
    /// </summary>
    public static int HashDifference(string hash1, string hash2)
    {
        if (string.IsNullOrEmpty(hash1) || string.IsNullOrEmpty(hash2))
            return 64;

        // Treat old 16-char hashes vs new 64-char hashes as completely different.
        // This causes one extra capture on first startup after update — harmless.
        if (hash1.Length != hash2.Length)
            return 64;

        try
        {
            var bytes1 = Convert.FromHexString(hash1);
            var bytes2 = Convert.FromHexString(hash2);

            int count = 0;
            for (int i = 0; i < bytes1.Length; i++)
            {
                int xor = bytes1[i] ^ bytes2[i];
                while (xor != 0)
                {
                    count += xor & 1;
                    xor >>= 1;
                }
            }
            return count;
        }
        catch
        {
            return 64;
        }
    }

    private void SaveImage(Image<Rgba32> image, string path)
    {
        switch (_format)
        {
            case "webp":
                var webpEncoder = new WebpEncoder { Quality = _quality };
                image.Save(path, webpEncoder);
                break;

            case "png":
                image.SaveAsPng(path);
                break;

            case "jpg":
            case "jpeg":
                image.SaveAsJpeg(path);
                break;

            default:
                image.SaveAsWebp(path);
                break;
        }
    }

    public void Dispose()
    {
        // Nothing to dispose
    }
}

/// <summary>
/// Holds a screenshot in memory with its perceptual hash.
/// Must be disposed after use (whether saved or discarded).
/// </summary>
public class MemoryCapture : IDisposable
{
    public Image<Rgba32> Image { get; }
    public string Hash { get; }

    public MemoryCapture(Image<Rgba32> image, string hash)
    {
        Image = image;
        Hash = hash;
    }

    public void Dispose()
    {
        Image.Dispose();
    }
}

/// <summary>
/// Cleans up old screenshots
/// </summary>
public static class ScreenshotCleanup
{
    public static int CleanupOldScreenshots(string directory, int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        int removed = 0;

        try
        {
            foreach (var dateDir in Directory.GetDirectories(directory))
            {
                var dirName = Path.GetFileName(dateDir);
                if (DateTime.TryParse(dirName, out var dirDate) && dirDate < cutoff)
                {
                    try
                    {
                        Directory.Delete(dateDir, true);
                        removed++;
                        Logger.Info($"Removed old screenshot directory: {dateDir}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warning($"Failed to delete {dateDir}: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Screenshot cleanup failed: {ex.Message}");
        }

        return removed;
    }
}
