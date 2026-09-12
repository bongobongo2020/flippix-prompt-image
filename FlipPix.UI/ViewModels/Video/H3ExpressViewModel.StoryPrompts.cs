using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's 📚 story prompts: every story's written clip prompts are saved under the story they
    /// came from, and a story the tab recognises renders from them instead of the clip writer.
    ///
    /// <para><b>Why.</b> Writing a story's clips is a beat-sheet call plus one llama-server call per clip —
    /// minutes per story, and not reproducible run to run. The render after it is the part worth repeating;
    /// the writing is not.</para>
    ///
    /// <para><b>How a story is recognised.</b> By its text (<see cref="StoryPromptStore.HashStory"/>), so it
    /// still matches renamed or moved, and stops matching once its words change. The folder scan reads every
    /// story in the background and puts a 📚 badge on the ones that have prompts saved; a story with none
    /// may still be found in the prompt libraries the other H3 tabs have always filed their chains in, and
    /// is adopted from there.</para>
    ///
    /// <para><b>Where it cuts in.</b> Two points in 🍀's run, and nowhere else:</para>
    /// <list type="bullet">
    /// <item>After the cast is read (<see cref="OnLuckyCastReadAsync"/>): the saved set is looked up, and its
    /// wardrobe is locked in before the portraits and sheets are generated — the clips describe those
    /// garments in their own prose, so a newly derived wardrobe would dress the cast against its own
    /// prompts.</item>
    /// <item>At "writing the clips" (<see cref="AnalyzeAsync"/>): the saved bodies are stamped for this run's
    /// cast and put in the prompt box. The queue, the renders and the join are untouched.</item>
    /// </list>
    ///
    /// <para><b>Length.</b> A saved set renders at the clip length it was written for — its shot timestamps
    /// run to that length. The slider is put back straight after the queue-add.</para>
    ///
    /// <para>A freshly written set is saved the moment it exists; an edit in the clip editor is saved when it
    /// is regenerated (the prompt that made the file is the one kept), or straight away with 💾.</para>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        /// <summary>The prompt libraries earlier chains may already be filed in, most trusted first. 🥽 H3 VR's
        /// is left out: its chains carry stereo rules this tab does not render.</summary>
        private static readonly (string Folder, string Label)[] LegacyChainLibraries =
        {
            ("h3express", "H3 Express"),
            ("h3batch", "H3 Batch"),
            ("h3eros", "H3 Eros"),
            ("h3experimental", "H3 Experimental"),
            ("h34step", "H3 4-Step"),
        };

        private StoryPromptStore? _storyPrompts;
        private bool _reuseSavedPrompts = true;
        private int _savedStoryCount;
        private int _recognizePass;

        /// <summary>The saved set the story in flight is rendering from, or null when its clips are written.</summary>
        private SavedStoryPrompts? _recalled;

        /// <summary>The story whose clips are on the board, so an edited clip can be saved back to it. Empty
        /// after a restart — the board comes back from the queue file, the story text does not.</summary>
        private string _boardStoryHash = string.Empty;

        private StoryPromptLibraryWindow? _storyPromptsWindow;

        public RelayCommand<BatchStory?> OpenStoryPromptsCommand { get; private set; } = null!;
        public RelayCommand SaveClipToStoryCommand { get; private set; } = null!;

        /// <summary>Built on first use, never in the constructor: its first read is a folder of files.</summary>
        private StoryPromptStore StoryPrompts
        {
            get
            {
                if (_storyPrompts != null) return _storyPrompts;
                _storyPrompts = new StoryPromptStore(
                    Path.Combine(ScenePromptLibrary.FolderFor(ChainLibraryFolder), "stories"),
                    SplitClips,
                    LegacyChainLibraries.Select(l => new StoryPromptStore.LegacyLibrary(
                        ScenePromptLibrary.FolderFor(l.Folder), l.Label)),
                    // The store logs from the thread pool; the log is appended to on the UI thread.
                    message => Application.Current?.Dispatcher.InvokeAsync(() => AddLog(message)));
                _storyPrompts.Changed += (_, _) => Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    _ = RefreshSavedCountsAsync();
                    _storyPromptsWindow?.ViewModel.OnStoreChanged();
                });
                return _storyPrompts;
            }
        }

        private void InitStoryPrompts()
        {
            _reuseSavedPrompts = _settingsService.Settings?.H3ExpressReuseSavedPrompts ?? true;
            OpenStoryPromptsCommand = new RelayCommand<BatchStory?>(story => _ = OpenStoryPromptsAsync(story));
            SaveClipToStoryCommand = new RelayCommand(
                () => { if (SelectedClip is { } row) _ = SaveClipToStoryAsync(row, ClipPromptDraft, manual: true); },
                () => CanSaveClipToStory);

            // A saved story taken off the list goes out of the remembered list too.
            Stories.CollectionChanged += (_, e) =>
            {
                if (e.OldItems?.Cast<BatchStory>().Any(s => s.IsFromLibrary) == true) RememberLibraryStories();
            };

            // The saved stories added last session, and the count on the button — off the constructor's thread.
            Application.Current?.Dispatcher.InvokeAsync(async () =>
            {
                await RestoreLibraryStoriesAsync();
                await RefreshSavedCountsAsync();
            }, DispatcherPriority.Background);
        }

        // ── Saved stories on the STORIES list ───────────────────────────────────────────────────────

        private bool _restoringLibraryStories;

        /// <summary>Where a story row's file is, for the store to record — none for a saved story, whose key is
        /// not a place.</summary>
        private static string? SourcePathOf(BatchStory? story) => story is { IsFromLibrary: false } ? story.FilePath : null;

        /// <summary>
        /// Puts a saved story on the STORIES list, from 📚 Story prompts. It renders like a story found in the
        /// folder — the CAST card, its saved prompts, one film named after it. True when it was added; a story
        /// already on the list, from the folder or from here, is not added twice.
        /// </summary>
        private bool AddSavedStoryToRun(SavedStoryPrompts saved)
        {
            if (string.IsNullOrWhiteSpace(saved.StoryText) || saved.StoryHash.Length == 0)
            {
                AddLog($"📚 \"{saved.Title}\" has no story text saved with it, so it cannot be put on the list.");
                return false;
            }

            var existing = Stories.FirstOrDefault(s => s.StoryHash == saved.StoryHash);
            if (existing != null)
            {
                AddLog($"📚 \"{saved.Title}\" is already on the stories list" +
                       (existing.IsFromLibrary ? "." : $", from the folder as {existing.FileName}."));
                return false;
            }

            AddStory(new BatchStory(saved.Title, saved.StoryText, saved.StoryHash) { SavedClipCount = saved.Clips.Count });
            RememberLibraryStories();
            OnPropertyChanged(nameof(ReuseSavedPromptsSummary));
            AddLog($"📚 \"{saved.Title}\" added to the stories list — {saved.Clips.Count} saved clip prompt(s)" +
                   (IsBatchRunning
                       ? ". It renders on the next ⚡ Render: the run in progress fixed its list when it started."
                       : "."));
            return true;
        }

        private void RememberLibraryStories()
        {
            if (_restoringLibraryStories) return;
            var settings = _settingsService.Settings;
            if (settings == null) return;
            settings.H3ExpressLibraryStories = Stories.Where(s => s.IsFromLibrary).Select(s => s.StoryHash).ToList();
            _settingsService.SaveSettings(settings);
        }

        /// <summary>The saved stories that were on the list when the app last closed, put back from the store. One
        /// the store no longer has (forgotten since) is dropped, and said.</summary>
        private async Task RestoreLibraryStoriesAsync()
        {
            var hashes = _settingsService.Settings?.H3ExpressLibraryStories?.ToList() ?? new List<string>();
            if (hashes.Count == 0) return;

            var restored = 0;
            var missing = 0;
            _restoringLibraryStories = true;
            try
            {
                foreach (var hash in hashes)
                {
                    var saved = await StoryPrompts.FindByHashAsync(hash);
                    if (saved == null || string.IsNullOrWhiteSpace(saved.StoryText))
                    {
                        missing++;
                        continue;
                    }
                    if (Stories.Any(s => s.StoryHash == hash)) continue;
                    AddStory(new BatchStory(saved.Title, saved.StoryText, saved.StoryHash) { SavedClipCount = saved.Clips.Count });
                    restored++;
                }
            }
            catch (Exception ex)
            {
                AddLog($"📚 The saved stories on the list could not be put back: {ex.Message}");
            }
            finally
            {
                _restoringLibraryStories = false;
            }

            if (restored > 0)
                AddLog($"📚 {restored} saved stor{(restored == 1 ? "y" : "ies")} put back on the stories list from last time.");
            if (missing > 0)
            {
                AddLog($"📚 {missing} saved stor{(missing == 1 ? "y is" : "ies are")} no longer in the story prompts and " +
                       "left the list.");
                RememberLibraryStories();
            }
        }

        // ── What the page shows ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// On (the default): a story with saved prompts renders from them. Off: every story's clips are
        /// written afresh and the new set replaces the saved one — the old one goes to history, not away.
        /// </summary>
        public bool ReuseSavedPrompts
        {
            get => _reuseSavedPrompts;
            set
            {
                if (_reuseSavedPrompts == value) return;
                _reuseSavedPrompts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ReuseSavedPromptsSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressReuseSavedPrompts = value;
                    _settingsService.SaveSettings(settings);
                }
                AddLog(value
                    ? "📚 Reuse saved prompts ON — a story that has them skips the clip writer."
                    : "📚 Reuse saved prompts OFF — every story's clips are written afresh and replace its saved set " +
                      "(the replaced set is kept in the history folder).");
            }
        }

        public int SavedStoryCount
        {
            get => _savedStoryCount;
            private set
            {
                if (_savedStoryCount == value) return;
                _savedStoryCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StoryPromptsButtonText));
                OnPropertyChanged(nameof(ReuseSavedPromptsSummary));
            }
        }

        public string StoryPromptsButtonText =>
            SavedStoryCount > 0 ? $"📚 Story prompts  ·  {SavedStoryCount} saved" : "📚 Story prompts";

        public string ReuseSavedPromptsSummary
        {
            get
            {
                var inFolder = Stories.Count(s => s.HasSavedPrompts);
                if (!ReuseSavedPrompts)
                    return "Off — every story's clips are written again, and the new set replaces the saved one.";
                return inFolder > 0
                    ? $"{inFolder} of {Stories.Count} stories here have saved prompts and skip the clip writer. " +
                      "New stories are written once and saved."
                    : "A story's clip prompts are saved the first time they are written; after that it renders " +
                      "from them and skips the clip writer.";
            }
        }

        private bool CanSaveClipToStory =>
            SelectedClip != null && _boardStoryHash.Length > 0 && !string.IsNullOrWhiteSpace(ClipPromptDraft);

        public string SaveClipToStoryTip =>
            _boardStoryHash.Length == 0
                ? "The story these clips came from is not known in this session (the board was restored after a " +
                  "restart), so there is nothing to save the prompt to."
                : "Save the prompt in the box as this clip's prompt for the story, without re-rendering. The next " +
                  "render of this story uses it. (Regenerating an edited clip saves it too.)";

        // ── The folder: which stories are already written ──────────────────────────────────────────

        protected override void OnStoriesRescanned() => _ = RecognizeStoriesAsync();

        /// <summary>
        /// Reads each story in the list off the UI thread and marks the ones with saved prompts. A newer scan
        /// supersedes an older one still running, so a double rescan does not badge rows twice.
        /// </summary>
        private async Task RecognizeStoriesAsync()
        {
            var pass = ++_recognizePass;
            // Saved stories were badged when they were added; only the folder's files need reading.
            var rows = Stories.Where(s => !s.IsFromLibrary).ToList();
            if (rows.Count == 0) return;

            try
            {
                var texts = await Task.Run(() => rows.Select(r =>
                {
                    try { return (Row: r, Text: (string?)File.ReadAllText(r.FilePath)); }
                    catch { return (Row: r, Text: (string?)null); }
                }).ToList());

                var recognised = 0;
                foreach (var (row, text) in texts)
                {
                    if (pass != _recognizePass) return;
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    row.StoryHash = StoryPromptStore.HashStory(text);

                    // The same story added from the story prompts earlier: the folder's copy is the real file, so
                    // it stays and the added one goes — unless the added one has already been rendered.
                    var twin = Stories.FirstOrDefault(s => s.IsFromLibrary && s.IsWaiting && s.StoryHash == row.StoryHash);
                    if (twin != null)
                    {
                        Stories.Remove(twin);
                        OnPropertyChanged(nameof(FolderSummary));
                        OnCanExecuteChanged();
                        AddLog($"📚 \"{twin.Title}\" is also in the folder as {row.FileName} — the folder's copy stays on the list.");
                    }

                    var saved = await StoryPrompts.FindAsync(text, row.Title, row.FilePath);
                    row.SavedClipCount = saved?.Clips.Count ?? 0;
                    if (saved != null) recognised++;
                }

                await RefreshSavedCountsAsync();
                if (recognised > 0)
                    AddLog($"📚 {recognised} of {rows.Count} stories have saved clip prompts" +
                           (ReuseSavedPrompts
                               ? " — they render from them without the clip writer."
                               : ", but ♻️ Reuse saved prompts is off, so they will be written again."));
            }
            catch (Exception ex)
            {
                AddLog($"📚 The stories could not be checked for saved prompts: {ex.Message}");
            }
        }

        /// <summary>The button's count, and each row's badge, from the store as it is now.</summary>
        private async Task RefreshSavedCountsAsync()
        {
            try
            {
                var counts = await StoryPrompts.ClipCountsAsync();
                SavedStoryCount = counts.Count;
                foreach (var story in Stories.Where(s => s.StoryHash.Length > 0))
                    story.SavedClipCount = counts.TryGetValue(story.StoryHash, out var n) ? n : 0;
                OnPropertyChanged(nameof(ReuseSavedPromptsSummary));
            }
            catch (Exception ex)
            {
                AddLog($"📚 Story prompts unavailable: {ex.Message}");
            }
        }

        // ── The run ─────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The cast has been read and nobody is dressed yet: look the story up, and put back the wardrobe its
        /// saved clips were written in. See the type remarks for why that cannot wait until the clips.
        /// </summary>
        protected override async Task OnLuckyCastReadAsync(CancellationToken token)
        {
            _recalled = null;
            if (!HasStoryText) return;

            var story = CurrentStory;
            if (!ReuseSavedPrompts && story?.HasSavedPrompts == true)
                AddLog("📚 This story has saved prompts, but Reuse saved prompts is off — its clips are written " +
                       "again, and the new set replaces the saved one.");

            SavedStoryPrompts? saved = null;
            if (ReuseSavedPrompts)
            {
                try
                {
                    saved = await StoryPrompts.FindAsync(StoryText, story?.Title ?? StoryTitle(), SourcePathOf(story));
                }
                catch (Exception ex)
                {
                    AddLog($"📚 Saved prompts could not be read ({ex.Message}) — this story's clips are written instead.");
                }
                token.ThrowIfCancellationRequested();

                if (saved is { Clips.Count: 0 }) saved = null;
                if (saved == null)
                    AddLog("📚 No saved prompts for this story yet — its clips are written now and saved for next time.");
            }

            if (saved != null)
            {
                _recalled = saved;
                if (story != null)
                {
                    story.StoryHash = saved.StoryHash;
                    story.SavedClipCount = saved.Clips.Count;
                }

                AddLog($"📚 Recognised \"{saved.Title}\": {saved.Clips.Count} saved clip prompt(s)" +
                       (string.IsNullOrWhiteSpace(saved.Origin) ? string.Empty : $" ({saved.Origin.ToLowerInvariant()})") +
                       (saved.EditedByHand ? ", edited by hand" : string.Empty) +
                       ". The clip writer is skipped for this story.");
            }

            // Who plays whom, before anyone is dressed. The clips' picture numbers, their pronouns and the
            // wardrobe lines all follow the story's character order — which is not the order of the CAST card.
            AlignCastToStory(saved);

            // What the cast wears. The story's saved wardrobe is the base; a cast in their own clothes replaces
            // their own lines of it, and a character the story still casts keeps the story's.
            var wardrobe = saved?.Wardrobe?.Trim() ?? string.Empty;
            if (HasCastOverride && CastOwnClothes)
            {
                var own = await OwnClothesWardrobeAsync(token);
                token.ThrowIfCancellationRequested();
                if (own.Length > 0)
                {
                    wardrobe = CastPromptStamp.MergeWardrobe(wardrobe, own);
                    AddLog("Cast: wearing their own clothes, as read from the photos.");
                }
            }
            else if (saved != null)
            {
                AddLog(wardrobe.Length > 0
                    ? "📚 Wardrobe: using the one the saved clips were written in, so the sheets match their prose."
                    : "📚 The saved prompts carry no wardrobe — one is derived from the story as usual.");
            }

            if (wardrobe.Length > 0) AdoptWardrobe(wardrobe);

            // The same photographs in the same outfit as an earlier story need no new sheet.
            if (HasCastOverride && HasCastWardrobe) await AdoptSheetsInWardrobeAsync();
        }

        /// <summary>
        /// "Writing the clips": a recognised story's saved bodies, stamped for this run's cast — or, for any
        /// other story, the writer as always, with what it writes saved under the story.
        /// </summary>
        protected override async Task AnalyzeAsync()
        {
            var saved = _recalled;
            if (saved != null && ReuseSavedPrompts && HasStoryText &&
                saved.StoryHash == StoryPromptStore.HashStory(StoryText))
            {
                await RecallStoryPromptsAsync(saved);
                return;
            }

            _recalled = null;
            var before = Prompt;
            await base.AnalyzeAsync();
            if (HasStoryText && !string.IsNullOrWhiteSpace(Prompt) && Prompt != before)
                await SaveWrittenPromptsAsync();
        }

        private async Task RecallStoryPromptsAsync(SavedStoryPrompts saved)
        {
            var clips = saved.Clips;
            var boardHash = saved.StoryHash;
            _boardVariantKey = string.Empty;

            // A cast in their own clothes: the clips' clothing wording has to follow them.
            var changes = RedressChanges(saved);
            if (changes.Count > 0)
            {
                var redressed = await RedressClipsAsync(saved, changes);
                if (redressed == null) return;   // stopped: no prompt, and 🍀 stops with the run
                clips = redressed.Clips;
                _boardVariantKey = redressed.Key;
                // Clips that are neither the story's own nor a filed version have nowhere an edit could be saved.
                if (!redressed.Complete) boardHash = string.Empty;
            }

            if (IsFeelingLucky) LuckyPhase = $"5/6 · Using {clips.Count} saved clip prompts…";

            Prompt = StampChain(JoinClips(clips));
            _boardStoryHash = boardHash;

            var len = saved.LengthSeconds > 0 ? ClampLength(saved.LengthSeconds) : ClampLength(LengthSeconds);
            var slider = ClampLength(LengthSeconds);
            AddLog($"📚 {PromptClipCount} clip(s) loaded from the saved prompts, stamped for this cast" +
                   (Math.Abs(len - slider) > 0.001
                       ? $". They were written for {len:0.#} s clips, so this story renders at {len:0.#} s rather " +
                         $"than the {slider:0.#} s on the slider."
                       : $", {len:0.#} s each."));

            _ = StoryPrompts.MarkUsedAsync(saved.StoryHash);
        }

        /// <summary>
        /// Queues a recalled story at the clip length its prompts were written for, then puts the slider back
        /// — the next story that has to be written is written at the length that was set. The flag holds off
        /// the writer run a length change arms.
        /// </summary>
        protected override void AddToQueue()
        {
            var written = _recalled?.LengthSeconds ?? 0;
            if (written <= 0 || Math.Abs(ClampLength(written) - ClampLength(LengthSeconds)) < 0.001)
            {
                base.AddToQueue();
                return;
            }

            var keep = LengthSeconds;
            var restoring = _restoringChain;
            _restoringChain = true;
            try
            {
                LengthSeconds = ClampLength(written);
                base.AddToQueue();
            }
            finally
            {
                LengthSeconds = keep;
                _restoringChain = restoring;
            }
        }

        /// <summary>Files what the writer just wrote under the story it was written from.</summary>
        private async Task SaveWrittenPromptsAsync()
        {
            var clips = SplitClips(Prompt).Select(CastPromptStamp.Strip).Where(c => c.Length > 0).ToList();
            if (clips.Count == 0) return;

            var story = CurrentStory;
            var entry = new SavedStoryPrompts
            {
                StoryHash = StoryPromptStore.HashStory(StoryText),
                Title = story?.Title ?? StoryTitle(),
                SourcePath = SourcePathOf(story) ?? string.Empty,
                SourceFileName = story == null ? StoryFileName : story.IsFromLibrary ? string.Empty : story.FileName,
                StoryText = StoryText.Trim(),
                Clips = clips,
                Wardrobe = CastWardrobe.Trim(),
                CastNouns = CastNouns(),
                LengthSeconds = ClampLength(LengthSeconds),
                PromptBuild = ResearchPrompts ? "researched" : "shipped",
                VisualStyle = VisualStyle.Name,
                Origin = "Written by H3 Express",
            };

            try
            {
                var previous = await StoryPrompts.FindAsync(StoryText, adoptLegacy: false);
                if (previous != null)
                {
                    // A rename in the library outlives a rewrite, and so does where the story file was — a saved
                    // story rendered off the list has no file of its own to record.
                    entry.Title = previous.Title;
                    entry.CreatedAt = previous.CreatedAt;
                    entry.UseCount = previous.UseCount;
                    if (entry.SourcePath.Length == 0)
                    {
                        entry.SourcePath = previous.SourcePath;
                        entry.SourceFileName = previous.SourceFileName;
                    }
                }

                await StoryPrompts.SaveAsync(entry);
                _boardStoryHash = entry.StoryHash;
                _boardVariantKey = string.Empty;
                if (story != null)
                {
                    story.StoryHash = entry.StoryHash;
                    story.SavedClipCount = clips.Count;
                }

                AddLog(previous == null
                    ? $"📚 Saved {clips.Count} clip prompt(s) for \"{entry.Title}\" — the next time this story is loaded " +
                      "they are used as they are. Open 📚 Story prompts to read or edit them."
                    : $"📚 Replaced the saved prompts for \"{entry.Title}\" with this new set of {clips.Count}; the " +
                      "previous set is kept in the history folder.");
            }
            catch (Exception ex)
            {
                // A library problem must never fail the story that is about to render.
                AddLog($"📚 This story's prompts could not be saved: {ex.Message}");
            }
        }

        // ── One clip, saved back to its story ───────────────────────────────────────────────────────

        /// <summary>
        /// Writes one clip's prompt into its story's saved set, by clip number. Called by 💾 with the editor's
        /// text, and after a regenerate or a mid-run edit with the prompt the clip now renders from.
        /// </summary>
        private async Task SaveClipToStoryAsync(ErosHuntClip row, string prompt, bool manual)
        {
            var hash = _boardStoryHash;
            var variantKey = _boardVariantKey;
            if (hash.Length == 0)
            {
                if (manual) AddLog("📚 The story these clips came from is not known in this session — nothing to save to.");
                return;
            }

            var body = CastPromptStamp.Strip(prompt);
            if (body.Length == 0) return;

            try
            {
                // Clips re-dressed for the cast's own clothes are saved to that version, never over the story's
                // own wording.
                var entry = variantKey.Length > 0
                    ? await StoryPrompts.FindVariantAsync(hash, variantKey)
                    : await StoryPrompts.FindByHashAsync(hash);
                if (entry == null)
                {
                    if (manual) AddLog("📚 This story's saved prompts are gone (forgotten in the library?) — nothing to save to.");
                    return;
                }

                var index = row.Item.ClipIndex - 1;
                if (index < 0 || index > entry.Clips.Count)
                {
                    AddLog($"📚 {ClipCaption(row)} has no place in the saved set of {entry.Clips.Count} clip(s) — not saved.");
                    return;
                }

                if (index < entry.Clips.Count && string.Equals(entry.Clips[index], body, StringComparison.Ordinal))
                {
                    if (manual) AddLog($"📚 {ClipCaption(row)}: the story already has this prompt saved.");
                    return;
                }

                if (index == entry.Clips.Count) entry.Clips.Add(body);
                else entry.Clips[index] = body;
                entry.EditedByHand = true;
                entry.ModifiedAt = DateTime.Now;
                if (variantKey.Length > 0)
                {
                    await StoryPrompts.SaveVariantAsync(entry, variantKey);
                    AddLog($"📚 {ClipCaption(row)}: prompt saved to this cast's re-dressed version of the story.");
                }
                else
                {
                    await StoryPrompts.SaveAsync(entry);
                    AddLog($"📚 {ClipCaption(row)}: prompt saved to the story — its next render uses it.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"📚 {ClipCaption(row)}: the prompt could not be saved to the story ({ex.Message}).");
            }
        }

        // ── The library window ──────────────────────────────────────────────────────────────────────

        /// <summary>Opens 📚 Story Prompts, or brings it forward — on the given story when it has prompts.</summary>
        private async Task OpenStoryPromptsAsync(BatchStory? focus)
        {
            var focusHash = focus is { HasSavedPrompts: true, StoryHash.Length: > 0 } ? focus.StoryHash : null;
            try
            {
                if (_storyPromptsWindow != null)
                {
                    if (_storyPromptsWindow.WindowState == WindowState.Minimized)
                        _storyPromptsWindow.WindowState = WindowState.Normal;
                    _storyPromptsWindow.Activate();
                    await _storyPromptsWindow.ViewModel.FocusAsync(focusHash);
                    return;
                }

                var vm = new StoryPromptLibraryViewModel(
                    StoryPrompts,
                    () => Stories.Select(s => s.StoryHash).Where(h => h.Length > 0).ToList(),
                    AddLog,
                    AddSavedStoryToRun);
                var window = new StoryPromptLibraryWindow(vm)
                {
                    Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                };
                // CenterOwner with no owner lands the window in the top-left corner.
                if (window.Owner == null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                window.Closed += (_, _) => _storyPromptsWindow = null;
                _storyPromptsWindow = window;
                window.Show();
                await vm.LoadAsync(focusHash);
            }
            catch (Exception ex)
            {
                AddLog($"📚 Story prompts could not be opened: {ex.Message}");
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

        private List<string> CastNouns()
        {
            var nouns = new List<string>();
            if (Character1.IsCast) nouns.Add(Character1.Noun);
            if (Character2.IsCast) nouns.Add(Character2.Noun);
            return nouns;
        }

        /// <summary>A name for a story that did not come from the folder: its file's name, else its opening words.</summary>
        private string StoryTitle()
        {
            if (!string.IsNullOrWhiteSpace(StoryFileName)) return Path.GetFileNameWithoutExtension(StoryFileName);
            var words = StoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Take(6);
            var title = string.Join(' ', words);
            return title.Length == 0 ? "Untitled story" : title + "…";
        }
    }
}
