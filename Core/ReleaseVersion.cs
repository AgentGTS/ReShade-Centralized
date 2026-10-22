using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace ReShadeCentralized;

public sealed partial class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly string[] numbers;
    private readonly string[] prereleaseIdentifiers;

    public string Text { get; }

    public bool IsPrerelease => prereleaseIdentifiers.Length > 0;

    private ReleaseVersion(string text, string[] numbers, string[] prereleaseIdentifiers)
    {
        Text = text;
        this.numbers = numbers;
        this.prereleaseIdentifiers = prereleaseIdentifiers;
    }

    [GeneratedRegex(
        @"\A(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)" +
        @"(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?" +
        @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static ReleaseVersion Parse(string text)
    {
        return TryParse(text, out ReleaseVersion? version) ?
            version : throw new FormatException($"Invalid application release version: {text}");
    }

    public static bool TryParse(string? text, [NotNullWhen(true)] out ReleaseVersion? version)
    {
        version = null;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        // GitHub tags may have a leading 'v'; it is not part of the version itself.
        string normalized = text.StartsWith('v') ? text[1..] : text;
        Match match = VersionPattern().Match(normalized);
        if (!match.Success)
        {
            return false;
        }

        string prerelease = match.Groups["prerelease"].Value;
        string[] identifiers = prerelease.Length == 0 ? [] : prerelease.Split('.');
        if (identifiers.Any(identifier => IsNumeric(identifier) &&
                identifier.Length > 1 && identifier[0] == '0'))
        {
            return false;
        }

        version = new ReleaseVersion(
            normalized,
            [match.Groups["major"].Value, match.Groups["minor"].Value, match.Groups["patch"].Value],
            identifiers);
        return true;
    }

    public int CompareTo(ReleaseVersion? other)
    {
        if (other == null)
        {
            return 1;
        }

        for (var index = 0; index < numbers.Length; index++)
        {
            int comparison = CompareNumeric(numbers[index], other.numbers[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        if (!IsPrerelease || !other.IsPrerelease)
        {
            return other.IsPrerelease.CompareTo(IsPrerelease);
        }

        int count = Math.Min(prereleaseIdentifiers.Length, other.prereleaseIdentifiers.Length);
        for (var index = 0; index < count; index++)
        {
            string left = prereleaseIdentifiers[index];
            string right = other.prereleaseIdentifiers[index];
            bool leftIsNumeric = IsNumeric(left);
            bool rightIsNumeric = IsNumeric(right);
            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = CompareNumeric(left, right);
            }
            else if (leftIsNumeric != rightIsNumeric)
            {
                comparison = leftIsNumeric ? -1 : 1;
            }
            else
            {
                comparison = string.CompareOrdinal(left, right);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        // Build metadata does not affect precedence.
        return prereleaseIdentifiers.Length.CompareTo(other.prereleaseIdentifiers.Length);
    }

    private static bool IsNumeric(string identifier) => identifier.All(char.IsAsciiDigit);

    private static int CompareNumeric(string left, string right)
    {
        int lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }
}
