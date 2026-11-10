using System.Text.RegularExpressions;

namespace Watchboard.Api.Services;

public static partial class VersionComparison
{
    public static string Compare(string? installed, string? latest)
    {
        var leftDate = ParseDate(installed);
        var rightDate = ParseDate(latest);
        if (leftDate.HasValue || rightDate.HasValue)
        {
            if (!leftDate.HasValue || !rightDate.HasValue) return "unknown";
            var dateComparison = leftDate.Value.Date.CompareTo(rightDate.Value.Date);
            if (dateComparison == 0)
            {
                if (leftDate.Value.Revision > 0 && rightDate.Value.Revision > 0 && leftDate.Value.Kind != rightDate.Value.Kind)
                    return "unknown"; // A letter hotfix and a numbered revision are not a proven identical build.
                dateComparison = leftDate.Value.Revision.CompareTo(rightDate.Value.Revision);
            }
            return dateComparison < 0 ? "update" : dateComparison > 0 ? "ahead" : "current";
        }
        var left = Parse(installed);
        var right = Parse(latest);
        if (left is null || right is null) return "unknown";
        var comparison = CompareCore(left.Value, right.Value);
        return comparison < 0 ? "update" : comparison > 0 ? "ahead" : "current";
    }

    private static (DateOnly Date, int Revision, char Kind)? ParseDate(string? value)
    {
        if (value is null || value.Length > 100) return null;
        var match = FulldexDate().Match(value.Trim());
        if (!match.Success || !DateOnly.TryParseExact(match.Groups[1].Value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)) return null;
        var revision = 0;
        if (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value, out revision)) return null;
        var kind = match.Groups[2].Success ? 'n' : 'b';
        if (match.Groups[3].Success) { revision = char.ToLowerInvariant(match.Groups[3].Value[0]) - 'a' + 1; kind = 'a'; }
        return (date, revision, kind);
    }

    private static (int[] Core, string[] Pre)? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 100) return null;
        value = WindowsRevision().Replace(value.Trim(), ".$1");
        var match = Semantic().Match(value);
        if (!match.Success) return null;
        var core = new int[4];
        var parts = match.Groups[1].Value.Split('.');
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out core[i])) return null;
        var pre = match.Groups[2].Success ? match.Groups[2].Value.Split('.') : [];
        return (core, pre);
    }

    private static int CompareCore((int[] Core, string[] Pre) a, (int[] Core, string[] Pre) b)
    {
        for (var i = 0; i < a.Core.Length; i++)
            if (a.Core[i] != b.Core[i]) return a.Core[i].CompareTo(b.Core[i]);
        if (a.Pre.Length == 0 || b.Pre.Length == 0)
            return (a.Pre.Length == 0 ? 1 : 0).CompareTo(b.Pre.Length == 0 ? 1 : 0);
        for (var i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            var x = a.Pre[i];
            var y = b.Pre[i];
            var xNumeric = x.All(char.IsAsciiDigit);
            var yNumeric = y.All(char.IsAsciiDigit);
            var compared = xNumeric && yNumeric
                ? (x.Length == y.Length ? string.CompareOrdinal(x, y) : x.Length.CompareTo(y.Length))
                : xNumeric != yNumeric ? (xNumeric ? -1 : 1) : string.CompareOrdinal(x, y);
            if (compared != 0) return compared;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    [GeneratedRegex(@"\.windows\.(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsRevision();
    [GeneratedRegex(@"^[vV]?(\d+\.\d+(?:\.\d+){0,2})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Semantic();
    [GeneratedRegex(@"^fulldex-(\d{4}-\d{2}-\d{2})(?:(?:[-.](?:r|rev)?(\d+))|([a-z]))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FulldexDate();
}
