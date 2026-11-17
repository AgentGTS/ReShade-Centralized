namespace ReShadeCentralized;

internal sealed record VulkanGameEntry(string Executable, bool IsManaged) {
    public override string ToString() => IsManaged ? $"{Executable} (Managed)" : Executable;
}

internal sealed class VulkanGamesList : ListBox {
    public VulkanGamesList() {
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 26;
        IntegralHeight = false;
        HorizontalScrollbar = true;
    }

    public void SetEntries(IEnumerable<VulkanGameEntry> entries) {
        VulkanGameEntry[] replacement = entries.ToArray();
        int extent = replacement
            .Select(entry => TextRenderer.MeasureText(entry.ToString(), Font).Width + LogicalToDeviceUnits(16))
            .DefaultIfEmpty(0)
            .Max();
        if (Items.Cast<VulkanGameEntry>().SequenceEqual(replacement)) {
            if (HorizontalExtent != extent)
                HorizontalExtent = extent;

            return;
        }

        string? selected = (SelectedItem as VulkanGameEntry)?.Executable;
        int topIndex = TopIndex;
        string? top = topIndex >= 0 && topIndex < Items.Count
            ? ((VulkanGameEntry)Items[topIndex]!).Executable : null;

        BeginUpdate();
        try {
            Items.Clear();
            Items.AddRange(replacement);

            HorizontalExtent = extent;
            SelectedIndex = Array.FindIndex(replacement, entry =>
                entry.Executable.Equals(selected, StringComparison.OrdinalIgnoreCase));

            if (replacement.Length > 0) {
                int restoredTop = Array.FindIndex(replacement, entry =>
                    entry.Executable.Equals(top, StringComparison.OrdinalIgnoreCase));
                TopIndex = restoredTop >= 0 ? restoredTop : Math.Clamp(topIndex, 0, replacement.Length - 1);
            }
        } finally {
            EndUpdate();
        }
    }

    protected override void OnDrawItem(DrawItemEventArgs e) {
        if (e.Index < 0 || e.Index >= Items.Count || Items[e.Index] is not VulkanGameEntry entry)
            return;

        bool selected = (e.State & DrawItemState.Selected) != 0;
        Color background = selected
            ? entry.IsManaged ? DarkTheme.Raised : DarkTheme.Selection
            : DarkTheme.Input;
        Color foreground = entry.IsManaged ? DarkTheme.Muted : DarkTheme.Text;
        if (SystemInformation.HighContrast) {
            foreground = entry.IsManaged ? SystemColors.GrayText :
                selected ? SystemColors.HighlightText : SystemColors.WindowText;
        }

        using var fill = new SolidBrush(background);
        e.Graphics.FillRectangle(fill, e.Bounds);
        var textBounds = new Rectangle(e.Bounds.Left + LogicalToDeviceUnits(8), e.Bounds.Top,
            Math.Max(0, e.Bounds.Width - LogicalToDeviceUnits(16)), e.Bounds.Height);
        TextRenderer.DrawText(e.Graphics, entry.ToString(), e.Font, textBounds, foreground,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if ((e.State & DrawItemState.Focus) != 0)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -1, -1), foreground, background);
    }
}
