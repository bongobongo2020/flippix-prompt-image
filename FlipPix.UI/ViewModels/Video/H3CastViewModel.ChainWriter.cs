using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// The H3 Cast family's chain writer — in practice 🪪🌀 <b>H3 Duo</b>, the one tab still reaching
    /// <see cref="H3CastViewModel.AnalyzeAsync"/> without overriding it. A story becomes N clips through one
    /// beat-sheet call and then one call per clip.
    ///
    /// <para><b>What the one-reply shape did here</b> (observed 2026-09-02, <c>test/result2.txt</c>, a
    /// 12-clip jungle fight). Clips 1-3 are clean. From clip 4 every cast tag arrives malformed —
    /// <c>&lt;Picture 1 leaps</c> with no closing bracket — and by clip 10 as <c>&lt; Picture 2,</c> with a
    /// space after the bracket too. Shot markers rot alongside them (<c>[Shot 2 at1.500</c> instead of
    /// <c>[Shot 2] At 00:01.500,</c>), the style word decays through "Liveaction" to "Live acion cinema",
    /// and the bodies shrink from 2,026 characters to 845.</para>
    ///
    /// <para><b>Why that was catastrophic rather than untidy.</b> A malformed tag is not a typo H3 reads
    /// past — it is a tag that no longer matches <c>CastPromptStamp</c>'s regex, so the clip reads as
    /// naming nobody. The selective cast then submits it with the second character's photographs left off
    /// entirely. In that sample only <b>3 of 12</b> clips actually cast character 2; the other nine were
    /// rendered with one person's references and H3 invented a different man in each. That is the "no
    /// consistency, no application of the reference image" the tab was producing.</para>
    ///
    /// <para>Three things changed, and the order matters — the first two make the third mostly redundant,
    /// which is how it should be:</para>
    /// <list type="number">
    /// <item><b>One call per clip</b>, so a clip is ~400 tokens and never reaches the length where a local
    /// model starts dropping brackets.</item>
    /// <item><b>Both characters are required by tag in every clip</b> of a two-hander, checked before the
    /// clip is accepted and rewritten once if it is missing — see <see cref="ValidateCastClip"/>.</item>
    /// <item><b>Near-miss tags are repaired deterministically</b> in <c>CastPromptStamp.RepairTags</c>, so
    /// a slipped bracket from any source — including a prompt edited by hand — still resolves to a
    /// reference instead of silently costing one.</item>
    /// </list>
    ///
    /// <para>A single-clip run is untouched: it was never the failing shape, and it still goes out as the
    /// one call this tab has always sent.</para>
    /// </summary>
    public partial class H3CastViewModel
    {
        /// <summary>The per-clip chain layer, laid over <c>texttovideoH3.md</c>. Shared with the 🧪 H3
        /// Experimental and 🌹🎯 H3 Eros forks, whose cast mode and three-field format are the same.</summary>
        protected const string ClipSystemPromptFile = "h3pw_clip.md";

        /// <summary>
        /// Appended to the cast brief the story writer works from, and to the per-clip CAST line, but
        /// <b>only when the cast is one character</b>. Empty everywhere but 🥽🎯 H3 VR, which turns a solo
        /// cast into a first-person shot — see <see cref="H3VrViewModel.SoloCastDirective"/>.
        ///
        /// <para>It exists because "there is one character" is not, on its own, an instruction not to write
        /// a second one. A story writer handed a scene with one named person will invent a partner for them
        /// to act against, and the render then has no reference photograph for that partner and casts them
        /// from whatever it likes. Both places are patched because they are asked at different times: the
        /// brief shapes the beats, the CAST line shapes the prose written from each beat, and a beat that
        /// already contains two people cannot be written out of one at clip level.</para>
        /// </summary>
        protected virtual string SoloCastDirective => string.Empty;

        /// <summary>
        /// Writes the chain clip by clip and returns it stamped and joined — the same shape
        /// <c>ApplyReferenceLineToChain</c> produced from a single reply.
        /// </summary>
        protected async Task<string> WriteChainClipByClipAsync(
            string model, double len, int clipCount, CancellationToken token)
        {
            ProcessingStatus = $"Dividing the story into {clipCount} beats...";

            var castBrief = HasCharacter2
                ? $"There are two characters: CHARACTER 1 (a {_character1.Noun}) and CHARACTER 2 " +
                  $"(a {_character2.Noun}). Call them CHARACTER 1 and CHARACTER 2 and nothing else — never " +
                  "by the names the story gives them. Cast the story's people onto them in the order the " +
                  "story introduces them, and keep that mapping identical in every beat: whoever strikes " +
                  "in beat 3 carries the same number in beat 9."
                : $"There is one character: CHARACTER 1 (a {_character1.Noun}). Call them CHARACTER 1 and " +
                  "nothing else — never by the name the story gives them." + SoloCastDirective;

            var (setting, beats) = await StoryBeatSheet.WriteAsync(
                _lmStudioService, model, StoryText, clipCount, len, castBrief,
                perBeatCast: false,
                imagePath: HasSceneImage ? SceneImagePath : null,
                log: AddLog,
                token: token);

            if (beats.Count == 0)
            {
                AddLog("WARNING: the story could not be divided into beats — nothing to write.");
                return string.Empty;
            }

            var system = await ReadSystemPromptAsync(ClipSystemPromptFile, token);
            // The guide's own pacing: roughly one cut per 1.25s, floored at 6 so a short clip is still cut
            // like a fight and capped at 14 so a long one stays inside 500 words.
            var shots = Math.Clamp((int)Math.Round(len * 0.8, MidpointRounding.AwayFromZero), 6, 14);

            var bodies = await ClipChainWriter.WriteAsync(
                _lmStudioService, model, system, clipCount,
                buildRequest: (i, reason) =>
                    BuildCastClipRequest(setting, beats, i, clipCount, len, shots, reason),
                normalize: NormalizeClipBody,
                validate: (_, body) => ValidateCastClip(body),
                onProgress: (n, total) => ProcessingStatus = $"Writing clip {n} of {total}...",
                log: AddLog,
                describe: b => $"{b.Length:N0} chars, {CountShots(b)} shots",
                token: token);

            // Stamped NON-selectively for a two-hander, unlike the single-reply path this replaces.
            // Selective stamping drops a character's photographs from any clip whose text does not name
            // them, which is right for a five-person ensemble sharing nine reference slots and wrong here:
            // both fighters are on screen throughout a duel, and a clip that lost one of them renders the
            // other fighting a stranger. It is also what turned a slipped bracket into a wrong render
            // rather than a cosmetic blemish.
            var chain = JoinClips(bodies
                .Select(b => CastPromptStamp.Apply(b, Panels1, Panels2, CastWardrobe,
                                                   selectiveCast: false, CastDescriptor))
                .Where(c => c.Length > 0)
                .ToList());

            ReportCastCoverage(chain);
            return chain;
        }

        /// <summary>
        /// Says out loud, per chain, whether every clip really casts everybody — the one check that would
        /// have caught the 2026-09-02 H3 Duo run before its nine bad renders. It is asked of the finished
        /// chain rather than of each body, because it is the finished chain that Add to Queue reads and the
        /// submit path re-derives the per-clip cast from.
        /// </summary>
        private void ReportCastCoverage(string chain)
        {
            if (!HasCharacter2) return;

            var clips = SplitClips(chain);
            if (clips.Count == 0) return;

            var missing = clips
                .Select((body, i) => (Index: i + 1, Body: CastPromptStamp.Strip(body)))
                .Where(c => !c.Body.Contains("<Picture 2>", StringComparison.Ordinal))
                .Select(c => c.Index)
                .ToList();

            if (missing.Count == 0)
                AddLog($"Cast check: all {clips.Count} clips name both characters — every clip is submitted " +
                       "with both sets of reference photographs.");
            else
                AddLog($"WARNING: clip(s) {string.Join(", ", missing)} name only one character. Both are " +
                       "still sent their references (this tab stamps non-selectively), but the prose in " +
                       "those clips does not tell H3 which body is which. Re-run Analyze, or tag them by hand.");
        }

        /// <summary>
        /// One clip's user message: the fixed context every clip shares (style, setting, cast tags,
        /// wardrobe, length, shot count) and the three lines of story that make this clip this clip — the
        /// beat before it for continuity, its own beat, and the beat after it so it ends mid-action.
        /// </summary>
        private string BuildCastClipRequest(
            string setting, IReadOnlyList<StoryBeatSheet.StoryBeat> beats, int index, int clipCount,
            double seconds, int shots, string rejection)
        {
            var beat = beats[index];
            var s = seconds.ToString("0.##", CultureInfo.InvariantCulture);
            var whole = (int)Math.Floor(seconds);
            var millis = (int)Math.Round((seconds - whole) * 1000);

            var cast = HasCharacter2
                ? "CAST — two reference photographs are attached to this clip. <Picture 1> is CHARACTER 1 " +
                  $"(a {_character1.Noun}); <Picture 2> is CHARACTER 2 (a {_character2.Noun}). The beat " +
                  "below says which of them does what — keep the numbers exactly as it uses them, and name " +
                  "BOTH by their tags in this clip."
                : "CAST — one reference photograph is attached to this clip. <Picture 1> is CHARACTER 1 " +
                  $"(a {_character1.Noun})." + SoloCastDirective;

            var wardrobe = HasCastWardrobe
                ? "WARDROBE — already decided, not yours to choose. Each line opens 'Character N wears …'; " +
                  "attach the garments after that prefix to that character's tag the first time they appear " +
                  "in this clip — '<Picture N>, wearing <those garments>,' — in exactly these words. This is " +
                  "the only clothing wording you may use; where the beat describes clothing differently, " +
                  "this wins:\n" + CastWardrobe.Trim()
                : "WARDROBE — none was decided. Read the outfits off the setting, write them out once in " +
                  "full when each character first appears, and keep that wording for the rest of the clip.";

            var location = setting.Length > 0
                ? $"SETTING — the same in every clip of this chain, restated inside [Shot 1]:\n{setting}"
                : "SETTING — read it off the beat below, and restate it inside [Shot 1].";

            var previous = index > 0
                ? "THE CLIP BEFORE THIS ONE has already been rendered and showed this — do NOT show it " +
                  $"again:\n{beats[index - 1].Text}"
                : "This is the chain's FIRST clip: it opens the video, already in motion.";

            var next = index + 1 < beats.Count
                ? "THE CLIP AFTER THIS ONE will show this — do NOT reach into it; end this clip mid-action, " +
                  $"on its way there:\n{beats[index + 1].Text}"
                : "This is the chain's LAST clip: the story's final moment lands inside it.";

            return
                // The mode line first: it is the one thing that decides whether H3 opens on the scene or on
                // the reference photographs themselves.
                "Mode: character-reference video. The attached pictures are studio reference photographs of " +
                "the cast — plain backdrop, neutral standing pose, shot for identity alone. They are NOT " +
                "frames of this video and the viewer never sees them. Write no alignment or anchor line; " +
                "begin with integrated_multimodal_description: and open [Shot 1] on the setting and the " +
                "action below.\n\n" +
                H3VisualStyles.Rule(VisualStyle) + "\n" +
                $"{location}\n\n" +
                $"{cast}\n\n" +
                $"{wardrobe}\n\n" +
                $"THIS IS CLIP {index + 1} OF {clipCount}. It is {s} seconds long and carries about {shots} " +
                "shots — one cut roughly every second and a half. Every timestamp after [Shot 1] falls " +
                $"inside 00:00.000–{whole / 60:00}:{whole % 60:00}.{millis:000}.\n\n" +
                $"{previous}\n\n" +
                $"THIS CLIP'S ACTION — expand ONLY this, and fill the whole {s} seconds with it:\n" +
                $"{beat.Text}{StoryBeatSheet.DescribePart(beat)}\n\n" +
                $"{next}\n\n" +
                "Reply with the three fields and nothing else." +
                (rejection.Length > 0
                    ? $"\n\nYour previous attempt at this clip was rejected: {rejection}"
                    : string.Empty);
        }

        /// <summary>Raw reply → clip body, with near-miss tags mended before anything reads them. A model
        /// told not to emit a clip header sometimes emits one anyway; <see cref="SplitClips"/> takes it off
        /// and is a no-op on a body that has none.</summary>
        protected virtual string NormalizeClipBody(string raw)
        {
            var body = CastPromptStamp.RepairTags(CleanOutput(raw));
            return SplitClips(body).FirstOrDefault() ?? body;
        }

        /// <summary>
        /// What makes a clip renderable: the description H3 renders from, and — in a two-hander — both
        /// characters named by their tags.
        ///
        /// <para>The cast check is the important one. A clip that names only one fighter is submitted with
        /// only that fighter's photographs attached, and H3 casts every other body in the frame from them:
        /// the opponent comes back as a duplicate of the tagged character, or as a stranger who changes
        /// between clips. Rejecting it here and asking again from the beat is far cheaper than the render.</para>
        /// </summary>
        protected virtual string? ValidateCastClip(string body)
        {
            if (!body.Contains("integrated_multimodal_description:", StringComparison.OrdinalIgnoreCase))
                return "it carried no integrated_multimodal_description to render. Reply with the three " +
                       "fields and nothing else, starting with that label.";

            if (!body.Contains("<Picture 1>", StringComparison.Ordinal))
                return "it never named <Picture 1>, so that character's reference photograph would not be " +
                       "attached and the generator would invent them. Write the tag in full, with both " +
                       "angle brackets, exactly as <Picture 1>.";

            if (HasCharacter2 && !body.Contains("<Picture 2>", StringComparison.Ordinal))
                return "it never named <Picture 2>, so the second character's reference photograph would " +
                       "not be attached and the generator would render them as a duplicate of the first. " +
                       "Name BOTH characters by their tags, written in full with both angle brackets, " +
                       "exactly as <Picture 1> and <Picture 2>.";

            return null;
        }
    }
}
