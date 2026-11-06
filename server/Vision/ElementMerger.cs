using Vestigo.Desktop;

namespace Vestigo.Vision;

/// <summary>How many elements a source contributed. <see cref="Total" /> is the sum.</summary>
public sealed record SourceCounts(int Uia, int Ocr, int Cv)
{
    /// <summary>The sum of the three source counts.</summary>
    public int Total => Uia + Ocr + Cv;
}

/// <summary>
///     The numbered merge. <paramref name="Candidates" /> survived deduplication and overlap removal;
///     <paramref name="Omitted" /> is how many of those did not fit in the cap. <paramref name="PriorityOmitted" />
///     counts priority matches that were dropped because the matches themselves filled the cap.
/// </summary>
public sealed record MergeResult(
    IReadOnlyList<ScreenElement> Elements,
    SourceCounts Candidates,
    SourceCounts Omitted,
    int PriorityOmitted);

/// <summary>Combines UI Automation, OCR, and computer-vision regions into one numbered list without duplicates.</summary>
public static class ElementMerger
{
    public const int MaxElements = 150;

    /// <summary>OCR lines covered this much are compared with the covering element's name.</summary>
    private const double OcrCoverage = 0.6;

    /// <summary>CV regions covered this much by any UI Automation or OCR candidate are dropped.</summary>
    private const double CvCoverage = 0.3;

    /// <summary>
    ///     All boxes are in one coordinate space; ids on the inputs are ignored and reassigned from 1.
    ///     <paramref name="priority" /> matches are kept first, in reading order. The remaining room is split evenly
    ///     between UI Automation and OCR, and a source that needs less passes its share to the other. Contour regions
    ///     fill whatever is left. Within a source, the elements that do not fit are the lowest on the screen.
    ///     An OCR line covered by a named UI Automation element is dropped when the texts agree, unless
    ///     <paramref name="priority" /> matches the line and not that element.
    /// </summary>
    public static MergeResult Merge(
        IEnumerable<ScreenElement> uia,
        IEnumerable<ScreenElement> ocr,
        IEnumerable<ScreenElement> cv,
        int maxElements = MaxElements,
        Func<ScreenElement, bool>? priority = null)
    {
        List<ScreenElement> uiaCandidates = [];
        foreach (ScreenElement element in uia)
        {
            bool duplicate = uiaCandidates.Any(other => other.Box == element.Box && other.Name == element.Name && other.Role == element.Role);
            if (!duplicate)
            {
                uiaCandidates.Add(element);
            }
        }

        List<ScreenElement> named = uiaCandidates.Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToList();
        List<ScreenElement> ocrCandidates = [];
        foreach (ScreenElement line in ocr)
        {
            if (IsCoveredByPreferredUia(line, named, priority))
            {
                continue;
            }

            ocrCandidates.Add(line);
        }

        List<ScreenElement> placed = [.. uiaCandidates, .. ocrCandidates];
        List<ScreenElement> cvCandidates = [];
        foreach (ScreenElement region in cv)
        {
            if (placed.Any(item => Coverage(region.Box, item.Box) >= CvCoverage))
            {
                continue;
            }

            cvCandidates.Add(region);
        }

        List<ScreenElement> priorityMatches = [];
        List<ScreenElement> uiaRest = [];
        List<ScreenElement> ocrRest = [];
        List<ScreenElement> cvRest = [];
        Divide(uiaCandidates, priority, priorityMatches, uiaRest);
        Divide(ocrCandidates, priority, priorityMatches, ocrRest);
        Divide(cvCandidates, priority, priorityMatches, cvRest);

        int room = Math.Max(0, maxElements);
        List<ScreenElement> kept = [];
        List<ScreenElement> priorityOrdered = InReadingOrder(priorityMatches);
        int priorityKept = Math.Min(room, priorityOrdered.Count);
        kept.AddRange(priorityOrdered.Take(priorityKept));
        room -= priorityKept;

        if (room > 0)
        {
            // The odd leftover goes to UI Automation. Each source then absorbs what the other did not use.
            List<ScreenElement> uiaOrdered = InReadingOrder(uiaRest);
            List<ScreenElement> ocrOrdered = InReadingOrder(ocrRest);
            int uiaHalf = (room + 1) / 2;
            int ocrHalf = room / 2;
            int uiaTake = Math.Min(uiaOrdered.Count, uiaHalf);
            int ocrTake = Math.Min(ocrOrdered.Count, ocrHalf);
            int uiaUnused = uiaHalf - uiaTake;
            int ocrUnused = ocrHalf - ocrTake;
            ocrTake += Math.Min(ocrOrdered.Count - ocrTake, uiaUnused);
            uiaTake += Math.Min(uiaOrdered.Count - uiaTake, ocrUnused);

            int cvTake = Math.Min(cvRest.Count, room - uiaTake - ocrTake);
            kept.AddRange(uiaOrdered.Take(uiaTake));
            kept.AddRange(ocrOrdered.Take(ocrTake));
            kept.AddRange(InReadingOrder(cvRest).Take(cvTake));
        }

        List<ScreenElement> ordered = InReadingOrder(kept);
        List<ScreenElement> numbered = new(ordered.Count);
        for (int index = 0; index < ordered.Count; index++)
        {
            numbered.Add(ordered[index] with { Id = index + 1 });
        }

        int keptUia = numbered.Count(item => item.Source == ElementSource.Uia);
        int keptOcr = numbered.Count(item => item.Source == ElementSource.Ocr);
        int keptCv = numbered.Count(item => item.Source == ElementSource.Cv);
        return new MergeResult(
            numbered,
            new SourceCounts(uiaCandidates.Count, ocrCandidates.Count, cvCandidates.Count),
            new SourceCounts(uiaCandidates.Count - keptUia, ocrCandidates.Count - keptOcr, cvCandidates.Count - keptCv),
            priorityOrdered.Count - priorityKept);
    }

    /// <summary>
    ///     True when a named UI Automation element covers <paramref name="line" /> and their texts agree.
    ///     That element only wins over a priority match when it matches <paramref name="priority" /> too.
    /// </summary>
    private static bool IsCoveredByPreferredUia(
        ScreenElement line,
        List<ScreenElement> named,
        Func<ScreenElement, bool>? priority)
    {
        bool lineMatches = priority?.Invoke(line) == true;
        foreach (ScreenElement item in named)
        {
            if (Coverage(line.Box, item.Box) < OcrCoverage || !TextsAgree(line.Text, item.Name))
            {
                continue;
            }

            if (!lineMatches || priority!(item))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>Overlap of the two boxes divided by the area of their union, 0 to 1.</summary>
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

    /// <summary>Moves elements that match <paramref name="priority" /> into <paramref name="matches" /> and the rest into <paramref name="rest" />.</summary>
    private static void Divide(
        List<ScreenElement> source,
        Func<ScreenElement, bool>? priority,
        List<ScreenElement> matches,
        List<ScreenElement> rest)
    {
        foreach (ScreenElement element in source)
        {
            if (priority?.Invoke(element) == true)
            {
                matches.Add(element);
            }
            else
            {
                rest.Add(element);
            }
        }
    }

    /// <summary>Top to bottom, then left to right. A 12px band keeps one text line in a row.</summary>
    private static List<ScreenElement> InReadingOrder(IEnumerable<ScreenElement> elements) =>
        elements.OrderBy(item => item.Box.Y / 12).ThenBy(item => item.Box.X).ToList();

    /// <summary>True when either normalized text contains the other. Empty text never agrees.</summary>
    private static bool TextsAgree(string? left, string? right)
    {
        string a = Normalize(left);
        string b = Normalize(right);
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Collapses whitespace so comparison ignores line breaks and repeated spaces.</summary>
    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
