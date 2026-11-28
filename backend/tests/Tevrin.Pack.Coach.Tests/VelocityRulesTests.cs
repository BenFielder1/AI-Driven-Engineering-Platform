namespace Tevrin.Pack.Coach.Tests;

/// <summary>FR-340, §4.63: the Velocity load rules R1–R5, their defaults and overrides (D-0.8.0-12), and the worked
/// example.</summary>
public class VelocityRulesTests
{
    private static readonly DateOnly Week1Monday = new(2026, 11, 23);

    /// <summary>§4.63 worked example: the last actual week is 50 km (Mon 2026-11-16 to Sun 2026-11-22).</summary>
    private static readonly CoachSession[] LastActualWeek50 =
    [
        new(new(2026, 11, 17), "intervals", SessionIntensity.Hard, 10),
        new(new(2026, 11, 19), "easy run", SessionIntensity.Easy, 15),
        new(new(2026, 11, 22), "long run", SessionIntensity.Easy, 25),
    ];

    private static readonly AthleteProfile NoRace = new(BaselineWeeklyVolume: 45);

    /// <summary>Week 1 = 50 km: Mon easy 8, Tue hard 10, Thu easy 10, Sat long 22 (rest Wed, Fri, Sun).</summary>
    private static PlanWeek WorkedWeek1() => new(1,
    [
        new(Week1Monday, "easy run", SessionIntensity.Easy, 8),
        new(Week1Monday.AddDays(1), "intervals", SessionIntensity.Hard, 10),
        new(Week1Monday.AddDays(3), "easy run", SessionIntensity.Easy, 10),
        new(Week1Monday.AddDays(5), "long run", SessionIntensity.Easy, 22),
    ]);

    /// <summary>Week 2 (Mon 2026-11-30): Mon easy 8, Tue hard 12, a second hard 12 on <paramref name="secondHardDay"/>
    /// (2 = Wed, 3 = Thu), Fri easy 7, Sun long (the rest of <paramref name="volume"/>).</summary>
    private static PlanWeek WorkedWeek2(double volume, int secondHardDay)
    {
        var monday = Week1Monday.AddDays(7);
        return new(2,
        [
            new(monday, "easy run", SessionIntensity.Easy, 8),
            new(monday.AddDays(1), "intervals", SessionIntensity.Hard, 12),
            new(monday.AddDays(secondHardDay), "tempo", SessionIntensity.Hard, 12),
            new(monday.AddDays(4), "easy run", SessionIntensity.Easy, 7),
            new(monday.AddDays(6), "long run", SessionIntensity.Easy, volume - 39),
        ]);
    }

    private static IReadOnlyList<string> Worked(double week2, int secondHardDay) =>
        VelocityRules.Check([WorkedWeek1(), WorkedWeek2(week2, secondHardDay)], NoRace, LastActualWeek50);

    [Fact]
    public void FR340_worked_example_attempt_1_breaks_R1_and_R2()
    {
        Assert.Equal(
            [
                "R1: week 2 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)",
                "R2: hard sessions on 2026-12-01 and 2026-12-02 are 24 h apart (min 48 h)",
            ],
            Worked(61, secondHardDay: 2));
    }

    [Fact]
    public void FR340_worked_example_attempt_2_breaks_R1_at_16_percent()
    {
        Assert.Equal(["R1: week 2 volume 58.0 km is +16% over week 1's 50.0 km (max +10%)"], Worked(58, secondHardDay: 3));
    }

    [Fact]
    public void FR340_worked_example_attempt_3_breaks_R1_at_12_percent()
    {
        Assert.Equal(["R1: week 2 volume 56.0 km is +12% over week 1's 50.0 km (max +10%)"], Worked(56, secondHardDay: 3));
    }

    [Fact]
    public void FR340_worked_example_passing_variant_55_km_with_tue_thu_hard_has_no_findings()
    {
        Assert.Empty(Worked(55, secondHardDay: 3));
    }

    [Fact]
    public void FR340_R1_week_1_compares_with_the_last_actual_week()
    {
        var week1 = new PlanWeek(1, [.. WorkedWeek1().Sessions.Select(s => s.Date == Week1Monday.AddDays(5) ? s with { DistanceKm = 32 } : s)]);

        Assert.Equal(["R1: week 1 volume 60.0 km is +20% over the last actual week's 50.0 km (max +10%)"],
            VelocityRules.Check([week1], NoRace, LastActualWeek50));
    }

    [Fact]
    public void FR340_R1_week_1_compares_with_the_baseline_without_actuals()
    {
        Assert.Equal(["R1: week 1 volume 50.0 km is +25% over the baseline's 40.0 km (max +10%)"],
            VelocityRules.Check([WorkedWeek1()], new AthleteProfile(40), []));
        Assert.Empty(VelocityRules.Check([WorkedWeek1()], new AthleteProfile(46), []));
    }

    [Fact]
    public void FR340_R1_identical_sessions_given_twice_both_count()
    {
        // 2 × 50 km: the last actual week is 100 km, so a 60 km week 1 is no rise (it would be +20% over 50 km).
        var week1 = new PlanWeek(1, [.. WorkedWeek1().Sessions.Select(s => s.Date == Week1Monday.AddDays(5) ? s with { DistanceKm = 32 } : s)]);

        Assert.Empty(VelocityRules.Check([week1], NoRace, [.. LastActualWeek50, .. LastActualWeek50]));
        Assert.Single(VelocityRules.Check([week1], NoRace, LastActualWeek50));
    }

    [Fact]
    public void FR340_R1_a_session_without_the_units_volume_field_is_a_finding()
    {
        var week = new PlanWeek(2,
        [
            new(Week1Monday, "easy run", SessionIntensity.Easy, DistanceKm: 10, DurationMin: 60),
            new(Week1Monday.AddDays(2), "long run", SessionIntensity.Easy, DurationMin: 120),
        ]);

        Assert.Equal(["R1: week 2 session on 2026-11-25 has no distanceKm"], VelocityRules.Check([week], new AthleteProfile(100), []));
        Assert.Equal(["R1: week 2 session on 2026-11-23 has no durationMin", "R1: week 2 session on 2026-11-25 has no durationMin"],
            VelocityRules.Check([week with { Sessions = [.. week.Sessions.Select(s => s with { DurationMin = null })] }], new AthleteProfile(500, VolumeInMinutes: true), []));
    }

    [Fact]
    public void FR340_R1_a_week_after_a_zero_volume_week_compares_with_the_last_week_with_volume()
    {
        var rest = new PlanWeek(2, []);
        var week3 = new PlanWeek(3, [new(Week1Monday.AddDays(14), "long run", SessionIntensity.Easy, 61)]);

        Assert.Equal(["R1: week 3 volume 61.0 km is +22% over week 1's 50.0 km (max +10%)"],
            VelocityRules.Check([WorkedWeek1(), rest, week3], NoRace, LastActualWeek50));
        Assert.Equal(["R1: week 2 volume 61.0 km is +22% over the last actual week's 50.0 km (max +10%)"],
            VelocityRules.Check([new PlanWeek(1, []), week3 with { Week = 2 }], NoRace, LastActualWeek50));
        Assert.Empty(VelocityRules.Check([new PlanWeek(1, []), week3 with { Week = 2 }], new AthleteProfile(0), []));
    }

    [Fact]
    public void FR340_breach_percentages_round_up()
    {
        var week = new PlanWeek(1, [new(Week1Monday, "long run", SessionIntensity.Easy, 55.2)]);

        Assert.Equal(["R1: week 1 volume 55.2 km is +11% over the baseline's 50.0 km (max +10%)"], VelocityRules.Check([week], new AthleteProfile(50), []));
    }

    [Fact]
    public void FR340_R4_a_week_ends_on_its_7th_day_not_its_last_session()
    {
        // Sessions Mon 2026-11-30 and Thu 2026-12-03; the race is Fri 2026-12-18: the last session is 15 days out, but the
        // week's 7th day (Sun 2026-12-06) is 12 days out, so the 70% limit applies.
        var profile = new AthleteProfile(60, RaceDate: new DateOnly(2026, 12, 18));
        var monday = new DateOnly(2026, 11, 30);
        var week = new PlanWeek(1, [new(monday, "easy run", SessionIntensity.Easy, 20), new(monday.AddDays(3), "long run", SessionIntensity.Easy, 30)]);

        Assert.Equal(["R4: week 1 volume 50.0 km is 84% of peak 60.0 km within 14 days of the race (max 70%)"], VelocityRules.Check([week], profile, []));
    }

    [Fact]
    public void FR340_a_date_time_with_an_offset_uses_its_own_calendar_date()
    {
        const string log = """{ "sessions": [ { "date": "2026-11-23T23:30:00-05:00", "type": "run", "distanceKm": 5 } ] }""";

        Assert.Equal(new DateOnly(2026, 11, 23), Assert.Single(VelocityJson.ReadSessionLog(log)).Date);
    }

    [Fact]
    public void FR340_R1_override_maxWeeklyIncreasePct()
    {
        var profile = NoRace with { Thresholds = new VelocityThresholds { MaxWeeklyIncreasePct = 25, MinHoursBetweenHard = 24 } };

        Assert.Empty(VelocityRules.Check([WorkedWeek1(), WorkedWeek2(61, 2)], profile, LastActualWeek50));
    }

    [Fact]
    public void FR340_R2_passes_at_48_hours_and_counts_race_sessions()
    {
        var monday = Week1Monday;
        PlanWeek Week(SessionIntensity second, int day) => new(1,
        [
            new(monday, "intervals", SessionIntensity.Hard, 10),
            new(monday.AddDays(day), "10k", second, 10),
        ]);

        Assert.Empty(VelocityRules.Check([Week(SessionIntensity.Hard, 2)], new AthleteProfile(100), []));
        Assert.Equal(["R2: hard sessions on 2026-11-23 and 2026-11-24 are 24 h apart (min 48 h)"],
            VelocityRules.Check([Week(SessionIntensity.Race, 1)], new AthleteProfile(100), []));
        Assert.Empty(VelocityRules.Check([Week(SessionIntensity.Moderate, 1)], new AthleteProfile(100), []));
    }

    [Fact]
    public void FR340_R2_override_minHoursBetweenHard()
    {
        var week = new PlanWeek(1, [new(Week1Monday, "a", SessionIntensity.Hard, 10), new(Week1Monday.AddDays(2), "b", SessionIntensity.Hard, 10)]);
        var profile = new AthleteProfile(100, Thresholds: new VelocityThresholds { MinHoursBetweenHard = 72 });

        Assert.Equal(["R2: hard sessions on 2026-11-23 and 2026-11-25 are 48 h apart (min 72 h)"], VelocityRules.Check([week], profile, []));
    }

    /// <summary>Hard sessions Mon, Wed, Fri, Sun (48 h apart, 3 rest days), as week <paramref name="number"/>.</summary>
    private static PlanWeek FourHard(int number) => new(number,
    [
        new(Week1Monday, "a", SessionIntensity.Hard, 10),
        new(Week1Monday.AddDays(2), "b", SessionIntensity.Hard, 10),
        new(Week1Monday.AddDays(4), "c", SessionIntensity.Hard, 10),
        new(Week1Monday.AddDays(6), "d", SessionIntensity.Hard, 10),
    ]);

    [Fact]
    public void FR340_R3_more_than_3_hard_sessions_in_a_week()
    {
        Assert.Equal(["R3: week 3 has 4 hard sessions (max 3)"], VelocityRules.Check([FourHard(3)], new AthleteProfile(100), []));
    }

    [Fact]
    public void FR340_R3_3_hard_sessions_pass_and_the_override_applies()
    {
        var three = new PlanWeek(1, [.. FourHard(1).Sessions.Take(3)]);
        Assert.Empty(VelocityRules.Check([three], new AthleteProfile(100), []));

        var profile = new AthleteProfile(100, Thresholds: new VelocityThresholds { MaxHardPerWeek = 4 });
        Assert.Empty(VelocityRules.Check([FourHard(1)], profile, []));
    }

    /// <summary>A plan week starting Monday 2026-11-30 + (n − 1) weeks: Mon easy 20%, Wed hard 30%, Sun long 50%.</summary>
    private static PlanWeek TaperWeek(int number, double volume)
    {
        var monday = new DateOnly(2026, 11, 30).AddDays((number - 1) * 7);
        return new(number,
        [
            new(monday, "easy run", SessionIntensity.Easy, volume * 0.2),
            new(monday.AddDays(2), "tempo", SessionIntensity.Hard, volume * 0.3),
            new(monday.AddDays(6), "long run", SessionIntensity.Easy, volume * 0.5),
        ]);
    }

    /// <summary>Race on Sunday 2027-01-10: week 4 ends 14 days before it, week 5 ends 7 days before it.</summary>
    private static readonly AthleteProfile Racing = new(60, RaceDate: new DateOnly(2027, 1, 10));

    [Fact]
    public void FR340_R4_taper_weeks_over_70_and_50_percent_of_peak()
    {
        IReadOnlyList<PlanWeek> plan = [TaperWeek(1, 60), TaperWeek(2, 60), TaperWeek(3, 60), TaperWeek(4, 48), TaperWeek(5, 36)];

        Assert.Equal(
            [
                "R4: week 4 volume 48.0 km is 80% of peak 60.0 km within 14 days of the race (max 70%)",
                "R4: week 5 volume 36.0 km is 60% of peak 60.0 km within 7 days of the race (max 50%)",
            ],
            VelocityRules.Check(plan, Racing, []));
    }

    [Fact]
    public void FR340_R4_a_70_and_50_percent_taper_passes_and_no_race_date_means_no_taper()
    {
        Assert.Empty(VelocityRules.Check([TaperWeek(1, 60), TaperWeek(2, 60), TaperWeek(3, 60), TaperWeek(4, 42), TaperWeek(5, 30)], Racing, []));
        Assert.Empty(VelocityRules.Check([TaperWeek(1, 60), TaperWeek(2, 60), TaperWeek(3, 60), TaperWeek(4, 60), TaperWeek(5, 60)], Racing with { RaceDate = null }, []));
    }

    [Fact]
    public void FR340_R4_overrides_taper_percentages()
    {
        var profile = Racing with { Thresholds = new VelocityThresholds { TaperPctWithin14Days = 85, TaperPctWithin7Days = 60 } };

        Assert.Empty(VelocityRules.Check([TaperWeek(1, 60), TaperWeek(2, 60), TaperWeek(3, 60), TaperWeek(4, 48), TaperWeek(5, 36)], profile, []));
    }

    [Fact]
    public void FR340_R5_a_week_with_a_session_every_day_has_no_rest_day()
    {
        var everyDay = new PlanWeek(1, [.. Enumerable.Range(0, 7).Select(d => new CoachSession(Week1Monday.AddDays(d), "easy run", SessionIntensity.Easy, 5))]);

        Assert.Equal(["R5: week 1 has no rest day"], VelocityRules.Check([everyDay], new AthleteProfile(100), []));
        Assert.Empty(VelocityRules.Check([everyDay with { Sessions = [.. everyDay.Sessions.Skip(1)] }], new AthleteProfile(100), []));
    }

    [Fact]
    public void FR340_R5_override_minRestDaysPerWeek()
    {
        var sixDays = new PlanWeek(1, [.. Enumerable.Range(0, 6).Select(d => new CoachSession(Week1Monday.AddDays(d), "easy run", SessionIntensity.Easy, 5))]);
        var profile = new AthleteProfile(100, Thresholds: new VelocityThresholds { MinRestDaysPerWeek = 2 });

        Assert.Equal(["R5: week 1 has 1 rest day (min 2)"], VelocityRules.Check([sixDays], profile, []));
    }

    [Fact]
    public void FR340_volume_unit_minutes_uses_durationMin()
    {
        var week = new PlanWeek(1,
        [
            new(Week1Monday, "easy run", SessionIntensity.Easy, DistanceKm: 1, DurationMin: 160),
            new(Week1Monday.AddDays(3), "long run", SessionIntensity.Easy, DistanceKm: 1, DurationMin: 200),
        ]);

        Assert.Equal(["R1: week 1 volume 360.0 min is +20% over the baseline's 300.0 min (max +10%)"],
            VelocityRules.Check([week], new AthleteProfile(300, VolumeInMinutes: true), []));
        Assert.Empty(VelocityRules.Check([week], new AthleteProfile(300), []));
    }

    [Fact]
    public void FR340_reads_the_json_artifacts_with_velocity_overrides()
    {
        const string plan = """
            { "weeks": [
              { "week": 1, "sessions": [
                { "date": "2026-11-23", "type": "easy run", "intensity": "easy", "distanceKm": 30 },
                { "date": "2026-11-24", "type": "intervals", "intensity": "hard", "distanceKm": 10 },
                { "date": "2026-11-25", "type": "tempo", "intensity": "hard", "distanceKm": 10 }
              ] }
            ] }
            """;
        const string profile = """{ "name": "Sam", "baselineWeeklyKm": 40, "velocity": { "maxWeeklyIncreasePct": 30 } }""";
        const string log = """{ "sessions": [ { "date": "2026-11-20", "type": "easy run", "distanceKm": 40 } ] }""";

        Assert.Equal(["R2: hard sessions on 2026-11-24 and 2026-11-25 are 24 h apart (min 48 h)"], VelocityRules.Check(plan, profile, [log]));
        Assert.Throws<FormatException>(() => VelocityRules.Check("not json", profile, []));
        Assert.Throws<FormatException>(() => VelocityRules.Check("""{ "weeks": [ { "week": 1, "sessions": [ { "date": "2026-11-23", "intensity": "max" } ] } ] }""", profile, []));
    }
}
