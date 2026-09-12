using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.UI.Models;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// Saved clip prompts, filed by the story they were written from — see <see cref="SavedStoryPrompts"/>.
    /// ⚡ H3 Express looks every story up here before it writes a single clip, and only calls the clip
    /// writer for a story it has not seen.
    ///
    /// <para><b>One file per story</b>, <c>&lt;hash&gt;.json</c>, rather than one index. A story's prompts
    /// are tens of kilobytes and are edited one story at a time, so a save rewrites that story and nothing
    /// else, and a half-written file can only ever cost the one story it belongs to. Each write goes to a
    /// <c>.tmp</c> and is moved into place.</para>
    ///
    /// <para><b>A replaced set is kept.</b> When a story's prompts are overwritten — written again with reuse
    /// switched off, or edited — the previous file is copied to <c>history\</c> first (the last
    /// <see cref="HistoryPerStory"/> per story). Hand edits are the one thing here that cannot be written
    /// again by pressing a button.</para>
    ///
    /// <para><b>Day one is not empty.</b> Every H3 tab built on the clip writer has always filed each chain
    /// it wrote in its own prompt library, with the story it was written from beside it. A story not found
    /// here is looked for in those libraries (<see cref="LegacyLibrary"/>) and, when one of them has it, the
    /// chain is adopted — so a folder already rendered on 🗂️ H3 Batch starts out recognised. Forgetting a
    /// story (<see cref="DeleteAsync"/>) records it so it is not adopted straight back.</para>
    ///
    /// <para>Everything on disk runs on the thread pool; the cache is guarded by one semaphore. Callers are on
    /// the UI thread and must never be made to wait on a mapped drive — see the slow-open regressions.</para>
    /// </summary>
    public sealed class StoryPromptStore
    {
        /// <summary>A prompt library an earlier chain may be filed in, and what to call it in the log.</summary>
        public sealed record LegacyLibrary(string Folder, string Label);

        private const int HistoryPerStory = 10;
        private const string ForgottenFile = "_forgotten.json";

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Func<string?, List<string>> _splitClips;
        private readonly IReadOnlyList<LegacyLibrary> _legacyLibraries;
        private readonly Action<string>? _log;
        private readonly SemaphoreSlim _gate = new(1, 1);

        private Dictionary<string, SavedStoryPrompts>? _entries;
        private HashSet<string>? _forgotten;
        private Dictionary<string, (ScenePrompt Entry, LegacyLibrary Library)>? _legacy;

        /// <param name="rootFolder">Where the story files live.</param>
        /// <param name="splitClips">Splits a <c>=== CLIP n of N ===</c> chain into its bodies — the H3 tabs' own
        /// splitter, so an adopted chain is cut exactly where the tab that wrote it would cut it.</param>
        /// <param name="legacyLibraries">Prompt libraries to adopt earlier chains from, most trusted first.</param>
        public StoryPromptStore(string rootFolder, Func<string?, List<string>> splitClips,
                                IEnumerable<LegacyLibrary>? legacyLibraries = null, Action<string>? log = null)
        {
            RootFolder = rootFolder;
            _splitClips = splitClips;
            _legacyLibraries = (legacyLibraries ?? Enumerable.Empty<LegacyLibrary>()).ToList();
            _log = log;
        }

        public string RootFolder { get; }

        private string HistoryFolder => Path.Combine(RootFolder, "history");

        /// <summary>Raised after any save or delete, on the thread that made it. Subscribers marshal.</summary>
        public event EventHandler? Changed;

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The key a story is filed under: SHA-256 of its text with every run of whitespace collapsed to one
        /// space. Line endings, indentation and trailing blank lines do not make a different story; a changed
        /// word does. Empty for an empty story.
        /// </summary>
        public static string HashStory(string? text)
        {
            var normal = string.Join(' ', (text ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (normal.Length == 0) return string.Empty;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normal))).ToLowerInvariant();
        }

        private string PathFor(string hash) => Path.Combine(RootFolder, hash[..Math.Min(24, hash.Length)] + ".json");

        // ── Reading ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>Every saved story, most recently changed first. Copies — edit them, then
        /// <see cref="SaveAsync"/>.</summary>
        public async Task<List<SavedStoryPrompts>> ListAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                return _entries!.Values
                    .OrderByDescending(e => e.ModifiedAt)
                    .Select(e => e.Clone())
                    .ToList();
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>How many clips each saved story holds, by hash — enough for a list of stories to mark
        /// the recognised ones without copying every prompt.</summary>
        public async Task<Dictionary<string, int>> ClipCountsAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                return _entries!.ToDictionary(p => p.Key, p => p.Value.Clips.Count);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>The saved prompts filed under a hash already worked out, or null. Never adopts.</summary>
        public async Task<SavedStoryPrompts?> FindByHashAsync(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                return _entries!.TryGetValue(hash, out var entry) ? entry.Clone() : null;
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// The saved prompts for this story, or null. A story not filed here is looked for in the legacy
        /// prompt libraries and adopted when found — unless <paramref name="adoptLegacy"/> is false, or the
        /// story was forgotten on purpose.
        /// </summary>
        /// <param name="title">The story's name as the caller knows it — used for an adopted entry, and to
        /// keep an existing one's file name current.</param>
        /// <param name="sourcePath">Where the story is now, recorded on the entry.</param>
        public async Task<SavedStoryPrompts?> FindAsync(string? storyText, string? title = null,
                                                        string? sourcePath = null, bool adoptLegacy = true)
        {
            var hash = HashStory(storyText);
            if (hash.Length == 0) return null;

            SavedStoryPrompts? moved = null;
            SavedStoryPrompts? found;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);

                if (_entries!.TryGetValue(hash, out var entry))
                {
                    // The same story in a new place: follow it, so the library says where it is now.
                    if (!string.IsNullOrWhiteSpace(sourcePath) &&
                        !string.Equals(entry.SourcePath, sourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        entry.SourcePath = sourcePath;
                        entry.SourceFileName = Path.GetFileName(sourcePath);
                        moved = entry;
                        await Task.Run(() => WriteEntry(entry)).ConfigureAwait(false);
                    }
                    found = entry.Clone();
                }
                else if (adoptLegacy && !_forgotten!.Contains(hash))
                {
                    found = await AdoptLegacyAsync(hash, storyText!, title, sourcePath).ConfigureAwait(false);
                    if (found != null) moved = found;
                    found = found?.Clone();
                }
                else
                {
                    found = null;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (moved != null) Changed?.Invoke(this, EventArgs.Empty);
            return found;
        }

        // ── Writing ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Files a story's prompts, replacing whatever was filed for it. The previous version goes to
        /// <c>history\</c> first when its clips or wardrobe actually differ.
        /// </summary>
        public async Task SaveAsync(SavedStoryPrompts entry)
        {
            if (string.IsNullOrEmpty(entry.StoryHash)) entry.StoryHash = HashStory(entry.StoryText);
            if (entry.StoryHash.Length == 0) throw new InvalidOperationException("A story with no text cannot be filed.");

            var copy = entry.Clone();
            copy.Clips = copy.Clips.Select(c => (c ?? string.Empty).Trim()).Where(c => c.Length > 0).ToList();

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                _entries!.TryGetValue(copy.StoryHash, out var previous);

                await Task.Run(() =>
                {
                    if (previous != null && Differs(previous, copy)) ArchivePrevious(copy.StoryHash);
                    WriteEntry(copy);
                }).ConfigureAwait(false);

                _entries[copy.StoryHash] = copy;
            }
            finally
            {
                _gate.Release();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Records that these prompts were just used for a render. No history entry — nothing
        /// about the prompts changed.</summary>
        public async Task MarkUsedAsync(string hash)
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                if (!_entries!.TryGetValue(hash, out var entry)) return;
                entry.UseCount++;
                entry.LastUsedAt = DateTime.Now;
                await Task.Run(() => WriteEntry(entry)).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Forgets a story: its file goes to <c>history\</c>, and it is recorded so the legacy libraries do not
        /// hand the same chain straight back on its next run. The next render of that story writes its clips
        /// from scratch.
        /// </summary>
        public async Task<bool> DeleteAsync(string hash)
        {
            var removed = false;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                removed = _entries!.Remove(hash);
                _forgotten!.Add(hash);
                await Task.Run(() =>
                {
                    ArchivePrevious(hash);
                    var path = PathFor(hash);
                    if (File.Exists(path)) File.Delete(path);
                    WriteForgotten();
                }).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return removed;
        }

        /// <summary>
        /// Adopts every story in the legacy libraries that is not filed here yet and was not forgotten.
        /// Returns how many were added.
        /// </summary>
        public async Task<int> ImportAllLegacyAsync()
        {
            var added = 0;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                await EnsureLoadedAsync().ConfigureAwait(false);
                await EnsureLegacyLoadedAsync().ConfigureAwait(false);

                foreach (var (hash, source) in _legacy!)
                {
                    if (_entries!.ContainsKey(hash) || _forgotten!.Contains(hash)) continue;
                    if (await AdoptLegacyAsync(hash, source.Entry.StoryText, null, null).ConfigureAwait(false) != null)
                        added++;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (added > 0) Changed?.Invoke(this, EventArgs.Empty);
            return added;
        }

        // ── Re-dressed versions ─────────────────────────────────────────────────────────────────────

        private const int VariantsPerStory = 6;

        private string VariantFolder => Path.Combine(RootFolder, "variants");

        private string VariantPath(string storyHash, string key) =>
            Path.Combine(VariantFolder, $"{storyHash[..Math.Min(24, storyHash.Length)]}_{key}.json");

        /// <summary>
        /// A story's clips as re-dressed for one cast's own clothes (<see cref="ClipRedress.VariantKey"/>), or
        /// null. Kept apart from the story's own prompts: they are the story in someone else's clothes, and the
        /// library lists and edits the story as written.
        /// </summary>
        public Task<SavedStoryPrompts?> FindVariantAsync(string storyHash, string key) => Task.Run(() =>
        {
            try
            {
                var path = VariantPath(storyHash, key);
                if (!File.Exists(path)) return null;
                var entry = JsonSerializer.Deserialize<SavedStoryPrompts>(File.ReadAllText(path));
                if (entry == null || entry.Clips == null || entry.Clips.Count == 0) return null;
                entry.CastNouns ??= new List<string>();
                return entry;
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Story prompts: a re-dressed version could not be read ({ex.Message}).");
                return null;
            }
        });

        /// <summary>Files a re-dressed version, keeping only the newest few per story.</summary>
        public Task SaveVariantAsync(SavedStoryPrompts entry, string key) => Task.Run(() =>
        {
            Directory.CreateDirectory(VariantFolder);
            var path = VariantPath(entry.StoryHash, key);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry, JsonOptions));
            File.Move(tmp, path, overwrite: true);

            var stem = entry.StoryHash[..Math.Min(24, entry.StoryHash.Length)];
            foreach (var old in new DirectoryInfo(VariantFolder).GetFiles(stem + "_*.json")
                         .OrderByDescending(f => f.LastWriteTimeUtc)
                         .Skip(VariantsPerStory))
            {
                try { old.Delete(); } catch { /* a stale version left behind costs nothing */ }
            }
        });

        // ── Internals (all called with the gate held) ───────────────────────────────────────────────

        private async Task EnsureLoadedAsync()
        {
            if (_entries != null) return;
            var (entries, forgotten) = await Task.Run(ReadAll).ConfigureAwait(false);
            _entries = entries;
            _forgotten = forgotten;
        }

        private (Dictionary<string, SavedStoryPrompts>, HashSet<string>) ReadAll()
        {
            var entries = new Dictionary<string, SavedStoryPrompts>(StringComparer.Ordinal);
            var forgotten = new HashSet<string>(StringComparer.Ordinal);
            if (!Directory.Exists(RootFolder)) return (entries, forgotten);

            foreach (var file in Directory.EnumerateFiles(RootFolder, "*.json"))
            {
                if (string.Equals(Path.GetFileName(file), ForgottenFile, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var entry = JsonSerializer.Deserialize<SavedStoryPrompts>(File.ReadAllText(file));
                    if (entry == null) continue;
                    entry.Clips ??= new List<string>();
                    entry.CastNouns ??= new List<string>();
                    if (string.IsNullOrEmpty(entry.StoryHash)) entry.StoryHash = HashStory(entry.StoryText);
                    if (entry.StoryHash.Length == 0 || entry.Clips.Count == 0) continue;

                    // Two files for one story can only come from a hand copy; the newer edit wins.
                    if (!entries.TryGetValue(entry.StoryHash, out var other) || other.ModifiedAt < entry.ModifiedAt)
                        entries[entry.StoryHash] = entry;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Story prompts: {Path.GetFileName(file)} could not be read ({ex.Message}) — skipped.");
                }
            }

            try
            {
                var path = Path.Combine(RootFolder, ForgottenFile);
                if (File.Exists(path))
                    foreach (var h in JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? new())
                        forgotten.Add(h);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Story prompts: the forgotten-stories list could not be read ({ex.Message}).");
            }

            return (entries, forgotten);
        }

        private void WriteEntry(SavedStoryPrompts entry)
        {
            Directory.CreateDirectory(RootFolder);
            var path = PathFor(entry.StoryHash);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }

        private void WriteForgotten()
        {
            Directory.CreateDirectory(RootFolder);
            var path = Path.Combine(RootFolder, ForgottenFile);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_forgotten!.OrderBy(h => h).ToList(), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }

        private static bool Differs(SavedStoryPrompts a, SavedStoryPrompts b) =>
            !a.Clips.SequenceEqual(b.Clips, StringComparer.Ordinal) ||
            !string.Equals(a.Wardrobe?.Trim(), b.Wardrobe?.Trim(), StringComparison.Ordinal);

        /// <summary>Copies the story's current file into history and trims its history to the newest few.</summary>
        private void ArchivePrevious(string hash)
        {
            try
            {
                var path = PathFor(hash);
                if (!File.Exists(path)) return;
                Directory.CreateDirectory(HistoryFolder);
                var stem = Path.GetFileNameWithoutExtension(path);
                File.Copy(path, Path.Combine(HistoryFolder, $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss_fff}.json"), overwrite: true);

                foreach (var old in Directory.GetFiles(HistoryFolder, stem + "_*.json")
                             .OrderByDescending(f => f, StringComparer.Ordinal)
                             .Skip(HistoryPerStory))
                    File.Delete(old);
            }
            catch (Exception ex)
            {
                // The new version still gets written; losing the backup is not a reason to lose the edit.
                _log?.Invoke($"Story prompts: the previous version could not be kept ({ex.Message}).");
            }
        }

        private async Task EnsureLegacyLoadedAsync()
        {
            if (_legacy != null) return;
            _legacy = await Task.Run(ReadLegacy).ConfigureAwait(false);
        }

        /// <summary>
        /// Every story in the legacy libraries, with the one chain per story worth adopting: the one with the
        /// most clips — the whole film rather than a short test run of the same story — and among those the
        /// most recently used.
        /// </summary>
        private Dictionary<string, (ScenePrompt, LegacyLibrary)> ReadLegacy()
        {
            var best = new Dictionary<string, (ScenePrompt Entry, LegacyLibrary Library, int Clips)>(StringComparer.Ordinal);
            foreach (var library in _legacyLibraries)
            {
                List<ScenePrompt> entries;
                try
                {
                    entries = new ScenePromptLibrary(_log, library.Folder).Load();
                }
                catch
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    var hash = HashStory(entry.StoryText);
                    if (hash.Length == 0) continue;
                    var clips = _splitClips(entry.Prompt).Count;
                    if (clips == 0) continue;

                    if (!best.TryGetValue(hash, out var current) || clips > current.Clips ||
                        (clips == current.Clips && entry.LastUsed > current.Entry.LastUsed))
                        best[hash] = (entry, library, clips);
                }
            }
            return best.ToDictionary(p => p.Key, p => (p.Value.Entry, p.Value.Library), StringComparer.Ordinal);
        }

        /// <summary>
        /// A name for an adopted chain with no story file to name it: the library's own name when it is a
        /// name — older entries were named after the prompt's opening, "[Shot 1] Cinematic fight…", which says
        /// nothing about which story it is — otherwise the story's first sentence.
        /// </summary>
        private static string TitleFor(ScenePrompt chain, string storyText)
        {
            var name = (chain.Name ?? string.Empty).Trim();
            if (name.Length > 0 && !name.StartsWith('[') &&
                name.IndexOf("multimodal", StringComparison.OrdinalIgnoreCase) < 0)
                return name;

            var story = string.Join(' ', storyText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var stop = story.IndexOfAny(new[] { '.', '!', '?' });
            if (stop > 12) story = story[..stop];
            if (story.Length > 48) story = story[..48].TrimEnd() + "…";
            return story.Length > 0 ? story : "Untitled story";
        }

        private async Task<SavedStoryPrompts?> AdoptLegacyAsync(string hash, string storyText, string? title, string? sourcePath)
        {
            await EnsureLegacyLoadedAsync().ConfigureAwait(false);
            if (!_legacy!.TryGetValue(hash, out var source)) return null;

            var (chain, library) = source;
            var clips = _splitClips(chain.Prompt).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            if (clips.Count == 0) return null;

            var entry = new SavedStoryPrompts
            {
                StoryHash = hash,
                Title = !string.IsNullOrWhiteSpace(title) ? title.Trim() : TitleFor(chain, storyText),
                SourcePath = sourcePath ?? string.Empty,
                SourceFileName = string.IsNullOrEmpty(sourcePath) ? string.Empty : Path.GetFileName(sourcePath),
                StoryText = storyText.Trim(),
                Clips = clips,
                Wardrobe = (chain.Wardrobe ?? string.Empty).Trim(),
                LengthSeconds = chain.LengthSeconds,
                Origin = $"Imported from the {library.Label} prompt library",
                CreatedAt = chain.CreatedAt,
                ModifiedAt = DateTime.Now,
            };

            try
            {
                await Task.Run(() => WriteEntry(entry)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Story prompts: could not adopt \"{entry.Title}\" ({ex.Message}).");
                return null;
            }

            _entries![hash] = entry;
            _log?.Invoke($"📚 \"{entry.Title}\": {clips.Count} clip prompt(s) adopted from the {library.Label} " +
                         "prompt library, where an earlier run filed them.");
            return entry;
        }
    }
}
