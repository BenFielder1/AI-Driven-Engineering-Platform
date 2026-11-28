using System.Globalization;

namespace Tevrin.Pack.Coach;

/// <summary>
/// FR-340, §4.63: the Velocity load rules, pure. <see cref="Check(IReadOnlyList{PlanWeek}, AthleteProfile, IEnumerable{CoachSession})"/>
/// returns one finding per broken instance (each week over a limit, each pair of hard sessions too close, each session
/// without volume), as <c>R&lt;n&gt;: message</c>, in rule order (R1–R5) and then plan order; an empty list means the plan
/// passes. Breach percentages round up to a whole percent.
/// <list type="bullet">
/// <item>Volume is the total <c>distanceKm</c> (<c>km</c>), or the total <c>durationMin</c> (<c>min</c>) when the profile's
/// <c>volumeUnit</c> is <c>minutes</c>.</item>
/// <item>The actuals are the logged sessions of the last 4 weeks, bucketed into 7-day weeks ending on the newest logged
/// date; the host passes each <c>session_log</c> version once, so every session given counts.</item>
/// <item>R1: a plan session without the unit's volume field (<c>distanceKm</c>, or <c>durationMin</c> in minutes) is a
/// finding. Week w is compared with the most recent earlier week with volume: a plan week, else the last actual week,
/// else the baseline; with none, it isn't checked.</item>
/// <item>Session starts are their dates (a plan has no times), so two hard sessions on consecutive days are 24 h apart.</item>
/// <item>R4: a plan week ends on its 7th day (its first session's date + 6); race-day sessions and later are not taper
/// volume.
/// The peak is the highest of the up to 4 weeks (actual or planned) before it.</item>
/// <item>R5: a plan week has 7 days, so its rest days are 7 minus its days with a session.</item>
/// </list>
/// </summary>
public static class VelocityRules
{
    private const double Epsilon = 1e-9;

    /// <summary>The weeks of actuals the rules read (§4.63: the last 4 weeks of <c>session_log</c>).</summary>
    public const int ActualWeeks = 4;

    /// <summary>Checks the JSON artifacts: <paramref name="planDraftJson"/> against <paramref name="athleteProfileJson"/>
    /// and the <paramref name="sessionLogJsons"/> versions. Malformed JSON throws <see cref="FormatException"/>.</summary>
    public static IReadOnlyList<string> Check(string planDraftJson, string athleteProfileJson, IEnumerable<string> sessionLogJsons)
    {
        ArgumentNullException.ThrowIfNull(sessionLogJsons);
        return Check(
            VelocityJson.ReadPlan(planDraftJson),
            VelocityJson.ReadProfile(athleteProfileJson),
            sessionLogJsons.SelectMany(VelocityJson.ReadSessionLog));
    }

    public static IReadOnlyList<string> Check(IReadOnlyList<PlanWeek> plan, AthleteProfile profile, IEnumerable<CoachSession> recentSessions)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(recentSessions);

        var rules = profile.Rules;
        var unit = profile.VolumeInMinutes ? "min" : "km";
        var weeks = plan.OrderBy(w => w.Week).ToList();
        var actuals = ActualWeekVolumes([.. recentSessions], profile);
        var findings = new List<string>();

        R1WeeklyIncrease(weeks, actuals, profile, rules, unit, findings);
        R2Recovery(weeks, rules, findings);
        R3HardCount(weeks, rules, findings);
        R4Taper(weeks, actuals, profile, rules, unit, findings);
        R5Rest(weeks, rules, findings);
        return findings;
    }

    /// <summary>A week's volume in the profile's unit.</summary>
    public static double Volume(IEnumerable<CoachSession> sessions, bool inMinutes)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        return sessions.Sum(s => (inMinutes ? s.DurationMin : s.DistanceKm) ?? 0);
    }

    private static void R1WeeklyIncrease(List<PlanWeek> weeks, List<double> actuals, AthleteProfile profile, VelocityThresholds rules, string unit, List<string> findings)
    {
        // The reference is the most recent earlier week with volume: a plan week, else the last actual week, else the
        // baseline. With none, there is nothing to compare with.
        (double Volume, string Name)? reference =
            actuals.Count > 0 && actuals[^1] > 0 ? (actuals[^1], "the last actual week's")
            : profile.BaselineWeeklyVolume > 0 ? (profile.BaselineWeeklyVolume, "the baseline's")
            : null;
        var field = profile.VolumeInMinutes ? "durationMin" : "distanceKm";

        foreach (var week in weeks)
        {
            foreach (var session in week.Sessions.Where(s => (profile.VolumeInMinutes ? s.DurationMin : s.DistanceKm) is null))
            {
                findings.Add(Invariant($"R1: week {week.Week} session on {Date(session.Date)} has no {field}"));
            }

            var volume = Volume(week.Sessions, profile.VolumeInMinutes);
            if (reference is { } prior && volume > prior.Volume * (1 + (rules.MaxWeeklyIncreasePct / 100)) + Epsilon)
            {
                var increase = (volume - prior.Volume) / prior.Volume * 100;
                findings.Add(Invariant(
                    $"R1: week {week.Week} volume {One(volume)} {unit} is +{Whole(increase)}% over {prior.Name} {One(prior.Volume)} {unit} (max +{Pct(rules.MaxWeeklyIncreasePct)}%)"));
            }

            if (volume > 0)
            {
                reference = (volume, Invariant($"week {week.Week}'s"));
            }
        }
    }

    private static void R2Recovery(List<PlanWeek> weeks, VelocityThresholds rules, List<string> findings)
    {
        var hard = weeks.SelectMany(w => w.Sessions)
            .Where(s => s.Intensity is SessionIntensity.Hard or SessionIntensity.Race)
            .Select(s => s.Date)
            .Order()
            .ToList();
        for (var i = 1; i < hard.Count; i++)
        {
            var hours = (hard[i].DayNumber - hard[i - 1].DayNumber) * 24.0;
            if (hours + Epsilon < rules.MinHoursBetweenHard)
            {
                findings.Add(Invariant(
                    $"R2: hard sessions on {Date(hard[i - 1])} and {Date(hard[i])} are {Pct(hours)} h apart (min {Pct(rules.MinHoursBetweenHard)} h)"));
            }
        }
    }

    private static void R3HardCount(List<PlanWeek> weeks, VelocityThresholds rules, List<string> findings)
    {
        foreach (var week in weeks)
        {
            var count = week.Sessions.Count(s => s.Intensity == SessionIntensity.Hard);
            if (count > rules.MaxHardPerWeek)
            {
                findings.Add(Invariant($"R3: week {week.Week} has {count} hard sessions (max {rules.MaxHardPerWeek})"));
            }
        }
    }

    private static void R4Taper(List<PlanWeek> weeks, List<double> actuals, AthleteProfile profile, VelocityThresholds rules, string unit, List<string> findings)
    {
        if (profile.RaceDate is not { } race)
        {
            return;
        }

        var history = new List<double>(actuals);
        if (history.Count == 0 && profile.BaselineWeeklyVolume > 0)
        {
            history.Add(profile.BaselineWeeklyVolume);
        }

        foreach (var week in weeks)
        {
            var beforeRace = week.Sessions.Where(s => s.Date < race).ToList();
            var peak = history.Skip(Math.Max(0, history.Count - ActualWeeks)).DefaultIfEmpty(0).Max();
            if (beforeRace.Count > 0 && peak > 0)
            {
                // The week ends on its 7th day: its first session's date + 6.
                var daysBefore = race.DayNumber - (week.Sessions.Min(s => s.Date).DayNumber + 6);
                var (limit, window) = daysBefore <= 7 ? (rules.TaperPctWithin7Days, 7) : (rules.TaperPctWithin14Days, 14);
                var volume = Volume(beforeRace, profile.VolumeInMinutes);
                if (daysBefore <= 14 && volume > (peak * limit / 100) + Epsilon)
                {
                    findings.Add(Invariant(
                        $"R4: week {week.Week} volume {One(volume)} {unit} is {Whole(volume / peak * 100)}% of peak {One(peak)} {unit} within {window} days of the race (max {Pct(limit)}%)"));
                }
            }

            history.Add(Volume(week.Sessions, profile.VolumeInMinutes));
        }
    }

    private static void R5Rest(List<PlanWeek> weeks, VelocityThresholds rules, List<string> findings)
    {
        foreach (var week in weeks)
        {
            var rest = Math.Max(0, 7 - week.Sessions.Select(s => s.Date).Distinct().Count());
            if (rest < rules.MinRestDaysPerWeek)
            {
                findings.Add(rest == 0
                    ? Invariant($"R5: week {week.Week} has no rest day")
                    : Invariant($"R5: week {week.Week} has {rest} rest day{(rest == 1 ? string.Empty : "s")} (min {rules.MinRestDaysPerWeek})"));
            }
        }
    }

    /// <summary>The volumes of the last <see cref="ActualWeeks"/> 7-day weeks of actuals ending on the newest logged
    /// date, oldest first, from the oldest week with a session.</summary>
    private static List<double> ActualWeekVolumes(List<CoachSession> sessions, AthleteProfile profile)
    {
        if (sessions.Count == 0)
        {
            return [];
        }

        var newest = sessions.Max(s => s.Date).DayNumber;
        var volumes = new double[ActualWeeks];
        var oldest = 0;
        foreach (var session in sessions)
        {
            var bucket = (newest - session.Date.DayNumber) / 7;
            if (bucket < ActualWeeks)
            {
                volumes[bucket] += (profile.VolumeInMinutes ? session.DurationMin : session.DistanceKm) ?? 0;
                oldest = Math.Max(oldest, bucket);
            }
        }

        return [.. volumes.Take(oldest + 1).Reverse()];
    }

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string One(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>A breach percentage rounded up to a whole percent (+10.4% is +11%), with a tolerance for binary
    /// fractions (58/50 is +16%, not +17%).</summary>
    private static string Whole(double value) => Math.Ceiling(value - 1e-6).ToString("0", CultureInfo.InvariantCulture);

    private static string Pct(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
