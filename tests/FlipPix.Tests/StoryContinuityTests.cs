using System.Collections.Generic;
using System.Linq;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="StoryContinuity"/> — the pass that decides where every clip of a story is, at what hour and
/// in what light.
///
/// <para>Every test here is a shape a real model actually produced. The class exists because H3 renders
/// each clip as an independent job and invents whatever the prose leaves out, so the repairs below are
/// the difference between a film and a film that cuts from midday to midnight and back.</para>
/// </summary>
public class StoryContinuityTests
{
    private static readonly string[] NoBeats = System.Array.Empty<string>();

    // ── Reading what the beat sheet wrote ───────────────────────────────────────────────────────

    [Fact]
    public void SplitBeat_takes_the_environment_off_the_beat()
    {
        var (text, env) = StoryContinuity.SplitBeat(
            "She shoves the door open and steps into the rain. " +
            "[EXT | the alley behind the club | night | heavy rain, neon signs]");

        Assert.Equal("She shoves the door open and steps into the rain.", text);
        Assert.Equal("EXT | the alley behind the club | night | heavy rain, neon signs", env);
    }

    [Fact]
    public void SplitBeat_leaves_a_bracketed_aside_where_it_was()
    {
        // A parenthesis with no separator in it is prose, not an environment. Eating it would delete
        // story from the beat.
        var (text, env) = StoryContinuity.SplitBeat("He turns away (she is still holding the knife)");

        Assert.Equal("He turns away (she is still holding the knife)", text);
        Assert.Equal(string.Empty, env);
    }

    [Fact]
    public void SplitBeat_passes_a_plain_beat_through()
    {
        var (text, env) = StoryContinuity.SplitBeat("No suffix at all here.");

        Assert.Equal("No suffix at all here.", text);
        Assert.Equal(string.Empty, env);
    }

    [Fact]
    public void Read_takes_the_four_fields_apart()
    {
        var env = StoryContinuity.Read("INT | the back office | late night | one desk lamp");

        Assert.True(env.Interior);
        Assert.Equal("the back office", env.Place);
        Assert.Equal("late night", env.TimeOfDay);
        Assert.Equal("one desk lamp", env.Light);
    }

    [Fact]
    public void Read_understands_a_looser_form()
    {
        var env = StoryContinuity.Read("EXT the rooftop, after dark, city glow");

        Assert.False(env.Interior);
        Assert.Equal("night", env.TimeOfDay);
    }

    [Fact]
    public void Read_can_take_the_SETTING_sentence_instead()
    {
        // The fallback when a model ignored the bracket instruction entirely: the chain is held to the
        // one sentence it did write.
        var env = StoryContinuity.Read(
            "A rain-slicked alley behind a nightclub, just after midnight, heavy rain");

        Assert.Equal("late night", env.TimeOfDay);
    }

    [Theory]
    [InlineData("late afternoon", "afternoon")]   // "afternoon" ends in "noon" — see HourIn
    [InlineData("early afternoon", "afternoon")]
    [InlineData("high noon", "midday")]
    [InlineData("just after midnight", "late night")]
    [InlineData("at first light", "dawn")]
    [InlineData("as the sun sets", "dusk")]
    [InlineData("well after dark", "night")]
    [InlineData("nothing about time at all", "")]
    public void HourIn_reads_a_written_hour(string text, string expected) =>
        Assert.Equal(expected, StoryContinuity.HourIn(text));

    // ── The repairs ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_beat_that_wrote_no_environment_inherits_the_one_before_it()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley | night | heavy rain", "", "" },
            settingLine: null,
            beatTexts: NoBeats);

        Assert.Equal(3, plan.Count);
        Assert.True(plan[1].SameAs(plan[0]));
        Assert.True(plan[2].SameAs(plan[0]));
    }

    [Fact]
    public void The_same_place_said_differently_keeps_the_first_wording()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley behind the club | night | heavy rain", "EXT | the alley | night |" },
            settingLine: null,
            beatTexts: NoBeats);

        Assert.Equal("the alley behind the club", plan[1].Place);
        Assert.Equal("heavy rain", plan[1].Light);   // same place, same hour ⇒ the same light words
    }

    [Fact]
    public void A_place_the_story_returns_to_comes_back_in_its_first_wording()
    {
        var plan = StoryContinuity.Plan(
            new[]
            {
                "EXT | the alley behind the club | night | heavy rain",
                "INT | the back office | night | one desk lamp",
                "EXT | the alley | night | rain",
            },
            settingLine: null,
            beatTexts: NoBeats);

        Assert.Equal(plan[0].Place, plan[2].Place);
        Assert.False(plan[2].Interior);
    }

    [Fact]
    public void A_real_move_is_kept()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley | night | heavy rain", "INT | the back office | night | one desk lamp" },
            settingLine: null,
            beatTexts: NoBeats);

        Assert.True(plan[1].Interior);
        Assert.Contains("office", plan[1].Place);
        Assert.False(plan[1].SameAs(plan[0]));
    }

    [Fact]
    public void The_clock_cannot_run_backwards()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley | night | neon", "EXT | the alley | afternoon | bright sun" },
            settingLine: null,
            beatTexts: new[] { "She steps out.", "Hours later, she is still there." });

        Assert.Equal("night", plan[1].TimeOfDay);
        // And the refused hour does not leave its sunlight behind: keeping "bright sun" would light the
        // clip in exactly the way the refusal was for.
        Assert.DoesNotContain("sun", plan[1].Light);
    }

    [Fact]
    public void Night_to_dawn_is_the_one_legal_wrap()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley | night | neon", "EXT | the alley | dawn | first grey light" },
            settingLine: null,
            beatTexts: new[] { "She steps out.", "Hours later, at first light, she walks out alone." });

        Assert.Equal("dawn", plan[1].TimeOfDay);
    }

    [Fact]
    public void The_clock_does_not_move_under_a_beat_that_never_said_time_passed()
    {
        // The failure a live model produced: one continuous two-minute fight planned as
        // midday -> morning -> late morning -> afternoon -> late afternoon -> evening, in six spellings
        // of one oasis. N x 15s is one moment, not a day.
        var rows = new[]
        {
            "EXT | the oasis | midday | bright sun",
            "EXT | the oasis | morning | bright sun",
            "EXT | the oasis | late morning | bright sun",
            "EXT | the oasis afternoon | afternoon | bright sun",
            "EXT | the oasis ground | afternoon | bright sun",
            "EXT | the sandy oasis floor | late afternoon | bright sun",
            "EXT | the sands near the oasis | late afternoon | warm light",
            "EXT | the sand pit near the oasis | evening | fading sun",
        };
        var beats = new[]
        {
            "Zora stands by the water as Kael approaches.",
            "Zora kicks his shin and knees his groin.",
            "Zora pulls his head back and knees his nose.",
            "Kael swings the scimitar; Zora rakes his chest.",
            "Zora straddles his chest and punches his face.",
            "Kael flips her off; she twists his arm.",
            "Zora stomps his back into the sand.",
            "Zora locks her legs around his throat until he goes limp.",
        };

        var plan = StoryContinuity.Plan(rows, "Desert oasis, midday, bright sun, hot and dry.", beats);

        Assert.All(plan, e => Assert.Equal("midday", e.TimeOfDay));
        Assert.All(plan, e => Assert.Equal(plan[0].Place, e.Place));
        Assert.All(plan, e => Assert.Equal(plan[0].Light, e.Light));
    }

    [Fact]
    public void A_story_that_says_time_passed_may_still_move_the_clock()
    {
        var rows = new[]
        {
            "EXT | the oasis | midday | bright sun",
            "EXT | the oasis | afternoon | bright sun",
            "EXT | the oasis | afternoon | bright sun",
        };
        var beats = new[]
        {
            "She stands by the water.",
            "Hours later, she comes back to the water alone.",
            "She sits down in the sand.",
        };

        var plan = StoryContinuity.Plan(rows, "Desert oasis, midday", beats);

        Assert.Equal("midday", plan[0].TimeOfDay);
        Assert.Equal("afternoon", plan[1].TimeOfDay);
        Assert.Equal("afternoon", plan[2].TimeOfDay);   // and it stays where the jump left it
    }

    [Fact]
    public void With_no_environments_at_all_the_whole_chain_is_held_to_the_SETTING_line()
    {
        var plan = StoryContinuity.Plan(
            new[] { "", "", "" },
            "A rain-slicked alley behind a nightclub at night, heavy rain",
            NoBeats);

        Assert.Equal("night", plan[0].TimeOfDay);
        Assert.All(plan, e => Assert.True(e.SameAs(plan[0])));
    }

    [Fact]
    public void With_nothing_to_go_on_nothing_is_invented()
    {
        var plan = StoryContinuity.Plan(new[] { "", "" }, settingLine: "", beatTexts: NoBeats);

        Assert.All(plan, e => Assert.True(e.IsEmpty));
    }

    // ── What the renderer is given ──────────────────────────────────────────────────────────────

    private const string Body =
        "integrated_multimodal_description: Live-action cinematic, shot on 35mm. [Shot 1] At 00:00.000, " +
        "<Picture 1> steps into frame.\n\noverall_soundscape: rain\n\nnon_diegetic_music: none";

    private static StoryContinuity.Environment Night =>
        new(false, "the alley behind the club", "night", "heavy rain, neon signs");

    [Fact]
    public void StampScene_writes_the_environment_in_ahead_of_the_first_shot()
    {
        var stamped = StoryContinuity.StampScene(Body, Night);

        Assert.True(stamped.IndexOf("Continuity of place", System.StringComparison.Ordinal) <
                    stamped.IndexOf("[Shot 1]", System.StringComparison.Ordinal));
        // The medium still leads: it is what H3 reads the whole look off.
        Assert.True(stamped.IndexOf("Live-action", System.StringComparison.Ordinal) <
                    stamped.IndexOf("Continuity of place", System.StringComparison.Ordinal));
        Assert.Contains("<Picture 1> steps into frame.", stamped);
        Assert.Contains("overall_soundscape: rain", stamped);
    }

    [Fact]
    public void StampScene_replaces_its_own_stamp_rather_than_stacking_one()
    {
        var once = StoryContinuity.StampScene(Body, Night);
        var twice = StoryContinuity.StampScene(
            once, new StoryContinuity.Environment(true, "the back office", "night", "one desk lamp"));

        Assert.Equal(1, Occurrences(twice, "Continuity of place"));
        // The sentence opens the place with a capital, so match on the part that is not sentence-cased.
        Assert.Contains("back office", twice);
        Assert.DoesNotContain("alley", twice);
        Assert.DoesNotContain("Continuity of place", StoryContinuity.StripScene(twice));
    }

    [Fact]
    public void StampScene_still_lands_on_a_body_with_no_shot_marker()
    {
        var stamped = StoryContinuity.StampScene("Just some prose with no fields.", Night);

        Assert.StartsWith("Continuity of place", stamped);
    }

    [Fact]
    public void SceneSentence_reads_as_a_sentence()
    {
        var sentence = StoryContinuity.SceneSentence(Night);

        Assert.StartsWith("Exterior. The alley behind the club", sentence);
        Assert.Contains("no daylight anywhere", sentence);
        Assert.EndsWith(".", sentence);
    }

    [Fact]
    public void An_empty_environment_stamps_nothing()
    {
        Assert.Equal(Body, StoryContinuity.StampScene(Body, default));
        Assert.Equal(string.Empty, StoryContinuity.SceneSentence(default));
    }

    // ── What the writer is told ─────────────────────────────────────────────────────────────────

    [Fact]
    public void An_unchanged_clip_is_told_to_hold_still()
    {
        var block = StoryContinuity.WriterBlock(Night, Night);

        Assert.Contains("SAME location", block);
        Assert.Contains("no sunlight", block);      // the specific things that would contradict the hour
        Assert.Contains("the alley behind the club", block);
    }

    [Fact]
    public void A_move_is_announced_as_a_move_and_names_where_it_came_from()
    {
        var block = StoryContinuity.WriterBlock(
            new StoryContinuity.Environment(true, "the back office", "night", "one desk lamp"), Night);

        Assert.Contains("story MOVES", block);
        Assert.Contains("alley", block);
        Assert.Contains("indoors", block);
    }

    [Fact]
    public void A_daylight_clip_is_told_the_opposite_things()
    {
        var block = StoryContinuity.WriterBlock(
            new StoryContinuity.Environment(false, "a field", "midday", "hard sun"), previous: null);

        Assert.Contains("no moonlight", block);
        Assert.DoesNotContain("SAME location", block);   // there is no clip before this one
    }

    [Fact]
    public void An_empty_environment_says_nothing_to_the_writer() =>
        Assert.Equal(string.Empty, StoryContinuity.WriterBlock(default, default));

    // ── Catching a clip that wrote the opposite anyway ──────────────────────────────────────────

    [Fact]
    public void Sunlight_in_a_night_clip_is_sent_back()
    {
        var reason = StoryContinuity.Contradiction("[Shot 2] sunlight catches the rail", Night);

        Assert.NotNull(reason);
        Assert.Contains("night", reason);
    }

    [Fact]
    public void Moonlight_in_a_daylight_clip_is_sent_back() =>
        Assert.NotNull(StoryContinuity.Contradiction(
            "[Shot 3] under moonlight she runs",
            new StoryContinuity.Environment(false, "a field", "midday", "hard sun")));

    [Theory]
    [InlineData("[Shot 2] the neon sign flickers in the rain")]
    [InlineData("[Shot 4] she steps into the doorway's shadow")]
    public void Ordinary_night_prose_passes(string body) =>
        Assert.Null(StoryContinuity.Contradiction(body, Night));

    [Fact]
    public void An_unplanned_clip_is_never_rejected() =>
        Assert.Null(StoryContinuity.Contradiction("sunlight everywhere, moonlight too", default));

    // ── The log line ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Describe_names_every_clip_and_counts_the_moves()
    {
        var plan = StoryContinuity.Plan(
            new[] { "EXT | the alley | night | rain", "INT | the office | night | a desk lamp" },
            settingLine: null,
            beatTexts: NoBeats);

        var lines = StoryContinuity.Describe(plan).ToList();

        Assert.Contains(lines, l => l.Contains("clip 1"));
        Assert.Contains(lines, l => l.Contains("clip 2") && l.Contains("moves"));
        Assert.Contains(lines, l => l.Contains("2 environment(s)"));
    }

    [Fact]
    public void Describe_says_so_when_there_is_no_plan_at_all()
    {
        var lines = StoryContinuity.Describe(new List<StoryContinuity.Environment>()).ToList();

        Assert.Single(lines);
        Assert.Contains("named no place or time", lines[0]);
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + 1, System.StringComparison.Ordinal)) n++;
        return n;
    }
}
