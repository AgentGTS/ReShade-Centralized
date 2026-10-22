namespace ReShadeCentralized;

internal static class DialogLayout
{
    public static TableLayoutPanel Table(Form form)
    {
        form.StartPosition = FormStartPosition.CenterParent;
        form.MinimizeBox = false;
        form.MaximizeBox = false;
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.Font = DarkTheme.BodyFont;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(24),
            AutoSize = true
        };
        table.ColumnStyles.Add(new(SizeType.Absolute, 145));
        table.ColumnStyles.Add(new(SizeType.Percent, 100));
        form.Controls.Add(table);
        return table;
    }

    public static Label? Row(TableLayoutPanel table, string label, Control control)
    {
        int row = table.RowCount++;
        table.RowStyles.Add(new(SizeType.AutoSize));
        Label? fieldLabel = null;
        if (label.Length > 0)
        {
            fieldLabel = new Label { Text = label, AutoSize = true, Margin = new Padding(0, 9, 8, 10) };
            table.Controls.Add(fieldLabel, 0, row);
        }

        control.Dock = DockStyle.Fill;
        control.Margin = new Padding(0, 4, 0, 9);
        table.Controls.Add(control, fieldLabel == null ? 0 : 1, row);
        if (fieldLabel == null)
        {
            table.SetColumnSpan(control, 2);
        }

        return fieldLabel;
    }

    public static Control Browse(TextBox box, bool file, string filter = "Executables|*.exe")
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 2,
            Height = 38,
            Margin = Padding.Empty
        };
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new(SizeType.Absolute, 42));
        box.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        box.Margin = new Padding(0, 0, 8, 0);
        panel.Controls.Add(box);
        var button = new FlatButton("...")
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = Padding.Empty,
            Margin = Padding.Empty,
            AccessibleName = file ? "Browse for a file" : "Browse for a folder"
        };
        panel.Controls.Add(button);
        button.Click += (_, _) =>
        {
            if (file)
            {
                using var dialog = new OpenFileDialog
                {
                    Filter = filter,
                    CheckFileExists = true
                };
                if (dialog.ShowDialog(box.FindForm()) == DialogResult.OK)
                {
                    box.Text = dialog.FileName;
                }
            }
            else
            {
                using var dialog = new FolderBrowserDialog
                {
                    SelectedPath = box.Text,
                    UseDescriptionForTitle = true,
                    Description = "Select central storage"
                };
                if (dialog.ShowDialog(box.FindForm()) == DialogResult.OK)
                {
                    box.Text = dialog.SelectedPath;
                }
            }
        };
        return panel;
    }

    public static Button Buttons(TableLayoutPanel table, Form form, string accept = "Save")
    {
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 16, 0, 0)
        };
        var ok = new FlatButton(accept, primary: true)
        {
            DialogResult = DialogResult.OK
        };
        var cancel = new FlatButton("Cancel")
        {
            DialogResult = DialogResult.Cancel
        };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        Row(table, "", buttons);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        DarkTheme.Apply(form);
        return ok;
    }
}
