using System.Linq;
using FlipPix.UI.Services;

namespace FlipPix.Tests;

/// <summary>
/// <see cref="StoryBeatSheet"/> — the half of a story chain that decides what each clip is about, and
/// (since the continuity plan) where it happens.
///
/// <para>The parsing tests matter more than they look: a reply that parses to zero beats sends the whole
/// story down the <see cref="StoryBeatSheet.FromStory"/> fallback, which has no SETTING line in it and no
/// environments — the state every film in an overnight batch was rendered from on 2026-09-08.</para>
/// </summary>
public class StoryBeatSheetTests
{
    private const string Reply =
        "SETTING: a rain-slicked alley behind a nightclub, at night, heavy rain\n" +
        "1. She shoves the door open and steps into the rain. [EXT | the alley | night | heavy rain]\n" +
        "2. He follows her out and catches her wrist. [EXT | the alley | night | heavy rain]\n" +
        "3. They fall back through the door into the office. [INT | the back office | night | one desk lamp]\n";

    [Fact]
    public void Parse_reads_the_setting_and_the_beats()
    {
        var (setting, beats) = StoryBeatSheet.Parse(Reply);

        Assert.StartsWith("a rain-slicked alley", setting);
        Assert.Equal(3, beats.Count);
    }

    [Fact]
    public void Parse_takes_the_environment_off_the_beat_text()
    {
        var (_, beats) = StoryBeatSheet.Parse(Reply);

        Assert.DoesNotContain("[", beats[0].Text);
        Assert.Equal("EXT | the alley | night | heavy rain", beats[0].Env);
        Assert.StartsWith("INT", beats[2].Env);
    }

    [Fact]
    public void Parse_survives_a_model_that_introduces_itself()
    {
        var (_, beats) = StoryBeatSheet.Parse("Sure! Here is the beat sheet:\n\n" + Reply);

        Assert.Equal(3, beats.Count);
    }

    [Fact]
    public void Fit_splits_a_beat_across_clips_and_each_part_keeps_its_environment()
    {
        var (_, beats) = StoryBeatSheet.Parse(Reply);

        var fitted = StoryBeatSheet.Fit(beats, 6);

        Assert.Equal(6, fitted.Count);
        Assert.All(fitted, b => Assert.NotEqual(string.Empty, b.Env));
        Assert.Contains(fitted, b => b.PartCount > 1);
    }

    [Fact]
    public void Fit_merges_beats_and_keeps_the_first_environment_of_each_clip()
    {
        // A clip that opens in one place and is told halfway through that it is in another is exactly
        // the cut the plan exists to prevent.
        var (_, beats) = StoryBeatSheet.Parse(Reply);

        var fitted = StoryBeatSheet.Fit(beats, 2);

        Assert.Equal(2, fitted.Count);
        Assert.StartsWith("EXT", fitted[0].Env);
    }

    [Fact]
    public void FromStory_still_works_when_the_model_gives_nothing_back()
    {
        var fitted = StoryBeatSheet.FromStory(
            "She opens the door. He follows her out. They fall back inside.", 3);

        Assert.Equal(3, fitted.Count);
        Assert.All(fitted, b => Assert.Equal(string.Empty, b.Env));
    }

    // ── The prompt ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_continuity_rules_are_opt_in()
    {
        var with = StoryBeatSheet.BuildSystem(perBeatCast: false, continuity: true);
        var without = StoryBeatSheet.BuildSystem(perBeatCast: false);

        Assert.Contains("[EXT | the alley behind the club | night", with);
        Assert.Contains("dawn, morning, midday, afternoon", with);   // the hours are a closed list
        Assert.DoesNotContain("EXT |", without);                     // byte for byte what it always sent
    }

    [Fact]
    public void The_continuity_rules_reach_the_user_message_too()
    {
        // Both messages, because a rule stated once in a system prompt is a rule a small local model
        // drops by beat 6.
        var user = StoryBeatSheet.BuildUser(
            "A story.", 8, 15, "There is one character.", perBeatCast: false, continuity: true);

        Assert.Contains("End every beat with its environment", user);
    }

    [Fact]
    public void Per_beat_casting_and_the_environment_suffix_coexist()
    {
        // The ensemble tabs open a beat with [S1, S2] and now close it with the environment; the two
        // brackets must not be read as each other.
        var (_, beats) = StoryBeatSheet.Parse(
            "SETTING: a hangar\n" +
            "1. [S1, S3] She walks the length of the hangar. [INT | the hangar | night | worklights]\n");

        Assert.Single(beats);
        Assert.Equal("1, 3", beats[0].Cast);
        Assert.Equal("INT | the hangar | night | worklights", beats[0].Env);
        Assert.Equal("She walks the length of the hangar.", beats[0].Text);
    }
}
