namespace ReShadeCentralized;

internal sealed class PackagesGrid : DataGridView
{
    private bool refreshing;
    public event Action<ShaderPackage, bool>? PackageChecked;

    public PackagesGrid()
    {
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
        Columns.Add(new DataGridViewCheckBoxColumn
        {
            Name = "Selected", HeaderText = "Selected", AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Package", HeaderText = "Package", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Status", HeaderText = "Status", ReadOnly = true,
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Resizable = DataGridViewTriState.False,
            SortMode = DataGridViewColumnSortMode.NotSortable
        });
        CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (IsCurrentCellDirty)
            {
                CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        CellValueChanged += (_, e) =>
        {
            if (!refreshing && e.RowIndex >= 0 && e.ColumnIndex == Columns["Selected"]!.Index &&
                Rows[e.RowIndex].Tag is ShaderPackage package)
            {
                PackageChecked?.Invoke(package, Rows[e.RowIndex].Cells["Selected"].Value is true);
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnDefaultCellStyleChanged(EventArgs e)
    {
        base.OnDefaultCellStyleChanged(e);
        UpdateStatusColumnWidth();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateStatusColumnWidth();
    }

    private void UpdateStatusColumnWidth()
    {
        if (Columns["Status"] is not DataGridViewColumn status)
        {
            return;
        }

        string[] statuses =
        [
            "Not installed (will be added)", "Installed (will be removed)", "Incomplete (will be removed)"
        ];
        Font statusFont = DefaultCellStyle.Font ?? Font;
        int textWidth = statuses.Max(text => TextRenderer.MeasureText(text, statusFont).Width);
        status.Width = textWidth + DefaultCellStyle.Padding.Horizontal + LogicalToDeviceUnits(8);
    }

    protected override void OnCellPainting(DataGridViewCellPaintingEventArgs e)
    {
        base.OnCellPainting(e);
        if (e.RowIndex < 0 || e.ColumnIndex != Columns["Selected"]!.Index || e.Graphics == null)
        {
            return;
        }

        e.Paint(e.ClipBounds, e.PaintParts & ~DataGridViewPaintParts.ContentForeground);
        bool locked = Rows[e.RowIndex].Cells[e.ColumnIndex].ReadOnly || !Enabled;
        bool selected = e.Value is true;
        int size = LogicalToDeviceUnits(16);
        var bounds = new Rectangle(e.CellBounds.Left + ((e.CellBounds.Width - size) / 2),
            e.CellBounds.Top + ((e.CellBounds.Height - size) / 2), size, size);
        DarkTheme.DrawCheckBox(e.Graphics, bounds, selected, locked, LogicalToDeviceUnits(2));
        e.Handled = true;
    }

    public void SetPackages(IEnumerable<ShaderPackage> packages, HashSet<string> selected, ManagerState? state,
        IReadOnlyDictionary<string, bool>? completeness = null)
    {
        refreshing = true;
        try
        {
            Rows.Clear();
            foreach (ShaderPackage package in packages)
            {
                InstalledPackage? installed = state?.Packages.SingleOrDefault(item => item.Id == package.Id);
                bool complete = installed != null && (completeness?.TryGetValue(installed.Id, out bool known) == true ?
                    known : ShaderService.IsPackageComplete(installed, state!.Paths));
                string status = installed == null ? "Not installed" : complete
                    ? "Installed" : "Incomplete";
                int row = Rows.Add(selected.Contains(package.Id), package.ToString(), status);
                Rows[row].Tag = package;
                Rows[row].Cells["Status"].Tag = installed == null ? null : status;
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    public void SetChecks(HashSet<string> selected, IReadOnlyList<ShaderPackage> packages)
    {
        refreshing = true;
        try
        {
            ShaderPackage[] checkedPackages = packages.Where(package => selected.Contains(package.Id)).ToArray();
            foreach (DataGridViewRow row in Rows)
            {
                var package = (ShaderPackage)row.Tag!;
                bool needed = package.Required || checkedPackages.Any(other =>
                    ShaderService.Dependencies(other, packages).Any(dependency => dependency.Id == package.Id));
                row.Cells["Selected"].Value = selected.Contains(package.Id);
                row.Cells["Selected"].ReadOnly = needed;
                row.Cells["Selected"].ToolTipText = needed ? "Required by the selected packages" : "";
                DataGridViewCell status = row.Cells["Status"];
                Color statusColor = Color.Empty;
                string statusText = "Not installed";
                if (status.Tag is string installedStatus)
                {
                    bool pendingRemoval = !selected.Contains(package.Id);
                    statusText = pendingRemoval ? $"{installedStatus} (will be removed)" : installedStatus;
                    statusColor = pendingRemoval ? DarkTheme.Danger : Color.Empty;
                }
                else if (selected.Contains(package.Id))
                {
                    statusText = "Not installed (will be added)";
                    statusColor = DarkTheme.Warning;
                }
                status.Value = statusText;
                status.Style.ForeColor = statusColor;
                status.Style.SelectionForeColor = statusColor;
            }
        }
        finally
        {
            refreshing = false;
        }
    }

}
