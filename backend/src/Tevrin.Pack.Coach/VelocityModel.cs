using System.Globalization;
using System.Text.Json;

namespace Tevrin.Pack.Coach;

/// <summary>§4.63: a session's intensity.</summary>
public enum SessionIntensity
{
    Easy = 0,
    Moderate = 1,
    Hard = 2,
    Race = 3,
}

/// <summary>§4.63: a planned or logged session (<c>date</c>, <c>type</c>, <c>intensity</c>, <c>distanceKm?</c>,
/// <c>durationMin?</c>). A logged session without an intensity is <see cref="SessionIntensity.Easy"/>.</summary>
public sealed record CoachSession(DateOnly Date, string Type, SessionIntensity Intensity, double? DistanceKm = null, double? DurationMin = null);

/// <summary>§4.63: one week of <c>plan_draft.weeks[]</c>.</summary>
public sealed record PlanWeek(int Week, IReadOnlyList<CoachSession> Sessions);

/// <summary>§4.63: the Velocity thresholds, the defaults unless <c>athlete_profile.velocity</c> overrides them
/// (D-0.8.0-12).</summary>
public sealed record VelocityThresholds
{
    public static VelocityThresholds Defaults { get; } = new();

    /// <summary>R1 (<c>maxWeeklyIncreasePct</c>): the most a week's volume may rise over the previous week's, in percent.</summary>
    public double MaxWeeklyIncreasePct { get; init; } = 10;

    /// <summary>R2 (<c>minHoursBetweenHard</c>): the fewest hours between the starts of two hard or race sessions.</summary>
    public double MinHoursBetweenHard { get; init; } = 48;

    /// <summary>R3 (<c>maxHardPerWeek</c>): the most hard sessions per plan week.</summary>
    public int MaxHardPerWeek { get; init; } = 3;

    /// <summary>R4 (<c>taperPctWithin14Days</c>): a week ending 8–14 days before the race, as a percentage of the previous
    /// 4 weeks' peak.</summary>
    public double TaperPctWithin14Days { get; init; } = 70;

    /// <summary>R4 (<c>taperPctWithin7Days</c>): a week ending within 7 days of the race, as a percentage of the previous
    /// 4 weeks' peak.</summary>
    public double TaperPctWithin7Days { get; init; } = 50;

    /// <summary>R5 (<c>minRestDaysPerWeek</c>): the fewest days with no session in a plan week.</summary>
    public int MinRestDaysPerWeek { get; init; } = 1;
}

/// <summary>§4.63: the <c>athlete_profile</c> fields the rules read. <see cref="BaselineWeeklyVolume"/> is
/// <c>baselineWeeklyKm</c>, read as minutes when <see cref="VolumeInMinutes"/>.</summary>
public sealed record AthleteProfile(double BaselineWeeklyVolume, bool VolumeInMinutes = false, DateOnly? RaceDate = null, VelocityThresholds? Thresholds = null)
{
    public VelocityThresholds Rules => Thresholds ?? VelocityThresholds.Defaults;
}

/// <summary>Reads the coach's JSON artifacts into the rules' model. Malformed content throws <see cref="FormatException"/>.</summary>
public static class VelocityJson
{
    public static IReadOnlyList<PlanWeek> ReadPlan(string planDraftJson)
    {
        using var document = Parse(planDraftJson, CoachArtifacts.PlanDraft);
        if (!document.RootElement.TryGetProperty("weeks", out var weeks) || weeks.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("plan_draft has no weeks array.");
        }

        var result = new List<PlanWeek>();
        var index = 0;
        foreach (var week in weeks.EnumerateArray())
        {
            index++;
            var number = week.TryGetProperty("week", out var w) && w.ValueKind == JsonValueKind.Number && w.TryGetInt32(out var n) ? n : index;
            List<CoachSession> sessions = week.TryGetProperty("sessions", out var s) && s.ValueKind == JsonValueKind.Array
                ? [.. s.EnumerateArray().Select(ReadSession)]
                : [];
            result.Add(new PlanWeek(number, sessions));
        }

        return result;
    }

    public static AthleteProfile ReadProfile(string athleteProfileJson)
    {
        using var document = Parse(athleteProfileJson, CoachArtifacts.AthleteProfile);
        var root = document.RootElement;
        var baseline = Number(root, "baselineWeeklyKm") ?? 0;
        var minutes = root.TryGetProperty("volumeUnit", out var unit) && unit.ValueKind == JsonValueKind.String
            && string.Equals(unit.GetString(), "minutes", StringComparison.Ordinal);
        DateOnly? raceDate = root.TryGetProperty("raceDate", out var race) && race.ValueKind == JsonValueKind.String
            ? ReadDate(race.GetString())
            : null;
        var thresholds = VelocityThresholds.Defaults;
        if (root.TryGetProperty("velocity", out var v) && v.ValueKind == JsonValueKind.Object)
        {
            thresholds = new VelocityThresholds
            {
                MaxWeeklyIncreasePct = Number(v, "maxWeeklyIncreasePct") ?? thresholds.MaxWeeklyIncreasePct,
                MinHoursBetweenHard = Number(v, "minHoursBetweenHard") ?? thresholds.MinHoursBetweenHard,
                MaxHardPerWeek = (int?)Number(v, "maxHardPerWeek") ?? thresholds.MaxHardPerWeek,
                TaperPctWithin14Days = Number(v, "taperPctWithin14Days") ?? thresholds.TaperPctWithin14Days,
                TaperPctWithin7Days = Number(v, "taperPctWithin7Days") ?? thresholds.TaperPctWithin7Days,
                MinRestDaysPerWeek = (int?)Number(v, "minRestDaysPerWeek") ?? thresholds.MinRestDaysPerWeek,
            };
        }

        return new AthleteProfile(baseline, minutes, raceDate, thresholds);
    }

    /// <summary>The sessions of one <c>session_log</c> version.</summary>
    public static IReadOnlyList<CoachSession> ReadSessionLog(string sessionLogJson)
    {
        using var document = Parse(sessionLogJson, CoachArtifacts.SessionLog);
        return document.RootElement.TryGetProperty("sessions", out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Select(ReadSession)]
            : [];
    }

    private static JsonDocument Parse(string json, string artifact)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"{artifact} is not valid JSON: {e.Message}", e);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new FormatException($"{artifact} is not a JSON object.");
        }

        return document;
    }

    private static CoachSession ReadSession(JsonElement session)
    {
        var date = session.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
            ? ReadDate(d.GetString())
            : throw new FormatException("A session has no date.");
        var type = session.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? string.Empty : string.Empty;
        var intensity = session.TryGetProperty("intensity", out var i) && i.ValueKind == JsonValueKind.String
            ? ReadIntensity(i.GetString())
            : SessionIntensity.Easy;
        return new CoachSession(date, type, intensity, Number(session, "distanceKm"), Number(session, "durationMin"));
    }

    private static SessionIntensity ReadIntensity(string? value) => value switch
    {
        "easy" => SessionIntensity.Easy,
        "moderate" => SessionIntensity.Moderate,
        "hard" => SessionIntensity.Hard,
        "race" => SessionIntensity.Race,
        _ => throw new FormatException($"'{value}' is not a session intensity (easy, moderate, hard or race)."),
    };

    private static DateOnly ReadDate(string? value)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        // A date-time's own calendar date (in its offset), not its UTC date.
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
            ? DateOnly.FromDateTime(instant.DateTime)
            : throw new FormatException($"'{value}' is not a date (YYYY-MM-DD).");
    }

    private static double? Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
