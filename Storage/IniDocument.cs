using System.Text;

namespace ReShadeCentralized;
// Keep comments and unknown keys. ReShade uses doubled commas inside list elements.
public sealed class IniDocument
{
    private readonly List<string> lines;

    public IniDocument(string text = "") => lines = text.Replace("\r\n", "\n").Split('\n').ToList();

    public static IniDocument Read(string path) => new(File.Exists(path) ? File.ReadAllText(path) : "");

    private static bool Section(string line, out string section)
    {
        line = line.Trim();
        section = line.StartsWith('[') && line.EndsWith(']') ? line[1..^1].Trim() : "";
        return line.StartsWith('[') && line.EndsWith(']');
    }

    public IEnumerable<string> Sections =>
        lines.Where(value => Section(value, out _))
        .Select(value => value.Trim()[1..^1]);

    public string? Get(string section, string key)
    {
        string current = "";
        foreach (string line in lines)
        {
            if (Section(line, out string? name))
            {
                current = name;
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals >= 0 && current.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(equals + 1)..].Trim();
            }
        }

        return null;
    }

    public void Set(string section, string key, string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            throw new ArgumentException("INI values must fit one line.");
        }

        string current = "";
        int end = section.Length == 0 ? 0 : -1;
        var matches = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (Section(lines[i], out string? name))
            {
                current = name;
                if (current.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = "[" + section + "]";
                }
            }

            if (!current.Equals(section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            end = i + 1;
            int equals = lines[i].IndexOf('=');
            if (equals >= 0 && lines[i][..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(i);
            }
        }

        if (matches.Count > 0)
        {
            lines[matches[0]] = key + "=" + value;
            foreach (int index in matches.Skip(1).Reverse())
            {
                lines.RemoveAt(index);
            }

            return;
        }

        if (end < 0)
        {
            lines.Add("[" + section + "]");
            lines.Add(key + "=" + value);
        }
        else
        {
            lines.Insert(end, key + "=" + value);
        }
    }

    public void SetDefault(string section, string key, string value)
    {
        if (Get(section, key) == null)
        {
            Set(section, key, value);
        }
    }

    public static string Join(IEnumerable<string> values) =>
        string.Join(
        ',',
        values.Select(value => value.Replace(",", ",,")));

    public static string[] Split(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        var values = new List<string>();
        var part = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != ',')
            {
                part.Append(value[i]);
            }
            else if (i + 1 < value.Length && value[i + 1] == ',')
            {
                part.Append(',');
                i++;
            }
            else
            {
                values.Add(part.ToString());
                part.Clear();
            }
        }

        values.Add(part.ToString());
        return values.ToArray();
    }

    public override string ToString() => string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
}
