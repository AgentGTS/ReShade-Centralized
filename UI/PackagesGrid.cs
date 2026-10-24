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
            AutoSizeMode = DataGridViewAutoSizeColumnMode.AllCells,
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

    public void SetPackages(IEnumerable<ShaderPackage> packages, HashSet<string> selected, ManagerState? state)
    {
        refreshing = true;
        try
        {
            Rows.Clear();
            foreach (ShaderPackage package in packages)
            {
                InstalledPackage? installed = state?.Packages.SingleOrDefault(item => item.Id == package.Id);
                string status = installed == null ? "Not installed" : IsComplete(installed, state!.Paths)
                    ? "Installed" : "Incomplete";
                int row = Rows.Add(selected.Contains(package.Id), package.ToString(), status);
                Rows[row].Tag = package;
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
            }
        }
        finally
        {
            refreshing = false;
        }
    }

    private static bool IsComplete(InstalledPackage package, CentralPaths paths)
    {
        if (package.Files.Count == 0)
        {
            return false;
        }
        try
        {
            return package.Files.Keys.All(key =>
            {
                string[] parts = key.Split('/', 2);
                if (parts.Length != 2 || parts[0] is not ("shaders" or "textures"))
                {
                    return false;
                }
                string root = parts[0] == "shaders" ? paths.Shaders : paths.Textures;
                string file = FileTransaction.Contained(ShaderService.PackageDirectory(root, package.Id), parts[1]);
                FileTransaction.CheckParents(file);
                return FileTransaction.Link(file) == null && File.Exists(file);
            });
        }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidDataException)
        {
            return false;
        }
    }
}
