using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Re-dressing a written clip: the same clip, with one or both characters' clothing wording swapped for a
    /// new outfit and every other word left alone. ⚡ H3 Express uses it when a saved story is rendered with a
    /// cast of the user's own, in their own clothes.
    ///
    /// <para><b>Why a model call and not a find-and-replace.</b> The clip writer is told to quote the locked
    /// wardrobe verbatim, and mostly does not: across the chains saved on this machine only 79 of 1,685
    /// appearances of a character carried the outfit word for word, another 865 carried only its first
    /// garment, and the rest reworded it ("in grey denim", "his jeans"). A text substitution would leave most
    /// of the old clothes standing in the prose while the wardrobe lock and the sheets show the new ones.</para>
    ///
    /// <para><b>Why the checks are strict.</b> The edit is supposed to be invisible outside the clothes. A
    /// reply that lost a shot, moved a cut, dropped a picture tag or grew by half has rewritten the clip, not
    /// re-dressed it — and the caller keeps the original rather than render that.</para>
    ///
    /// <para>WPF-free, so the harness can exercise it without the tab.</para>
    /// </summary>
    public static class ClipRedress
    {
        /// <summary>One character's clothing change. <see cref="From"/> may be empty when the saved set carried
        /// no wardrobe — the prose is then the only record of the old outfit.</summary>
        public sealed record Change(int Character, string Noun, string From, string To);

        public const string SystemPrompt =
            "You are a continuity editor for video-generation prompts. You change the clothing a prompt describes " +
            "and nothing else. You reply with the complete edited prompt only — no preamble, no notes, no " +
            "markdown, no code fences.";

        private static readonly Regex ShotMarker = new(@"\[\s*Shot\s+\d+\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Timestamp = new(@"\b\d{2}:\d{2}\.\d{3}\b", RegexOptions.Compiled);
        private static readonly string[] Fields =
            { "integrated_multimodal_description:", "overall_soundscape:", "non_diegetic_music:" };

        /// <summary>The user message for one clip. <paramref name="rejection"/> is empty on the first attempt.</summary>
        public static string BuildRequest(string clip, IReadOnlyList<Change> changes, string rejection)
        {
            var sb = new StringBuilder();
            sb.Append("The video prompt below was written for a cast who have since changed clothes. Edit it so ")
              .Append("it describes the NEW clothing.\n\n");

            foreach (var c in changes)
            {
                sb.Append($"<Picture {c.Character}> is a {c.Noun}.\n");
                sb.Append(c.From.Length > 0
                    ? $"  OLD outfit: {c.From.Trim().TrimEnd('.')}.\n"
                    : "  OLD outfit: whatever the prompt currently says they wear.\n");
                sb.Append($"  NEW outfit: {c.To.Trim().TrimEnd('.')}.\n");
            }

            var others = new[] { 1, 2 }.Where(n => changes.All(c => c.Character != n)).ToList();
            sb.Append("\nRules:\n")
              .Append("1. Every word about what the character(s) above wear — garments, footwear, accessories, their ")
              .Append("colours, materials and fit — must describe the NEW outfit. Where the action touches a garment ")
              .Append("of the old outfit (tugging a jacket, a skirt swirling), point it at a matching item of the new ")
              .Append("outfit, or keep the action and drop the garment.\n")
              .Append("2. Change NOTHING else. Keep every other word as it is: the three field labels, every [Shot n] ")
              .Append("marker and every timestamp, every <Picture 1> / <Picture 2> tag, the camera, the action, the ")
              .Append("setting, the lighting, the sound and the music.\n")
              .Append("3. Do not add or change hair, face, skin, body, age or expression.\n");
            if (others.Count > 0)
                sb.Append($"4. <Picture {others[0]}>'s clothing is not changing — leave every word about it exactly as it is.\n");
            sb.Append("Reply with the complete edited prompt, starting with integrated_multimodal_description:\n");

            if (rejection.Length > 0)
                sb.Append($"\nYour previous attempt was rejected: {rejection} Start again from the original below.\n");

            sb.Append("\nTHE PROMPT:\n").Append(clip.Trim());
            return sb.ToString();
        }

        /// <summary>
        /// Null when <paramref name="edited"/> is the same clip in different clothes; otherwise the reason it is
        /// not, phrased for the retry.
        /// </summary>
        public static string? Validate(string original, string edited)
        {
            if (string.IsNullOrWhiteSpace(edited))
                return "the reply was empty.";

            foreach (var field in Fields)
                if (original.Contains(field, StringComparison.OrdinalIgnoreCase) &&
                    !edited.Contains(field, StringComparison.OrdinalIgnoreCase))
                    return $"it lost the {field} field. Keep all three field labels.";

            if (ShotMarker.Matches(original).Count != ShotMarker.Matches(edited).Count)
                return $"it has {ShotMarker.Matches(edited).Count} [Shot n] markers where the prompt has " +
                       $"{ShotMarker.Matches(original).Count}. Keep every shot.";

            var before = Timestamp.Matches(original).Select(m => m.Value).ToList();
            var after = Timestamp.Matches(edited).Select(m => m.Value).ToList();
            if (!before.SequenceEqual(after))
                return "its timestamps differ from the prompt's. Copy every timestamp exactly.";

            foreach (var tag in new[] { "<Picture 1>", "<Picture 2>" })
                if (original.Contains(tag, StringComparison.Ordinal) && !edited.Contains(tag, StringComparison.Ordinal))
                    return $"it dropped the {tag} tag. Keep every tag exactly as written.";

            var ratio = (double)edited.Length / Math.Max(1, original.Length);
            if (ratio < 0.75 || ratio > 1.35)
                return $"it is {ratio:P0} of the prompt's length — rewrite only the clothing words, not the clip.";

            return null;
        }

        /// <summary>
        /// Identifies one re-dressed version of a story's clips: the clips it was made from and the outfits it
        /// was made for. An edit to the saved clips, or a different outfit, is a different version.
        /// </summary>
        public static string VariantKey(IEnumerable<string> baseClips, IEnumerable<Change> changes)
        {
            var text = string.Join("", baseClips) + "" +
                       string.Join("", changes.OrderBy(c => c.Character)
                                                    .Select(c => $"{c.Character}:{c.Noun}:{Collapse(c.To)}"));
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        }

        /// <summary>The request for what a photographed person is wearing — one line, in the wardrobe pass's shape.</summary>
        public static string OutfitRequest(int character, string noun) =>
            $"Image role: this photograph shows the person who plays Character {character}, a {noun}, in a video. " +
            "Describe exactly what they are wearing in it, so they can be dressed the same way in every shot. " +
            "Reply with exactly this line and nothing else:\n" +
            $"CHARACTER {character} ({noun}): <outfit>\n\n" +
            "The <outfit> is ONE sentence of at most 45 words naming every visible garment and worn item — top, " +
            "bottom or dress, outer layer, footwear, headwear, gloves, eyewear, jewellery, bag, belt — each with " +
            "its colour and its material. Only what is actually visible in the photograph; where the photo is " +
            "cropped, say nothing about what cannot be seen rather than guessing. Write only clothing and worn " +
            "accessories: no face, hair, skin, build, age, name, pose, expression or background.";

        public const string OutfitSystemPrompt =
            "You are a costume supervisor recording the wardrobe of an actor from a photograph. You reply with " +
            "nothing but the wardrobe line you were asked for — no preamble, no headings, no markdown, no notes.";

        private static string Collapse(string s) =>
            string.Join(' ', (s ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
