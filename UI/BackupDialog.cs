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
        Text = "Restore";
        ClientSize = new(880, 390);
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

        list.AccessibleName = "Operations";
        DialogLayout.Row(table, "", list);
        Button restore = DialogLayout.Buttons(table, this, "Restore before operation");
        restore.Enabled = false;
        list.SelectedIndexChanged += (_, _) => restore.Enabled = list.SelectedItem is Choice;
        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }
    }
}
