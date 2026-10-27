using System.Reflection;
using System.Text.Json;

namespace ReShadeCentralized;

internal static partial class UiPreview
{
    private static void VerifyFilteredShaderSelections(MainForm main)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var manager = (Manager)typeof(MainForm).GetField("manager", flags)!.GetValue(main)!;
        FieldInfo catalogField = typeof(MainForm).GetField("catalog", flags)!;
        PropertyInfo stateProperty = typeof(Manager).GetProperty(nameof(Manager.State))!;
        MethodInfo filter = typeof(MainForm).GetMethod("FilterPackages", flags)!;
        var choices = (HashSet<string>)typeof(MainForm).GetField("selectedPackageIds", flags)!.GetValue(main)!;
        object? originalCatalog = catalogField.GetValue(main);
        ManagerState? originalState = manager.State;
        string[] originalChoices = choices.ToArray();
        PackagesGrid grid = Descendants(main).OfType<PackagesGrid>().Single();
        TextBox search = Descendants(main).OfType<TextBox>()
            .Single(control => control.AccessibleName == "Search shader packages");
        string originalSearch = search.Text;
        TabControl tabs = Descendants(main).OfType<TabControl>().Single();
        int originalTab = tabs.SelectedIndex;
        int statusWidth = grid.Columns["Status"]!.Width;
        int scenarios = 0;
        int verifiedStates = 0;

        ShaderPackage required = FilterTestPackage("required", "Required", required: true);
        ShaderPackage alpha = FilterTestPackage("alpha", "Alpha", "shared");
        ShaderPackage beta = FilterTestPackage("beta", "Beta", "shared");
        ShaderPackage framework = FilterTestPackage("framework", "ZenteonFX", repository: "Zenteon/ZenteonFX");
        ShaderPackage dependent = FilterTestPackage("dependent", "BFBFX", repository: "yplebedev/BFBFX");
        ShaderPackage recorded = FilterTestPackage("recorded", "Recorded");
        ShaderPackage[] catalog = [required, alpha, beta, framework, dependent];
        ShaderPackage[] available = [.. catalog, recorded];
        ShaderPackage[] editable = [alpha, beta];
        (string Query, string[] Visible)[] filters =
        [
            ("", available.Select(package => package.Id).ToArray()),
            ("ALPHA", [alpha.Id]),
            ("beta", [beta.Id]),
            ("shared", [alpha.Id, beta.Id]),
            ("__no_match__", []),
            ("Recorded", [recorded.Id]),
            ("BFBFX", [dependent.Id]),
            ("ZenteonFX", [framework.Id])
        ];
        var state = new ManagerState
        {
            Paths = CentralPaths.At(Path.Combine(AppContext.BaseDirectory, "filter-validation-" + Guid.NewGuid()))
        };
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string savedState = "";

        DataGridViewRow Row(string id) => grid.Rows.Cast<DataGridViewRow>()
            .Single(row => ((ShaderPackage)row.Tag!).Id == id);

        void VerifyState(string[] visible)
        {
            verifiedStates++;
            if (!choices.SetEquals(expected) || !grid.Rows.Cast<DataGridViewRow>()
                .Select(row => ((ShaderPackage)row.Tag!).Id).ToHashSet().SetEquals(visible))
            {
                throw new InvalidOperationException(
                    $"Search '{search.Text}' changed the selection or visible packages.");
            }
            HashSet<string> installed = state.Packages.Select(package => package.Id).ToHashSet();
            foreach (DataGridViewRow row in grid.Rows)
            {
                string id = ((ShaderPackage)row.Tag!).Id;
                bool selected = expected.Contains(id);
                bool locked = id == required.Id || (id == framework.Id && expected.Contains(dependent.Id));
                string text = installed.Contains(id) ? "Incomplete" : "Not installed";
                Color color = Color.Empty;
                if (installed.Contains(id) && !selected)
                {
                    text += " (will be removed)";
                    color = DarkTheme.Danger;
                }
                else if (!installed.Contains(id) && selected)
                {
                    text += " (will be added)";
                    color = DarkTheme.Warning;
                }
                DataGridViewCell status = row.Cells["Status"];
                if (!Equals(row.Cells["Selected"].Value, selected) || row.Cells["Selected"].ReadOnly != locked ||
                    !Equals(status.Value, text) || status.Style.ForeColor != color ||
                    status.Style.SelectionForeColor != color)
                {
                    throw new InvalidOperationException(
                        $"Search '{search.Text}' shows the wrong selection state for {id}.");
                }
            }
            string action = expected.Count > 0 && expected.SetEquals(installed) ? "Update selected" : "Apply selection";
            VerifyButtonStates(main, true, action);
            ShaderSelectionPlan plan = manager.CreateShaderSelectionPlan(
                available.Where(package => choices.Contains(package.Id)).ToArray(), available);
            if (!plan.Selected.Select(package => package.Id).ToHashSet().SetEquals(expected) ||
                !plan.Removals.Select(package => package.Id).ToHashSet().SetEquals(installed.Except(expected)))
            {
                throw new InvalidOperationException("The selection plan lost hidden additions or removals.");
            }
            if (grid.Columns["Status"]!.Width != statusWidth ||
                JsonSerializer.Serialize(state, ManagerState.Json) != savedState || Directory.Exists(state.Paths.Root))
            {
                throw new InvalidOperationException("Selection/filter edits changed width, saved state, or storage.");
            }
        }

        void Edit(string id, bool selected)
        {
            if (id != required.Id && !(id == framework.Id && expected.Contains(dependent.Id)))
            {
                if (selected)
                {
                    expected.Add(id);
                }
                else
                {
                    expected.Remove(id);
                }
            }
            if (expected.Contains(dependent.Id))
            {
                expected.Add(framework.Id);
            }
            Row(id).Cells["Selected"].Value = selected;
        }

        try
        {
            catalogField.SetValue(main, catalog.ToList());
            stateProperty.SetValue(manager, state);
            for (var inventoryMask = 0; inventoryMask < 4; inventoryMask++)
            {
                state.Packages = available.Where(package => package.Id == required.Id || package.Id == recorded.Id ||
                    (package.Id == alpha.Id && (inventoryMask & 1) != 0) ||
                    (package.Id == beta.Id && (inventoryMask & 2) != 0)).Select(package => new InstalledPackage
                    {
                        Id = package.Id, Name = package.Name, Repository = package.Repository,
                        DownloadUrl = package.DownloadUrl
                    }).ToList();
                state.SelectedPackages = state.Packages.Select(package => package.Id).ToList();
                savedState = JsonSerializer.Serialize(state, ManagerState.Json);
                for (var selectionMask = 0; selectionMask < 4; selectionMask++)
                {
                    foreach ((string query, string[] visible) in filters)
                    {
                        ShaderPackage?[] targets = [null, .. editable.Where(package => visible.Contains(package.Id))];
                        foreach (ShaderPackage? target in targets)
                        {
                            foreach (bool selected in target == null ? new[] { false } : new[] { false, true })
                            {
                                scenarios++;
                                expected.Clear();
                                expected.UnionWith([required.Id, recorded.Id]);
                                if ((selectionMask & 1) != 0)
                                {
                                    expected.Add(alpha.Id);
                                }
                                if ((selectionMask & 2) != 0)
                                {
                                    expected.Add(beta.Id);
                                }
                                choices.Clear();
                                choices.UnionWith(expected);
                                search.Text = query;
                                filter.Invoke(main, null);
                                VerifyState(visible);
                                if (target != null)
                                {
                                    Edit(target.Id, selected);
                                    VerifyState(visible);
                                }
                                foreach ((string nextQuery, string[] nextVisible) in filters)
                                {
                                    search.Text = nextQuery;
                                    VerifyState(nextVisible);
                                }
                                search.Clear();
                                VerifyState(filters[0].Visible);
                            }
                        }
                    }
                }
            }

            expected.Clear();
            expected.UnionWith([required.Id, recorded.Id]);
            choices.Clear();
            choices.UnionWith(expected);
            filter.Invoke(main, null);
            (string Query, string Id, bool Selected)[] edits =
            [
                ("BFBFX", dependent.Id, true), ("ZenteonFX", framework.Id, false),
                ("Recorded", recorded.Id, false), ("Required", required.Id, false),
                ("BFBFX", dependent.Id, false), ("ZenteonFX", framework.Id, false),
                ("ALPHA", alpha.Id, true), ("beta", beta.Id, true),
                ("ALPHA", alpha.Id, false), ("beta", beta.Id, false),
                ("ALPHA", alpha.Id, true), ("ALPHA", alpha.Id, false)
            ];
            foreach ((string query, string id, bool selected) in edits)
            {
                search.Text = query;
                Edit(id, selected);
                VerifyState([id]);
                search.Text = "__no_match__";
                VerifyState([]);
                search.Clear();
                VerifyState(filters[0].Visible);
            }

            tabs.SelectedIndex = 1;
            tabs.SelectedIndex = 0;
            expected.Clear();
            expected.UnionWith(state.SelectedPackages);
            VerifyState(filters[0].Visible);
            Console.WriteLine($"PASS Filtered shader selection: {scenarios} matrix scenarios, " +
                $"{edits.Length} dependency/repeated edits, {verifiedStates} verified UI states.");
        }
        finally
        {
            tabs.SelectedIndex = originalTab;
            catalogField.SetValue(main, originalCatalog);
            stateProperty.SetValue(manager, originalState);
            choices.Clear();
            choices.UnionWith(originalChoices);
            search.Text = originalSearch;
            filter.Invoke(main, null);
        }
    }

    private static ShaderPackage FilterTestPackage(
        string id,
        string name,
        string description = "",
        string? repository = null,
        bool required = false) => new()
        {
            Id = id,
            Name = name,
            Description = description,
            Repository = "https://github.com/" + (repository ?? "filter-tests/" + id),
            DownloadUrl = $"https://example.test/{id}.zip",
            Required = required
        };
}
