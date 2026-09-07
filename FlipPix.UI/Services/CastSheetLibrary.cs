using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// A folder of character sheets that have already been built, and an index saying which photograph
    /// each one was built from — so loading a cast photo the app has seen before costs nothing instead of
    /// a Qwen-Image-Edit pass on the GPU.
    ///
    /// <para><b>Why an index at all.</b> A sheet's file name is <c>sheet_&lt;slot&gt;_&lt;timestamp&gt;</c>:
    /// it records which card the sheet was built on and when, and nothing whatsoever about <i>who is in
    /// it</i>. Two sheets of the same person, built a week apart, look no more related than two sheets of
    /// strangers. So the identity has to be carried beside the files, and the only identity a sheet
    /// actually has is the photograph it came from.</para>
    ///
    /// <para><b>Why the back-fill works.</b> A library that only knew about sheets built after it was
    /// written would be useless for a year. It does not have to be: the sheet builder has always logged
    /// the pairing — <c>"Character 1 (woman): generating a 1536×864 sheet from &lt;photo&gt;..."</c>,
    /// <c>"Character 1 is being dressed in the locked wardrobe: &lt;outfit&gt;"</c>, <c>"Character 1: sheet
    /// ready — &lt;sheet&gt;"</c> — and those lines are still in <c>%AppData%\FlipPix\Logs</c>.
    /// <see cref="SyncAsync"/> reads them back. On the machine this was written for that recovered the
    /// source photograph of <b>every</b> sheet in the library, and the wardrobe for almost all of them.</para>
    ///
    /// <para><b>Matching is exact, never a guess.</b> A photograph matches by full path, then by content
    /// hash, then by file name — in that order, and nothing else. There is no image comparison here: two
    /// different photographs of the same person are two different characters as far as this class is
    /// concerned, because the alternative is silently dressing a video in a stranger's sheet. The file-name
    /// tier exists only because the log lines carry names and not paths; it is the weakest tier and it says
    /// so when it is used.</para>
    /// </summary>
    public sealed class CastSheetLibrary
    {
        /// <summary>Bumped only when an old index can no longer be read. Entries are additive otherwise.</summary>
        private const int CurrentVersion = 1;

        private const string IndexFileName = "sheet-index.json";

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Action<string>? _log;
        private SheetIndex? _index;

        public CastSheetLibrary(string? folder = null, Action<string>? log = null)
        {
            Folder = string.IsNullOrWhiteSpace(folder) ? DefaultFolder : folder!;
            _log = log;
        }

        /// <summary>Where the sheets live. Beside the rest of the app's pictures, under the cast's own
        /// folder — the place the sheets were already being filed by hand.</summary>
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "flippix-images", "faces-ai", "cast", "sheets");

        public string Folder { get; }

        public string IndexPath => Path.Combine(Folder, IndexFileName);

        /// <summary>Where the sheet builder's own log lines are, for <see cref="SyncAsync"/>.</summary>
        private static string LogFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FlipPix", "Logs");

        // ── The record ──────────────────────────────────────────────────────────────────────────────

        /// <summary>One sheet, and what is known about where it came from.</summary>
        public sealed class Entry
        {
            /// <summary>File name inside <see cref="Folder"/>. Not a full path, so the library survives
            /// being moved or copied to another machine.</summary>
            public string SheetFile { get; set; } = string.Empty;

            /// <summary>File name of the photograph the sheet was built from — the only identity a
            /// back-filled entry has.</summary>
            public string SourceFile { get; set; } = string.Empty;

            /// <summary>Full path of that photograph, when it was recorded live rather than recovered
            /// from a log.</summary>
            public string? SourcePath { get; set; }

            /// <summary>SHA-256 of the photograph's bytes, when it could be read. What lets the same
            /// picture match after it has been moved or renamed.</summary>
            public string? SourceHash { get; set; }

            /// <summary>The cast card the sheet was built on. Only a tiebreaker — a sheet of a woman
            /// built on card 1 is still her sheet when she is loaded into card 2.</summary>
            public int Slot { get; set; }

            /// <summary>"woman" / "man" as the card had it, for the log line.</summary>
            public string? Sex { get; set; }

            /// <summary>The locked wardrobe the sheet was generated wearing, empty when none was locked.
            /// Carried because <c>CharacterSlot.SheetMatchesWardrobe</c> reads it — an adopted sheet with
            /// no wardrobe recorded would nag to be rebuilt the moment a wardrobe was locked.</summary>
            public string? Wardrobe { get; set; }

            public DateTime BuiltUtc { get; set; }
        }

        /// <summary>A hit, and how sure it is — <see cref="Confidence"/> is what the log line reports so a
        /// name-only match is never mistaken for an exact one.</summary>
        public sealed record Match(Entry Entry, string SheetPath, MatchKind Confidence);

        public enum MatchKind
        {
            /// <summary>Same file, same place.</summary>
            SourcePath,
            /// <summary>Same bytes, wherever it now lives.</summary>
            SourceHash,
            /// <summary>Same file name — all a log line can prove.</summary>
            SourceName
        }

        private sealed class SheetIndex
        {
            public int Version { get; set; } = CurrentVersion;
            /// <summary>Newest log file already read, so a later launch only reads what is new. The 846 MB
            /// of logs on this machine take seconds to walk once and must not be walked twice.</summary>
            public DateTime? LastLogScanUtc { get; set; }
            public List<Entry> Entries { get; set; } = new();
        }

        // ── Lookup ──────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The sheet already built from this photograph, or null. Strongest match wins; among equals, the
        /// most recently built, because that is the one made with the newest sheet prompt and canvas.
        /// </summary>
        public async Task<Match?> FindAsync(string? sourcePhotoPath, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(sourcePhotoPath)) return null;

            var index = await LoadAsync(token).ConfigureAwait(false);
            if (index.Entries.Count == 0) return null;

            var name = Path.GetFileName(sourcePhotoPath);
            var hash = await Task.Run(() => TryHash(sourcePhotoPath), token).ConfigureAwait(false);

            Match? best = null;
            foreach (var e in index.Entries)
            {
                var sheetPath = Path.Combine(Folder, e.SheetFile);
                if (!File.Exists(sheetPath)) continue;

                MatchKind kind;
                if (!string.IsNullOrEmpty(e.SourcePath) &&
                    string.Equals(e.SourcePath, sourcePhotoPath, StringComparison.OrdinalIgnoreCase))
                    kind = MatchKind.SourcePath;
                else if (hash != null && string.Equals(e.SourceHash, hash, StringComparison.OrdinalIgnoreCase))
                    kind = MatchKind.SourceHash;
                else if (string.Equals(e.SourceFile, name, StringComparison.OrdinalIgnoreCase))
                    kind = MatchKind.SourceName;
                else
                    continue;

                if (best == null || kind < best.Confidence ||
                    (kind == best.Confidence && e.BuiltUtc > best.Entry.BuiltUtc))
                    best = new Match(e, sheetPath, kind);
            }

            return best;
        }

        // ── Recording ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Files a freshly built sheet: copies it into <see cref="Folder"/> if it is not already there, and
        /// records what it was built from. Failure is swallowed and logged — a library that cannot be
        /// written is a lost shortcut, never a lost sheet.
        /// </summary>
        public async Task RecordAsync(string sourcePhotoPath, string sheetPath, int slot,
                                      string? sex, string? wardrobe, CancellationToken token = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sourcePhotoPath) || !File.Exists(sheetPath)) return;

                Directory.CreateDirectory(Folder);
                var fileName = Path.GetFileName(sheetPath);
                var target = Path.Combine(Folder, fileName);
                if (!string.Equals(Path.GetFullPath(sheetPath), Path.GetFullPath(target),
                                   StringComparison.OrdinalIgnoreCase) && !File.Exists(target))
                    await Task.Run(() => File.Copy(sheetPath, target, false), token).ConfigureAwait(false);

                var entry = new Entry
                {
                    SheetFile = fileName,
                    SourceFile = Path.GetFileName(sourcePhotoPath),
                    SourcePath = sourcePhotoPath,
                    SourceHash = await Task.Run(() => TryHash(sourcePhotoPath), token).ConfigureAwait(false),
                    Slot = slot,
                    Sex = string.IsNullOrWhiteSpace(sex) ? null : sex,
                    Wardrobe = string.IsNullOrWhiteSpace(wardrobe) ? null : wardrobe!.Trim(),
                    BuiltUtc = DateTime.UtcNow
                };

                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var index = _index ??= await ReadIndexAsync(token).ConfigureAwait(false);
                    index.Entries.RemoveAll(e =>
                        string.Equals(e.SheetFile, fileName, StringComparison.OrdinalIgnoreCase));
                    index.Entries.Add(entry);
                    await WriteIndexAsync(index, token).ConfigureAwait(false);
                }
                finally { _gate.Release(); }

                _log?.Invoke($"Sheet library: filed {fileName} under {entry.SourceFile}.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Sheet library: could not file this sheet ({ex.Message}) — it still works " +
                             "for this run, it just will not be found again automatically.");
            }
        }

        // ── Back-fill ───────────────────────────────────────────────────────────────────────────────

        // "Character 1 (woman): generating a 1536×864 sheet from cast_1_20260831140020_00001_.png..."
        private static readonly Regex GeneratingLine = new(
            @"Character (?<slot>\d+)(?: \((?<sex>[^)]*)\))?: generating a \d+\D\d+ sheet from (?<src>.+?)\.\.\.\s*$",
            RegexOptions.Compiled);

        // "Character 1 is being dressed in the locked wardrobe: <outfit>"
        private static readonly Regex WardrobeLine = new(
            @"Character (?<slot>\d+) is being dressed in the locked wardrobe: (?<outfit>.+?)\s*$",
            RegexOptions.Compiled);

        // "Character 1: sheet ready — sheet_1_20260905080121_00001_.png"
        private static readonly Regex ReadyLine = new(
            @"Character (?<slot>\d+): sheet ready [—-] (?<sheet>.+?)\s*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Reads the sheet builder's own log lines and indexes every sheet in the folder whose source
        /// photograph they name. Returns how many entries were added.
        ///
        /// <para>Only log files newer than the last scan are read, so the one expensive pass happens once.
        /// A back-filled entry has a file name and no path or hash, which is why
        /// <see cref="MatchKind.SourceName"/> exists.</para>
        /// </summary>
        public async Task<int> SyncAsync(CancellationToken token = default)
        {
            try
            {
                if (!Directory.Exists(Folder)) return 0;

                await _gate.WaitAsync(token).ConfigureAwait(false);
                SheetIndex index;
                try { index = _index ??= await ReadIndexAsync(token).ConfigureAwait(false); }
                finally { _gate.Release(); }

                var known = new HashSet<string>(index.Entries.Select(e => e.SheetFile),
                                                StringComparer.OrdinalIgnoreCase);
                var onDisk = await Task.Run(() => Directory
                    .EnumerateFiles(Folder, "sheet_*.png")
                    .Select(Path.GetFileName)
                    .Where(n => n != null)
                    .Select(n => n!)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase), token).ConfigureAwait(false);

                var since = index.LastLogScanUtc;
                // Nothing unindexed and nothing new to read: the common case, and it must cost nothing.
                if (since != null && onDisk.All(known.Contains)) return 0;
                if (!Directory.Exists(LogFolder)) return 0;

                var found = await Task.Run(() => ScanLogs(since, onDisk, known, token), token)
                                      .ConfigureAwait(false);

                await _gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    index.Entries.AddRange(found);
                    index.LastLogScanUtc = DateTime.UtcNow;
                    await WriteIndexAsync(index, token).ConfigureAwait(false);
                }
                finally { _gate.Release(); }

                if (found.Count > 0)
                    _log?.Invoke($"Sheet library: {found.Count} existing sheet(s) matched to the photograph " +
                                 $"they were built from, out of the build log. {index.Entries.Count} sheet(s) " +
                                 "are now reusable — load one of those photos again and its sheet comes back " +
                                 "without a render.");
                return found.Count;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Sheet library: the build log could not be read ({ex.Message}) — sheets built " +
                             "from now on are still indexed.");
                return 0;
            }
        }

        /// <summary>
        /// The log walk itself. Pairing is per cast card and strictly forward: a "generating" line opens a
        /// build, the wardrobe line (if any) attaches to it, and the next "sheet ready" for that same card
        /// closes it. A build that was cancelled or failed simply never closes and is replaced by the next
        /// one — which is why the pending build is overwritten rather than queued.
        /// </summary>
        private static List<Entry> ScanLogs(DateTime? since, IReadOnlySet<string> onDisk,
                                            IReadOnlySet<string> known, CancellationToken token)
        {
            var files = new DirectoryInfo(LogFolder)
                .EnumerateFiles("*.log")
                .Where(f => since == null || f.LastWriteTimeUtc > since)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            var pending = new Dictionary<int, (string Source, string? Sex, string? Wardrobe, DateTime When)>();
            var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                foreach (var raw in File.ReadLines(file.FullName))
                {
                    // Two cheap rejections before any regex: these lines are a handful in a million.
                    if (raw.Length < 40 || raw.IndexOf("Character ", StringComparison.Ordinal) < 0) continue;

                    var m = GeneratingLine.Match(raw);
                    if (m.Success)
                    {
                        pending[int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture)] =
                            (m.Groups["src"].Value.Trim(),
                             m.Groups["sex"].Success ? m.Groups["sex"].Value : null,
                             null,
                             ParseStamp(raw) ?? file.LastWriteTimeUtc);
                        continue;
                    }

                    m = WardrobeLine.Match(raw);
                    if (m.Success)
                    {
                        var slot = int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture);
                        if (pending.TryGetValue(slot, out var open))
                            pending[slot] = open with { Wardrobe = m.Groups["outfit"].Value.Trim() };
                        continue;
                    }

                    m = ReadyLine.Match(raw);
                    if (!m.Success) continue;

                    var readySlot = int.Parse(m.Groups["slot"].Value, CultureInfo.InvariantCulture);
                    if (!pending.Remove(readySlot, out var built)) continue;

                    var sheet = m.Groups["sheet"].Value.Trim();
                    // Only sheets that are actually in the library, and only ones not already indexed —
                    // a live RecordAsync entry knows the full path and must not be replaced by a log guess.
                    if (!onDisk.Contains(sheet) || known.Contains(sheet)) continue;

                    found[sheet] = new Entry
                    {
                        SheetFile = sheet,
                        SourceFile = built.Source,
                        SourcePath = null,
                        SourceHash = null,
                        Slot = readySlot,
                        Sex = built.Sex,
                        Wardrobe = built.Wardrobe,
                        BuiltUtc = built.When
                    };
                }
            }

            return found.Values.ToList();
        }

        /// <summary>"[2026-09-05 08:01:21.006] [INFO] …" → when, as UTC. The stamp is local time; a sheet
        /// an hour out in either direction changes nothing here, it only orders equal matches.</summary>
        private static DateTime? ParseStamp(string line)
        {
            if (line.Length < 25 || line[0] != '[') return null;
            var close = line.IndexOf(']');
            if (close < 20) return null;
            return DateTime.TryParse(line.AsSpan(1, close - 1), CultureInfo.InvariantCulture,
                                     DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
                                     out var when)
                ? when
                : null;
        }

        // ── The index file ──────────────────────────────────────────────────────────────────────────

        private async Task<SheetIndex> LoadAsync(CancellationToken token)
        {
            if (_index != null) return _index;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { return _index ??= await ReadIndexAsync(token).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        private async Task<SheetIndex> ReadIndexAsync(CancellationToken token)
        {
            try
            {
                if (!File.Exists(IndexPath)) return new SheetIndex();
                var text = await Task.Run(() => File.ReadAllText(IndexPath), token).ConfigureAwait(false);
                var index = JsonSerializer.Deserialize<SheetIndex>(text, Json);
                if (index == null || index.Version != CurrentVersion) return new SheetIndex();
                index.Entries.RemoveAll(e => string.IsNullOrWhiteSpace(e.SheetFile) ||
                                             string.IsNullOrWhiteSpace(e.SourceFile));
                return index;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Sheet library: the index could not be read ({ex.Message}) — starting a new one.");
                return new SheetIndex();
            }
        }

        private async Task WriteIndexAsync(SheetIndex index, CancellationToken token)
        {
            Directory.CreateDirectory(Folder);
            var text = JsonSerializer.Serialize(index, Json);
            // Written beside and moved into place, so an interrupted write cannot leave a half-index that
            // reads as an empty library and quietly re-builds every sheet.
            var temp = IndexPath + ".tmp";
            await Task.Run(() =>
            {
                File.WriteAllText(temp, text);
                File.Move(temp, IndexPath, true);
            }, token).ConfigureAwait(false);
        }

        private static string? TryHash(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var stream = File.OpenRead(path);
                return Convert.ToHexString(SHA256.HashData(stream));
            }
            catch { return null; }
        }
    }
}
