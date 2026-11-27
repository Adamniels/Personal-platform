using System.Globalization;
using System.Text.RegularExpressions;

namespace DevFlow.Schedules;

/// <summary>How a project runs: <see cref="Always"/>, the default, or by its
/// <see cref="Schedule"/>.</summary>
public static class RunsBy
{
    /// <summary>It runs at any hour.</summary>
    public const string Always = "always";

    /// <summary>It runs inside any of its windows that is on, and with none on, never.</summary>
    public const string Schedule = "schedule";
}

/// <summary>A window as the rule reads it: the days it starts on, from and to as wall clock, a
/// <paramref name="To"/> earlier than <paramref name="From"/> crossing midnight, and whether it is
/// on.</summary>
public sealed record ScheduleWindow(IReadOnlySet<DayOfWeek> Days, TimeOnly From, TimeOnly To, bool Active);

/// <summary>A window as the routes answer it and the page shows it.</summary>
/// <param name="Id">Its id, kept while it is saved with it.</param>
/// <param name="Name">Its name, unique in its project whatever its case.</param>
/// <param name="Days">The days it starts on, <c>mon</c> to <c>sun</c>, in the week's order.</param>
/// <param name="From">When it starts, <c>HH:MM</c>.</param>
/// <param name="To">When it ends, <c>HH:MM</c>; earlier than <paramref name="From"/>, the next
/// day.</param>
/// <param name="Active">Whether it is on.</param>
public sealed record ScheduleEntry(Guid Id, string Name, IReadOnlyList<string> Days, string From, string To, bool Active)
{
    /// <summary>The window the rule reads.</summary>
    public ScheduleWindow Window() => new(
        Days.Select(day => ScheduleRules.Day(day)!.Value).ToHashSet(),
        ScheduleRules.Time(From)!.Value,
        ScheduleRules.Time(To)!.Value,
        Active);
}

/// <summary>A window as <c>PUT .../schedules</c> takes it, every field checked by
/// <see cref="ScheduleRules.Check"/>.</summary>
/// <param name="Id">Its id, to keep it; a new one is made without.</param>
public sealed record ScheduleWindowRequest(Guid? Id, string? Name, IReadOnlyList<string?>? Days, string? From, string? To, bool? Active);

/// <summary>What <c>PUT .../schedules</c> takes: the mode and the whole list.</summary>
public sealed record ScheduleRequest(string? RunsBy, IReadOnlyList<ScheduleWindowRequest?>? Schedules);

/// <summary>
/// The rules a project's schedules keep, which <c>PUT .../schedules</c> checks and the table's checks
/// repeat: the mode <c>always</c> or <c>schedule</c>; at most <see cref="MostWindows"/> windows; a
/// name 1 to 60 characters once trimmed, unique in the project whatever its case; <c>days</c> a
/// non-empty set of <c>mon</c> to <c>sun</c>; <c>from</c> and <c>to</c> <c>HH:MM</c> in 24 hours,
/// different; <c>active</c> true or false; an id given at most once. See "Schedules" in
/// devflow/README.md.
/// </summary>
public static partial class ScheduleRules
{
    /// <summary>The most windows a project has.</summary>
    public const int MostWindows = 20;

    /// <summary>The longest name, once trimmed.</summary>
    public const int LongestName = 60;

    /// <summary>The days' names, in the week's order from Monday.</summary>
    public static readonly IReadOnlyList<string> DayNames = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];

    private static readonly DayOfWeek[] Week =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];

    /// <summary>The day a name names, or null for one that is not <c>mon</c> to <c>sun</c>.</summary>
    public static DayOfWeek? Day(string? name)
    {
        for (var place = 0; place < Week.Length; place++)
        {
            if (string.Equals(DayNames[place], name, StringComparison.Ordinal))
            {
                return Week[place];
            }
        }

        return null;
    }

    /// <summary>A day's name.</summary>
    public static string DayName(DayOfWeek day) => DayNames[((int)day + 6) % 7];

    /// <summary>The time <c>HH:MM</c> in 24 hours names, or null for anything else.</summary>
    public static TimeOnly? Time(string? text) =>
        text is not null && Clock().IsMatch(text) ? TimeOnly.ParseExact(text, "HH:mm", CultureInfo.InvariantCulture) : null;

    /// <summary>
    /// The request checked: the mode and the windows to save, in order, their names trimmed, their
    /// days a set in the week's order and a new id for each without one; or what is wrong, by field,
    /// <c>runs_by</c>, <c>schedules</c> or <c>schedules[i].&lt;field&gt;</c>, every rule broken
    /// named.
    /// </summary>
    public static (string RunsBy, IReadOnlyList<ScheduleEntry> Windows)? Check(ScheduleRequest request, out Dictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(request);
        errors = [];
        if (request.RunsBy is not (Schedules.RunsBy.Always or Schedules.RunsBy.Schedule))
        {
            errors["runs_by"] = ["must be always or schedule"];
        }

        if (request.Schedules is not { } given)
        {
            errors["schedules"] = ["is required, an empty list for none"];
            return null;
        }

        if (given.Count > MostWindows)
        {
            errors["schedules"] = [string.Create(CultureInfo.InvariantCulture, $"holds at most {MostWindows} windows")];
            return null;
        }

        var windows = new List<ScheduleEntry>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<Guid>();
        for (var index = 0; index < given.Count; index++)
        {
            var field = string.Create(CultureInfo.InvariantCulture, $"schedules[{index}]");
            if (given[index] is not { } window)
            {
                errors[field] = ["is required"];
                continue;
            }

            var name = window.Name?.Trim() ?? "";
            if (name.Length is 0 or > LongestName)
            {
                errors[$"{field}.name"] = [string.Create(CultureInfo.InvariantCulture, $"must be 1 to {LongestName} characters")];
            }
            else if (!names.Add(name.ToLowerInvariant()))
            {
                errors[$"{field}.name"] = ["is the name of another window, whatever its case"];
            }

            var days = window.Days?.Select(Day).ToList();
            if (days is null || days.Count == 0 || days.Any(day => day is null))
            {
                errors[$"{field}.days"] = ["must be one or more of mon, tue, wed, thu, fri, sat and sun"];
            }

            var from = Time(window.From);
            var to = Time(window.To);
            if (from is null)
            {
                errors[$"{field}.from"] = ["must be HH:MM in 24 hours, 00:00 to 23:59"];
            }

            if (to is null)
            {
                errors[$"{field}.to"] = ["must be HH:MM in 24 hours, 00:00 to 23:59"];
            }
            else if (to == from)
            {
                errors[$"{field}.to"] = ["must differ from from"];
            }

            if (window.Active is null)
            {
                errors[$"{field}.active"] = ["must be true or false"];
            }

            if (window.Id is { } id && !ids.Add(id))
            {
                errors[$"{field}.id"] = ["is the id of another window in the list"];
            }

            if (errors.Count == 0)
            {
                var set = days!.Select(day => day!.Value).ToHashSet();
                windows.Add(new ScheduleEntry(
                    window.Id ?? Guid.CreateVersion7(),
                    name,
                    [.. Week.Where(set.Contains).Select(DayName)],
                    window.From!,
                    window.To!,
                    window.Active!.Value));
            }
        }

        return errors.Count == 0 ? (request.RunsBy!, windows) : null;
    }

    [GeneratedRegex("^([01][0-9]|2[0-3]):[0-5][0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex Clock();
}