using System.Windows;
using VoltManager.Models;

namespace VoltManager.Services;

public readonly record struct PixelRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public double Area => Math.Max(0, Width) * Math.Max(0, Height);

    public double IntersectionArea(PixelRect other)
    {
        double left = Math.Max(X, other.X);
        double top = Math.Max(Y, other.Y);
        double right = Math.Min(Right, other.Right);
        double bottom = Math.Min(Bottom, other.Bottom);
        if (right <= left || bottom <= top) return 0;
        return (right - left) * (bottom - top);
    }

    public bool Intersects(PixelRect other, double gapX = 0, double gapY = 0)
    {
        // Expand only this rect by the full gap so adjacent widgets with exactly
        // `gap` separation are considered non-overlapping.
        return !(Right + gapX <= other.X
            || other.Right + gapX <= X
            || Bottom + gapY <= other.Y
            || other.Bottom + gapY <= Y);
    }
}

public sealed record LayoutRequest(
    string Type,
    string DesiredMonitorId,
    string Anchor,
    double OffsetX,
    double OffsetY,
    Size SizeDip);

public sealed record WidgetPlacement(
    string Type,
    DisplayInfo DesiredDisplay,
    DisplayInfo EffectiveDisplay,
    string RequestedAnchor,
    string EffectiveAnchor,
    PixelRect BaseBounds,
    PixelRect FinalBounds,
    double AppliedOffsetX,
    double AppliedOffsetY,
    bool UsesFallbackDisplay);

public static class WidgetLayout
{
    public const double MarginDip = 24;
    public const double GapDip = 12;

    private static readonly string[] Anchors = WidgetSettings.Anchors;

    private static readonly Dictionary<string, (int Col, int Row)> AnchorGrid =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["topLeft"] = (0, 0),
            ["topCenter"] = (1, 0),
            ["topRight"] = (2, 0),
            ["middleLeft"] = (0, 1),
            ["center"] = (1, 1),
            ["middleRight"] = (2, 1),
            ["bottomLeft"] = (0, 2),
            ["bottomCenter"] = (1, 2),
            ["bottomRight"] = (2, 2),
        };

    public static IReadOnlyList<WidgetPlacement> Calculate(
        IReadOnlyList<LayoutRequest> requests,
        DisplaySnapshot displays)
    {
        if (requests.Count == 0) return Array.Empty<WidgetPlacement>();

        var primary = displays.Primary;
        var occupied = new Dictionary<string, List<PixelRect>>(StringComparer.OrdinalIgnoreCase);
        var results = new List<WidgetPlacement>(requests.Count);

        foreach (var request in requests)
        {
            var found = ResolveDisplay(request.DesiredMonitorId, displays);
            DisplayInfo desired;
            DisplayInfo effective;
            bool fallback;
            if (found != null)
            {
                desired = found;
                effective = found;
                fallback = false;
            }
            else
            {
                effective = primary;
                fallback = !string.IsNullOrEmpty(request.DesiredMonitorId)
                    && !string.Equals(request.DesiredMonitorId, primary.Id, StringComparison.OrdinalIgnoreCase);
                desired = fallback
                    ? new DisplayInfo(
                        request.DesiredMonitorId,
                        primary.Number,
                        primary.Name,
                        primary.WorkArea,
                        primary.DpiScaleX,
                        primary.DpiScaleY,
                        primary.IsPrimary)
                    : primary;
            }

            double sx = effective.DpiScaleX <= 0 ? 1 : effective.DpiScaleX;
            double sy = effective.DpiScaleY <= 0 ? 1 : effective.DpiScaleY;
            double widthPx = request.SizeDip.Width * sx;
            double heightPx = request.SizeDip.Height * sy;
            double marginX = MarginDip * sx;
            double marginY = MarginDip * sy;
            double gapX = GapDip * sx;
            double gapY = GapDip * sy;
            double offsetXPx = request.OffsetX * sx;
            double offsetYPx = request.OffsetY * sy;

            if (!occupied.TryGetValue(effective.Id, out var occupiedList))
            {
                occupiedList = new List<PixelRect>();
                occupied[effective.Id] = occupiedList;
            }

            string requestedAnchor = WidgetSettings.NormalizeAnchor(request.Anchor);
            var candidates = GetOverflowOrder(requestedAnchor);
            WidgetPlacement? chosen = null;

            foreach (var anchor in candidates)
            {
                var slots = EnumerateSlots(
                    effective.WorkArea, widthPx, heightPx, marginX, marginY, gapX, gapY, anchor);

                foreach (var baseBounds in slots)
                {
                    var withOffset = new PixelRect(
                        baseBounds.X + offsetXPx,
                        baseBounds.Y + offsetYPx,
                        widthPx,
                        heightPx);
                    var finalBounds = ClampToWorkArea(withOffset, effective.WorkArea);

                    if (occupiedList.Any(o => o.Intersects(finalBounds, gapX, gapY)))
                        continue;

                    double appliedOffsetX = (finalBounds.X - baseBounds.X) / sx;
                    double appliedOffsetY = (finalBounds.Y - baseBounds.Y) / sy;

                    chosen = new WidgetPlacement(
                        request.Type,
                        desired,
                        effective,
                        requestedAnchor,
                        anchor,
                        baseBounds,
                        finalBounds,
                        appliedOffsetX,
                        appliedOffsetY,
                        fallback);
                    break;
                }

                if (chosen != null) break;
            }

            if (chosen == null)
            {
                // Best-effort: place at requested anchor origin, clamped.
                var baseBounds = AnchorOrigin(
                    effective.WorkArea, widthPx, heightPx, marginX, marginY, requestedAnchor);
                var withOffset = new PixelRect(
                    baseBounds.X + offsetXPx,
                    baseBounds.Y + offsetYPx,
                    widthPx,
                    heightPx);
                var finalBounds = ClampToWorkArea(withOffset, effective.WorkArea);
                chosen = new WidgetPlacement(
                    request.Type,
                    desired,
                    effective,
                    requestedAnchor,
                    requestedAnchor,
                    baseBounds,
                    finalBounds,
                    (finalBounds.X - baseBounds.X) / sx,
                    (finalBounds.Y - baseBounds.Y) / sy,
                    fallback);
            }

            occupiedList.Add(chosen.FinalBounds);
            results.Add(chosen);
        }

        return results;
    }

    public static (DisplayInfo Display, string Anchor, double OffsetX, double OffsetY)
        MigrateLegacy(PixelRect legacyBounds, DisplaySnapshot displays)
    {
        var display = SelectDisplayForBounds(legacyBounds, displays);
        double sx = display.DpiScaleX <= 0 ? 1 : display.DpiScaleX;
        double sy = display.DpiScaleY <= 0 ? 1 : display.DpiScaleY;
        double marginX = MarginDip * sx;
        double marginY = MarginDip * sy;

        var clamped = ClampToWorkArea(legacyBounds, display.WorkArea);

        string bestAnchor = "topRight";
        double bestDist = double.MaxValue;
        PixelRect bestOrigin = default;

        foreach (var anchor in Anchors)
        {
            var origin = AnchorOrigin(
                display.WorkArea, clamped.Width, clamped.Height, marginX, marginY, anchor);
            double dx = clamped.X - origin.X;
            double dy = clamped.Y - origin.Y;
            double dist = dx * dx + dy * dy;
            if (dist < bestDist)
            {
                bestDist = dist;
                bestAnchor = anchor;
                bestOrigin = origin;
            }
        }

        return (
            display,
            bestAnchor,
            (clamped.X - bestOrigin.X) / sx,
            (clamped.Y - bestOrigin.Y) / sy);
    }

    public static IReadOnlyList<string> GetOverflowOrder(string requestedAnchor)
    {
        string anchor = WidgetSettings.NormalizeAnchor(requestedAnchor);
        var (col, row) = AnchorGrid[anchor];
        return Anchors
            .Select((a, index) =>
            {
                var (c, r) = AnchorGrid[a];
                int manhattan = Math.Abs(c - col) + Math.Abs(r - row);
                return (Anchor: a, Manhattan: manhattan, Index: index);
            })
            .OrderBy(x => x.Manhattan)
            .ThenBy(x => x.Index)
            .Select(x => x.Anchor)
            .ToArray();
    }

    private static DisplayInfo? ResolveDisplay(string id, DisplaySnapshot displays)
        => displays.Displays.FirstOrDefault(d =>
            string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    private static DisplayInfo SelectDisplayForBounds(PixelRect bounds, DisplaySnapshot displays)
    {
        DisplayInfo? best = null;
        double bestArea = -1;
        double bestCenterDist = double.MaxValue;
        double cx = bounds.X + bounds.Width / 2;
        double cy = bounds.Y + bounds.Height / 2;

        foreach (var display in displays.Displays)
        {
            double area = bounds.IntersectionArea(display.WorkArea);
            double dx = cx - (display.WorkArea.X + display.WorkArea.Width / 2);
            double dy = cy - (display.WorkArea.Y + display.WorkArea.Height / 2);
            double dist = dx * dx + dy * dy;
            if (area > bestArea || (Math.Abs(area - bestArea) < 0.001 && dist < bestCenterDist))
            {
                best = display;
                bestArea = area;
                bestCenterDist = dist;
            }
        }

        return best ?? displays.Primary;
    }

    private static IEnumerable<PixelRect> EnumerateSlots(
        PixelRect workArea,
        double widthPx,
        double heightPx,
        double marginX,
        double marginY,
        double gapX,
        double gapY,
        string anchor)
    {
        var (col, row) = AnchorGrid[WidgetSettings.NormalizeAnchor(anchor)];
        double originX = ColumnX(workArea, widthPx, marginX, col);
        double originY = RowY(workArea, heightPx, marginY, row);

        // Generate a generous number of stack candidates along the stack axis.
        for (int i = 0; i < 24; i++)
        {
            double x = originX;
            double y = originY;

            if (row == 0)
            {
                // top → down
                y = originY + i * (heightPx + gapY);
            }
            else if (row == 2)
            {
                // bottom → up
                y = originY - i * (heightPx + gapY);
            }
            else
            {
                // center, below, above alternating
                y = CenterStackY(originY, heightPx, gapY, i);
            }

            var rect = new PixelRect(x, y, widthPx, heightPx);
            var clamped = ClampToWorkArea(rect, workArea);
            // Skip slots that would leave the work area entirely when unclamped far away.
            if (clamped.Width <= 0 || clamped.Height <= 0) continue;
            if (!IsMostlyInside(clamped, workArea, widthPx, heightPx)) continue;
            yield return new PixelRect(clamped.X, clamped.Y, widthPx, heightPx);
        }
    }

    private static double CenterStackY(double originY, double heightPx, double gapY, int index)
    {
        if (index == 0) return originY;
        int step = (index + 1) / 2;
        bool below = index % 2 == 1;
        return below
            ? originY + step * (heightPx + gapY)
            : originY - step * (heightPx + gapY);
    }

    private static bool IsMostlyInside(PixelRect rect, PixelRect workArea, double widthPx, double heightPx)
    {
        // Accept only slots whose clamped origin still keeps the full widget size
        // without needing further shrink (we never shrink widgets).
        return rect.X >= workArea.X - 0.5
            && rect.Y >= workArea.Y - 0.5
            && rect.Right <= workArea.Right + 0.5
            && rect.Bottom <= workArea.Bottom + 0.5
            && Math.Abs(rect.Width - widthPx) < 0.5
            && Math.Abs(rect.Height - heightPx) < 0.5;
    }

    private static PixelRect AnchorOrigin(
        PixelRect workArea,
        double widthPx,
        double heightPx,
        double marginX,
        double marginY,
        string anchor)
    {
        var (col, row) = AnchorGrid[WidgetSettings.NormalizeAnchor(anchor)];
        return new PixelRect(
            ColumnX(workArea, widthPx, marginX, col),
            RowY(workArea, heightPx, marginY, row),
            widthPx,
            heightPx);
    }

    private static double ColumnX(PixelRect workArea, double widthPx, double marginX, int col)
        => col switch
        {
            0 => workArea.X + marginX,
            2 => workArea.Right - marginX - widthPx,
            _ => workArea.X + (workArea.Width - widthPx) / 2,
        };

    private static double RowY(PixelRect workArea, double heightPx, double marginY, int row)
        => row switch
        {
            0 => workArea.Y + marginY,
            2 => workArea.Bottom - marginY - heightPx,
            _ => workArea.Y + (workArea.Height - heightPx) / 2,
        };

    private static PixelRect ClampToWorkArea(PixelRect rect, PixelRect workArea)
    {
        double maxX = Math.Max(workArea.X, workArea.Right - rect.Width);
        double maxY = Math.Max(workArea.Y, workArea.Bottom - rect.Height);
        double x = Math.Clamp(rect.X, workArea.X, maxX);
        double y = Math.Clamp(rect.Y, workArea.Y, maxY);
        return new PixelRect(x, y, rect.Width, rect.Height);
    }
}
