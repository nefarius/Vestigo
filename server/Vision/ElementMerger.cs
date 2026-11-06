using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>Combines UI Automation, OCR, and computer-vision regions into one numbered list without duplicates.</summary>
public static class ElementMerger
{
    public const int MaxElements = 150;

    /// <summary>OCR lines covered this much by a named UI Automation element are dropped.</summary>
    private const double OcrCoverage = 0.6;

    /// <summary>CV regions covered this much by any existing element are dropped.</summary>
    private const double CvCoverage = 0.3;

    /// <summary>All boxes are in one coordinate space; ids on the inputs are ignored and reassigned from 1.</summary>
    public static IReadOnlyList<ScreenElement> Merge(
        IEnumerable<ScreenElement> uia,
        IEnumerable<ScreenElement> ocr,
        IEnumerable<ScreenElement> cv,
        int maxElements = MaxElements)
    {
        List<ScreenElement> kept = [];
        foreach (ScreenElement element in uia)
        {
            bool duplicate = kept.Any(other => other.Box == element.Box && other.Name == element.Name && other.Role == element.Role);
            if (!duplicate && kept.Count < maxElements)
            {
                kept.Add(element);
            }
        }

        List<ScreenElement> named = kept.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToList();
        foreach (ScreenElement line in ocr)
        {
            if (kept.Count >= maxElements)
            {
                break;
            }

            if (named.Any(item => Coverage(line.Box, item.Box) >= OcrCoverage))
            {
                continue;
            }

            kept.Add(line);
        }

        foreach (ScreenElement region in cv)
        {
            if (kept.Count >= maxElements)
            {
                break;
            }

            if (kept.Any(item => Coverage(region.Box, item.Box) >= CvCoverage))
            {
                continue;
            }

            kept.Add(region);
        }

        List<ScreenElement> ordered = kept
            .OrderBy(item => item.Box.Y / 12)
            .ThenBy(item => item.Box.X)
            .ToList();
        List<ScreenElement> numbered = new(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            numbered.Add(ordered[index] with { Id = index + 1 });
        }

        return numbered;
    }

    /// <summary>The share of <paramref name="inner" /> that lies inside <paramref name="outer" />, 0 to 1.</summary>
    public static double Coverage(DesktopRect inner, DesktopRect outer)
    {
        DesktopRect? overlap = inner.Intersection(outer);
        if (overlap is null)
        {
            return 0;
        }

        double area = (double)inner.Width * inner.Height;
        return area <= 0 ? 0 : (double)overlap.Width * overlap.Height / area;
    }

    public static double IntersectionOverUnion(DesktopRect left, DesktopRect right)
    {
        DesktopRect? overlap = left.Intersection(right);
        if (overlap is null)
        {
            return 0;
        }

        double intersection = (double)overlap.Width * overlap.Height;
        double union = (double)left.Width * left.Height + (double)right.Width * right.Height - intersection;
        return union <= 0 ? 0 : intersection / union;
    }
}
