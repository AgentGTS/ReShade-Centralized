using System.Reflection;

namespace ReShadeCentralized;

public sealed partial class MainForm
{
    private sealed record CreditSource(string Name, Func<string> Read)
    {
        public override string ToString() => Name;
    }

    private readonly ListBox creditSources = new()
    {
        Dock = DockStyle.Fill,
        IntegralHeight = false,
        HorizontalScrollbar = true,
        AccessibleName = "Credit sources",
        Margin = new Padding(0, 0, 16, 0)
    };

    private readonly TextBox licenseText = new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        Margin = Padding.Empty,
        AccessibleName = "License text"
    };

    private void BuildCredits()
    {
        TableLayoutPanel page = CreatePage("Credits");
        var columns = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 72));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        columns.Controls.Add(creditSources, 0, 0);
        columns.Controls.Add(licenseText, 1, 0);
        page.Controls.Add(columns, 0, 0);
        page.SetRowSpan(columns, 3);
        creditSources.SelectedIndexChanged += (_, _) => ShowSelectedCredit();
        tabs.SelectedIndexChanged += (_, _) =>
        {
            if (tabs.SelectedTab?.Text == "Credits")
            {
                RefreshCredits();
            }
        };
        RefreshCredits();
    }

    private static string NoticeResource(string name)
    {
        Assembly assembly = typeof(MainForm).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(value => value.EndsWith("." + name));
        using Stream stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void RefreshCredits()
    {
        string? selected = (creditSources.SelectedItem as CreditSource)?.Name;
        creditSources.Items.Clear();
        creditSources.Items.Add(new CreditSource("ReShade Centralized", () =>
            "ReShade Centralized\r\nAgentGTS and original author OopyDoopy\r\n" +
            "https://github.com/AgentGTS/ReShade-Centralized\r\n" +
            "https://github.com/OopyDoopy/ReShade-Centralized\r\n\r\n" +
            NoticeResource("ApplicationLicense.txt")));
        creditSources.Items.Add(new CreditSource("ReShade", () => NoticeResource("ReShade-BSD-3-Clause.txt")));
        creditSources.Items.Add(new CreditSource("Catalogs and shaders", () =>
            NoticeResource("Catalogs-and-Shaders.txt")));
        creditSources.Items.Add(new CreditSource(".NET and dependencies", () =>
            typeof(MainForm).Assembly.GetManifestResourceNames().Contains("ReShadeCentralized.ReleaseNotices.txt") ?
                NoticeResource("ReleaseNotices.txt") : NoticeResource("DotNet-MIT.txt")));

        foreach (InstalledPackage package in manager.State?.Packages ?? [])
        {
            string[] notices = package.Files.Keys.Where(key => key.StartsWith("shaders/_notices/",
                StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            creditSources.Items.Add(new CreditSource(package.Name, () =>
                package.Name + "\r\n" + package.Repository + "\r\n" + package.DownloadUrl +
                (notices.Length == 0 ? "\r\n\r\nNo separate notices were retained for this installation. " +
                    "Reapply the package to collect its current notice documents; also consult its source repository." :
                    "\r\n\r\nSelect a document below to read its original text.")));
            foreach (string notice in notices)
            {
                string relative = notice["shaders/".Length..];
                string path = FileTransaction.Contained(
                    ShaderService.PackageDirectory(manager.State!.Paths.Shaders, package.Id), relative);
                creditSources.Items.Add(new CreditSource(package.Name + ": " +
                    notice["shaders/_notices/".Length..], () => ReadCreditFile(path)));
            }
        }
        creditSources.SelectedIndex = Math.Max(0, creditSources.Items.Cast<CreditSource>()
            .ToList().FindIndex(source => source.Name == selected));
    }

    private static string ReadCreditFile(string path)
    {
        FileTransaction.CheckParents(path);
        if (FileTransaction.Link(path) != null)
        {
            throw new IOException("The notice was replaced by a symbolic link: " + path);
        }
        if (new FileInfo(path).Length > 8 * 1024 * 1024)
        {
            return "This notice exceeds the display limit. Read the original file at:\r\n" + path;
        }
        return File.ReadAllText(path);
    }

    private void ShowSelectedCredit()
    {
        try
        {
            licenseText.Text = (creditSources.SelectedItem as CreditSource)?.Read()
                .ReplaceLineEndings(Environment.NewLine) ?? "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            licenseText.Text = "Cannot read this notice: " + error.Message;
        }
        licenseText.Select(0, 0);
    }
}
