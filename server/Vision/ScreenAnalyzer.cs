using OpenCvSharp;

using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>Result of <see cref="ScreenAnalyzer.Analyze" />. Element boxes are physical pixels.</summary>
public sealed record AnalysisResult(
    IReadOnlyList<ScreenElement> Elements,
    DesktopSize ImageSize,
    byte[] WorkingPng,
    byte[]? AnnotatedPng,
    SourceCounts Candidates,
    SourceCounts Omitted,
    int PriorityOmitted);

/// <summary>OpenCV image work: contour fallback, set-of-marks and grid annotation, diff, template matching.</summary>
public static class ScreenAnalyzer
{
    public const int DefaultMaxEdge = 1280;

    public const int MinMaxEdge = 320;

    public const int MaxMaxEdge = 3840;

    private const int MinRegionEdge = 14;

    private const int MaxCvRegions = 40;

    public const int MaxTemplateMatches = 5;

    /// <summary>Downscales the capture, merges the element lists, and draws the annotated or grid image.</summary>
    public static AnalysisResult Analyze(
        byte[] fullPng,
        DesktopRect bounds,
        IReadOnlyList<ScreenElement> uia,
        IReadOnlyList<ScreenElement> ocr,
        int maxEdge,
        bool grid,
        bool annotate,
        Func<ScreenElement, bool>? priority)
    {
        using Mat full = Decode(fullPng);
        using Mat working = Downscale(full, maxEdge);
        double sx = (double)bounds.Width / working.Width;
        double sy = (double)bounds.Height / working.Height;

        List<ScreenElement> cv = [];
        foreach (Rect rect in FindRegions(working))
        {
            cv.Add(new ScreenElement(
                0,
                ElementSource.Cv,
                "region",
                null,
                null,
                new DesktopRect(
                    bounds.X + (int)Math.Round(rect.X * sx),
                    bounds.Y + (int)Math.Round(rect.Y * sy),
                    Math.Max(1, (int)Math.Round(rect.Width * sx)),
                    Math.Max(1, (int)Math.Round(rect.Height * sy)))));
        }

        MergeResult merged = ElementMerger.Merge(uia, ocr, cv, priority: priority);
        byte[] workingPng = Encode(working);
        byte[]? annotatedPng = null;
        if (annotate)
        {
            using Mat canvas = working.Clone();
            if (grid)
            {
                DrawGrid(canvas);
            }
            else
            {
                DrawMarks(canvas, merged.Elements, bounds);
            }

            annotatedPng = Encode(canvas);
        }

        return new AnalysisResult(
            merged.Elements,
            new DesktopSize(working.Width, working.Height),
            workingPng,
            annotatedPng,
            merged.Candidates,
            merged.Omitted,
            merged.PriorityOmitted);
    }

    /// <summary>Returns regions that changed between two same-sized PNG images, in image pixels.</summary>
    public static DiffResult Diff(byte[] previousPng, byte[] currentPng)
    {
        using Mat before = Decode(previousPng);
        using Mat after = Decode(currentPng);
        if (before.Size() != after.Size())
        {
            throw new ArgumentException("Images differ in size.");
        }

        using Mat delta = new();
        Cv2.Absdiff(before, after, delta);
        using Mat gray = new();
        Cv2.CvtColor(delta, gray, ColorConversionCodes.BGR2GRAY);
        using Mat mask = new();
        Cv2.Threshold(gray, mask, 24, 255, ThresholdTypes.Binary);
        double changed = Cv2.CountNonZero(mask) * 100.0 / ((double)mask.Width * mask.Height);

        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 15));
        using Mat merged = new();
        Cv2.Dilate(mask, merged, kernel);
        Cv2.FindContours(merged, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        List<ChangedRegion> regions = contours
            .Select(Cv2.BoundingRect)
            .Where(rect => rect.Width >= 4 && rect.Height >= 4)
            .OrderByDescending(rect => (long)rect.Width * rect.Height)
            .Take(20)
            .Select(rect => new ChangedRegion(new DesktopRect(rect.X, rect.Y, rect.Width, rect.Height)))
            .ToList();
        return new DiffResult(regions, Math.Round(changed, 3));
    }

    /// <summary>Multi-scale normalized template match on the working image. Returns up to five non-overlapping candidates.</summary>
    public static IReadOnlyList<TemplateMatch> FindTemplate(byte[] imagePng, byte[] templatePng, double minConfidence)
    {
        using Mat image = Decode(imagePng);
        using Mat template = Decode(templatePng);
        using Mat imageGray = new();
        using Mat templateGray = new();
        Cv2.CvtColor(image, imageGray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(template, templateGray, ColorConversionCodes.BGR2GRAY);

        List<TemplateMatch> candidates = [];
        for (double scale = 0.3; scale <= 1.65; scale *= 1.12)
        {
            int width = (int)Math.Round(templateGray.Width * scale);
            int height = (int)Math.Round(templateGray.Height * scale);
            if (width < 8 || height < 8 || width > imageGray.Width || height > imageGray.Height)
            {
                continue;
            }

            using Mat scaled = new();
            Cv2.Resize(templateGray, scaled, new Size(width, height), 0, 0, scale < 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
            using Mat result = new();
            Cv2.MatchTemplate(imageGray, scaled, result, TemplateMatchModes.CCoeffNormed);
            for (int found = 0; found < MaxTemplateMatches; found++)
            {
                Cv2.MinMaxLoc(result, out _, out double max, out _, out Point location);
                if (double.IsNaN(max) || max < minConfidence)
                {
                    break;
                }

                candidates.Add(new TemplateMatch(new DesktopRect(location.X, location.Y, width, height), Math.Round(max, 4)));
                // Suppress the neighbourhood so the next maximum is a different place.
                int left = Math.Max(0, location.X - width / 2);
                int top = Math.Max(0, location.Y - height / 2);
                int right = Math.Min(result.Width, location.X + width / 2);
                int bottom = Math.Min(result.Height, location.Y + height / 2);
                result.Rectangle(new Point(left, top), new Point(right, bottom), Scalar.All(-1), -1);
            }
        }

        List<TemplateMatch> picked = [];
        foreach (TemplateMatch candidate in candidates.OrderByDescending(item => item.Confidence))
        {
            if (picked.All(other => ElementMerger.IntersectionOverUnion(other.Box, candidate.Box) < 0.3))
            {
                picked.Add(candidate);
                if (picked.Count == MaxTemplateMatches)
                {
                    break;
                }
            }
        }

        return picked;
    }

    /// <summary>Crops a PNG to <paramref name="region" /> (image pixels), clipped to the image. Returns null when nothing is left.</summary>
    public static byte[]? Crop(byte[] png, DesktopRect region)
    {
        using Mat image = Decode(png);
        DesktopRect? clipped = region.Intersection(new DesktopRect(0, 0, image.Width, image.Height));
        if (clipped is null)
        {
            return null;
        }

        using Mat cropped = new(image, new Rect(clipped.X, clipped.Y, clipped.Width, clipped.Height));
        return Encode(cropped);
    }

    public static DesktopSize ImageSize(byte[] png)
    {
        using Mat image = Decode(png);
        return new DesktopSize(image.Width, image.Height);
    }

    private static Mat Decode(byte[] png)
    {
        Mat image = Cv2.ImDecode(png, ImreadModes.Color);
        if (image.Empty())
        {
            image.Dispose();
            throw new ArgumentException("Image data could not be decoded.");
        }

        return image;
    }

    private static byte[] Encode(Mat image)
    {
        Cv2.ImEncode(".png", image, out byte[] bytes);
        return bytes;
    }

    private static Mat Downscale(Mat source, int maxEdge)
    {
        int edge = Math.Max(source.Width, source.Height);
        if (edge <= maxEdge)
        {
            return source.Clone();
        }

        double scale = (double)maxEdge / edge;
        Mat scaled = new();
        Cv2.Resize(
            source,
            scaled,
            new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale))),
            0,
            0,
            InterpolationFlags.Area);
        return scaled;
    }

    /// <summary>Edge-based rectangular regions, the fallback where UI Automation and OCR report nothing.</summary>
    private static List<Rect> FindRegions(Mat image)
    {
        using Mat gray = new();
        Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        using Mat blurred = new();
        Cv2.GaussianBlur(gray, blurred, new Size(3, 3), 0);
        using Mat edges = new();
        Cv2.Canny(blurred, edges, 40, 120);
        using Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        using Mat closed = new();
        Cv2.MorphologyEx(edges, closed, MorphTypes.Close, kernel);
        Cv2.FindContours(closed, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        double imageArea = (double)image.Width * image.Height;
        return contours
            .Select(Cv2.BoundingRect)
            .Where(rect => rect.Width >= MinRegionEdge && rect.Height >= MinRegionEdge && (double)rect.Width * rect.Height < imageArea * 0.6)
            .OrderByDescending(rect => (long)rect.Width * rect.Height)
            .Take(MaxCvRegions)
            .ToList();
    }

    private static Scalar ColorFor(string source) => source switch
    {
        ElementSource.Uia => new Scalar(60, 180, 0),
        ElementSource.Ocr => new Scalar(220, 120, 0),
        _ => new Scalar(30, 60, 230)
    };

    private static void DrawMarks(Mat canvas, IReadOnlyList<ScreenElement> elements, DesktopRect bounds)
    {
        double sx = (double)canvas.Width / bounds.Width;
        double sy = (double)canvas.Height / bounds.Height;
        foreach (ScreenElement element in elements)
        {
            int x = (int)Math.Round((element.Box.X - bounds.X) * sx);
            int y = (int)Math.Round((element.Box.Y - bounds.Y) * sy);
            int width = Math.Max(2, (int)Math.Round(element.Box.Width * sx));
            int height = Math.Max(2, (int)Math.Round(element.Box.Height * sy));
            Scalar color = ColorFor(element.Source);
            Cv2.Rectangle(canvas, new Rect(x, y, width, height), color, 1);

            string label = element.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Size text = Cv2.GetTextSize(label, HersheyFonts.HersheySimplex, 0.45, 1, out int baseline);
            int labelTop = Math.Max(0, y - text.Height - baseline - 2);
            Cv2.Rectangle(canvas, new Rect(x, labelTop, text.Width + 4, text.Height + baseline + 2), color, -1);
            Cv2.PutText(canvas, label, new Point(x + 2, labelTop + text.Height + 1), HersheyFonts.HersheySimplex, 0.45, Scalar.White, 1, LineTypes.AntiAlias);
        }
    }

    private static void DrawGrid(Mat canvas)
    {
        int step = canvas.Width >= 1600 ? 200 : 100;
        Scalar line = new(0, 0, 255);
        for (int x = step; x < canvas.Width; x += step)
        {
            Cv2.Line(canvas, new Point(x, 0), new Point(x, canvas.Height), line, 1);
            Cv2.PutText(canvas, x.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point(x + 2, 12), HersheyFonts.HersheySimplex, 0.4, line, 1, LineTypes.AntiAlias);
        }

        for (int y = step; y < canvas.Height; y += step)
        {
            Cv2.Line(canvas, new Point(0, y), new Point(canvas.Width, y), line, 1);
            Cv2.PutText(canvas, y.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point(2, y - 3), HersheyFonts.HersheySimplex, 0.4, line, 1, LineTypes.AntiAlias);
        }
    }
}
