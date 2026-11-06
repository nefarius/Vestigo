using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using Vestigo.Desktop;

using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Vestigo.Vision;

/// <summary>Recognizes text lines with the built-in <c>Windows.Media.Ocr</c> engine. Runs inside the session helper.</summary>
public static class OcrReader
{
    public const int MaxLines = 200;

    /// <summary>
    ///     Lines come back in image pixels shifted by <paramref name="origin" />, so they are virtual-screen
    ///     physical pixels like every other element.
    /// </summary>
    public static (IReadOnlyList<ScreenElement> Lines, string Status) Read(byte[] png, DesktopRect origin)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return ([], "ocr_unavailable: Windows 10 2004 or later is required.");
        }

        try
        {
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null)
            {
                return ([], "ocr_unavailable: no OCR language pack is installed for the user's languages.");
            }

            using InMemoryRandomAccessStream stream = new();
            stream.WriteAsync(png.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
            if (decoder.PixelWidth > OcrEngine.MaxImageDimension || decoder.PixelHeight > OcrEngine.MaxImageDimension)
            {
                return ([], $"ocr_unavailable: the image exceeds the engine limit of {OcrEngine.MaxImageDimension} pixels; capture a smaller target.");
            }

            using SoftwareBitmap bitmap = decoder.GetSoftwareBitmapAsync().AsTask().GetAwaiter().GetResult();
            OcrResult recognized = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            List<ScreenElement> lines = [];
            bool truncated = false;
            foreach (OcrLine line in recognized.Lines)
            {
                if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0)
                {
                    continue;
                }

                if (lines.Count >= MaxLines)
                {
                    truncated = true;
                    break;
                }

                double left = line.Words.Min(word => word.BoundingRect.Left);
                double top = line.Words.Min(word => word.BoundingRect.Top);
                double right = line.Words.Max(word => word.BoundingRect.Right);
                double bottom = line.Words.Max(word => word.BoundingRect.Bottom);
                string text = line.Text.Trim();
                lines.Add(new ScreenElement(
                    0,
                    ElementSource.Ocr,
                    "text",
                    null,
                    text.Length <= 200 ? text : text[..200],
                    new DesktopRect(
                        origin.X + (int)Math.Floor(left),
                        origin.Y + (int)Math.Floor(top),
                        Math.Max(1, (int)Math.Ceiling(right - left)),
                        Math.Max(1, (int)Math.Ceiling(bottom - top)))));
            }

            return (lines, truncated ? "truncated" : "ok");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            return ([], "ocr_unavailable: " + ex.Message);
        }
    }
}
