using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Holds one story's <b>place, hour and light</b> still across its clips.
    ///
    /// <para><b>The failure this exists for.</b> H3 renders every clip of a chain as an independent job: it
    /// has never seen the clip before it and never will. Whatever a clip's prose does not say, the model
    /// invents — and lighting is the thing prose leaves out most. So a twelve-clip film came back with clip
    /// 3 in broad daylight and clip 4 at midnight, clip 6 indoors and clip 7 in a street, with nothing in
    /// the story asking for any of it. Nobody wrote those cuts; they are what "unspecified" looks like
    /// twelve times over.</para>
    ///
    /// <para><b>Why the SETTING line was not enough.</b> The beat sheet already produced one sentence of
    /// setting and every clip request restated it. That line is global, so it cannot describe a story that
    /// genuinely moves — and a writer handed a global line and a beat that happens somewhere else resolves
    /// the contradiction per clip, silently. What was missing was not a stronger instruction but a
    /// <i>plan</i>: where each individual clip is, in what light, decided once for the whole chain.</para>
    ///
    /// <para><b>The shape.</b> The beat sheet is asked to end each beat with
    /// <c>[EXT | place | time of day | weather and light]</c>. This class turns those rows into one
    /// <see cref="Environment"/> per clip, and — the part that actually does the work — <b>repairs them
    /// deterministically</b>:</para>
    /// <list type="bullet">
    /// <item>a missing or unreadable field <b>carries forward</b> from the clip before it, so a model that
    /// stops writing the suffix half way down the list costs nothing;</item>
    /// <item>a place that is the previous place said differently is <b>rewritten to the previous wording</b>,
    /// so "the alley" and "the rain-slicked alley behind the club" cannot become two locations;</item>
    /// <item>the clock only ever moves <b>forward</b> (with one legal wrap, night → dawn), so a chain cannot
    /// return to the afternoon after dark;</item>
    /// <item>and it only moves at all where the story <b>says time passes</b>. A chain is N × 15 seconds of
    /// continuous action — two minutes, not a day — and a model asked for the hour of each beat writes a
    /// sunrise into it anyway: one observed eight-beat fight went midday, morning, late morning, afternoon,
    /// afternoon, late afternoon, late afternoon, evening. Nothing in that story mentioned time at all.</item>
    /// <item>when place and hour are unchanged, the light and weather wording is <b>forced to the previous
    /// clip's words</b>, because two descriptions of the same light are two lights to a video model.</item>
    /// </list>
    ///
    /// <para>The result is used twice, and the difference matters. <see cref="WriterBlock"/> goes to the
    /// language model that writes the clip — rules, phrased as rules. <see cref="SceneSentence"/> is
    /// written into the rendered description itself, in code, phrased as scene description, because that
    /// is the only text H3 actually reads and the only way two clips can be guaranteed the same words for
    /// the same place. <see cref="Contradiction"/> catches a body that wrote the opposite anyway.</para>
    /// </summary>
    public static class StoryContinuity
    {
        /// <summary>One clip's environment: inside or out, where, at what hour, in what light.</summary>
        /// <param name="Interior">True for INT. Interior and exterior are tracked apart from the place
        /// because a story moves between them within one location — a bar and the street outside it.</param>
        /// <param name="Place">The location, in the wording every clip that shares it uses.</param>
        /// <param name="TimeOfDay">One of <see cref="Clock"/>, never free text.</param>
        /// <param name="Light">Weather and light, in the wording every clip that shares it uses.</param>
        public readonly record struct Environment(bool Interior, string Place, string TimeOfDay, string Light)
        {
            /// <summary>An environment that says nothing — what a chain with no plan is held to, which is
            /// nothing at all. Used in place of <c>default</c>, whose strings are null.</summary>
            public static readonly Environment None = new(false, string.Empty, string.Empty, string.Empty);

            /// <summary>The fields as text, never null: a <c>default</c> struct reaches these members from
            /// every caller that has no plan for a clip.</summary>
            public string Where => Place ?? string.Empty;
            public string Hour => TimeOfDay ?? string.Empty;
            public string Lighting => Light ?? string.Empty;

            public bool IsEmpty => Where.Length == 0 && Hour.Length == 0 && Lighting.Length == 0;

            /// <summary>Whether two clips are in the same place, at the same hour, in the same light — the
            /// question that decides whether the writer is told to hold still or to move.</summary>
            public bool SameAs(Environment other) =>
                Interior == other.Interior &&
                string.Equals(Where, other.Where, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Hour, other.Hour, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Lighting, other.Lighting, StringComparison.OrdinalIgnoreCase);

            /// <summary>The one-line form used in logs and in the writer's rules.</summary>
            public string Describe()
            {
                var parts = new List<string> { Interior ? "INT" : "EXT" };
                if (Where.Length > 0) parts.Add(Where);
                if (Hour.Length > 0) parts.Add(Hour);
                if (Lighting.Length > 0) parts.Add(Lighting);
                return string.Join(" | ", parts);
            }
        }

        /// <summary>
        /// The hours a story may be told in, in order. A closed list rather than free text because the
        /// whole point is comparison: "late evening", "after dark" and "night-time" have to be one hour or
        /// the monotonic check has nothing to compare.
        /// </summary>
        public static readonly string[] Clock =
        {
            "dawn", "morning", "midday", "afternoon", "evening", "dusk", "night", "late night",
        };

        /// <summary>What each hour is called when it is written into a rendered description. The clock's
        /// own words read as labels; these read as scene.</summary>
        private static readonly Dictionary<string, string> ClockProse = new(StringComparer.OrdinalIgnoreCase)
        {
            ["dawn"] = "at dawn, in the first grey light before sunrise",
            ["morning"] = "in the morning, in clear daylight",
            ["midday"] = "at midday, in hard overhead daylight",
            ["afternoon"] = "in the afternoon, in full daylight",
            ["evening"] = "in the evening, in the last low sunlight",
            ["dusk"] = "at dusk, after sunset, in failing blue light",
            ["night"] = "at night, in full darkness with no daylight anywhere",
            ["late night"] = "in the small hours of the night, in full darkness with no daylight anywhere",
        };

        /// <summary>How a written hour is recognised. Longest phrases first — "late night" before "night",
        /// "early morning" before "morning" — because the first match wins.</summary>
        private static readonly (string Pattern, string Hour)[] ClockWords =
        {
            ("late night", "late night"), ("small hours", "late night"), ("after midnight", "late night"),
            ("midnight", "late night"), ("witching", "late night"),
            ("pre-dawn", "dawn"), ("predawn", "dawn"), ("first light", "dawn"), ("sunrise", "dawn"),
            ("daybreak", "dawn"), ("dawn", "dawn"),
            ("early morning", "morning"), ("mid-morning", "morning"), ("morning", "morning"),
            ("high noon", "midday"), ("noon", "midday"), ("midday", "midday"), ("mid-day", "midday"),
            ("early afternoon", "afternoon"), ("late afternoon", "afternoon"), ("afternoon", "afternoon"),
            ("golden hour", "evening"), ("sunset", "dusk"), ("sundown", "dusk"), ("twilight", "dusk"),
            ("sun sets", "dusk"), ("sun setting", "dusk"), ("sun goes down", "dusk"),
            ("sun rises", "dawn"), ("sun rising", "dawn"), ("sun comes up", "dawn"),
            ("dusk", "dusk"), ("gloaming", "dusk"),
            ("early evening", "evening"), ("evening", "evening"),
            ("after dark", "night"), ("nightfall", "night"), ("night", "night"), ("nocturnal", "night"),
            ("day", "afternoon"), ("daytime", "afternoon"), ("daylight", "afternoon"),
        };

        /// <summary>
        /// What a beat has to say for the clock to be allowed to move under it.
        ///
        /// <para>Deliberately about elapsed time, not about light: "the sun sets" is a thing that can happen
        /// inside one continuous scene and a model writes it as scenery, while "the next morning" is a cut.
        /// A beat that says none of this is a beat happening at the hour the one before it happened.</para>
        /// </summary>
        private static readonly Regex TimeJumpRegex = new(
            @"\b(later|afterwards?|after\s+a\s+while|hours?\s+later|minutes\s+later|days?\s+later|" +
            @"weeks?\s+later|that\s+(?:night|evening|afternoon|morning)|the\s+(?:next|following)\s+" +
            @"(?:day|morning|night|evening|afternoon)|by\s+(?:then|now|nightfall|dawn|morning|midnight)|" +
            @"time\s+passes|some\s+time|eventually|meanwhile|hours\s+pass|the\s+following)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The environment suffix a beat carries — <c>[EXT | alley | night | heavy rain]</c>.
        /// Brackets or parentheses, INT/EXT optional, any of the three separators, so a model that writes
        /// it slightly wrong is still understood.</summary>
        private static readonly Regex SuffixRegex = new(
            @"[\[(]\s*(?<body>(?:INT|EXT|INT/EXT|INTERIOR|EXTERIOR)\s*[|/·,-][^\]\)]*|[^\]\)]*\|[^\]\)]*)\s*[\])]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex InteriorWordRegex = new(
            @"^\s*(INT|INTERIOR|INSIDE|INDOORS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ExteriorWordRegex = new(
            @"^\s*(EXT|EXTERIOR|OUTSIDE|OUTDOORS?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        #region Reading the beat sheet

        /// <summary>
        /// Splits a beat line into the beat itself and the environment suffix the beat sheet was asked to
        /// end it with. A line with no suffix comes back unchanged with an empty environment, which is the
        /// case every carry-forward below exists for.
        /// </summary>
        public static (string Text, string Environment) SplitBeat(string beatLine)
        {
            var text = (beatLine ?? string.Empty).Trim();
            if (text.Length == 0) return (text, string.Empty);

            var m = SuffixRegex.Match(text);
            if (!m.Success) return (text, string.Empty);

            var env = m.Groups["body"].Value.Trim();
            // A bracketed aside that is not an environment ("(she is still holding the knife)") has no
            // separator in it and no INT/EXT in front, so it is left where it was written.
            if (env.Length == 0) return (text, string.Empty);

            var stripped = text[..m.Index].TrimEnd(' ', '\t', '-', '–', '—', ',', ';');
            return stripped.Length == 0 ? (text, string.Empty) : (stripped, env);
        }

        /// <summary>
        /// One environment per clip, repaired into a sequence that can actually be rendered — see the class
        /// remarks for what "repaired" means and why each repair is there.
        /// </summary>
        /// <param name="rows">The raw environment strings, one per clip, in clip order. Empty entries are
        /// expected and are what the carry-forward is for.</param>
        /// <param name="settingLine">The beat sheet's SETTING sentence, used as the first clip's
        /// environment when it wrote no suffix at all. Without it a chain whose model ignored the whole
        /// instruction would have nothing to hold still <i>to</i>.</param>
        /// <param name="beatTexts">The beats themselves, in the same order, when the caller has them. They
        /// are read for one thing only: whether this beat says that time passed. Without them the clock is
        /// held at the first clip's hour for the whole chain, which is the right answer for a chain of
        /// consecutive 15-second takes and the wrong one only for a story that skips a night.</param>
        public static IReadOnlyList<Environment> Plan(
            IReadOnlyList<string> rows, string? settingLine, IReadOnlyList<string>? beatTexts = null)
        {
            var plan = new List<Environment>(rows.Count);
            if (rows.Count == 0) return plan;

            // The seed: the first row that said anything, or the SETTING line read as one. A chain with
            // neither is left empty and nothing downstream stamps anything — better than inventing a
            // location the story never mentioned.
            var seed = rows.Select(Read).FirstOrDefault(e => !e.IsEmpty);
            if (seed.IsEmpty) seed = Read(settingLine ?? string.Empty);
            if (seed.IsEmpty) seed = Environment.None;

            var previous = seed;
            var clock = ClockIndex(seed.Hour);
            var wrapped = false;

            // Every place the chain has been in, in the words it was first written in. A story that goes
            // back to where it started ("the alley" in beat 9, "the alley behind the club" in beat 1) has
            // to go back to the same words as well, or it is a third location.
            var known = new List<(string Place, string Light)>();

            for (var i = 0; i < rows.Count; i++)
            {
                var raw = rows[i];
                // The clock may move under this beat only where the story says it does. Clip 1 sets it.
                var mayMove = i == 0 ||
                              (beatTexts != null && i < beatTexts.Count &&
                               TimeJumpRegex.IsMatch(beatTexts[i] ?? string.Empty));
                var read = Read(raw);

                var interior = raw.Length == 0 ? previous.Interior : read.Interior;
                if (raw.Length > 0 && !InteriorWordRegex.IsMatch(raw) && !ExteriorWordRegex.IsMatch(raw))
                    interior = previous.Interior;

                var place = read.Where.Length > 0 ? read.Where : previous.Where;
                // The same place said two ways is the commonest way a chain "moves": keep the wording it
                // was first given, whether that was the clip before or ten clips ago.
                var seen = known.FirstOrDefault(k => SamePlace(place, k.Place));
                if (seen.Place != null) place = seen.Place;

                var hour = read.Hour.Length > 0 ? read.Hour : previous.Hour;
                var index = ClockIndex(hour);
                var refused = false;

                // A different hour in a beat that never said time passed is the model varying a detail,
                // not the story moving: two minutes of continuous action are one hour of the day.
                if (!mayMove && index >= 0 && clock >= 0 && index != clock)
                {
                    hour = previous.Hour;
                    index = clock;
                    refused = true;
                }

                if (index >= 0 && clock >= 0 && index < clock)
                {
                    // Backwards. The one legal exception is the wrap the clock actually has — a chain that
                    // runs through the night into the morning — and it may only be taken once.
                    var isWrap = !wrapped && clock >= ClockIndex("night") && index <= ClockIndex("morning");
                    if (isWrap) wrapped = true;
                    else { hour = previous.Hour; refused = true; }
                }
                if (ClockIndex(hour) is var h && h >= 0) clock = h;

                // A refused hour takes its light with it: "afternoon | bright sun" rejected back to night
                // must not leave the sun behind, which would light the clip in exactly the way the refusal
                // was for. A place the chain has been in before brings its own light back instead.
                var light = refused
                    ? (seen.Place != null ? seen.Light : previous.Lighting)
                    : read.Lighting.Length > 0 ? read.Lighting : previous.Lighting;
                if (refused && light.Length == 0) light = previous.Lighting;
                // Same place, same hour ⇒ the same light, in the same words. Two wordings of one light are
                // two lights once they are rendered a clip apart.
                if (place == previous.Where && interior == previous.Interior &&
                    string.Equals(hour, previous.Hour, StringComparison.OrdinalIgnoreCase))
                    light = previous.Lighting;

                previous = new Environment(interior, place, hour, light);
                plan.Add(previous);
                if (place.Length > 0 && !known.Any(k => SamePlace(place, k.Place)))
                    known.Add((place, light));
            }

            return plan;
        }

        /// <summary>Reads one written environment — <c>EXT | the alley | night | heavy rain</c>, or the
        /// SETTING sentence — into its four fields. Anything unreadable comes back empty rather than
        /// guessed, so the carry-forward in <see cref="Plan"/> is what fills it.</summary>
        public static Environment Read(string? raw)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0) return default;

            var interior = InteriorWordRegex.IsMatch(text);
            var explicitSide = interior || ExteriorWordRegex.IsMatch(text);

            var fields = text.Split(new[] { '|', '·' }, StringSplitOptions.TrimEntries)
                             .Where(f => f.Length > 0)
                             .ToList();
            if (explicitSide && fields.Count > 0)
            {
                // "EXT alley" — the side word and the place share a field.
                var head = InteriorWordRegex.Replace(fields[0], string.Empty);
                head = ExteriorWordRegex.Replace(head, string.Empty).Trim(' ', '.', ',', '-', '–', '—', ':');
                if (head.Length > 0) fields[0] = head;
                else fields.RemoveAt(0);
            }

            if (fields.Count == 0) return new Environment(interior, string.Empty, HourIn(text), string.Empty);

            // One unsplit sentence (the SETTING line): the whole of it is the place, and the hour is
            // whatever the sentence names.
            if (fields.Count == 1)
                return new Environment(interior, Tidy(fields[0]), HourIn(fields[0]), string.Empty);

            var place = Tidy(fields[0]);
            var hour = HourIn(fields[1]);
            var light = fields.Count > 2 ? Tidy(string.Join(", ", fields.Skip(2))) : string.Empty;

            // A model that wrote the hour and the light the other way round, or wrote no hour at all.
            if (hour.Length == 0 && light.Length > 0)
            {
                hour = HourIn(light);
                if (hour.Length > 0) light = Tidy(fields[1]);
            }
            if (hour.Length == 0) hour = HourIn(text);

            return new Environment(interior, place, hour, light);
        }

        /// <summary>The clock hour a phrase names, or empty. Longest phrase wins.</summary>
        public static string HourIn(string? text)
        {
            var t = (text ?? string.Empty).ToLowerInvariant();
            if (t.Length == 0) return string.Empty;
            foreach (var (pattern, hour) in ClockWords)
                if (ClockWordRegex(pattern).IsMatch(t)) return hour;
            return string.Empty;
        }

        /// <summary>
        /// One clock word, matched on word boundaries and cached.
        ///
        /// <para>Boundaries rather than <c>Contains</c> because "afternoon" ends in "noon": a substring
        /// search read every "late afternoon" in a beat sheet as midday, and a chain whose hours are all
        /// read wrong is held perfectly still at the wrong hour.</para>
        /// </summary>
        private static Regex ClockWordRegex(string word) =>
            ClockWordCache.TryGetValue(word, out var rx)
                ? rx
                : ClockWordCache[word] = new Regex(@"\b" + Regex.Escape(word).Replace(@"\ ", @"\s+") + @"\b",
                                                  RegexOptions.Compiled);

        private static readonly Dictionary<string, Regex> ClockWordCache = new(StringComparer.Ordinal);

        /// <summary>Where an hour sits on the clock, or -1 when it is not one.</summary>
        public static int ClockIndex(string? hour) =>
            Array.FindIndex(Clock, h => string.Equals(h, hour, StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether two written places are the same place. Deliberately generous: the failure being
        /// prevented is one location becoming two, and two genuinely different rooms are rarely one an
        /// abbreviation of the other.</summary>
        private static bool SamePlace(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

            var x = Normalize(a);
            var y = Normalize(b);
            if (x.Length == 0 || y.Length == 0) return false;
            return x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal);

            static string Normalize(string s) =>
                Regex.Replace(s.ToLowerInvariant(), @"\b(the|a|an|of|in|at|on|inside|outside)\b|[^\w ]", " ")
                     .Replace("  ", " ").Trim();
        }

        private static string Tidy(string s) =>
            s.Trim(' ', '\t', '.', ',', ';', ':', '-', '–', '—');

        #endregion

        #region What the writer is told

        /// <summary>
        /// The CONTINUITY block for one clip's request — the hour and the place as facts, and, when they
        /// are the ones the clip before had, an explicit instruction not to re-decide them.
        ///
        /// <para>The negative list is not padding. A writer told only "it is night" still writes "sunlight
        /// catches the rail" in the third shot, because it is describing shots one at a time and the
        /// sentence is a good sentence. Naming the specific things that contradict the hour is what stops
        /// it.</para>
        /// </summary>
        public static string WriterBlock(Environment env, Environment? previous)
        {
            if (env.IsEmpty) return string.Empty;

            var where = env.Interior ? "INTERIOR — inside, under a roof" : "EXTERIOR — outdoors";
            var lines = new List<string>
            {
                "CONTINUITY — this clip's place, hour and light are already decided and are NOT yours to " +
                "change, invent or improve:",
                $"  {where}",
            };
            if (env.Where.Length > 0) lines.Add($"  LOCATION: {env.Where}");
            if (env.Hour.Length > 0) lines.Add($"  TIME OF DAY: {env.Hour}");
            if (env.Lighting.Length > 0) lines.Add($"  LIGHT AND WEATHER: {env.Lighting}");

            if (previous is { } prev && !prev.IsEmpty)
            {
                if (prev.SameAs(env))
                    lines.Add(
                        "This is the SAME location, the SAME hour and the SAME light as the clip before " +
                        "this one, which the viewer has just watched. Nothing about the environment has " +
                        "moved between them: do not relocate the scene, do not let time pass, do not " +
                        "change the weather, and do not re-light it. Describe the place in the same words " +
                        "you are given above.");
                else
                    lines.Add(
                        $"The clip before this one was {prev.Describe()}. The story MOVES between them, so " +
                        "establish this new place clearly in [Shot 1] — and nothing of the previous " +
                        "location, its light or its weather appears anywhere in this clip.");
            }

            lines.Add(Forbidden(env));
            return string.Join("\n", lines);
        }

        /// <summary>The wording that keeps an hour from being contradicted three shots later.</summary>
        private static string Forbidden(Environment env)
        {
            var index = ClockIndex(env.Hour);
            var hour = index >= ClockIndex("dusk")
                ? "It is dark. Write no sunlight, no daylight, no sun, no blue sky and no bright overcast " +
                  "sky anywhere in this clip — every light in it comes from a lamp, a screen, a fire, a " +
                  "sign, the moon or the sky's own darkness."
                : index >= 0
                    ? "It is daylight. Write no moonlight, no night, no darkness and no lit streetlamps " +
                      "standing in for the light anywhere in this clip."
                    : string.Empty;

            var side = env.Interior
                ? "The whole clip happens indoors: no open sky, no street, no weather falling on anyone."
                : "The whole clip happens outdoors, in this weather, in this light.";

            return string.IsNullOrEmpty(hour) ? side : hour + " " + side;
        }

        #endregion

        #region What the renderer is given

        /// <summary>
        /// The environment as a sentence for the rendered description — the words H3 itself reads.
        ///
        /// <para>Written in code rather than left to the writer for the same reason the wardrobe lock is:
        /// it is the only text in a chain that is identical, word for word, in every clip that shares an
        /// environment. Two clips described as "a rain-slicked alley at night" and "a dark wet alleyway"
        /// are two places to a video model that never sees them together.</para>
        /// </summary>
        public static string SceneSentence(Environment env)
        {
            if (env.IsEmpty) return string.Empty;

            var parts = new List<string> { env.Interior ? "Interior" : "Exterior" };
            if (env.Where.Length > 0) parts.Add(env.Where);

            var hour = env.Hour.Length > 0 && ClockProse.TryGetValue(env.Hour, out var prose)
                ? prose
                : env.Hour;
            if (hour.Length > 0) parts.Add(hour);
            if (env.Lighting.Length > 0) parts.Add(env.Lighting);

            var tail = string.Join(", ", parts.Skip(1));
            if (tail.Length > 0) tail = char.ToUpperInvariant(tail[0]) + tail[1..];

            return string.Join(". ", new[] { parts[0], tail }.Where(p => p.Length > 0)).TrimEnd('.') + ".";
        }

        /// <summary>The label the stamped sentence is written under, so it can be found again.</summary>
        private const string SceneMarker = "Continuity of place and time: ";

        /// <summary>
        /// Writes the environment into one clip's rendered description, immediately ahead of
        /// <c>[Shot 1]</c> so the medium still leads and the shots still follow.
        ///
        /// <para>Idempotent: a body already carrying a stamp has it replaced, so re-analysing or hand-editing
        /// a chain never stacks two environments on one clip.</para>
        /// </summary>
        public static string StampScene(string body, Environment env)
        {
            var text = (body ?? string.Empty);
            if (text.Length == 0) return text;

            text = StripScene(text);
            var sentence = SceneSentence(env);
            if (sentence.Length == 0) return text;

            var stamp = SceneMarker + sentence + " ";

            // Ahead of the first shot marker where there is one — the style clause and the mode words come
            // before it and have to keep coming first.
            var shot = text.IndexOf("[Shot", StringComparison.OrdinalIgnoreCase);
            if (shot > 0) return text[..shot] + stamp + text[shot..];

            var label = text.IndexOf("integrated_multimodal_description:", StringComparison.OrdinalIgnoreCase);
            if (label < 0) return stamp + text;
            var after = label + "integrated_multimodal_description:".Length;
            return text[..after] + " " + stamp + text[after..].TrimStart();
        }

        /// <summary>Removes a stamp written by <see cref="StampScene"/>, whatever it said.</summary>
        public static string StripScene(string? body) =>
            Regex.Replace(body ?? string.Empty,
                          Regex.Escape(SceneMarker) + @"[^\[\n]*", string.Empty);

        #endregion

        #region Catching a clip that did it anyway

        /// <summary>Daylight words that cannot appear in a clip set after dusk.</summary>
        private static readonly string[] DaylightWords =
        {
            "sunlight", "sunlit", "sunbeam", "sunshine", "broad daylight", "bright daylight", "midday sun",
            "morning light", "afternoon light", "blue sky", "sunny", "in the sun ", "overhead sun",
        };

        /// <summary>Night words that cannot appear in a clip set in daylight.</summary>
        private static readonly string[] NightWords =
        {
            "moonlight", "moonlit", "midnight", "starlight", "starlit", "pitch black", "pitch-black",
            "in the dark of night", "under the night sky",
        };

        /// <summary>
        /// Why this clip contradicts its own environment, or null when it does not — the rejection reason
        /// the clip writer is handed so it writes the clip once more.
        ///
        /// <para>Deliberately narrow. It fires only on words that cannot be true at the hour the plan
        /// assigned, because a check that also guessed at mood or architecture would reject far more good
        /// clips than bad ones — and the failure being caught here is exactly the blunt one: a clip written
        /// in sunlight inside a story that has been at night since beat 4.</para>
        /// </summary>
        public static string? Contradiction(string? body, Environment env)
        {
            var text = (body ?? string.Empty).ToLowerInvariant();
            if (text.Length == 0 || env.Hour.Length == 0) return null;

            var index = ClockIndex(env.Hour);
            if (index < 0) return null;

            if (index >= ClockIndex("dusk"))
            {
                var hit = DaylightWords.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal));
                if (hit != null)
                    return $"it is set at {env.Hour} and the previous clips are too, but it describes " +
                           $"\"{hit.Trim()}\" — daylight in a scene that is dark. Write the same action in " +
                           $"the light it actually happens in: {env.Describe()}.";
            }
            else
            {
                var hit = NightWords.FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal));
                if (hit != null)
                    return $"it is set at {env.Hour}, in daylight, but it describes " +
                           $"\"{hit.Trim()}\" — night in a scene that is lit by the sun. Write the same " +
                           $"action in the light it actually happens in: {env.Describe()}.";
            }

            return null;
        }

        #endregion

        /// <summary>The plan as the log shows it: one line per clip, and the changes called out, because a
        /// move the story did not ask for is something to see before the render rather than after.</summary>
        public static IEnumerable<string> Describe(IReadOnlyList<Environment> plan)
        {
            if (plan.Count == 0)
            {
                yield return "Continuity: the beat sheet named no place or time — every clip will decide " +
                             "its own, which is where daylight in one clip and midnight in the next comes from.";
                yield break;
            }

            var moves = 0;
            for (var i = 0; i < plan.Count; i++)
            {
                var changed = i > 0 && !plan[i].SameAs(plan[i - 1]);
                if (changed) moves++;
                yield return $"  clip {i + 1}: {plan[i].Describe()}" + (changed ? "   ← moves" : string.Empty);
            }

            yield return moves == 0
                ? $"Continuity: one environment for all {plan.Count} clips — {plan[0].Describe()}."
                : $"Continuity: {plan.Count} clips across {moves + 1} environment(s); every clip in between " +
                  "is held to the one before it.";
        }
    }
}
