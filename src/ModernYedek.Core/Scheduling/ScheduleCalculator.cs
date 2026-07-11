using System.Globalization;
using ModernYedek.Core.Models;

namespace ModernYedek.Core.Scheduling;

public static class ScheduleCalculator
{
    public static DateTimeOffset? NextRun(ScheduleSettings schedule, DateTimeOffset now)
    {
        if (!schedule.Enabled || schedule.Days.Count == 0 || schedule.Times.Count == 0)
        {
            return null;
        }

        var parsedTimes = schedule.Times
            .Select(ParseTime)
            .Where(time => time is not null)
            .Select(time => time!.Value)
            .OrderBy(time => time)
            .ToList();

        if (parsedTimes.Count == 0)
        {
            return null;
        }

        for (var offset = 0; offset <= 14; offset++)
        {
            var date = now.Date.AddDays(offset);
            if (!schedule.Days.Contains(date.DayOfWeek))
            {
                continue;
            }

            foreach (var time in parsedTimes)
            {
                var candidate = new DateTimeOffset(date + time.ToTimeSpan(), now.Offset);
                if (candidate > now)
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static bool IsValidTime(string value)
    {
        return ParseTime(value) is not null;
    }

    private static TimeOnly? ParseTime(string value)
    {
        return TimeOnly.TryParseExact(value.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time
            : null;
    }
}
