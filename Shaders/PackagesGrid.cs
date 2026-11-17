namespace ReShadeCentralized;

internal sealed class PackagesGrid : AppGrid {
    private bool refreshing;
    private string? previousSearch;

    public event Action<ShaderPackage, bool>? PackageChecked;

    public PackagesGrid() {
        DoubleBuffered = true;
        Dock = DockStyle.Fill;
        AutoGenerateColumns = false;
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells;
        AllowUserToAddRows = false;
        AllowUserToDeleteRows = false;
        RowHeadersVisible = false;
        SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        MultiSelect = false;
        AccessibleName = "Shader packages";
        Columns.Add(new DataGridViewCheckBoxColumn {
            Name = "Selected",
            HeaderText = "Selected",
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        Columns.Add(new DataGridViewTextBoxColumn {
            Name = "Package",
            HeaderText = "Package",
            ReadOnly = true,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        Columns.Add(new DataGridViewTextBoxColumn {
            Name = "Status",
            HeaderText = "Status",
            ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });

        CurrentCellDirtyStateChanged += (_, _) => {
            if (IsCurrentCellDirty)
                CommitEdit(DataGridViewDataErrorContexts.Commit);
        };

        CellValueChanged += (_, e) => {
            if (!refreshing && e.RowIndex >= 0 && e.ColumnIndex == Columns["Selected"]!.Index
                && Rows[e.RowIndex].Tag is ShaderPackage package)
                PackageChecked?.Invoke(package, Rows[e.RowIndex].Cells["Selected"].Value is true);
        };
    }

    protected override void OnHandleCreated(EventArgs e) {
        base.OnHandleCreated(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnFontChanged(EventArgs e) {
        base.OnFontChanged(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnDefaultCellStyleChanged(EventArgs e) {
        base.OnDefaultCellStyleChanged(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) {
        base.OnDpiChangedAfterParent(e);
        UpdateStatusColumnWidth();
    }

    private void UpdateStatusColumnWidth() {
        if (Columns["Status"] is not DataGridViewColumn status)
            return;

        string[] statuses =
        [
            "Not installed (will be added)",
            "Installed (will be removed)",
            "Incomplete (will be removed)"
        ];
        Font statusFont = DefaultCellStyle.Font ?? Font;
        int textWidth = statuses.Max(text => TextRenderer.MeasureText(text, statusFont).Width);
        status.Width = textWidth + DefaultCellStyle.Padding.Horizontal + LogicalToDeviceUnits(8);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e) {
        base.OnCellPainting(e);
        if (e.RowIndex < 0 || e.ColumnIndex != Columns["Selected"]!.Index || e.Graphics == null)
            return;

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        bool locked = Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly || !Enabled;
        bool selected = e.Value is true;
        int size = LogicalToDeviceUnits(16);
        var bounds = new Rectangle(e.CellBounds.Left + ((e.CellBounds.Width - size) / 2),
            e.CellBounds.Top + ((e.CellBounds.Height - size) / 2), size, size);
        DarkTheme.DrawCheckBox(e.Graphics, bounds, selected, locked, LogicalToDeviceUnits(2));
        e.Handled = true;
    }

    public void SetPackages(
        IReadOnlyList<ShaderPackage> packages,
        string search,
        HashSet<string> selected,
        ManagerState? state,
        IReadOnlyDictionary<string, bool>? completeness) {
        HashSet<string> needed = RequiredPackageIds(selected, packages);
        HashSet<string> installed = (state?.Packages ?? []).Select(package => package.Id)
            .ToHashSet(StringComparer.Ordinal);
        int checkColumn = Columns["Selected"]!.Index;
        int packageColumn = Columns["Package"]!.Index;
        int statusColumn = Columns["Status"]!.Index;
        ShaderPackage[] shown = packages.Where(package => search.Length == 0 || $"{package.Name} {package.Description}"
            .Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        bool sameRows = Rows.Count == shown.Length && Rows.Cast<DataGridViewRow>().Select(PackageId)
            .SequenceEqual(shown.Select(package => package.Id), StringComparer.OrdinalIgnoreCase);
        bool sameSearch = string.Equals(previousSearch, search, StringComparison.OrdinalIgnoreCase);
        previousSearch = search;

        refreshing = true;
        try {
            if (sameRows) {
                for (int index = 0; index < shown.Length; index++) {
                    SetRow(Rows[index], shown[index]);
                }
                return;
            }

            GridViewport viewport = GridViewport.Capture(this, PackageId);
            var rows = new DataGridViewRow[shown.Length];
            for (int index = 0; index < shown.Length; index++) {
                var row = (DataGridViewRow)RowTemplate.Clone();
                row.CreateCells(this);
                SetRow(row, shown[index]);
                rows[index] = row;
            }

            DataGridViewAutoSizeColumnsMode sizing = AutoSizeColumnsMode;
            SuspendLayout();
            try {
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
                Rows.Clear();
                if (rows.Length > 0)
                    Rows.AddRange(rows);
            } finally {
                AutoSizeColumnsMode = sizing;
                ResumeLayout();
            }
            if (viewport.TopIndex >= 0 || viewport.CurrentId != null)
                viewport.Restore(this, PackageId, preserveIndex: sameSearch);
        } finally {
            refreshing = false;
        }

        void SetRow(DataGridViewRow row, ShaderPackage package) {
            row.Tag = package;
            DataGridViewCell name = row.Cells[packageColumn];
            string label = package.ToString();
            if (name.Value as string != label)
                name.Value = label;

            DataGridViewCell status = row.Cells[statusColumn];
            bool isInstalled = installed.Contains(package.Id);
            status.Tag = !isInstalled ? null :
                completeness?.TryGetValue(package.Id, out bool complete) == true && !complete
                    ? "Incomplete" : "Installed";
            status.ToolTipText = isInstalled && package.InstalledOnly
                ? "Unavailable from the loaded catalog. Keep it selected or uncheck it to remove."
                : "";
            UpdateRow(row.Cells[checkColumn], status, selected.Contains(package.Id),
                package.Required || needed.Contains(package.Id), package.InstalledOnly);
        }
    }

    private static string? PackageId(DataGridViewRow row) => (row.Tag as ShaderPackage)?.Id;

    public void SetChecks(HashSet<string> selected, IReadOnlyList<ShaderPackage> packages) {
        HashSet<string> needed = RequiredPackageIds(selected, packages);
        int checkColumn = Columns["Selected"]!.Index;
        int statusColumn = Columns["Status"]!.Index;
        refreshing = true;
        try {
            foreach (DataGridViewRow row in Rows) {
                var package = (ShaderPackage)row.Tag!;
                UpdateRow(row.Cells[checkColumn], row.Cells[statusColumn], selected.Contains(package.Id),
                    package.Required || needed.Contains(package.Id), package.InstalledOnly);
            }
        } finally {
            refreshing = false;
        }
    }

    private static HashSet<string> RequiredPackageIds(
        HashSet<string> selected, IReadOnlyList<ShaderPackage> packages) => packages
        .Where(package => selected.Contains(package.Id))
        .SelectMany(package => ShaderDependencies.Dependencies(package, packages))
        .Select(dependency => dependency.Id)
        .ToHashSet(StringComparer.Ordinal);

    private static void UpdateRow(
        DataGridViewCell checkBox, DataGridViewCell status, bool isChecked, bool needed, bool installedOnly) {
        if (checkBox.Value is not bool prior || prior != isChecked)
            checkBox.Value = isChecked;
        checkBox.ReadOnly = needed;
        checkBox.ToolTipText = needed ? "Required by the selected packages" : "";

        Color statusColor = Color.Empty;
        string statusText = "Not installed";
        if (status.Tag is string installedStatus) {
            statusText = !isChecked ? $"{installedStatus} (will be removed)"
                : installedOnly ? $"{installedStatus} (local only)" : installedStatus;
            statusColor = isChecked ? Color.Empty : DarkTheme.Danger;
        } else if (isChecked) {
            statusText = "Not installed (will be added)";
            statusColor = DarkTheme.Warning;
        }

        if (status.Value as string != statusText)
            status.Value = statusText;
        if (status.HasStyle || statusColor != Color.Empty) {
            status.Style.ForeColor = statusColor;
            status.Style.SelectionForeColor = statusColor;
        }
    }
}
