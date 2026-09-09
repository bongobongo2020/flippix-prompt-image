using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Cast Hybrid, the chain writer: a story becomes N clips through one beat-sheet call and then one
    /// call per clip, rather than a single reply asked to hold the whole chain.
    ///
    /// <para><b>Why.</b> The one-reply shape fails past roughly four clips on a local model — the reply
    /// comes back short, the tags start swapping between the characters, then arrive malformed, and the
    /// prose degenerates into unpunctuated word-salad. It was also the one call in this tab that ran on
    /// <c>LlmSampling.StoryChain</c>, whose presence/frequency penalties are documented in
    /// <see cref="LlmSampling.StoryChainFormatted"/> as the direct cause of that word-salad: llama.cpp
    /// levies them over a 64-token sliding window, so a model inside an enumeration can neither reuse a
    /// word nor emit the full stop that would close the sentence. One clip per call removes both — the
    /// reply is short enough not to drift, and it runs on the penalty-free profile.</para>
    ///
    /// <para>Unlike the ensemble's, this tab's cast is fixed: the same one or two reference sheets are
    /// attached to every clip, so there is no per-clip casting to decide and the beat sheet is not asked
    /// for one. What each clip must do instead is name <i>both</i> characters by their tags wherever the
    /// beat involves them — a character left as an untagged pronoun is rendered as a duplicate of the one
    /// that was tagged.</para>
    ///
    /// <para>A single-clip run is untouched: it was never the failing shape, and it still goes out as the
    /// one call this tab has always sent.</para>
    /// </summary>
    public partial class H3CastHybridViewModel
    {
        /// <summary>The chain layer laid over <c>h3-cast-hybrid.md</c> when writing one clip of many.
        /// Replaces <c>h3-cast-hybrid_story.md</c>, which described the whole-chain-in-one-reply shape.</summary>
        private const string ClipSystemPromptFile = "h3-cast-hybrid_clip.md";

        /// <summary>
        /// Writes the chain clip by clip and returns the raw bodies joined behind
        /// <c>=== CLIP n of N ===</c> headers — ready for <c>AssembleChain</c>, exactly as the single reply
        /// used to be.
        /// </summary>
        private async Task<string> WriteChainClipByClipAsync(
            string model, double len, int clipCount, CancellationToken token)
        {
            AnalyzePhase = $"Dividing the story into {clipCount} beats…";

            var (setting, beats) = await StoryBeatSheet.WriteAsync(
                _lmStudioService, model, StoryText, clipCount, len,
                castBrief: BuildBeatSheetCastBrief(),
                perBeatCast: false,
                imagePath: HasSceneImage ? SceneImagePath : null,
                log: AddLog,
                token: token,
                continuity: true);

            if (beats.Count == 0)
            {
                AddLog("WARNING: the story could not be divided into beats — nothing to write.");
                return string.Empty;
            }

            // One environment per clip — where it is, at what hour, in what light — with every gap the beat
            // sheet left filled from the clip before it. H3 renders each clip as an independent job and has
            // never seen the one before it, so anything left unsaid is re-decided per clip, which is a film
            // that cuts from daylight to midnight and back. See StoryContinuity.
            var environments = StoryContinuity.Plan(beats.Select(b => b.Env).ToList(), setting,
                                                       beats.Select(b => b.Text).ToList());
            foreach (var line in StoryContinuity.Describe(environments)) AddLog(line);


            var system = await ReadSystemPromptAsync(SystemPromptFile, token) + "\n\n" +
                         await ReadSystemPromptAsync(ClipSystemPromptFile, token);

            var bodies = await ClipChainWriter.WriteAsync(
                _lmStudioService, model, system, clipCount,
                buildRequest: (i, reason) =>
                    BuildHybridClipRequest(setting, beats, environments, i, clipCount, len, reason),
                normalize: NormalizeClipBody,
                validate: (i, body) => ValidateHybridClip(body)
                                       ?? StoryContinuity.Contradiction(body, EnvironmentFor(environments, i)),
                onProgress: (n, total) => AnalyzePhase = $"Writing clip {n} of {total}…",
                log: AddLog,
                describe: b => $"{b.Length:N0} chars, {CountShots(b)} shots",
                token: token);

            // The environment written into each description in code, ahead of [Shot 1]: the writer was told
            // the same thing in words, but only this is identical word for word in every clip that shares a
            // place — and two wordings of one room are two rooms to a model that renders them a job apart.
            return JoinClips(bodies
                .Select((b, i) => StoryContinuity.StampScene(b, EnvironmentFor(environments, i)))
                .ToList());
        }

        /// <summary>The environment clip <paramref name="index"/> was planned in, or an empty one when the
        /// chain has no plan. Clamped: a clip that failed both its attempts shifts the ones after it up by
        /// one, and that is a chain the writer has already warned is short.</summary>
        private static StoryContinuity.Environment EnvironmentFor(
            IReadOnlyList<StoryContinuity.Environment> plan, int index) =>
            plan.Count == 0 ? default : plan[Math.Clamp(index, 0, plan.Count - 1)];


        /// <summary>How the cast is named to the beat sheet — by the same <c>&lt;Subject n&gt;</c> numbers the
        /// clips use, so the mapping the sheet settles survives into them.</summary>
        private string BuildBeatSheetCastBrief() => HasCharacter2
            ? $"There are two characters: S1 (a {_character1.Noun}) and S2 (a {_character2.Noun}). Call them " +
              "S1 and S2 and nothing else — never by the names the story gives them. Cast the story's people " +
              "onto them in the order the story introduces them, and keep that mapping identical in every " +
              "beat: whoever acts as S1 in beat 3 is S1 in beat 9 too."
            : $"There is one character: S1 (a {_character1.Noun}). Call them S1 and nothing else — never by " +
              "the name the story gives them.";

        /// <summary>
        /// One clip's user message: the fixed context every clip of the chain shares (style, setting, cast
        /// tags, wardrobe, framing), this clip's keyframes, and the three lines of story that make it this
        /// clip — the beat before for continuity, its own beat, and the beat after so it ends mid-action.
        ///
        /// <para>It is short on purpose. What used to be sent here was the whole story plus a whole-chain
        /// brief, and a model handed all of it writes a little of all of it into every clip.</para>
        /// </summary>
        private string BuildHybridClipRequest(
            string setting, IReadOnlyList<StoryBeatSheet.StoryBeat> beats,
            IReadOnlyList<StoryContinuity.Environment> environments, int index, int clipCount,
            double seconds, string rejection)
        {
            var beat = beats[index];
            var s = seconds.ToString("0.##", CultureInfo.InvariantCulture);

            var location = setting.Length > 0
                ? "SETTING — the same story world in every clip of this chain; restate it in full inside " +
                  "[Shot 1]:\n" + setting
                : "SETTING — read it off the beat below and restate it inside [Shot 1].";

            var continuity = StoryContinuity.WriterBlock(
                EnvironmentFor(environments, index),
                index > 0 ? EnvironmentFor(environments, index - 1) : (StoryContinuity.Environment?)null);
            if (continuity.Length > 0) location += "\n\n" + continuity;


            var last = index == clipCount - 1;
            var previous = index > 0
                ? "THE CLIP BEFORE THIS ONE has already been rendered and showed this — do NOT show it " +
                  $"again:\n{beats[index - 1].Text}"
                : "This is the chain's FIRST clip: it opens the video, already in motion.";
            var next = index + 1 < beats.Count
                ? "THE CLIP AFTER THIS ONE will show this — do NOT reach into it; end this clip mid-action, " +
                  $"on its way there:\n{beats[index + 1].Text}"
                : "This is the chain's LAST clip: the story's final moment lands inside it, and this is the " +
                  "only clip that may resolve anything.";

            var complaint = rejection.Length > 0
                ? $"\n\nYour previous attempt at this clip was rejected: {rejection}"
                : string.Empty;

            return
                BuildClipKeyframeBrief(index + 1, seconds) +
                BuildCastBrief() + "\n\n" +
                (HasCharacter2
                    ? "NAME BOTH CHARACTERS BY THEIR TAGS IN THIS CLIP — <Subject 1> AND <Subject 2> — at " +
                      "each one's first appearance and wherever either is struck, grabbed, named or reacted " +
                      "to after it. A character who appears only as an untagged pronoun ('he', 'his chest', " +
                      "'the man', 'her opponent') has no identity here and renders as a duplicate of the " +
                      "one that IS tagged. A close-up of a body part belongs to whoever's part it is, so " +
                      "say the tag. The two tags are two different people: the beat below says which does " +
                      "what, and the one who strikes is not the one who falls.\n\n"
                    : string.Empty) +
                $"THIS IS CLIP {index + 1} OF {clipCount} of one continuous story, and it is {s} seconds " +
                "long. It is rendered on its own and joined to its neighbours, so it must be a complete, " +
                "self-contained set of the four sections.\n" +
                H3VisualStyles.Rule(VisualStyle) +
                $"{location}\n\n" +
                $"{previous}\n\n" +
                $"THIS CLIP'S ACTION — expand ONLY this, and fill the whole {s} seconds with it:\n" +
                $"{beat.Text}{StoryBeatSheet.DescribePart(beat)}\n\n" +
                $"{next}\n\n" +
                (last ? string.Empty : "Do not resolve or close the story in this clip.\n") +
                "Reply with the four sections and nothing else." +
                complaint;
        }

        /// <summary>This clip's own keyframes, named by the picture numbers
        /// <c>HybridCastPrompt.Assemble</c> will actually give them — the locks are numbered first and the
        /// cast's photographs follow, per clip. Clips past the first usually have none; one does when a
        /// storyboard still was rendered as its opening frame.</summary>
        private string BuildClipKeyframeBrief(int clipNumber, double seconds)
        {
            var keys = KeyframesForClip(clipNumber);
            if (keys.Count == 0)
                return "KEYFRAMES: this clip has none. No attached picture is a frame of it — write one " +
                       "continuous take with no frame lock at either end, and no `<Picture n>` anywhere in " +
                       "your text.\n";

            var sb = new StringBuilder(
                $"KEYFRAMES: {keys.Count} still(s) are attached to THIS clip as frame locks, numbered in " +
                "timestamp order. Each must be a shot boundary in your shot list, opening with the lock " +
                "sentence:\n");
            for (var i = 0; i < keys.Count; i++)
                sb.Append($"  <Picture {i + 1}> is locked at {keys[i].Seconds:0.00} seconds" +
                          (i == 0 && keys[i].Seconds <= 0.001 ? " — the exact opening frame.\n" : ".\n"));
            sb.Append($"After the last lock the clip continues to {seconds:0.00} seconds with no end-frame " +
                      $"lock. Never name a `<Picture n>` above {keys.Count}: those numbers are the cast's " +
                      "studio photographs, which are never frames.\n");
            return sb.ToString();
        }

        /// <summary>Raw reply → clip body. A model told not to emit a clip header sometimes emits one
        /// anyway; <c>SplitClips</c> takes it off and is a no-op on a body that has none.</summary>
        private static string NormalizeClipBody(string raw)
        {
            var body = CleanOutput(raw);
            return SplitClips(body).FirstOrDefault() ?? body;
        }

        /// <summary>
        /// What makes a hybrid clip renderable: the shot list H3 renders from, and both characters named by
        /// their tags.
        ///
        /// <para>The cast check is not cosmetic. <c>HybridCastPrompt.Assemble</c> attaches exactly the
        /// sheets the body names, so a two-hander clip that named only one character is a clip rendered
        /// with one person's photographs — and H3 casts every unnamed body in the frame from those, which
        /// is the fighter-duplicated-against-themselves render.</para>
        /// </summary>
        private string? ValidateHybridClip(string body)
        {
            var sections = HybridCastPrompt.SplitSections(body);

            if (!sections.TryGetValue(HybridCastPrompt.DetailedDescription, out var shots) ||
                string.IsNullOrWhiteSpace(shots))
                return "it carried no detailed_description to render. Reply with the four sections and " +
                       "nothing else, starting with summary:.";

            if (!HybridCastPrompt.IncludesSubject(body, 1))
                return "it never named <Subject 1>, so that character's reference photographs would not be " +
                       "attached and the generator would invent them. Name every character by their tag.";

            if (HasCharacter2 && !HybridCastPrompt.IncludesSubject(body, 2))
                return "it never named <Subject 2>, so only <Subject 1>'s photographs would be attached and " +
                       "the generator would render the second character as a duplicate of the first. Name " +
                       "both characters by their tags.";

            return null;
        }
    }
}
