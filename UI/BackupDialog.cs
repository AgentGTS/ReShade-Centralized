namespace ReShadeCentralized;

internal sealed class BackupDialog : Form
{
    private sealed record Choice(string Path, string Description)
    {
        public override string ToString() => Description;
    }

    private readonly ListBox list = new()
    {
        Height = 260
    };

    public string SelectedBackup =>
        (list.SelectedItem as Choice)?.Path ??
        throw new InvalidOperationException("Select a backup.");

    public BackupDialog(string backups)
    {
        Text = "Restore a previous state";
        ClientSize = new(880, 500);
        TableLayoutPanel table = DialogLayout.Table(this);
        if (Directory.Exists(backups))
        {
            foreach (string path in Directory.GetDirectories(backups).OrderDescending())
            {
                TransactionJournal journal = FileTransaction.ReadJournal(path);
                if (journal.Status == "committed")
                {
                    list.Items.Add(new Choice(path, Path.GetFileName(path) + "  —  " + journal.Description));
                }
            }
        }

        DialogLayout.Row(table, "Backup", list);
        DialogLayout.Row(
            table,
            "",
            new Label
            {
                Text = "Restore the state immediately before this operation.\nNewer operations are " +
            "undone first. Close affected games before restoring.",
                AutoSize = true
            });
        DialogLayout.Buttons(table, this, "Restore");
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }
}
