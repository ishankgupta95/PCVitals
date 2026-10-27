namespace PCVitals;

/// <summary>
/// Where the taskbar strip may go and how it shrinks to fit there. Pure functions (no windows,
/// no drawing) so the rules can be unit-tested.
/// </summary>
static class StripFit
{
    /// <summary>
    /// The free stretch of taskbar the strip may use: between the app icons and the tray, or, for
    /// "left side", between the left edge and the icons (when there's room there).
    /// </summary>
    /// <summary>
    /// Windows 10's app-button area stretches to the tray however few buttons there are; trim it to
    /// the last button (<paramref name="buttons"/>: their screen rects). Windows 11 reports the real
    /// extent, which this leaves alone.
    /// </summary>
    public static bool StretchesToTray(Rectangle tasks, Rectangle tray) =>
        tasks.Width > 0 && tray.Width > 0 && tasks.Right >= tray.Left - 2;

    public static Rectangle TrimTasks(Rectangle tasks, IEnumerable<Rectangle> buttons)
    {
        int right = buttons.Where(b => b.Width > 0 && b.Left >= tasks.Left - 1 && b.Right <= tasks.Right + 1)
                           .Select(b => b.Right).DefaultIfEmpty(tasks.Left).Max();
        return Rectangle.FromLTRB(tasks.Left, tasks.Top, right, tasks.Bottom);
    }

    public static (int Left, int Right, bool AnchorLeft) FreeRegion(TaskbarInfo tb, WidgetPosition position, float s)
    {
        int gap = (int)(8 * s);
        int trayLeft = tb.Tray.Width > 0 ? tb.Tray.Left : tb.Bounds.Right - (int)(240 * s);
        var icons = tb.Icons;
        if (position == WidgetPosition.LeftSide && !icons.IsEmpty && icons.Left - tb.Bounds.Left > 160 * s)
            return (tb.Bounds.Left + gap, icons.Left - gap, true);
        int left = !icons.IsEmpty && icons.Right < trayLeft ? icons.Right + gap : tb.Bounds.Left + gap;
        return (left, trayLeft - gap, position == WidgetPosition.LeftSide);
    }

    /// <summary>
    /// Shrinks the strip until it fits <paramref name="available"/> pixels, so it never covers
    /// Start or app icons: first hide graphs, then scale text down to 70%, then drop items from
    /// the end of the list (always keeping at least one).
    /// </summary>
    /// <param name="measure">Width in pixels of the first n items at a scale, with or without graphs.</param>
    public static (float Scale, bool Graphs, int Shown, string? Note) Fit(
        int count, Func<int, float, bool, int> measure, int available, float dpiScale, int userScalePercent, bool graphs)
    {
        float scale = dpiScale * Math.Clamp(userScalePercent, 60, 150) / 100f;
        if (count == 0) return (scale, graphs, 0, null);
        if (measure(count, scale, graphs) <= available) return (scale, graphs, count, null);

        if (graphs && measure(count, scale, false) <= available)
            return (scale, false, count, "Graphs are hidden so the stats fit between the app icons and the tray.");

        float min = dpiScale * 0.7f, step = dpiScale * 0.05f;
        while (scale - step >= min - 0.0001f && measure(count, scale, false) > available) scale -= step;
        int shown = count;
        while (shown > 1 && measure(shown, scale, false) > available) shown--;
        var note = shown < count
            ? $"{count - shown} item{(count - shown == 1 ? " is" : "s are")} hidden: there isn't room between the app icons and the tray. " +
              "Turn some items off, make the strip smaller, or move it to the left side."
            : "The stats were made smaller and graphs hidden so they fit between the app icons and the tray.";
        return (scale, false, shown, note);
    }
}
