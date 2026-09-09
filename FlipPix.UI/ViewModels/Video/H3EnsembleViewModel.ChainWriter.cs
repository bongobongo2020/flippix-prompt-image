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
    /// H3 Ensemble, the chain writer: a story becomes N clips through one beat-sheet call and then one call
    /// per clip, rather than a single reply asked to hold the whole chain.
    ///
    /// <para><b>Why.</b> The one-reply shape fails past roughly four clips on a local model — the reply
    /// comes back short, the tags start swapping between characters, then arrive malformed, and the prose
    /// degenerates into unpunctuated word-salad. It was also the one call in this tab that ran on
    /// <c>LlmSampling.StoryChain</c>, whose presence/frequency penalties are documented in
    /// <see cref="LlmSampling.StoryChainFormatted"/> as the direct cause of that word-salad: llama.cpp
    /// levies them over a 64-token sliding window, so a model in an enumeration can neither reuse a word
    /// nor emit the full stop that would end the sentence. Writing one clip at a time removes both — the
    /// reply is short enough not to drift, and it runs on the penalty-free profile.</para>
    ///
    /// <para><b>The ensemble's own wrinkle</b> is casting. A clip is sent only the reference photographs of
    /// the subjects its own text names — that is what makes five characters affordable at all — so which
    /// subjects are in which clip is a decision that has to be made before the clips are written, not
    /// discovered afterwards. The beat sheet makes it: every beat comes back tagged with the subjects in it
    /// (<c>1. [S1, S3] …</c>), and each clip request carries only its own. <c>HybridCastPrompt.Assemble</c>
    /// then attaches exactly the sheets the body named, as it always did.</para>
    ///
    /// <para>A single-clip run is untouched: it was never the failing shape, and it still goes out as the
    /// one call this tab has always sent.</para>
    /// </summary>
    public partial class H3EnsembleViewModel
    {
        /// <summary>The chain layer laid over <c>h3-ensemble.md</c> when writing one clip of many. Replaces
        /// <c>h3-ensemble_story.md</c>, which described the whole-chain-in-one-reply shape.</summary>
        private const string ClipSystemPromptFile = "h3-ensemble_clip.md";

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
                perBeatCast: LoadedCharacters.Count > 1,
                imagePath: HasEnvironment ? EnvironmentPath : null,
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

            ReportBeatCasting(beats);

            var system = await ReadSystemPromptAsync(SystemPromptFile, token) + "\n\n" +
                         await ReadSystemPromptAsync(ClipSystemPromptFile, token);

            var bodies = await ClipChainWriter.WriteAsync(
                _lmStudioService, model, system, clipCount,
                buildRequest: (i, reason) =>
                    BuildEnsembleClipRequest(setting, beats, environments, i, clipCount, len, reason),
                normalize: NormalizeClipBody,
                validate: (i, body) => ValidateEnsembleClip(body, beats[i])
                                       ?? StoryContinuity.Contradiction(body, EnvironmentFor(environments, i)),
                onProgress: (n, total) => AnalyzePhase = $"Writing clip {n} of {total}…",
                log: AddLog,
                describe: b => $"{b.Length:N0} chars, {CountShots(b)} shots",
                token: token);

            // The environment written into each description in code, ahead of [Shot 1]: the writer was told
            // the same thing in words, but only this is identical word for word in every clip that shares a
            // place — and two wordings of one location are two locations to a model that renders them a job
            // apart.
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
        /// clips use, so the casting the sheet decides survives into them.</summary>
        private string BuildBeatSheetCastBrief()
        {
            var loaded = LoadedCharacters;
            if (loaded.Count == 0)
                return "There are no named characters. Divide the story by what happens in it.";

            var sb = new StringBuilder(
                $"There {(loaded.Count == 1 ? "is" : "are")} {loaded.Count} character(s). Call them by these " +
                "tags and nothing else — never by a name the story gives them. Cast the story's people onto " +
                "them in the order the story introduces them, and keep that mapping identical in every beat:\n");

            foreach (var slot in loaded)
            {
                sb.Append($"  S{slot.Index} — ");
                sb.Append(slot.IsPerson
                    ? $"a {slot.Noun}" + (slot.HasRole ? $", playing {slot.Role}" : string.Empty)
                    : $"{slot.Descriptor} (NOT a person)");
                sb.Append('\n');
            }

            if (loaded.Count > 2)
                sb.Append($"Aim for two or three characters per beat — a beat with four or more is a crowd " +
                          "scene. Give every one of them at least one beat, and prefer a run of consecutive " +
                          "beats for a character over single scattered appearances.");

            return sb.ToString();
        }

        /// <summary>
        /// Says out loud how the beat sheet cast the chain, and warns about the two failures that are cheap
        /// to see here and expensive to see in a rendered clip: a character who was given no beat at all
        /// (their sheet was built for nothing) and a beat that named more subjects than a clip can hold
        /// likenesses for.
        /// </summary>
        private void ReportBeatCasting(IReadOnlyList<StoryBeatSheet.StoryBeat> beats)
        {
            var loaded = LoadedCharacters;
            if (loaded.Count == 0) return;

            var cast = beats.Select(b => b.CastIndices).ToList();
            if (cast.All(c => c.Count == 0))
            {
                AddLog("Note: the beat sheet did not tag its beats with a cast, so every clip is written for " +
                       "the whole cast and each one is sent everybody's sheets. With more than three " +
                       "characters that is where likenesses go soft — re-run Analyze for a tagged sheet.");
                return;
            }

            AddLog("Casting: " + string.Join("  ", cast.Select((c, i) =>
                $"clip {i + 1}=[{(c.Count == 0 ? "—" : string.Join(",", c.Select(n => $"S{n}")))}]")));

            var missing = loaded.Where(s => !cast.Any(c => c.Contains(s.Index))).ToList();
            if (missing.Count > 0)
                AddLog($"WARNING: character {string.Join(", ", missing.Select(s => s.Index))} " +
                       $"({string.Join("; ", missing.Select(s => s.Descriptor))}) " +
                       $"{(missing.Count == 1 ? "is" : "are")} in no beat and will never reach the screen. " +
                       "Re-run Analyze, or write them into a clip by hand.");

            var crowded = cast.Select((c, i) => (Clip: i + 1, c.Count))
                              .Where(x => x.Count > 3).ToList();
            if (crowded.Count > 0)
                AddLog($"Note: clip(s) {string.Join(", ", crowded.Select(x => $"{x.Clip} ({x.Count} subjects)"))} " +
                       "name more than three subjects — the reference slots are split that many ways, so " +
                       "expect softer likenesses in them.");
        }

        /// <summary>
        /// One clip's user message: the fixed context every clip of the chain shares (style, setting,
        /// location, wardrobe, framing), the cast for THIS clip only, its keyframes, and the three lines of
        /// story that make it this clip — the beat before for continuity, its own beat, and the beat after
        /// so it ends mid-action.
        /// </summary>
        private string BuildEnsembleClipRequest(
            string setting, IReadOnlyList<StoryBeatSheet.StoryBeat> beats,
            IReadOnlyList<StoryContinuity.Environment> environments, int index, int clipCount,
            double seconds, string rejection)
        {
            var beat = beats[index];
            var c = CultureInfo.InvariantCulture;
            var s = seconds.ToString("0.##", c);

            var location = HasEnvironment || setting.Length > 0
                ? "SETTING — the same story world in every clip of this chain; restate it in full inside " +
                  "[Shot 1]:\n" +
                  (setting.Length > 0 ? setting : "(read it off the location image)")
                : "SETTING — read it off the beat below and restate it inside [Shot 1].";

            // This clip's own place, hour and light — facts, not mood, and the half of the brief that may
            // not move between clips.
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
                BuildLocationBrief() +
                BuildClipKeyframeBrief(index + 1, seconds) +
                BuildClipCastBrief(beat) + "\n" +
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
        /// cast's photographs follow, per clip.</summary>
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
                      "studio photographs and the location, which are never frames.\n");
            return sb.ToString();
        }

        /// <summary>The cast brief scoped to one clip: only the subjects this beat cast, described the way
        /// the whole-chain brief described them, plus the rule that no other subject may be named.</summary>
        private string BuildClipCastBrief(StoryBeatSheet.StoryBeat beat)
        {
            var loaded = LoadedCharacters;
            if (loaded.Count == 0) return "CAST: no character reference sheets are attached.\n";

            var wanted = beat.CastIndices;
            var inClip = wanted.Count > 0
                ? loaded.Where(s => wanted.Contains(s.Index)).ToList()
                : loaded.ToList();
            // A beat that cast nobody the tab actually has loaded still has to render somebody.
            if (inClip.Count == 0) inClip = loaded.ToList();

            var sb = new StringBuilder(
                $"CAST FOR THIS CLIP: {inClip.Count} character(s) are on screen, and their reference sheets " +
                "are the ones attached to it. Refer to them only by these tags — never by a picture number " +
                "and never by a name from the story:\n");

            foreach (var slot in inClip)
            {
                sb.Append($"  <Subject {slot.Index}> — ");
                if (slot.IsPerson)
                    sb.Append($"a {slot.Noun}")
                      .Append(slot.HasRole ? $", playing {slot.Role}" : string.Empty)
                      .Append($". Use \"{slot.Pronoun}\" for them. Write no word for their hair, face, skin, " +
                              "build or age — the sheet carries all of it.");
                else
                    sb.Append($"{slot.Descriptor}. NOT a person — do not describe it as a man, a woman, a " +
                              "child or a person, do not give it a human face, human hair, human hands or a " +
                              "human body, and do not put a person on screen to stand in for it. It still " +
                              "acts: it moves, reacts and carries its beats.");
                sb.Append('\n');
            }

            var absent = loaded.Where(s => inClip.All(x => x.Index != s.Index)).ToList();
            if (absent.Count > 0)
                sb.Append("NAME NOBODY ELSE. " +
                          string.Join(", ", absent.Select(s => $"<Subject {s.Index}>")) +
                          $" {(absent.Count == 1 ? "is" : "are")} NOT in this clip and must not appear in " +
                          "your text at all — not in the action, not in the background, not in the " +
                          "soundscape. Each clip is sent only the photographs of the subjects it names, so " +
                          "naming one who is not here spends a reference slot the others needed.\n");

            if (HasCastWardrobe)
                sb.Append("CLOTHING IS ALREADY DECIDED AND IS NOT YOURS TO CHOOSE. Attach the outfit to the " +
                          "tag the first time each character appears — \"<Subject 1>, wearing …\" — copying " +
                          "this wording rather than rephrasing it, and never invent a costume change:\n")
                  .Append(CastWardrobe.Trim()).Append('\n')
                  .Append(SheetsShowWardrobe
                      ? "Their sheets were photographed in exactly these clothes, so the pictures and the " +
                        "words agree — do not contradict either.\n"
                      : "Their sheets are studio photographs and whatever those show them wearing is " +
                        "irrelevant.\n");
            else
                sb.Append("CLOTHING: dress the cast as the setting plainly calls for, write each outfit out " +
                          "explicitly the first time that character appears, and use the identical wording " +
                          "every later time.\n");

            sb.Append("FRAMING IS A HARD CONSTRAINT: no shot may be wider than a full-body wide shot — no " +
                      "ultra-wide, no extreme long shot, no aerial. ");
            if (inClip.Any(s => s.IsPerson))
                sb.Append("Every shot a person appears in must frame their face legibly. ");
            if (inClip.Any(s => !s.IsPerson))
                sb.Append("A character who is not a person must be large and clearly readable in every shot " +
                          "they are in. ");
            sb.Append("Do not combine a wide framing with a fast or large camera move.\n");

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
        /// What makes an ensemble clip renderable: the shot list H3 renders from, and the subject tags this
        /// clip was cast with.
        ///
        /// <para>The cast check is not cosmetic. <c>HybridCastPrompt.Assemble</c> attaches exactly the
        /// sheets the body names, so a body that forgot a tag is a clip rendered without that character's
        /// photographs — and a body that names nobody is a clip with no references at all, which is a clip
        /// with nothing holding a face still.</para>
        /// </summary>
        private string? ValidateEnsembleClip(string body, StoryBeatSheet.StoryBeat beat)
        {
            var sections = HybridCastPrompt.SplitSections(body);

            if (!sections.TryGetValue(HybridCastPrompt.DetailedDescription, out var shots) ||
                string.IsNullOrWhiteSpace(shots))
                return "it carried no detailed_description to render. Reply with the four sections and " +
                       "nothing else, starting with summary:.";

            var expected = beat.CastIndices.Count > 0
                ? beat.CastIndices.Where(i => LoadedCharacters.Any(s => s.Index == i)).ToList()
                : LoadedCharacters.Select(s => s.Index).ToList();
            if (expected.Count == 0) return null;

            var missing = expected.Where(i => !HybridCastPrompt.IncludesSubject(body, i)).ToList();
            if (missing.Count == expected.Count)
                return "it named none of this clip's subjects, so the clip would be rendered with no " +
                       "reference photographs attached. Name every character listed for this clip by " +
                       "their <Subject n> tag.";
            if (missing.Count > 0)
                return $"it never named {string.Join(" or ", missing.Select(i => $"<Subject {i}>"))}, so " +
                       "that character's reference photographs would not be attached and the generator " +
                       "would invent them. Name every character listed for this clip by their tag.";

            return null;
        }
    }
}
