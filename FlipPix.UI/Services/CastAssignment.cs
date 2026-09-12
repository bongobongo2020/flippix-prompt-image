using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Which of the run's photographed people plays which character of a story.
    ///
    /// <para><b>Why this is not simply "photo 1 plays character 1".</b> A story's character numbers are the
    /// order it was cast in, and that order is the story's, not the user's: in <i>ArenaBlood_01</i> character 1 is
    /// Lyris and character 2 is Cassian, and the saved clips call <c>&lt;Picture 1&gt;</c> "she" and lock her red
    /// silk skirt onto that tag. Put a man's photo in slot 1 and he plays Lyris, in her skirt. Observed on the
    /// first cast run, 2026-09-11. So the photos are matched to the story's characters by sex, story by story.</para>
    ///
    /// <para><b>Where a story's sexes come from.</b> For saved prompts, what they were written for: the recorded
    /// cast nouns, else the wardrobe block's own "Character N (&lt;Picture N&gt;, a woman)" lines — which every
    /// saved chain carries, including the imported ones that record no nouns. For a story being written fresh,
    /// the cast pass's reading of the story, in its order.</para>
    ///
    /// <para>WPF-free, so the harness covers it.</para>
    /// </summary>
    public static class CastAssignment
    {
        public const string Male = "Male";
        public const string Female = "Female";

        /// <summary>"Male"/"Female" for any of the words the app uses for a person's sex, else null.</summary>
        public static string? SexOf(string? word)
        {
            var w = (word ?? string.Empty).Trim().ToLowerInvariant();
            return w switch
            {
                "female" or "woman" or "girl" or "she" => Female,
                "male" or "man" or "boy" or "he" => Male,
                _ => null
            };
        }

        private static readonly Regex WardrobeLine = new(
            @"^[ \t]*Character[ \t]+(\d+)[ \t]*\(\s*<Picture[ \t]*\d+>\s*,\s*(?:an?\s+)?([A-Za-z]+)",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The sex each of characters 1 and 2 has in a wardrobe block, null where it says nothing.</summary>
        public static string?[] FromWardrobe(string? wardrobe)
        {
            var sexes = new string?[2];
            foreach (Match m in WardrobeLine.Matches(wardrobe ?? string.Empty))
                if (int.TryParse(m.Groups[1].Value, out var n) && n is 1 or 2)
                    sexes[n - 1] ??= SexOf(m.Groups[2].Value);
            return sexes;
        }

        /// <summary>The sexes recorded as nouns ("man", "woman"), in character order.</summary>
        public static string?[] FromNouns(IReadOnlyList<string>? nouns)
        {
            var sexes = new string?[2];
            for (var i = 0; i < Math.Min(2, nouns?.Count ?? 0); i++) sexes[i] = SexOf(nouns![i]);
            return sexes;
        }

        /// <summary>Each slot's first known answer, in the order given.</summary>
        public static string?[] FirstKnown(params string?[][] sources)
        {
            var sexes = new string?[2];
            foreach (var source in sources)
                for (var i = 0; i < 2; i++)
                    sexes[i] ??= source.Length > i ? source[i] : null;
            return sexes;
        }

        /// <summary>
        /// Which slot each photographed member plays: member 1 in slot 1 and member 2 in slot 2, unless crossing
        /// them over gets strictly fewer people into a part of the other sex. A slot whose sex is not known
        /// counts as a match either way, so nothing moves on a guess.
        /// </summary>
        /// <param name="photos">(member number 1 or 2, their sex) for every member with a photo.</param>
        /// <param name="need">The sex of the story's character 1 and 2; null where unknown.</param>
        public static Dictionary<int, int> Decide(IReadOnlyList<(int Member, string Sex)> photos, string?[] need)
        {
            var straight = Mismatches(photos, need, crossed: false);
            var crossed = Mismatches(photos, need, crossed: true);
            var cross = crossed < straight;
            return photos.ToDictionary(p => p.Member, p => cross ? 3 - p.Member : p.Member);
        }

        public static int Mismatches(IReadOnlyList<(int Member, string Sex)> photos, string?[] need, bool crossed) =>
            photos.Count(p =>
            {
                var slot = crossed ? 3 - p.Member : p.Member;
                var wanted = need.Length >= slot ? need[slot - 1] : null;
                return wanted != null && !string.Equals(wanted, SexOf(p.Sex), StringComparison.Ordinal);
            });
    }
}
