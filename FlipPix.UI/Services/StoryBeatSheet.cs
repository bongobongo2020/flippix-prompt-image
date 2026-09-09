using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Turns one story into exactly N beats — the first half of every story-chain tab's writer.
    ///
    /// <para><b>Why this exists.</b> Asking a local model for a whole N-clip chain in one reply fails past
    /// roughly four clips: the reply comes back short, the cast tags start swapping between characters,
    /// then arrive malformed, and the prose finally collapses into unpunctuated word-salad. Observed
    /// 2026-09-02 on a 12-clip H3 Eros run that returned 7 clips, the last of which was a tag per noun.
    /// No downstream repair recovers that, because the failure is output length: 12 clips is a 6k-token
    /// structured reply and the model loses the tag → character binding long before the end of it.</para>
    ///
    /// <para><b>The shape that works.</b> Two deterministic steps. This class is step one: one cheap call
    /// that produces a one-line setting and N plain numbered beats — no H3 format, no camera work, a few
    /// hundred tokens out. Step two is the caller's: one small call per clip, each handed its own beat.
    /// The clip count stops being the model's to decide, because the caller's loop owns it.</para>
    ///
    /// <para>Nothing here can fail a run. A reply with the wrong number of beats is asked for once more and
    /// then fitted to the plan by <see cref="Fit"/>; a reply with no beats at all falls back to the story's
    /// own units (<see cref="FromStory"/>).</para>
    ///
    /// <para><b>The retry closes the scratchpad</b> (observed 2026-09-09, Qwen3.6-35B on the user's own
    /// server). <see cref="LlmSampling.StoryChainBrief"/> is the app's one profile that lets a model think
    /// before it answers, on the reasoning that dividing a story is a planning task. On a model that routes
    /// its thinking into <c>reasoning_content</c>, that scratchpad is spent from the same token budget as
    /// the answer — and a 12-clip beat sheet's budget is ~2.5k tokens, which this model spent entirely on
    /// thinking. Both attempts returned an empty <c>content</c>, so every story of a ten-film overnight
    /// batch fell through to <see cref="FromStory"/>: no beats, and no SETTING line for the clips to be
    /// held to. The films that came out of it cut between daylight and midnight clip by clip, because
    /// nothing downstream had ever been told what hour it was. With <c>enable_thinking=false</c> the same
    /// model answered the same prompt in 6 seconds and 349 tokens — so the first attempt is left exactly as
    /// it was, and the retry, which used to be the identical request sent twice, is now the one thing that
    /// can actually make a difference.</para>
    /// </summary>
    public static class StoryBeatSheet
    {
        /// <summary>
        /// One beat of the story — the whole content of one clip.
        ///
        /// <para><see cref="Part"/> and <see cref="PartCount"/> exist because the clip plan rarely divides
        /// the story evenly: a beat that has to fill two clips is handed to both of them, each told which
        /// half of it to show.</para>
        /// </summary>
        /// <param name="Text">What physically happens in this beat.</param>
        /// <param name="Part">Which part of its beat this clip shows, 1-based.</param>
        /// <param name="PartCount">How many consecutive clips share this beat. 1 for the common case.</param>
        /// <param name="Cast">The cast tags the beat named, verbatim and comma-separated ("1, 3"), or empty
        /// when the caller did not ask for per-beat casting. Only the ensemble tabs use it: a clip there is
        /// sent only the reference sheets of the subjects it names, so which subjects are in which beat is
        /// a casting decision that has to be made before the clips are written.</param>
        /// <param name="Env">The environment the beat ended with — <c>EXT | the alley | night | heavy
        /// rain</c> — or empty when the caller did not ask for a continuity plan, or the model did not
        /// write one for this beat. Read by <see cref="StoryContinuity"/>, which fills the gaps.</param>
        public readonly record struct StoryBeat(string Text, int Part, int PartCount, string Cast = "",
                                                string Env = "")
        {
            /// <summary>The cast tags as numbers, or empty when the beat named none.</summary>
            public IReadOnlyList<int> CastIndices => string.IsNullOrWhiteSpace(Cast)
                ? Array.Empty<int>()
                : Cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                      .Select(t => int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
                      .Where(n => n > 0)
                      .Distinct()
                      .ToList();
        }

        /// <summary>A numbered beat line — <c>1.</c>, <c>1)</c>, <c>- 1.</c>, <c>Beat 1:</c>, <c>Shot 1:</c>.
        /// Anchored to the start of a line, so a numeral inside a beat's own prose is never mistaken for the
        /// next beat.</summary>
        private static readonly Regex BeatLineRegex = new(
            @"^[ \t]*(?:[-*•]\s*)?(?:Beat|Clip|Shot)?[ \t]*(\d{1,3})[ \t]*[.):\-–—][ \t]*(\S.*)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The setting line, however the model labels it.</summary>
        private static readonly Regex SettingLineRegex = new(
            @"^[ \t]*(?:[-*•]\s*)?(?:#{1,6}\s*)?SETTING[ \t]*[:\-–—][ \t]*(\S.*)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The optional cast prefix a beat opens with when per-beat casting was asked for —
        /// <c>[S1, S3]</c>, <c>[1,3]</c>, <c>(S2)</c>. Stripped off the beat text and returned separately.</summary>
        private static readonly Regex BeatCastPrefixRegex = new(
            @"^[\[(][ \t]*(?:S[ \t]*\d{1,2}[ \t]*[,;/&+]?[ \t]*)+[\])][ \t]*:?[ \t]*|" +
            @"^[\[(][ \t]*(?:\d{1,2}[ \t]*[,;/&+][ \t]*)*\d{1,2}[ \t]*[\])][ \t]*:?[ \t]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CastNumberRegex = new(@"\d{1,2}", RegexOptions.Compiled);

        /// <summary>The shortest a line can be and still be a beat rather than a heading or a stray numeral.</summary>
        private const int MinBeatLength = 12;

        #region The call

        /// <summary>
        /// Runs the beat sheet: up to two attempts at the model, then whatever came back fitted to
        /// <paramref name="clipCount"/>. Never throws for a bad reply — only for cancellation or a transport
        /// failure the caller should see.
        /// </summary>
        /// <param name="lm">The chat service.</param>
        /// <param name="model">The resolved model name.</param>
        /// <param name="story">The story to divide. Also the fallback's source.</param>
        /// <param name="clipCount">How many beats to come back with. Authoritative.</param>
        /// <param name="seconds">One clip's length, quoted to the model so it paces the beats.</param>
        /// <param name="castBrief">How the cast is named to the model — one or two sentences telling it
        /// what to call the story's people ("CHARACTER 1 / CHARACTER 2", "&lt;Subject 1&gt;…").</param>
        /// <param name="perBeatCast">When set, every beat is asked to open with the cast tags it contains,
        /// as <c>[S1, S3]</c>. The ensemble tabs need it; the two-hander tabs do not.</param>
        /// <param name="imagePath">A scene/location image read once here, so its setting lands in the
        /// SETTING line every clip is then handed as text — far cheaper than attaching it to all N clip
        /// calls, and it keeps the setting identical across the chain by construction.</param>
        /// <param name="log">Where the progress and warning lines go.</param>
        /// <param name="extraRules">Optional rules appended to the user message, for a caller whose clips
        /// cannot render just any division of a story. Null for every caller that has no such constraint,
        /// so the shared beat sheet is what it always was unless someone asks otherwise.</param>
        /// <param name="continuity">When set, every beat is asked to end with the environment it happens in
        /// — <c>[EXT | the alley | night | heavy rain]</c> — so the chain has a plan for place, hour and
        /// light rather than a per-clip guess at them. Off by default: a caller that does not read
        /// <see cref="StoryBeat.Env"/> would only be paying for a longer reply.</param>
        public static async Task<(string Setting, List<StoryBeat> Beats)> WriteAsync(
            LMStudioService lm,
            string model,
            string story,
            int clipCount,
            double seconds,
            string castBrief,
            bool perBeatCast,
            string? imagePath,
            Action<string> log,
            CancellationToken token,
            string? extraRules = null,
            bool continuity = false)
        {
            var system = BuildSystem(perBeatCast, continuity);
            var user = BuildUser(story, clipCount, seconds, castBrief, perBeatCast, extraRules, continuity);
            var maxTokens = Math.Min(8000, 800 + 140 * clipCount);

            var setting = string.Empty;
            var beats = new List<(string Text, string Cast, string Env)>();

            for (var attempt = 1; attempt <= 2; attempt++)
            {
                token.ThrowIfCancellationRequested();

                // The second attempt closes the scratchpad — see "The retry closes the scratchpad" in the
                // class remarks. Asking the same way twice fails the same way twice.
                var sampling = attempt == 1
                    ? LlmSampling.StoryChainBrief
                    : LlmSampling.StoryChainBrief with { AllowThinking = false };

                var raw = !string.IsNullOrWhiteSpace(imagePath) && attempt == 1
                    ? await lm.AnalyzeImageWithSystemPromptAsync(
                        model, imagePath!, user, system,
                        maxTokens: maxTokens, cancellationToken: token,
                        sampling: sampling)
                    : await lm.SendTextChatAsync(
                        model, system, user,
                        maxTokens: maxTokens, cancellationToken: token,
                        sampling: sampling);

                var parsed = Parse(raw);
                if (parsed.Beats.Count > beats.Count)
                {
                    beats = parsed.Beats;
                    if (parsed.Setting.Length > 0) setting = parsed.Setting;
                }

                if (beats.Count == clipCount) break;

                if (attempt == 1)
                    log(beats.Count == 0
                        ? "Beat sheet: the model returned nothing — on a reasoning model that means the " +
                          "whole token budget went into its scratchpad. Asking again with the scratchpad " +
                          "closed."
                        : $"Beat sheet: {beats.Count} beat(s) came back for {clipCount} clips — asking " +
                          "once more...");
            }

            if (beats.Count == 0)
            {
                log("WARNING: the beat sheet came back empty, twice — dividing the story by its own lines " +
                    "instead. The clips will follow the prose literally, and with no SETTING and no " +
                    "continuity plan behind them each clip decides its own place, hour and light: that is " +
                    "what a film that cuts from daylight to midnight and back is made of. Try a shorter " +
                    "story, or a model that answers rather than reasons.");
                return (setting, FromStory(story, clipCount));
            }

            var fitted = Fit(beats, clipCount);

            if (beats.Count == clipCount)
                log($"Beat sheet: {clipCount} beats, one per clip.");
            else if (beats.Count < clipCount)
                log($"Beat sheet: {beats.Count} beats for {clipCount} clips — the longer beats are split " +
                    "across consecutive clips, each told which part of its beat it shows.");
            else
                log($"Beat sheet: {beats.Count} beats for {clipCount} clips — adjacent beats merged so the " +
                    "story still ends in the last clip.");

            if (continuity)
            {
                var written = beats.Count(b => b.Env.Length > 0);
                if (written == 0)
                    log("Beat sheet: no beat carried an environment — the whole chain will be held to the " +
                        "SETTING line instead, which is the next best thing to a plan.");
                else if (written < beats.Count)
                    log($"Beat sheet: {written} of {beats.Count} beats carried an environment; the rest " +
                        "inherit the beat before them.");
            }

            if (setting.Length > 0)
                log($"Setting: {setting}");
            else
                log("Note: the beat sheet gave no SETTING line — each clip will read the location off its own " +
                    "beat, which is where between-clip location drift comes from.");

            return (setting, fitted);
        }

        #endregion

        #region The request

        /// <summary>The beat sheet's system prompt. Deliberately not about H3 at all: the job is a division
        /// of a story, and a model told to think about video formats starts writing one.</summary>
        public static string BuildSystem(bool perBeatCast, bool continuity = false)
        {
            // The environment suffix, shown in the shape and then explained. It rides on the beat line
            // rather than arriving as a second numbered list because a second list is a second thing to
            // keep aligned — and an environment that lines up with the wrong beat is worse than none.
            var envExample = continuity
                ? "  [EXT | the alley behind the club | night | heavy rain, neon signs]"
                : string.Empty;

            var castLine = perBeatCast
                ? "SETTING: <one sentence — place, time of day, weather, light, mood>\n" +
                  "1. [S1, S2] <what happens in beat 1>" + envExample + "\n" +
                  "2. [S3] <what happens in beat 2>" + envExample + "\n" +
                  "(one numbered line per beat, through to the last)\n\n" +
                  "Each beat opens with the tags of the characters who are IN it, in square brackets. Only " +
                  "those characters appear in that beat; a character who is not in the brackets is not on " +
                  "screen. Keep it to two or three characters per beat, give every character at least one " +
                  "beat, and prefer runs of consecutive beats for a character over scattered single ones.\n\n"
                : "SETTING: <one sentence — place, time of day, weather, light, mood>\n" +
                  "1. <what happens in beat 1>" + envExample + "\n" +
                  "2. <what happens in beat 2>" + envExample + "\n" +
                  "(one numbered line per beat, through to the last)\n\n";

            return
                "You are a script supervisor. You divide one story into a fixed number of consecutive beats " +
                "for a video that is shot clip by clip. You do not write video prompts, camera directions or " +
                "prose — you write one plain line per beat saying what physically happens in it and who does " +
                "it.\n\n" +
                "Reply in EXACTLY this shape, and nothing else:\n" +
                castLine +
                "Rules:\n" +
                "- Emit EXACTLY the number of beats you are asked for. Not one more, not one fewer.\n" +
                "- The beats are consecutive and cover the whole story in its own order: beat 1 is the " +
                "story's first action and the last beat is its final one.\n" +
                "- Divide only the action the story narrates. Invent no event, location, character or " +
                "outcome it does not contain. When there are more beats than the story has moments, split " +
                "its moments finer — one blow becomes the wind-up, the contact and the recoil — never add " +
                "new ones.\n" +
                "- Every beat names who acts and who it lands on, by the tags you were given.\n" +
                "- One or two sentences per beat. No camera work, no lighting, no style adjectives." +
                (continuity ? ContinuityRules : string.Empty);
        }

        /// <summary>
        /// What the model is told about the environment suffix, appended to the system rules.
        ///
        /// <para>Every line of it is a way a chain went wrong before it existed. The closed list of hours is
        /// there because "late evening", "after dark" and "night-time" cannot be compared with each other;
        /// the forward-only rule because a story that had been dark since beat 4 came back to the afternoon
        /// in beat 9; the same-words rule because one alley described two ways is two alleys once the clips
        /// are rendered a job apart; and the "only when the story moves them" rule because a model will
        /// otherwise vary the environment for the reason a writer varies a word — to avoid repeating
        /// itself.</para>
        /// </summary>
        private const string ContinuityRules =
            "\n- End EVERY beat with the environment it happens in, in square brackets, in exactly this " +
            "shape: [EXT | <the place> | <time of day> | <weather and light>]. INT for indoors, EXT for " +
            "outdoors.\n" +
            "- The time of day is ONE of: dawn, morning, midday, afternoon, evening, dusk, night, late " +
            "night. Use no other words for it.\n" +
            "- The environment is the SAME in consecutive beats unless the story physically moves the " +
            "characters somewhere else, or says that time passes. Copy the previous beat's bracket exactly " +
            "when nothing has moved: the same place written two different ways is read as two places.\n" +
            "- Time only ever moves FORWARD through that list, and only as far as the story takes it. A " +
            "story told in one evening is 'evening' in every beat.\n" +
            "- Invent no location the story does not contain. Where it never says whether a scene is " +
            "indoors or out, or what hour it is, choose once in beat 1 and keep that choice.";

        /// <summary>The beat sheet's user message.</summary>
        public static string BuildUser(
            string story, int clipCount, double seconds, string castBrief, bool perBeatCast,
            string? extraRules = null, bool continuity = false)
        {
            var c = CultureInfo.InvariantCulture;
            var s = seconds.ToString("0.##", c);

            var storyBlock = string.IsNullOrWhiteSpace(story)
                ? "There is no written story. Invent one that suits the setting and the cast above, and " +
                  "carry it from beginning to end across the beats."
                : $"The story:\n{story.Trim()}";

            return
                $"{castBrief.Trim()}\n\n" +
                $"Divide this story into EXACTLY {clipCount} beats — one per {s}-second clip, " +
                $"{clipCount} × {s}s ≈ {(clipCount * seconds).ToString("0.##", c)}s of video in total.\n" +
                (perBeatCast
                    ? "Open every beat with the tags of the characters in it, in square brackets.\n"
                    : string.Empty) +
                (continuity
                    ? "End every beat with its environment in square brackets: " +
                      "[EXT | the place | time of day | weather and light].\n"
                    : string.Empty) +
                (string.IsNullOrWhiteSpace(extraRules) ? string.Empty : "\n" + extraRules.Trim() + "\n") +
                "\n" + storyBlock;
        }

        #endregion

        #region Parsing and fitting

        /// <summary>Reads a beat-sheet reply into its setting line and its beats, in the order written.
        /// Anything that is neither is discarded, so a model that opens with "Here is the beat sheet:" costs
        /// nothing.</summary>
        public static (string Setting, List<(string Text, string Cast, string Env)> Beats) Parse(string? reply)
        {
            var beats = new List<(string Text, string Cast, string Env)>();
            if (string.IsNullOrWhiteSpace(reply)) return (string.Empty, beats);

            var text = reply.Replace("\r\n", "\n").Replace('\r', '\n');

            var settingMatch = SettingLineRegex.Match(text);
            var setting = settingMatch.Success ? settingMatch.Groups[1].Value.Trim() : string.Empty;

            foreach (Match m in BeatLineRegex.Matches(text))
            {
                var body = m.Groups[2].Value.Trim();

                var cast = string.Empty;
                var prefix = BeatCastPrefixRegex.Match(body);
                if (prefix.Success)
                {
                    cast = string.Join(", ", CastNumberRegex.Matches(prefix.Value).Select(n => n.Value));
                    body = body[prefix.Length..].Trim();
                }

                // The environment suffix comes off here, not downstream: it is the beat sheet's own
                // notation, and every pass after this one treats a beat as the sentence it renders.
                var (beatText, env) = StoryContinuity.SplitBeat(body);

                if (beatText.Length >= MinBeatLength) beats.Add((beatText, cast, env));
            }

            return (setting, beats);
        }

        /// <summary>
        /// The beat sheet fitted to the clip plan, which is authoritative.
        ///
        /// <para>Fewer beats than clips: a beat is handed to as many consecutive clips as its share of the
        /// runtime needs, each told which part of it to show, so the story still stretches across the whole
        /// chain. More beats than clips: adjacent beats are merged, so the story's last beat still lands in
        /// the last clip. Equal counts pass through one to one.</para>
        /// </summary>
        public static List<StoryBeat> Fit(List<(string Text, string Cast, string Env)> beats, int clipCount)
        {
            var fitted = new List<StoryBeat>(Math.Max(0, clipCount));
            if (beats.Count == 0 || clipCount <= 0) return fitted;

            if (beats.Count >= clipCount)
            {
                // Merge: clip i takes every beat in [i·M/N, (i+1)·M/N).
                for (var i = 0; i < clipCount; i++)
                {
                    var from = (int)((long)i * beats.Count / clipCount);
                    var to = (int)((long)(i + 1) * beats.Count / clipCount);
                    if (to <= from) to = from + 1;
                    to = Math.Min(to, beats.Count);

                    var span = beats.GetRange(from, to - from);
                    // Merged beats keep the FIRST environment they carried: a clip that opens in one place
                    // and is told it is in another halfway through is exactly the cut being prevented.
                    var env = span.Select(b => b.Env).FirstOrDefault(e => e.Length > 0) ?? string.Empty;
                    // The merged clip is cast from the union of what its beats named, in first-seen order.
                    var cast = string.Join(", ", span
                        .SelectMany(b => b.Cast.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        .Distinct());
                    fitted.Add(new StoryBeat(string.Join(" Then ", span.Select(b => b.Text)), 1, 1, cast, env));
                }
                return fitted;
            }

            // Split: how many clips each beat is owed, so they are spread as evenly as the counts allow.
            var owed = new int[beats.Count];
            for (var i = 0; i < clipCount; i++) owed[(int)((long)i * beats.Count / clipCount)]++;

            for (var b = 0; b < beats.Count; b++)
                for (var part = 1; part <= owed[b]; part++)
                    fitted.Add(new StoryBeat(beats[b].Text, part, owed[b], beats[b].Cast, beats[b].Env));

            return fitted;
        }

        /// <summary>
        /// The beat sheet the caller writes for itself when the model gives it nothing: the story's own
        /// units — its <c>Shot n:</c> / numbered lines where it has them, its sentences where it does not —
        /// fitted to the clip plan. Literal rather than good, and it keeps a run alive that would otherwise
        /// end with an empty prompt box.
        /// </summary>
        public static List<StoryBeat> FromStory(string? story, int clipCount)
        {
            var text = (story ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

            var units = BeatLineRegex.Matches(text)
                .Select(m => m.Groups[2].Value.Trim())
                .Where(s => s.Length >= MinBeatLength)
                .ToList();

            if (units.Count == 0)
                units = Regex.Split(text, @"(?<=[.!?])\s+")
                    .Select(s => s.Trim())
                    .Where(s => s.Length >= MinBeatLength)
                    .ToList();

            if (units.Count == 0)
            {
                if (text.Length == 0) return new List<StoryBeat>();
                units = new List<string> { text };
            }

            return Fit(units.Select(u => (u, string.Empty, string.Empty)).ToList(), clipCount);
        }

        /// <summary>The line a clip request carries when its beat is one slice of a longer one.</summary>
        public static string DescribePart(StoryBeat beat)
        {
            if (beat.PartCount <= 1) return string.Empty;

            var which = beat.Part == 1
                ? "its opening — the approach and the wind-up."
                : beat.Part == beat.PartCount
                    ? "its end — the contact, the recoil, and what it leaves behind."
                    : "its middle, picking up exactly where the previous clip left it.";

            return $" This beat spans {beat.PartCount} clips and this clip is part {beat.Part} of " +
                   $"{beat.PartCount}, so show only {which}";
        }

        #endregion
    }
}
