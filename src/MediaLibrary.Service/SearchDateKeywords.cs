using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaLibrary.Service;

public sealed partial class LibraryDatabase
{
    // Interpret dates in the same local time zone as explicit search date filters.
    internal static (string From, string Until)? DateKeywordRange(string term, TimeZoneInfo timeZone)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime);
        var month = new DateOnly(today.Year, today.Month, 1);
        (DateOnly From, DateOnly Until)? relative = term switch
        {
            "今天" => (today, today.AddDays(1)),
            "昨天" => (today.AddDays(-1), today),
            "前天" => (today.AddDays(-2), today.AddDays(-1)),
            "本月" or "这个月" => (month, month.AddMonths(1)),
            "上月" or "上个月" => (month.AddMonths(-1), month),
            "今年" => (new(today.Year, 1, 1), new(today.Year + 1, 1, 1)),
            "去年" => (new(today.Year - 1, 1, 1), new(today.Year, 1, 1)),
            _ => null
        };
        if (relative is { } range) return Boundaries(range.From, range.Until);

        var value = term.Trim().Replace("年", "-").Replace("月", "-").Replace("日", "")
            .Replace('/', '-').Replace('.', '-').TrimEnd('-');
        var match = Regex.Match(value, @"\A(?<year>[0-9]{4})(?:-?(?<month>[0-9]{1,2})(?:-?(?<day>[0-9]{1,2}))?)?\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        var year = int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        var hasMonth = match.Groups["month"].Success;
        var hasDay = match.Groups["day"].Success;
        var monthNumber = hasMonth ? int.Parse(match.Groups["month"].Value, CultureInfo.InvariantCulture) : 1;
        var day = hasDay ? int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture) : 1;
        try
        {
            var start = new DateOnly(year, monthNumber, day);
            return Boundaries(start, hasDay ? start.AddDays(1) : hasMonth ? start.AddMonths(1) : start.AddYears(1));
        }
        catch (ArgumentOutOfRangeException) { return null; }

        (string From, string Until) Boundaries(DateOnly from, DateOnly until) =>
            (UtcBoundary(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZone),
                UtcBoundary(until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), timeZone));
    }

    private static bool DateKeywordMatches(string value, (string From, string Until) range) =>
        string.CompareOrdinal(value, range.From) >= 0 && string.CompareOrdinal(value, range.Until) < 0;
}
