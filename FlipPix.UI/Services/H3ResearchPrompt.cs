using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The 🌹🎯 H3 Eros tab's <b>researched</b> prompt build — everything the "📚 Researched prompts"
    /// toggle switches on, in one place so the other build stays exactly as it was.
    ///
    /// <para><b>Where this comes from.</b> Every rule here is read off the guides in
    /// <c>prompts/documents/</c>: <c>MINIMAX_H3_PROMPTING_GUIDE.md</c> (v2.10.0, Issues 1–3 and Rules
    /// 4–45), <c>C2V_CONTINUOUS_CHAIN_GUIDE.md</c> (§4 framing and locomotion) and
    /// <c>MINIMAX_REFMODS_VS_REFERENCE_IMAGES.md</c> (§7, what reference conditioning does and does not
    /// tolerate in the prose). The guide's own rule numbers are cited on each block, so a future edit can
    /// be checked against the source rather than re-argued from taste.</para>
    ///
    /// <para><b>What it changes, in one paragraph.</b> The shipped build cuts a clip roughly every 1.25
    /// seconds and says almost nothing about shot size, silence, screen sides or the audio fields. The
    /// guide's worked structures cut a 15-second clip <i>three</i> times (Rule 9 structures A–C, Rule 24,
    /// Rule 13), keep every shot at medium or closer because H3 destroys distant faces (Rule 36, C2V §4.1),
    /// make non-speaking mouths explicitly silent because H3 otherwise invents mumbling (Issue 1, Rule 41),
    /// isolate a speaker's face for their line because H3 otherwise gives it to the opponent (Issue 2,
    /// Rule 19), and keep voices out of <c>overall_soundscape:</c> because naming them there is a known
    /// double-voice source (Rule 8). Those are the levers.</para>
    /// </summary>
    internal static class H3ResearchPrompt
    {
        /// <summary>The per-clip system prompt this build writes against. The shipped one is
        /// <c>h3pw_clip.md</c>; both take the same three-field reply, so every downstream sanitizer,
        /// validator and stamp is shared and untouched.</summary>
        public const string ClipSystemPromptFile = "h3pw_clip_research.md";

        // ── Shot budget ────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// How many shots one clip of <paramref name="seconds"/> carries — the guide's pacing, not the
        /// shipped tab's.
        ///
        /// <para>The shipped build asks for <c>len × 0.8</c> shots (12 for a 15s clip, a cut every 1.25s).
        /// The guide's worked 15-second structures are three shots, cut near <c>00:05.500</c> and
        /// <c>00:10.500</c> (Rule 9 structures A–C; Rule 13; Rule 24's "3-shot storyboards across the full
        /// clip"), and Rule 44 says a beat crammed into too many rushed pieces comes back as rapid-fire
        /// filler rather than a described action. One shot per ~4.5 seconds lands on the guide's numbers,
        /// floored at 3 so even a short clip is still cut, capped at 5 so a two-minute chain's longest
        /// clip does not drift back into the synonym walk.</para>
        /// </summary>
        public static int ShotCount(double seconds) =>
            Math.Clamp((int)Math.Round(seconds / 4.5, MidpointRounding.AwayFromZero), 3, 5);

        /// <summary>
        /// The cut times for <paramref name="shots"/> shots across <paramref name="seconds"/>, as the
        /// guide writes them — <c>MM:SS.mmm</c>, evenly spread, <c>[Shot 1]</c> at zero and unstamped.
        ///
        /// <para>Handing the writer the actual times matters: Rule 27's timestamp note is that a model left
        /// to choose reuses the times it saw in an example (<c>00:03</c> / <c>00:06</c>) whatever the clip's
        /// real length is, which puts the last beat a third of the way in and leaves the tail as dead
        /// air.</para>
        /// </summary>
        public static IReadOnlyList<string> CutTimes(double seconds, int shots)
        {
            var times = new List<string>();
            for (var i = 1; i < Math.Max(1, shots); i++)
                times.Add(Timecode(seconds * i / shots));
            return times;
        }

        /// <summary>Seconds as the guide's <c>MM:SS.mmm</c>.</summary>
        public static string Timecode(double seconds)
        {
            if (seconds < 0) seconds = 0;
            var whole = (int)Math.Floor(seconds);
            var millis = (int)Math.Round((seconds - whole) * 1000);
            if (millis >= 1000) { whole++; millis -= 1000; }
            return $"{whole / 60:00}:{whole % 60:00}.{millis:000}";
        }

        // ── The beat sheet ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Appended to the beat sheet's user message. The beat sheet is where a clip's workload is decided,
        /// and two of the guide's failures are decided there rather than in the clip writer: Rule 35 /
        /// C2V §4.4 (a beat that carries a multi-stage locomotion change tears the motion latent — one
        /// physical beat per clip) and Rule 44 (a beat that carries an alert, a cascade, a report and a
        /// deduction at once comes back rushed).
        /// </summary>
        public const string BeatSheetRules =
            "Two extra rules for these beats:\n" +
            "- ONE physical beat per clip. A beat may carry one change of posture or position — a rise, a " +
            "turn, a step in, a fall — never a chain of them. Never write a beat that runs someone from " +
            "lying down through sitting, standing and walking into another place; split that across " +
            "consecutive beats, one stage each.\n" +
            "- One thing happens per beat. If a beat has an alert, an escalation and a conclusion in it, " +
            "it is three beats, not one.";

        // ── Per-clip request blocks ────────────────────────────────────────────────────────────────

        /// <summary>
        /// The opening shot class for clip <paramref name="clipIndex"/> (0-based), rotated so a twelve-clip
        /// chain does not open twelve clips the same way.
        ///
        /// <para>Rule 12.3 — "do not use the exact same shot breakdown in every scene" — and Rule 28's
        /// closer rotation, which is the same observation from the other end: repeating one framing class
        /// down a chain compounds it into a "home" look the model keeps falling back to. Every entry here
        /// obeys the Rule 36 framing floor: nothing wider than a medium, nothing distant.</para>
        /// </summary>
        public static string OpeningShotClass(int clipIndex, bool hasSecondCharacter)
        {
            var solo = new[]
            {
                "a single medium close-up of <Picture 1> alone in frame",
                "a tight close-up on <Picture 1>'s hands and what they are doing",
                "a low-angle medium shot of <Picture 1>, camera near the ground",
                "a medium tracking shot travelling alongside <Picture 1> at their own speed",
                "a close-up on the point of contact, the bodies filling the frame",
                "a high-angle medium shot looking down on <Picture 1>",
            };
            var duo = new[]
            {
                "a medium two-shot holding both characters apart in frame",
                "a single medium close-up of <Picture 1> alone in frame",
                "an over-the-shoulder medium close-up past <Picture 2> onto <Picture 1>",
                "a low-angle medium shot from below the action",
                "a single medium close-up of <Picture 2> alone in frame",
                "a close-up on the point of contact, the bodies filling the frame",
                "a medium tracking shot travelling alongside the action",
                "an over-the-shoulder medium close-up past <Picture 1> onto <Picture 2>",
            };
            var list = hasSecondCharacter ? duo : solo;
            return list[Math.Abs(clipIndex) % list.Length];
        }

        /// <summary>
        /// This clip's screen sides, kept for the whole chain. Rule 26 — a two-hander whose fighters are
        /// never given a side swaps them between clips and between cuts inside one clip, and a stated
        /// left/right is the only anchor the model has.
        ///
        /// <para>The sides alternate every clip rather than staying fixed for the chain, because a joined
        /// chain that never crosses the line is a chain shot from one camera position for two minutes.
        /// They are stated, so the model is never guessing.</para>
        /// </summary>
        public static string ScreenSides(int clipIndex, bool hasSecondCharacter)
        {
            if (!hasSecondCharacter)
                return "SCREEN POSITION — <Picture 1> holds screen-center for this clip. Say so in any " +
                       "shot that is not a close-up.";

            var flipped = clipIndex % 2 == 1;
            var first = flipped ? "screen-right" : "screen-left";
            var second = flipped ? "screen-left" : "screen-right";
            return "SCREEN POSITIONS — fixed for this whole clip, and you must write them into every shot " +
                   $"that holds both characters: <Picture 1> is on {first}, <Picture 2> is on {second}. " +
                   "They keep those sides unless the beat has one of them physically cross past the other, " +
                   "and if it does, say the crossing out loud.";
        }

        /// <summary>
        /// The shot plan handed to the writer: how many shots, and the exact timecode each cut lands on.
        /// Rules 7, 9, 13 and 27's timestamp note.
        /// </summary>
        public static string ShotPlan(double seconds, int shots)
        {
            var times = CutTimes(seconds, shots);
            var sb = new StringBuilder();
            sb.Append("SHOT PLAN — this clip carries EXACTLY ").Append(shots)
              .Append(" shots, and no more. [Shot 1] opens the clip and carries no timestamp. ");
            if (times.Count > 0)
                sb.Append("The remaining cuts land on these times, in this order, written exactly like " +
                          "this: ").Append(string.Join(", ", times.Select((t, i) => $"[Shot {i + 2}] At {t}")))
                  .Append(". ");
            sb.Append("The last shot runs from its cut to ").Append(Timecode(seconds))
              .Append(" and is still in motion when it gets there — a body moving, a camera travelling, a " +
                      "blow still landing. Never a held pose, never a stare, never a smile into the lens.");
            return sb.ToString();
        }

        /// <summary>
        /// The lighting and time-of-day lock, identical in every clip of the chain. Rule 22 and §7.4 of the
        /// guide's continuity protocol: lighting jumping between clips is the most jarring break a joined
        /// chain has, and the fix the guide prescribes is a single registered string re-used verbatim —
        /// which is exactly what the tab already does for the wardrobe.
        ///
        /// <para>Derived from the beat sheet's own SETTING line, so it costs no extra model call. With no
        /// SETTING line the writer is told to fix one in clip 1's own words and hold it, which is the best
        /// that can be done without one.</para>
        /// </summary>
        public static string LightingLock(string? setting) =>
            string.IsNullOrWhiteSpace(setting)
                ? "LIGHTING LOCK — the beat sheet gave no setting line, so read the light and the time of " +
                  "day off the beat below, state them explicitly inside [Shot 1] (for example 'harsh " +
                  "midday sun' or 'blue pre-dawn light'), and never let them drift inside this clip."
                : "LIGHTING LOCK — the light and the time of day are the same in every clip of this chain " +
                  "and are not yours to change. Restate them inside [Shot 1] in these words, and keep them " +
                  "through every shot; never advance the time of day, never change the light source:\n" +
                  setting.Trim();

        /// <summary>
        /// The framing floor, said in the request as well as the system prompt because it is the rule the
        /// writer breaks most and the one that costs the most: Rule 36 and C2V §4.1 — H3 renders warped,
        /// low-detail faces the moment a person is small in frame, which is precisely when the reference
        /// photographs stop being able to hold the identity.
        /// </summary>
        public const string FramingRule =
            "FRAMING — every shot in this clip is a medium (waist-up), a medium close-up (chest-up) or a " +
            "close-up. No extreme wide shot, no distant full-body framing, no establishing vista, no " +
            "aerial. If someone crosses the space, track alongside them at medium distance rather than " +
            "pulling back. Faces stay frontal or three-quarter to camera, never strict profile.";

        /// <summary>
        /// Issue 1 and Rule 41 for a clip with no spoken words in its beat — which on this tab is most of
        /// them. H3 fills silence at the top of a clip with invented mumbling unless the absence of speech
        /// is stated as a positive instruction.
        /// </summary>
        public static string SpeechRule(bool hasSecondCharacter)
        {
            var who = hasSecondCharacter
                ? "Both characters remain completely silent with their mouths closed, speaking no dialogue"
                : "<Picture 1> remains completely silent with a closed mouth, speaking no dialogue";
            return
                "SPEECH — write dialogue ONLY if the beat below contains spoken words.\n" +
                $"- If it does not: put this sentence in [Shot 1] and write no <d> tag anywhere — \"{who}, " +
                "and no voice, narration or voiceover of any kind is heard.\" Leaving it out is what makes " +
                "H3 invent mumbling over a silent clip.\n" +
                "- If it does: give the line its own shot, framed as a single medium close-up of the " +
                "speaker ALONE in frame with the other character declared out of frame, name the speaker's " +
                "tag immediately before the line, and wrap it as <d>[English in <Picture N>'s voice] …</d>. " +
                "A line spoken in a two-shot comes out of the wrong face in the wrong voice. Budget about " +
                "two words per second." +
                (hasSecondCharacter
                    ? " Any character visible while another speaks is written as \"remains completely " +
                      "silent with a closed mouth, speaking no dialogue\"."
                    : string.Empty);
        }

        /// <summary>
        /// Rule 8's split, restated per clip. The shipped build asks for "the diegetic sound" and gets
        /// clips whose soundscape names the voice that the <c>&lt;d&gt;</c> tag already generates — the
        /// guide's documented double-voice and mumbling source — and clips whose soundscape carries the
        /// score.
        /// </summary>
        public const string AudioFieldRule =
            "AUDIO FIELDS — overall_soundscape: carries room tone, impacts, footfalls, fabric, breath and " +
            "non-verbal sounds only. Never name speech, dialogue, a voice or a character speaking in it, " +
            "and never put music in it. non_diegetic_music: carries the score alone — instrumentation, " +
            "tempo, dynamics — or N/A.";

        /// <summary>
        /// Rule 18: the guide's own diagnosis of timid action prose, and the shape that fixes it. This is
        /// the rule that turns "he gets hit" into a described impact, and it is the one that most directly
        /// answers "better videos from the stories" on an action tab.
        /// </summary>
        public const string ActionRule =
            "ACTION — write mechanics, not verbs. Never \"he dodges\", \"she strikes\", \"he falls\": write " +
            "the wind-up, the line the blow travels, exactly where it connects, what gives way, and what " +
            "the struck body does about it — all inside the same shot, before the cut. Every blow that " +
            "lands carries its consequence in the same shot: the recoil, the breath, what is dropped, what " +
            "the feet do. Give weight, speed, dust and the way clothing and hair move with the hit. That " +
            "detail is where the seconds come from, not extra cuts.";

        /// <summary>
        /// Rule 17 and Rule 43 adapted to a tag-based cast. The guide's anchor is a restated physical
        /// descriptor per shot; here the tag carries the face (and describing it would fight the reference,
        /// per the RefMods guide §7.4), so the per-shot anchor is the tag plus one garment from the locked
        /// wardrobe.
        /// </summary>
        public const string AnchorRule =
            "ANCHORING — every time a character appears in a shot, name their tag AND one distinguishing " +
            "garment from the wardrobe quote, in the quote's own words. Never \"the man\", never \"her " +
            "opponent\". Anyone not in a shot is declared out of it: \"<Picture 2> is not in frame.\"";

        /// <summary>
        /// Rule 42. A prominent object mentioned loosely, shot after shot, spawns a second one; declared
        /// singular once and then left alone, it stays one object.
        /// </summary>
        public const string PropRule =
            "PROPS — if the beat has a prominent held or worn object, name it once as singular (\"the same " +
            "single [object] — exactly one, never two\") and then leave it as set dressing rather than " +
            "re-describing it in every shot.";

        /// <summary>
        /// RefMods guide §7.4 plus Rule 24's banned render vocabulary. Generic beauty adjectives switch the
        /// model onto its own beauty prior and paint over the reference face — on a tab whose whole point
        /// is that the cast comes from photographs, that is the most expensive single word class there is.
        /// </summary>
        public const string VocabularyRule =
            "BANNED VOCABULARY — no beauty adjectives about the cast (attractive, beautiful, soft oval " +
            "face, slender nose, perfect skin, toned, chiselled): they override the reference photographs " +
            "with the model's own generic face. And no render words: masterpiece, 8k, hyperrealistic, " +
            "unreal engine, CGI, render, photorealistic.";

        /// <summary>
        /// The whole researched rule block for one clip, in the order the guide's own failures are ranked:
        /// what the clip looks like (framing, shots), then who is where, then what happens, then what is
        /// heard.
        /// </summary>
        public static string RulesFor(
            int clipIndex, bool hasSecondCharacter, double seconds, int shots, string? setting)
        {
            var sb = new StringBuilder();
            sb.Append(LightingLock(setting)).Append("\n\n");
            sb.Append(FramingRule).Append("\n\n");
            sb.Append(ShotPlan(seconds, shots)).Append("\n\n");
            sb.Append("OPENING SHOT — [Shot 1] of this clip opens on ")
              .Append(OpeningShotClass(clipIndex, hasSecondCharacter))
              .Append(". The chain rotates this deliberately, so do not open on any other class.\n\n");
            sb.Append(ScreenSides(clipIndex, hasSecondCharacter)).Append("\n\n");
            sb.Append(AnchorRule).Append("\n\n");
            sb.Append(ActionRule).Append("\n\n");
            sb.Append(PropRule).Append("\n\n");
            sb.Append(SpeechRule(hasSecondCharacter)).Append("\n\n");
            sb.Append(AudioFieldRule).Append("\n\n");
            sb.Append(VocabularyRule);
            return sb.ToString();
        }

        /// <summary>One line for the log and the UI, saying what the toggle actually did to this run.</summary>
        public static string DescribeRun(int clipCount, double seconds) =>
            $"Researched prompts ON — h3pw_clip_research.md, {ShotCount(seconds)} shots per {seconds:0.#}s " +
            $"clip (cuts at {string.Join(", ", CutTimes(seconds, ShotCount(seconds)))}) instead of " +
            $"{Math.Clamp((int)Math.Round(seconds * 0.8, MidpointRounding.AwayFromZero), 6, 14)}, with the " +
            "MiniMax-H3 guide's framing floor, silence mandates, speaker isolation, screen positions, " +
            $"lighting lock and soundscape split applied to all {clipCount} clip(s).";

        /// <summary>The counterpart line when the toggle is off — so a run's log always says which build wrote it.</summary>
        public static string DescribeShippedRun(double seconds) =>
            $"Researched prompts OFF — h3pw_clip.md as shipped, " +
            $"{Math.Clamp((int)Math.Round(seconds * 0.8, MidpointRounding.AwayFromZero), 6, 14)} shots per " +
            $"{seconds.ToString("0.#", CultureInfo.InvariantCulture)}s clip.";
    }
}
