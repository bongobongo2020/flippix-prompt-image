using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Writes — and unwrites — the preamble the 🪪👥 H3 Cast tab puts in front of every prompt it sends to
    /// MiniMax H3: the sheet reference line and the wardrobe lock.
    ///
    /// <para><b>What the preamble is for.</b> The cast reaches H3 as reference photographs, one per view of
    /// each character's sheet. Several photographs of one person read as several <i>people</i> unless the
    /// prompt says otherwise, and the sheet's studio clothing has nothing to do with the video, so both facts
    /// have to be stated in words. They are stated <i>here</i>, in code, rather than left to the model that
    /// writes the body — a story's clips are written as independent blocks by a model that cannot see its own
    /// earlier output, so this preamble is the only text in a chain that is identical in every clip by
    /// construction, and therefore the only place an outfit can be held still.</para>
    ///
    /// <para><b>Three forms of the same prompt.</b> The model writes bodies in the <i>canonical</i> form,
    /// naming two characters as <c>&lt;Picture 1&gt;</c> and <c>&lt;Picture 2&gt;</c>. Stamping produces the
    /// <i>tagged</i> form, where each panel has a stable alias (<c>@char1_face</c>) —
    /// <c>MiniMaxH3TaggedReferenceToVideo</c> resolves those per clip and assigns the real picture numbers
    /// itself. <see cref="Detag"/> produces the <i>numbered</i> form for a ComfyUI whose
    /// MiniMaxH3-Contex-Loop predates tagged references. All three are the same words; only the reference
    /// tokens differ, which is why the fallback is a token substitution and not a second code path.</para>
    ///
    /// <para><b>The invariant everything here rests on:</b> <see cref="Strip"/> ∘ <see cref="Apply"/> is the
    /// identity on canonical bodies, and <see cref="Apply"/> ∘ <see cref="Strip"/> is idempotent. Analyze
    /// stamps a chain, the user edits the wardrobe box, Add to Queue re-stamps the same chain — none of that
    /// may accumulate preambles or lose the cast. It is why <see cref="Strip"/> collapses <i>any</i> picture
    /// number above 1 to character 2: once a character occupies several reference slots the old numbering
    /// cannot be recovered, so it is discarded rather than remembered.</para>
    /// </summary>
    public static class CastPromptStamp
    {
        /// <summary>Every reference line starts with this, so an existing one can be found and rewritten.</summary>
        public const string ReferenceLinePrefix = "For the target video,";

        /// <summary>
        /// What the preamble needs to know about the cast beyond how many pictures each of them occupies: the
        /// sex to name them by, and whether their reference sheets were built <i>wearing the locked
        /// wardrobe</i>.
        ///
        /// <para>That second flag flips the reference line's clothing sentence between the only two honest
        /// things it can say. A sheet built from an ordinary photo shows clothes that have nothing to do with
        /// the video, so the line has to disown them; a sheet the tab dressed from the wardrobe shows exactly
        /// the clothes the video wants, so the line has to point at them instead — telling H3 to ignore the
        /// clothing in a picture that is right is how the outfit ends up re-invented per clip.</para>
        /// </summary>
        public sealed record CastInfo(string? Sex1 = null, string? Sex2 = null, bool SheetsShowWardrobe = false)
        {
            /// <summary>No sexes given and sheets of unknown clothing — how a prompt stamped by older code reads.</summary>
            public static readonly CastInfo Unknown = new();

            /// <summary>"man" / "woman" for one character, or null when it was never set.</summary>
            public string? SexOf(int character) =>
                string.IsNullOrWhiteSpace(character == 1 ? Sex1 : Sex2) ? null
                                                                       : (character == 1 ? Sex1 : Sex2);
        }

        /// <summary>
        /// Opens the code-written wardrobe block. Also how an existing block is found and replaced — see
        /// <see cref="StripWardrobeBlock"/>.
        /// </summary>
        public const string WardrobeLockPrefix = "WARDROBE LOCK";

        /// <summary>
        /// The sentence the wardrobe block opens with. It has to outrank the prompt body, because the body is
        /// written per clip and will drift: whatever the model says three paragraphs down, this line is the
        /// same in clip 1 and clip 8, so it is the only description in the prompt that can hold an outfit
        /// still.
        /// </summary>
        public const string WardrobeLockHeader =
            WardrobeLockPrefix + " — this is the authoritative wardrobe for the whole video and it is identical " +
            "in every clip. Dress the cast in exactly these clothes, unchanged from the first frame to the last, " +
            "and ignore any other clothing wording anywhere below; nobody changes, adds, removes or restyles a " +
            "garment unless this block says so:";

        /// <summary>
        /// The sentence that stops H3 rendering the reference sheet instead of the video: no studio
        /// backdrop, no neutral pose, and — the part that does the work — no duplicate of one person and no
        /// grid or split-screen layout. Several photographs of one person are a strong pull toward a frame
        /// laid out like the sheet, and this is what resists it.
        ///
        /// <para>Held as a constant because <see cref="MakeStereo"/> has to find it again word for word in
        /// an already-stamped prompt.</para>
        /// </summary>
        public const string SceneRule =
            "These references are NOT the scene: never show the same person more than once in a frame, " +
            "never line the cast up side by side against a plain backdrop, and do not copy the " +
            "references' plain background, their neutral standing pose, or any panel, grid or " +
            "split-screen layout into the video.";

        /// <summary>
        /// <see cref="SceneRule"/> for a tab whose output is a <b>stereoscopic side-by-side pair</b> —
        /// 🥽🎯 H3 VR.
        ///
        /// <para><b>Why the ordinary rule cannot be used there.</b> A VR180 SBS frame <i>is</i> a
        /// split-screen layout in which the same person appears twice, so <see cref="SceneRule"/> forbids
        /// exactly what the LoRA is for. Sent together, the two instructions contradict each other, and the
        /// only reading that satisfies both literally is the one the model actually took: split the frame,
        /// and put a <b>different person in each half</b>. That is the "second character" a solo cast was
        /// coming back with — not an invented partner standing beside her, but her other eye rendered as
        /// somebody else.</para>
        ///
        /// <para>So this version keeps the two halves of the rule that still apply — no studio backdrop, no
        /// neutral pose — and replaces the anti-duplication half with the duplication the format requires,
        /// stated precisely enough to be a constraint rather than a licence: one person per half, the same
        /// person in both, and no duplicate inside either half.</para>
        /// </summary>
        public const string StereoSceneRule =
            "These references are NOT the scene: do not copy the references' plain background or their " +
            "neutral standing pose into the video, and do not line the cast up against a plain backdrop " +
            "the way the references do. This video's frame IS a stereoscopic side-by-side pair and must be " +
            "split down the middle: the left half is the left eye's view and the right half is the right " +
            "eye's view of ONE single scene at ONE single moment. Everyone in the scene therefore appears " +
            "once in the left half and once in the right half, and the two are the SAME person — the same " +
            "face, the same clothing, the same pose, the same position in the scene — differing only by a " +
            "small horizontal parallax shift. Never put a different person, a different pose or a different " +
            "moment in the two halves, and never show the same person twice inside one half. The frame is " +
            "one scene seen by two eyes, never two scenes and never two casts.";

        /// <summary>The H3 field the body proper begins at, used to find where the preamble ends.</summary>
        private const string BodyAnchor = "integrated_multimodal_description:";

        #region Cast aliases

        /// <summary>Matches a picture reference in a prompt body, whatever number it carries.</summary>
        private static readonly Regex PictureTagRegex =
            new(@"<\s*Picture\s+(\d+)\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Matches one of this tab's cast aliases, whatever view it names: <c>@char2_face</c>, <c>@char1</c>.
        /// Anchored on a non-word boundary at the front so an e-mail address or a stray <c>@</c> in the prose
        /// cannot be mistaken for one.
        /// </summary>
        public static readonly Regex CastTagRegex =
            new(@"(?<![\w@])@char(\d+)(?:_(front|back|face|v\d+))?\b",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The aliases character <paramref name="character"/>'s panels are registered under, in panel order.
        ///
        /// <para>Three panels are named for the views the sheet builder is asked for, because a name a
        /// language model can read is a name it can keep straight; any other count falls back to
        /// <c>_v1…_vN</c>. Index 0 is the character's <i>primary</i> alias — the one the prompt body uses
        /// wherever it just means "this person".</para>
        /// </summary>
        public static IReadOnlyList<string> Aliases(int character, int panels)
        {
            if (panels <= 1) return new[] { $"char{character}" };
            if (panels == 3)
                return new[] { $"char{character}_front", $"char{character}_back", $"char{character}_face" };
            return Enumerable.Range(1, panels).Select(v => $"char{character}_v{v}").ToArray();
        }

        /// <summary>Every alias of a cast, character 1's first — the order the references are wired in.</summary>
        public static IReadOnlyList<string> AllAliases(int panels1, int panels2) =>
            Aliases(1, Math.Max(1, panels1))
                .Concat(panels2 > 0 ? Aliases(2, panels2) : Enumerable.Empty<string>())
                .ToList();

        /// <summary>An alias as it appears in a prompt.</summary>
        public static string Token(string alias) => "@" + alias;

        /// <summary>One character's aliases as a readable list, for the log and the reference line.</summary>
        public static string DescribeAliases(int character, int panels)
        {
            var tags = Aliases(character, Math.Max(1, panels)).Select(Token).ToList();
            return tags.Count == 1
                ? tags[0]
                : string.Join(", ", tags.Take(tags.Count - 1)) + " and " + tags[^1];
        }

        /// <summary>
        /// Whether a stamped prompt actually casts the second character — read back at submit time so a clip
        /// is only ever sent the references it uses. Character 1 is not asked about: <see cref="Apply"/> names
        /// them in every reference line it writes, and a job with no character 1 does not exist.
        ///
        /// <para>A prompt stamped before this tab used aliases carries none, and reports its cast through
        /// plain picture numbers instead — where <c>&lt;Picture 2&gt;</c> may mean either character 2 or
        /// character 1's second panel. That ambiguity is why such a prompt is answered <paramref
        /// name="hasCharacter2"/>: it is the reading that can only ever send one reference too many, never one
        /// too few.</para>
        /// </summary>
        public static bool IncludesCharacter2(string? prompt, bool hasCharacter2)
        {
            var tags = CastTagRegex.Matches(prompt ?? string.Empty);
            if (tags.Count == 0) return hasCharacter2;
            return hasCharacter2 && tags.Any(m => m.Groups[1].Value != "1");
        }

        /// <summary>True when a prompt is in the tagged form and can drive the tagged reference nodes.</summary>
        public static bool IsTagged(string? prompt) => CastTagRegex.IsMatch(prompt ?? string.Empty);

        /// <summary>
        /// Turns a stamped, tagged prompt back into fixed picture numbers, for a ComfyUI whose
        /// MiniMaxH3-Contex-Loop is too old to have the tagged reference nodes. Slots are counted over the
        /// characters actually present, matching the order the references are wired in.
        /// </summary>
        public static string Detag(string prompt, int panels1, int panels2)
        {
            var slot = 1;
            foreach (var alias in AllAliases(panels1, panels2))
                prompt = prompt.Replace(Token(alias), $"<Picture {slot++}>", StringComparison.OrdinalIgnoreCase);
            return prompt;
        }

        /// <summary>
        /// Rewrites a canonical body so each character is named by their primary alias. Only the body is
        /// touched — the reference line names every panel alias, and it is that mention which activates the
        /// panels for the clip.
        /// </summary>
        private static string Retag(string body, int panels1, int panels2)
        {
            body = body.Replace("<Picture 1>", Token(Aliases(1, Math.Max(1, panels1))[0]), StringComparison.Ordinal);
            return panels2 > 0
                ? body.Replace("<Picture 2>", Token(Aliases(2, panels2)[0]), StringComparison.Ordinal)
                : body;
        }

        /// <summary>
        /// A picture tag a model <i>meant</i> to write and did not quite: an opening angle bracket, an
        /// optional space, some spelling of "Picture", the number — and then anything or nothing where the
        /// closing bracket belongs.
        ///
        /// <para>Observed on H3 Duo 2026-09-02: from clip 4 of a 12-clip chain onwards, every tag arrived as
        /// <c>&lt;Picture 1</c> with no closing bracket, and by clip 10 as <c>&lt; Picture 2</c> with a space
        /// after the bracket as well. Earlier, on H3 Eros, as <c>&lt;P 1&gt;</c>. None of those match
        /// <see cref="PictureTagRegex"/>, and the consequences are silent and total: the tag is never
        /// resolved to a reference, so H3 is handed the literal text; and because
        /// <see cref="IncludesCharacter2"/> and the selective cast read the same regex, a clip whose only
        /// mention of character 2 was malformed is submitted <b>with character 2's photographs left off
        /// altogether</b> — which is the clip that comes back with a stranger in it.</para>
        ///
        /// <para>The opening <c>&lt;</c> is what makes this safe to repair: prose does not contain it, so a
        /// match is always a tag that was aimed at and missed.</para>
        /// </summary>
        /// <para>The closing bracket and the space in front of it are consumed <i>together or not at
        /// all</i> — <c>(?:\s*&gt;)?</c>, never <c>\s*&gt;?</c>. The loose form swallows the space after an
        /// unclosed tag and welds the tag to the next word (<c>&lt;Picture 1&gt;leaps</c>), which trades one
        /// malformation for another.</para>
        private static readonly Regex BrokenPictureTagRegex =
            new(@"<\s*(?:Picture|Pictuer|Picutre|Pic|P)\s*(\d{1,2})(?:\s*>)?",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// A cast alias run straight into the next word — <c>@char1_frontkicks off his collarbone</c>,
        /// observed in the same chain. <see cref="CastTagRegex"/> ends on <c>\b</c>, so this matches
        /// <i>nothing at all</i>: the alias is not seen, and the character it names loses their references
        /// for that clip exactly as a malformed picture tag does.
        /// </summary>
        private static readonly Regex RunOnAliasRegex =
            new(@"(?<![\w@])(@char\d+(?:_(?:front|back|face|v\d+))?)(?=[A-Za-z])",
                RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Repairs the near-miss cast tags a model writes when a long reply starts to drift, so they resolve
        /// to references instead of being silently dropped. Idempotent, and a no-op on a clean body.
        ///
        /// <para>Public because it is worth running — and reporting — before a prompt is stamped, so the
        /// tab can say how many tags it had to mend rather than repairing them invisibly.</para>
        /// </summary>
        public static string RepairTags(string? body)
        {
            if (string.IsNullOrEmpty(body)) return body ?? string.Empty;
            body = RunOnAliasRegex.Replace(body, "$1 ");
            return BrokenPictureTagRegex.Replace(body, m => $"<Picture {m.Groups[1].Value}>");
        }

        /// <summary>How many tags <see cref="RepairTags"/> would mend — what the tab reports.</summary>
        public static int CountBrokenTags(string? body)
        {
            if (string.IsNullOrEmpty(body)) return 0;
            var broken = RunOnAliasRegex.Matches(body).Count;
            foreach (Match m in BrokenPictureTagRegex.Matches(body))
                if (m.Value != $"<Picture {m.Groups[1].Value}>") broken++;
            return broken;
        }

        /// <summary>
        /// Puts a body into the one form everything here is written against: two characters, named
        /// <c>&lt;Picture 1&gt;</c> and <c>&lt;Picture 2&gt;</c>. Both the numbered slots of an already-stamped
        /// prompt and the aliases of a tagged one collapse to it.
        ///
        /// <para>Near-miss tags are mended first (<see cref="RepairTags"/>). It happens here rather than at
        /// the call sites because every path into the stamp — Analyze, a re-stamp after a wardrobe edit, Add
        /// to Queue, a prompt typed by hand — goes through <see cref="Strip"/>, and a tag that is still
        /// broken by the time it reaches the queue costs a render.</para>
        /// </summary>
        private static string Canonicalize(string body)
        {
            body = RepairTags(body);
            body = CastTagRegex.Replace(body, m => m.Groups[1].Value == "1" ? "<Picture 1>" : "<Picture 2>");
            return PictureTagRegex.Replace(body, m => m.Groups[1].Value == "1" ? "<Picture 1>" : "<Picture 2>");
        }

        #endregion

        #region The description field

        /// <summary>The fields that follow the description, in the order H3 expects them. Whichever comes
        /// first is where the description ends.</summary>
        private static readonly string[] TailAnchors = { "overall_soundscape:", "non_diegetic_music:" };

        /// <summary>
        /// Just the <c>integrated_multimodal_description</c> body — the field H3 actually renders motion
        /// from — with its label, the reference preamble, the wardrobe lock and the two sound fields all
        /// left behind.
        ///
        /// <para>This is the half of a prompt worth putting in front of someone: the preamble and the
        /// wardrobe block are code-written and identical in every clip of a chain, and editing them by hand
        /// is how a cast stops being wired correctly. Empty for a prompt carrying no label at all, which is
        /// the honest answer — there is no description field to edit.</para>
        /// </summary>
        public static string ExtractDescription(string? prompt)
        {
            var t = prompt ?? string.Empty;
            var start = t.IndexOf(BodyAnchor, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;
            start += BodyAnchor.Length;
            return t[start..TailStart(t, start)].Trim();
        }

        /// <summary>
        /// Puts an edited description back into its prompt, leaving the preamble, the wardrobe lock and the
        /// two sound fields exactly as they were. A prompt with no label gets one appended, so a hand-written
        /// clip can still be given a description rather than silently swallowing the edit.
        /// </summary>
        public static string ReplaceDescription(string? prompt, string? description)
        {
            var t = prompt ?? string.Empty;
            var body = (description ?? string.Empty).Trim();
            var start = t.IndexOf(BodyAnchor, StringComparison.OrdinalIgnoreCase);

            if (start < 0)
            {
                if (body.Length == 0) return t;
                var head = t.TrimEnd();
                return head.Length == 0 ? $"{BodyAnchor} {body}" : $"{head}\n\n{BodyAnchor} {body}";
            }

            var afterLabel = start + BodyAnchor.Length;
            var tail = t[TailStart(t, afterLabel)..].TrimStart();
            return t[..afterLabel] + " " + body + (tail.Length > 0 ? "\n\n" + tail : string.Empty);
        }

        /// <summary>Matches a shot header wherever it appears in a description body.</summary>
        private static readonly Regex ShotHeaderRegex =
            new(@"\s*(\[\s*Shot\s*\d+\s*\])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Collapses every run of whitespace to one space, for comparing two spellings of the
        /// same description.</summary>
        private static readonly Regex WhitespaceRunRegex = new(@"\s+", RegexOptions.Compiled);

        /// <summary>
        /// The same description with every <c>[Shot n]</c> starting its own line. Purely how it is laid out
        /// for reading and editing — a description is a list of shots, and run together in one paragraph it
        /// cannot be scanned, let alone edited.
        /// </summary>
        public static string ShotLines(string? description) =>
            ShotHeaderRegex.Replace((description ?? string.Empty).Trim(), "\n$1").TrimStart();

        /// <summary>
        /// Whether two descriptions say the same thing, ignoring how they are laid out.
        ///
        /// <para>The board hands its box the <see cref="ShotLines"/> spelling while the prompt holds whatever
        /// the writer produced, so a straight comparison would read every clip as edited the moment the board
        /// was built — and staling a clip costs its takes.</para>
        /// </summary>
        public static bool SameDescription(string? a, string? b) =>
            string.Equals(Flatten(a), Flatten(b), StringComparison.Ordinal);

        private static string Flatten(string? text) =>
            WhitespaceRunRegex.Replace((text ?? string.Empty).Trim(), " ");

        /// <summary>Where the description stops: the first sound field after it, or the end of the prompt.</summary>
        private static int TailStart(string prompt, int from)
        {
            var end = prompt.Length;
            foreach (var anchor in TailAnchors)
            {
                var i = prompt.IndexOf(anchor, from, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && i < end) end = i;
            }
            return end;
        }

        #endregion

        #region Stamping

        /// <summary>
        /// Removes the code-written preamble — reference line and wardrobe block — and returns the body in
        /// canonical form, ready to be stamped again for a different cast, wardrobe or panel split.
        /// </summary>
        public static string Strip(string? prompt)
        {
            var t = (prompt ?? string.Empty).Trim();
            if (!t.StartsWith(ReferenceLinePrefix, StringComparison.OrdinalIgnoreCase))
                return Canonicalize(StripWardrobeBlock(t).Trim());

            var idx = t.IndexOf(BodyAnchor, StringComparison.OrdinalIgnoreCase);
            if (idx > 0) return Canonicalize(t[idx..].Trim());

            // No H3 field to anchor on (a hand-written prompt, or a model that dropped the labels): fall back
            // to dropping the reference line's own paragraph, then whatever wardrobe block follows it.
            var nl = t.IndexOf('\n');
            return nl > 0 ? Canonicalize(StripWardrobeBlock(t[(nl + 1)..].Trim()).Trim()) : string.Empty;
        }

        /// <summary>
        /// Stamps one clip: the reference line for the cast wired into it, the wardrobe lock, then the body.
        ///
        /// <para>The cast and the wardrobe are passed in rather than read off the tab, because a queued item's
        /// preamble must describe the cast and wardrobe it was queued with, not whichever happen to be loaded
        /// when the item eventually runs.</para>
        ///
        /// <para><paramref name="selectiveCast"/> drops a character the clip never names — references,
        /// wardrobe line and all. Pass it only for a story chain, where clips genuinely take turns; a lone clip
        /// keeps whoever is loaded, because loading a character you did not want in your one clip is not a
        /// thing anyone does, whereas a model forgetting to name someone in beat 6 of 12 certainly is.</para>
        /// </summary>
        public static string Apply(
            string? prompt, int panels1, int panels2, string? wardrobe, bool selectiveCast = false,
            CastInfo? cast = null)
        {
            var body = Strip(prompt);
            if (body.Length == 0) return string.Empty;

            cast ??= CastInfo.Unknown;

            // Read off the canonical body, so it does not matter which form the prompt arrived in.
            if (selectiveCast && !body.Contains("<Picture 2>", StringComparison.Ordinal)) panels2 = 0;

            var sb = new StringBuilder(BuildReferenceLine(panels1, panels2, cast));
            // The wardrobe box is kept in the canonical form the user typed and the derive pass wrote, so its
            // "Character 2 (<Picture 2>)" lines are re-tagged here alongside the body — otherwise the block
            // would dress a reference that belongs to character 1.
            var wardrobeLock = Retag(
                DropAbsentWardrobeLines(BuildWardrobeLock(wardrobe, cast.SheetsShowWardrobe), panels2 > 0),
                panels1, panels2);
            if (wardrobeLock.Length > 0) sb.Append("\n\n").Append(wardrobeLock);
            return sb.Append("\n\n").Append(Retag(body, panels1, panels2)).ToString();
        }

        /// <summary>How the cast member this pass is not about is referred to once their pictures are gone.</summary>
        private const string OtherPerson = "the other person";

        /// <summary>
        /// The clip as <b>one character's own face-refine pass</b> needs to read it: their panels numbered
        /// from <c>&lt;Picture 1&gt;</c>, their wardrobe line alone, and the rest of the cast reduced to
        /// "the other person".
        ///
        /// <para><c>H3FaceTrackCrop</c> follows a single subject, so a two-character clip is refined by two
        /// passes, one per face. A pass shown both cast members' photographs has nothing to say about which
        /// of the two faces it is looking at — and a body still naming character 2 as
        /// <c>&lt;Picture 2&gt;</c> would be pointing at character 1's <i>back view</i> once only character
        /// 1's panels are wired. Both problems are the same problem: the pass has a cast of one, so the
        /// prompt is rewritten to have a cast of one.</para>
        ///
        /// <para>The other person is not deleted from the prose — they are in the scene, and sometimes in the
        /// crop's background. They are simply left unidentified, which is the truth of what this pass
        /// receives.</para>
        /// </summary>
        /// <param name="character">1 or 2 — whose face this pass regenerates.</param>
        /// <param name="panels">How many of that character's panels the pass is conditioned on.</param>
        public static string SoloRefinePrompt(
            string? prompt, int character, int panels, string? wardrobe, CastInfo? cast = null)
        {
            var body = Strip(prompt);
            if (body.Length == 0) return string.Empty;

            cast ??= CastInfo.Unknown;
            panels = Math.Max(1, panels);
            var other = character == 1 ? 2 : 1;

            body = body.Replace($"<Picture {other}>", OtherPerson, StringComparison.Ordinal);
            if (character != 1)
                body = body.Replace($"<Picture {character}>", "<Picture 1>", StringComparison.Ordinal);

            // Everything below is written for "character 1" because that is what a cast of one is; the sex
            // comes from whoever this actually is.
            var solo = new CastInfo(cast.SexOf(character), null, cast.SheetsShowWardrobe);
            var sb = new StringBuilder(BuildReferenceLine(panels, 0, solo));

            var wardrobeLock = SoloWardrobeLines(
                BuildWardrobeLock(wardrobe, cast.SheetsShowWardrobe), character, cast.SexOf(character));
            if (wardrobeLock.Length > 0) sb.Append("\n\n").Append(wardrobeLock);
            sb.Append("\n\n").Append(body);

            // The refine pass stays on the core reference node, so its pictures are numbers, not aliases.
            return Detag(sb.ToString(), panels, 0);
        }

        /// <summary>
        /// Keeps the wardrobe block's header and the one character's own line, renumbered to the cast of one
        /// this pass has: <c>Character 2 (&lt;Picture 2&gt;, a woman)</c> becomes
        /// <c>Character 1 (&lt;Picture 1&gt;, a woman)</c>.
        /// </summary>
        private static string SoloWardrobeLines(string block, int character, string? sex)
        {
            if (block.Length == 0) return block;

            var lines = block.Split('\n').ToList();
            var kept = lines.Where(line =>
            {
                var m = Regex.Match(line, @"^\s*Character\s+(\d+)\b", RegexOptions.IgnoreCase);
                return !m.Success || m.Groups[1].Value == character.ToString(CultureInfo.InvariantCulture);
            }).ToList();
            // A block with no per-character lines at all is one unparseable paragraph — keep it whole rather
            // than reducing the authoritative wardrobe to its heading.
            if (kept.Count <= 1) kept = lines;

            var who = sex == null ? "Character 1 (<Picture 1>)" : $"Character 1 (<Picture 1>, a {sex})";
            return string.Join("\n", kept.Select(line =>
                Regex.Replace(line, @"^\s*Character\s+\d+\s*(\([^)]*\))?", who, RegexOptions.IgnoreCase)));
        }

        /// <summary>
        /// Writes the reference line for a cast cut into <paramref name="panels1"/> and
        /// <paramref name="panels2"/> separate references (<paramref name="panels2"/> is 0 when character 2 is
        /// not in this clip).
        ///
        /// <para>Its job is to undo, in words, the one thing splitting the sheet cannot: several pictures of
        /// one person read as several <i>people</i> unless they are explicitly grouped. So it says which
        /// pictures belong to whom, that they are the same individual from different angles, and — the part
        /// this whole mechanism exists for — that these are reference photographs rather than frames, so their
        /// backdrops, their standing poses and any side-by-side arrangement of them must not appear in the
        /// video. It also disowns the clothing, because the wardrobe is decided separately and stamped in
        /// below.</para>
        ///
        /// <para>Naming every panel alias here is not merely description: with the tagged reference nodes, a
        /// registered picture is sent to H3 <i>only</i> when its alias occurs in the clip's prompt. This line
        /// is the mention that activates the cast, which is why a clip character 2 is not in does not name
        /// them.</para>
        /// </summary>
        private static string BuildReferenceLine(int panels1, int panels2, CastInfo cast)
        {
            var total = Math.Max(1, panels1) + Math.Max(0, panels2);

            var sb = new StringBuilder(ReferenceLinePrefix).Append(' ');
            sb.Append(total == 1
                ? "the attached picture is a studio reference photograph of the cast, not a frame of the video. "
                : $"the {total} attached pictures are separate studio reference photographs of the cast, not frames of the video. ");

            sb.Append(DescribeCharacter(1, Math.Max(1, panels1), cast.SexOf(1)));
            if (panels2 > 0)
                sb.Append(' ').Append(DescribeCharacter(2, panels2, cast.SexOf(2)));

            var them = panels2 > 0 ? "each of them" : "them";
            var each = panels2 > 0 ? "each person's" : "that person's";
            var they = total > 1 ? "them" : "it";
            var own = total > 1 ? "their own pictures" : "that picture";

            sb.Append($" Take ONLY {each} identity from {own} — face, facial features, hair, skin " +
                      $"and build — and keep {them} identical and unchanged from the first frame to the last. ");
            sb.Append(SceneRule).Append(' ');

            // The clothing sentence is the one part of this line that depends on how the sheets were made —
            // see CastInfo.SheetsShowWardrobe.
            sb.Append(cast.SheetsShowWardrobe
                ? $"The references DO, however, show the exact clothing the cast wears in this video: copy " +
                  $"every garment from {they} — the same items, colours, materials and details — and keep " +
                  $"the outfits identical from the first frame to the last, matching the WARDROBE LOCK below. " +
                  $"Only the plain studio background and the neutral pose are to be discarded; place {them} in " +
                  "the scene described below, dressed exactly as the references show."
                : $"Do NOT dress the cast from {they} either: place {them} in the scene described below and " +
                  $"dress {them} strictly in the outfit written there, unchanged throughout.");
            return sb.ToString();
        }

        /// <summary>
        /// Swaps <see cref="SceneRule"/> for <see cref="StereoSceneRule"/> in an <b>already-stamped</b>
        /// prompt. A no-op on a prompt that does not carry the rule, so it is safe to call on anything.
        ///
        /// <para>Done as a substitution at submit time rather than as another flag on
        /// <see cref="CastInfo"/> deliberately: a flag would only reach prompts stamped after it existed,
        /// and a queue full of clips already written and hunted would keep rendering the contradiction
        /// until every one of them was re-queued. The rule is code-written and identical in every prompt in
        /// the repository's history, so finding it by its own text is exact.</para>
        /// </summary>
        public static string MakeStereo(string? prompt) =>
            string.IsNullOrEmpty(prompt) ? prompt ?? string.Empty
                                         : prompt.Replace(SceneRule, StereoSceneRule, StringComparison.Ordinal);

        /// <summary>Names the pictures one character occupies, and insists they are one person.</summary>
        private static string DescribeCharacter(int character, int panels, string? sex)
        {
            var list = DescribeAliases(character, panels);
            // "Character 1, a man" — the sheets carry the face, but H3 reads the prompt first, and a cast whose
            // sex is only implied is a cast the model is free to re-read differently in the next clip.
            var who = sex == null ? $"Character {character}" : $"Character {character}, a {sex}";

            if (panels <= 1)
                return $"{list} is {who} — a reference sheet showing one and " +
                       "the same person from several angles, not several different people.";

            return $"{list} are {panels} separate photographs of one and the same person — {who} — " +
                   "shot from different angles (full-body front, full-body back, face close-up); " +
                   $"they are one individual, not {panels} different people.";
        }

        #endregion

        #region Wardrobe block

        /// <summary>
        /// The wardrobe as it appears inside a prompt — the header plus the block, or nothing at all when
        /// there is no wardrobe to lock.
        /// </summary>
        public static string BuildWardrobeLock(string? wardrobe, bool sheetsShowWardrobe = false)
        {
            // Blank lines are squeezed out because a blank line is what ends the block: a hand-typed wardrobe
            // with a paragraph break would otherwise leave its tail behind when the block is replaced.
            var body = Regex.Replace((wardrobe ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim(),
                                     @"\n[ \t]*\n+", "\n");
            if (body.Length == 0) return string.Empty;

            // Written into the header's own line, never as a second line: the block ends at the first blank line
            // and StripWardrobeBlock finds it by its prefix, so its shape has to stay header-then-body. The
            // extra sentence goes before the header's closing colon, which introduces the outfits themselves.
            var header = sheetsShowWardrobe
                ? WardrobeLockHeader.TrimEnd(':') +
                  ". The attached reference photographs already show the cast in these clothes, so the words " +
                  "below and the pictures agree — follow both:"
                : WardrobeLockHeader;
            return $"{header}\n{body}";
        }

        /// <summary>
        /// Removes a leading wardrobe block so a new one can replace it — the block runs from its header to
        /// the first blank line, which is exactly how <see cref="BuildWardrobeLock"/> writes it. Without this,
        /// re-queueing a chain that Analyze already stamped would stack a second block on top of the first.
        /// </summary>
        public static string StripWardrobeBlock(string? text)
        {
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').TrimStart();
            if (!t.StartsWith(WardrobeLockPrefix, StringComparison.OrdinalIgnoreCase)) return t;

            var end = t.IndexOf("\n\n", StringComparison.Ordinal);
            return end < 0 ? string.Empty : t[(end + 2)..].TrimStart();
        }

        /// <summary>
        /// Reads the outfits back out of a stamped prompt — the wardrobe block's body, without its header.
        ///
        /// <para>Taken from the prompt rather than carried alongside it so that a hand-edited prompt box is
        /// what a re-stamp or a refine pass reads: the block in the prompt is the wardrobe that clip was
        /// actually queued with, whoever last touched it.</para>
        /// </summary>
        public static string ExtractWardrobe(string? prompt)
        {
            var t = (prompt ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');
            var start = t.IndexOf(WardrobeLockPrefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return string.Empty;

            var end = t.IndexOf("\n\n", start, StringComparison.Ordinal);
            var block = end < 0 ? t[start..] : t[start..end];
            var newline = block.IndexOf('\n');
            return newline < 0 ? string.Empty : block[(newline + 1)..].Trim();
        }

        /// <summary>Matches one character's line inside a wardrobe block, however it was written.</summary>
        private static readonly Regex WardrobeCharacterLineRegex = new(
            @"^[ \t]*[-*>]*[ \t]*(?:CHARACTER|CHAR|PERSON)[ \t]*#?[ \t]*(\d+)\b[^:\r\n]*:[ \t]*(.+)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// One character's outfit, pulled out of the wardrobe block — what the sheet builder dresses that
        /// character in so the reference photographs and the prompt describe the same clothes.
        ///
        /// <para>A block with no recognisable per-character lines is one paragraph about the whole cast, and is
        /// returned whole for either character: that is how the clip prompts read it too, and dressing both
        /// from the same paragraph is the same answer they will get.</para>
        /// </summary>
        public static string OutfitFor(string? wardrobe, int character)
        {
            var block = (wardrobe ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (block.Length == 0) return string.Empty;

            var matches = WardrobeCharacterLineRegex.Matches(block);
            if (matches.Count == 0) return block;

            foreach (Match m in matches)
                if (int.TryParse(m.Groups[1].Value, out var index) && index == character)
                    return m.Groups[2].Value.Trim();

            return string.Empty;
        }

        /// <summary>
        /// Removes character 2's line from a wardrobe block for a clip they are not in. Dressing someone who
        /// is not there is an invitation to put them there.
        /// </summary>
        private static string DropAbsentWardrobeLines(string block, bool keepCharacter2)
        {
            if (keepCharacter2 || block.Length == 0) return block;

            var kept = block.Split('\n')
                            .Where(line => !Regex.IsMatch(line, @"^\s*Character\s+(?!1\b)\d+\b",
                                                          RegexOptions.IgnoreCase))
                            .ToList();
            // A block that is one unparseable paragraph has no per-character lines to drop; keep it whole
            // rather than returning a header with nothing under it.
            return kept.Count > 1 ? string.Join("\n", kept) : block;
        }

        /// <summary>One member of the cast as the wardrobe text needs them: their position, the word the
        /// prompts call them by, and — when they are not a person — what they actually are. Keeps this file
        /// free of the view model's <c>CharacterSlot</c>, and therefore of WPF, which is what lets the whole
        /// of it be exercised offline.</summary>
        /// <param name="Descriptor">"Nimbus, a fluffy little cloud". Empty for a person, whose
        /// <paramref name="Noun"/> already says everything the wardrobe block needs.</param>
        public readonly record struct CastRole(int Index, string Noun, string? Descriptor = null)
        {
            /// <summary>How the wardrobe block names them: "a man", or their own description.</summary>
            public string Describe =>
                string.IsNullOrWhiteSpace(Descriptor) ? $"a {Noun}" : Descriptor!.Trim();
        }

        /// <summary>
        /// Matches the reply's <c>CHARACTER 1: …</c> lines, however the model decorated them. The optional
        /// parenthetical and the one or two words after it are there because the request now asks for
        /// <c>CHARACTER 1 (man): …</c>, and a model echoing the shape it was given — which is the point of
        /// giving it one — answers <c>CHARACTER 1 (a man) wears: …</c> as often as not.
        /// </summary>
        private static readonly Regex WardrobeLineRegex = new(
            @"^[ \t]*[-*>#]*[ \t]*(?:CHARACTER|CHAR|PICTURE|PERSON)[ \t]*#?[ \t]*(\d+)[ \t]*(?:\([^)\r\n]*\))?(?:[ \t]+[A-Za-z]{1,8}){0,2}[ \t]*[:\-–—)]+[ \t]*(.+)$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Turns the reply into the block that goes into the prompts: one line per character, each naming the
        /// tag the video model actually resolves. A reply with no recognisable lines but some prose is kept as
        /// it stands — the block is free text by the time it reaches the prompt, and a human editing the box
        /// is under no obligation to use this shape either.
        /// </summary>
        /// <param name="dress">Only these characters' lines are kept. A model asked to dress character 2 alone
        /// will often re-state character 1 as well, and letting that through would silently re-roll an outfit a
        /// character sheet has already been built in.</param>
        public static string NormalizeWardrobe(
            string reply, IReadOnlyList<CastRole> cast, IReadOnlyList<CastRole> dress)
        {
            if (string.IsNullOrWhiteSpace(reply)) return string.Empty;

            var lines = new List<string>();
            foreach (Match m in WardrobeLineRegex.Matches(reply))
            {
                if (!int.TryParse(m.Groups[1].Value, out var index) || index < 1 || index > cast.Count) continue;
                if (dress.All(d => d.Index != index)) continue;
                var outfit = m.Groups[2].Value.Trim().TrimEnd('.', ' ');
                if (outfit.Length == 0) continue;
                // A model that answers "CHARACTER 1: wearing a red coat" would otherwise read "wears wearing".
                foreach (var lead in new[] { "wearing ", "wears ", "is wearing ", "dressed in " })
                    if (outfit.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
                    {
                        outfit = outfit[lead.Length..].TrimStart();
                        break;
                    }
                // Named by their own description rather than "a man" whenever there is one: this block is
                // the authoritative wardrobe, repeated in every clip, and telling H3 that a cloud is a man
                // in the one piece of text designed to be believed is how a cloud becomes a man.
                lines.Add($"Character {index} (<Picture {index}>, {cast[index - 1].Describe}) wears: {outfit}.");
            }

            if (lines.Count > 0)
                return string.Join("\n", lines.Distinct(StringComparer.OrdinalIgnoreCase));

            // Nothing parseable: better a paragraph repeated identically in every clip than a wardrobe
            // re-invented in each one, which is exactly what this whole step exists to stop.
            return reply.Trim();
        }

        /// <summary>Reads the character number off a line of a normalized wardrobe block.</summary>
        private static readonly Regex WardrobeBlockLineRegex = new(
            @"^[ \t]*Character[ \t]+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Folds a partial pass's lines into the block already in the box, per character — the point of a
        /// top-up being that the outfit a sheet was already built in survives it. Falls back to appending when
        /// either side is free prose rather than per-character lines.
        /// </summary>
        public static string MergeWardrobe(string existing, string added)
        {
            if (string.IsNullOrWhiteSpace(existing)) return added.Trim();
            if (string.IsNullOrWhiteSpace(added)) return existing.Trim();

            var byCharacter = new SortedDictionary<int, string>();
            var loose = new List<string>();
            foreach (var block in new[] { existing, added })
                foreach (var line in block.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    if (line.Trim().Length == 0) continue;
                    var m = WardrobeBlockLineRegex.Match(line);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out var index))
                        byCharacter[index] = line.Trim();   // the later block wins, which is the new pass
                    else
                        loose.Add(line.Trim());
                }

            if (byCharacter.Count == 0) return string.Join("\n", loose);
            return string.Join("\n", byCharacter.Values);
        }

        #endregion
    }
}
