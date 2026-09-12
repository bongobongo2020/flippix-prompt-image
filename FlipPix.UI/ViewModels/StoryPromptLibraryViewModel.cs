using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels
{
    /// <summary>What to do with edits that have not been saved when the user tries to leave them.</summary>
    public enum UnsavedChoice
    {
        Save,
        Discard,
        Stay
    }

    /// <summary>
    /// The 📚 Story Prompts window: every story ⚡ H3 Express has saved clip prompts for, and an editor for
    /// one story's clips.
    ///
    /// <para><b>Nothing is written until Save.</b> The editor works on a copy of the entry; switching story,
    /// closing the window or forgetting the story with unsaved edits asks first. A save replaces the story's
    /// file and the store keeps the version it replaced (see <see cref="StoryPromptStore"/>).</para>
    ///
    /// <para><b>What is edited is the clip body</b> — <c>&lt;Picture 1&gt;</c> and <c>&lt;Picture 2&gt;</c>
    /// for the cast, no reference line, no wardrobe lock. Those two are written by the tab when a clip is
    /// queued, from the cast of that run, and are not the user's to type.</para>
    /// </summary>
    public sealed partial class StoryPromptLibraryViewModel : ObservableObject
    {
        private readonly StoryPromptStore _store;
        private readonly Func<IReadOnlyCollection<string>> _folderHashes;
        private readonly Action<string> _log;
        private readonly List<StoryRow> _allRows = new();

        private SavedStoryPrompts? _editing;
        private bool _loadingEditor;
        private bool _skipUnsavedCheck;
        private bool _reloadPending;

        private readonly Func<SavedStoryPrompts, bool>? _addToRun;

        /// <param name="folderHashes">The hashes of the stories on the tab's STORIES list right now, so the list
        /// here can mark which saved stories are already on it.</param>
        /// <param name="log">The tab's log — a save or a forget is worth a line there too.</param>
        /// <param name="addToRun">Puts a saved story on the tab's STORIES list; true when it was added. Null hides
        /// the buttons.</param>
        public StoryPromptLibraryViewModel(StoryPromptStore store, Func<IReadOnlyCollection<string>> folderHashes,
                                           Action<string> log, Func<SavedStoryPrompts, bool>? addToRun = null)
        {
            _store = store;
            _folderHashes = folderHashes;
            _log = log;
            _addToRun = addToRun;
            Clips.CollectionChanged += (_, _) => RaiseEditorState();
        }

        // ── Onto the render list ───────────────────────────────────────────────────────────────────

        public bool CanAddToRun => _addToRun != null;

        public bool IsSelectedInRunList => _selectedStory?.IsInRunList == true;

        public string AddToRunText => IsSelectedInRunList ? "✓ On the stories list" : "➕ Add to stories list";

        private bool CanAddSelected => _addToRun != null && _selectedStory is { IsInRunList: false };

        /// <summary>Puts the open story on the tab's STORIES list, where it renders like a story from the folder —
        /// with the CAST card, the saved prompts and everything else. Unsaved edits are settled first: the run
        /// reads the prompts from the store, not from this window.</summary>
        [RelayCommand(CanExecute = nameof(CanAddSelected))]
        private async Task AddToRunAsync()
        {
            if (_addToRun == null || _selectedStory == null) return;
            if (IsDirty && !ResolveUnsaved()) return;
            var entry = _selectedStory.Entry;
            if (_addToRun(entry)) await LoadAsync(entry.StoryHash);
        }

        private bool CanAddAllShown => _addToRun != null && Stories.Any(r => !r.IsInRunList);

        public string AddAllShownText
        {
            get
            {
                var n = Stories.Count(r => !r.IsInRunList);
                return n == 0 ? "All shown are on the list"
                     : string.IsNullOrWhiteSpace(SearchText) ? $"➕ Add all {n}" : $"➕ Add {n} shown";
            }
        }

        /// <summary>Every story the list is showing that is not on the STORIES list yet, in the order shown — the
        /// search narrows it first.</summary>
        [RelayCommand(CanExecute = nameof(CanAddAllShown))]
        private async Task AddAllShownToRunAsync()
        {
            if (_addToRun == null) return;
            var todo = Stories.Where(r => !r.IsInRunList).Select(r => r.Entry).ToList();
            if (todo.Count == 0) return;
            if (todo.Count > 1 &&
                Confirm?.Invoke($"Add {todo.Count} saved stories to the stories list? They render in this order, after " +
                                "the stories already on it.") != true)
                return;
            var added = todo.Count(e => _addToRun(e));
            _log($"📚 {added} saved stor{(added == 1 ? "y" : "ies")} added to the stories list.");
            await LoadAsync();
        }

        private void RaiseRunListState()
        {
            OnPropertyChanged(nameof(IsSelectedInRunList));
            OnPropertyChanged(nameof(AddToRunText));
            OnPropertyChanged(nameof(AddAllShownText));
            AddToRunCommand.NotifyCanExecuteChanged();
            AddAllShownToRunCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Asks the user about unsaved edits. Set by the window; without one, edits are kept (Stay).</summary>
        public Func<string, UnsavedChoice>? ConfirmUnsaved { get; set; }

        /// <summary>Asks a yes/no question. Set by the window; without one, the answer is no.</summary>
        public Func<string, bool>? Confirm { get; set; }

        // ── The list ───────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<StoryRow> Stories { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ListSummary))]
        private bool _isLoading;

        [ObservableProperty]
        private string _searchText = string.Empty;

        partial void OnSearchTextChanged(string value) => ApplyFilter();

        private StoryRow? _selectedStory;

        /// <summary>The story in the editor. Leaving one with unsaved edits asks first; answering Stay puts
        /// the selection back.</summary>
        public StoryRow? SelectedStory
        {
            get => _selectedStory;
            set
            {
                if (ReferenceEquals(_selectedStory, value)) return;
                if (!_skipUnsavedCheck && IsDirty && !ResolveUnsaved())
                {
                    // Raised after the list has finished moving its own selection, or it would win.
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(
                        () => OnPropertyChanged(nameof(SelectedStory)));
                    return;
                }
                _selectedStory = value;
                OnPropertyChanged();
                LoadEditor(value?.Entry);
            }
        }

        public bool HasStories => _allRows.Count > 0;

        public string ListSummary
        {
            get
            {
                if (IsLoading) return "Reading saved stories…";
                var total = _allRows.Count;
                if (total == 0) return "No saved stories yet.";
                var inFolder = _allRows.Count(r => r.IsInRunList);
                var clips = _allRows.Sum(r => r.Entry.Clips.Count);
                return $"{total} stor{(total == 1 ? "y" : "ies")} · {clips:N0} clip prompts" +
                       (inFolder > 0 ? $" · {inFolder} on the stories list" : string.Empty);
            }
        }

        public string EmptyListText => string.IsNullOrWhiteSpace(SearchText) || !HasStories
            ? "No saved stories yet.\n\nRender a story on ⚡ H3 Express and its clip prompts land here — or " +
              "import the chains the other H3 tabs have already written."
            : "No story matches that search.";

        public bool ShowEmptyList => Stories.Count == 0 && !IsLoading;

        /// <summary>Reads the store and rebuilds the list, keeping the selection where it can.</summary>
        public async Task LoadAsync(string? focusHash = null)
        {
            IsLoading = true;
            try
            {
                var entries = await _store.ListAsync();
                var inFolder = new HashSet<string>(_folderHashes(), StringComparer.Ordinal);
                var keep = focusHash ?? _selectedStory?.Entry.StoryHash;

                _allRows.Clear();
                _allRows.AddRange(entries.Select(e => new StoryRow(e, inFolder.Contains(e.StoryHash))));
                ApplyFilter();

                var target = Stories.FirstOrDefault(r => r.Entry.StoryHash == keep)
                             ?? (keep == null ? Stories.FirstOrDefault() : null);

                if (target != null && _selectedStory != null && target.Entry.StoryHash == _selectedStory.Entry.StoryHash)
                {
                    // The same story: swap the row under the editor without losing edits in progress.
                    _selectedStory = target;
                    OnPropertyChanged(nameof(SelectedStory));
                    if (!IsDirty) LoadEditor(target.Entry);
                }
                else if (!IsDirty)
                {
                    _skipUnsavedCheck = true;
                    try { SelectedStory = target; }
                    finally { _skipUnsavedCheck = false; }
                }
            }
            catch (Exception ex)
            {
                _log($"Story prompts could not be read: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
                OnPropertyChanged(nameof(HasStories));
                OnPropertyChanged(nameof(ListSummary));
                OnPropertyChanged(nameof(ShowEmptyList));
                OnPropertyChanged(nameof(EmptyListText));
            }
        }

        /// <summary>The store changed underneath the window — a run saved a story, a regenerate saved a clip.
        /// Reloaded now unless the user is mid-edit, in which case it waits for their save or discard.</summary>
        public void OnStoreChanged()
        {
            if (IsDirty)
            {
                _reloadPending = true;
                return;
            }
            _ = LoadAsync();
        }

        /// <summary>Puts a particular story in the editor — the row a story's 📚 badge was clicked on.</summary>
        public async Task FocusAsync(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return;
            var row = _allRows.FirstOrDefault(r => r.Entry.StoryHash == hash);
            if (row == null)
            {
                await LoadAsync(hash);
                return;
            }
            if (!Stories.Contains(row)) SearchText = string.Empty;
            SelectedStory = row;
        }

        private void ApplyFilter()
        {
            var q = (SearchText ?? string.Empty).Trim();
            var rows = q.Length == 0
                ? _allRows
                : _allRows.Where(r => r.Matches(q)).ToList();

            Stories.Clear();
            foreach (var r in rows) Stories.Add(r);
            OnPropertyChanged(nameof(ShowEmptyList));
            OnPropertyChanged(nameof(EmptyListText));
            RaiseRunListState();
        }

        // ── The editor ─────────────────────────────────────────────────────────────────────────────

        public ObservableCollection<ClipPromptRow> Clips { get; } = new();

        public bool HasSelection => _editing != null;

        [ObservableProperty]
        private string _editorTitle = string.Empty;

        partial void OnEditorTitleChanged(string value) => RaiseEditorState();

        [ObservableProperty]
        private string _editorWardrobe = string.Empty;

        partial void OnEditorWardrobeChanged(string value) => RaiseEditorState();

        public string StoryText => _editing?.StoryText ?? string.Empty;

        public string StoryWordCount
        {
            get
            {
                var words = StoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                return words == 0 ? string.Empty : $"{words:N0} words";
            }
        }

        /// <summary>The facts about the selected story, as chips under its title.</summary>
        public IReadOnlyList<string> EditorFacts
        {
            get
            {
                var e = _editing;
                if (e == null) return Array.Empty<string>();
                var facts = new List<string> { $"{Clips.Count} clip{(Clips.Count == 1 ? string.Empty : "s")}" };
                if (e.LengthSeconds > 0) facts.Add($"{e.LengthSeconds.ToString("0.#", CultureInfo.CurrentCulture)} s per clip");
                if (e.PromptBuild == "researched") facts.Add("Researched build");
                else if (e.PromptBuild == "shipped") facts.Add("Shipped build");
                if (!string.IsNullOrWhiteSpace(e.VisualStyle)) facts.Add($"Style: {e.VisualStyle}");
                if (e.UseCount > 0) facts.Add($"Used {e.UseCount}×");
                if (e.EditedByHand) facts.Add("Edited by hand");
                return facts;
            }
        }

        public string EditorOrigin
        {
            get
            {
                var e = _editing;
                if (e == null) return string.Empty;
                var origin = string.IsNullOrWhiteSpace(e.Origin) ? "Saved" : e.Origin;
                var file = string.IsNullOrWhiteSpace(e.SourceFileName) ? string.Empty : $" · from {e.SourceFileName}";
                return $"{origin}{file} · updated {e.ModifiedAt:d MMM yyyy, HH:mm}";
            }
        }

        public bool IsDirty =>
            _editing != null &&
            (!string.Equals(EditorTitle.Trim(), _editing.Title, StringComparison.Ordinal) ||
             !string.Equals(EditorWardrobe.Trim(), (_editing.Wardrobe ?? string.Empty).Trim(), StringComparison.Ordinal) ||
             Clips.Count != _editing.Clips.Count ||
             Clips.Any(c => c.IsEdited || c.IsNew));

        public string DirtySummary
        {
            get
            {
                if (_editing == null) return string.Empty;
                if (!IsDirty) return "All changes saved";
                var parts = new List<string>();
                var edited = Clips.Count(c => c.IsEdited && !c.IsNew);
                if (edited > 0) parts.Add($"{edited} clip{(edited == 1 ? string.Empty : "s")} edited");
                var added = Clips.Count(c => c.IsNew);
                if (added > 0) parts.Add($"{added} added");
                var removed = _editing.Clips.Count - Clips.Count(c => !c.IsNew);
                if (removed > 0) parts.Add($"{removed} removed");
                if (!string.Equals(EditorWardrobe.Trim(), (_editing.Wardrobe ?? string.Empty).Trim(), StringComparison.Ordinal))
                    parts.Add("wardrobe changed");
                if (!string.Equals(EditorTitle.Trim(), _editing.Title, StringComparison.Ordinal)) parts.Add("renamed");
                return "Unsaved: " + string.Join(", ", parts);
            }
        }

        private void LoadEditor(SavedStoryPrompts? entry)
        {
            _loadingEditor = true;
            try
            {
                foreach (var c in Clips) c.PropertyChanged -= Clip_PropertyChanged;
                Clips.Clear();
                _editing = entry?.Clone();
                if (_editing != null)
                {
                    for (var i = 0; i < _editing.Clips.Count; i++)
                        AddClipRow(new ClipPromptRow(i + 1, _editing.Clips[i], isNew: false));
                }
                EditorTitle = _editing?.Title ?? string.Empty;
                EditorWardrobe = _editing?.Wardrobe ?? string.Empty;
            }
            finally
            {
                _loadingEditor = false;
            }
            OnPropertyChanged(nameof(HasSelection));
            RaiseRunListState();
            OnPropertyChanged(nameof(StoryText));
            OnPropertyChanged(nameof(StoryWordCount));
            OnPropertyChanged(nameof(EditorOrigin));
            RaiseEditorState();
        }

        private void AddClipRow(ClipPromptRow row, int at = -1)
        {
            row.PropertyChanged += Clip_PropertyChanged;
            if (at < 0 || at >= Clips.Count) Clips.Add(row);
            else Clips.Insert(at, row);
        }

        private void Clip_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(ClipPromptRow.Text) or nameof(ClipPromptRow.IsEdited)) RaiseEditorState();
        }

        private void RaiseEditorState()
        {
            if (_loadingEditor) return;
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(DirtySummary));
            OnPropertyChanged(nameof(EditorFacts));
            SaveCommand.NotifyCanExecuteChanged();
            DiscardCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            AddClipCommand.NotifyCanExecuteChanged();
            OpenSourceCommand.NotifyCanExecuteChanged();
        }

        private void Renumber()
        {
            for (var i = 0; i < Clips.Count; i++) Clips[i].Index = i + 1;
        }

        // ── Commands ───────────────────────────────────────────────────────────────────────────────

        private bool CanSave => IsDirty && Clips.Any(c => !string.IsNullOrWhiteSpace(c.Text));

        [RelayCommand(CanExecute = nameof(CanSave))]
        private async Task SaveAsync() => await SaveCoreAsync(stayOnStory: true);

        /// <summary>Writes the editor back as the story's prompts. Returns whether it was written.</summary>
        /// <param name="stayOnStory">False when the save was asked for on the way to another story: the
        /// editor is no longer this story's by the time the write lands, and must not be reloaded with it.</param>
        private async Task<bool> SaveCoreAsync(bool stayOnStory)
        {
            if (_editing == null) return false;

            var clips = Clips.Select(c => c.StoredText).Where(c => c.Length > 0).ToList();
            if (clips.Count == 0) return false;

            var updated = _editing.Clone();
            updated.Title = EditorTitle.Trim().Length > 0 ? EditorTitle.Trim() : _editing.Title;
            updated.Wardrobe = EditorWardrobe.Trim();
            updated.Clips = clips;
            updated.EditedByHand = true;
            updated.ModifiedAt = DateTime.Now;

            try
            {
                await _store.SaveAsync(updated);
            }
            catch (Exception ex)
            {
                _log($"📚 Could not save \"{updated.Title}\": {ex.Message}");
                return false;
            }

            _log($"📚 Saved \"{updated.Title}\" — {clips.Count} clip prompt(s). The next render of this story uses them.");
            _reloadPending = false;
            if (stayOnStory)
            {
                LoadEditor(updated);
                await LoadAsync(updated.StoryHash);
            }
            else
            {
                await LoadAsync();
            }
            return true;
        }

        [RelayCommand(CanExecute = nameof(IsDirty))]
        private void Discard()
        {
            LoadEditor(_selectedStory?.Entry);
            if (_reloadPending)
            {
                _reloadPending = false;
                _ = LoadAsync();
            }
        }

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private async Task DeleteAsync()
        {
            var entry = _editing;
            if (entry == null) return;
            var ask = $"Forget the saved prompts for \"{entry.Title}\"?\n\nThe next time this story is rendered its " +
                      "clips are written from scratch by the clip writer. The prompts are kept in the history folder.";
            if (Confirm?.Invoke(ask) != true) return;

            await _store.DeleteAsync(entry.StoryHash);
            _log($"📚 Forgot the saved prompts for \"{entry.Title}\" — its next render writes them again.");

            _selectedStory = null;
            OnPropertyChanged(nameof(SelectedStory));
            LoadEditor(null);
            await LoadAsync();
        }

        [RelayCommand(CanExecute = nameof(HasSelection))]
        private void AddClip()
        {
            var row = new ClipPromptRow(Clips.Count + 1, string.Empty, isNew: true);
            AddClipRow(row);
            Renumber();
        }

        [RelayCommand]
        private void RemoveClip(ClipPromptRow? row)
        {
            if (row == null || !Clips.Contains(row)) return;
            if (!string.IsNullOrWhiteSpace(row.Text) &&
                Confirm?.Invoke($"Remove clip {row.Index} from this story? Nothing is saved until you press Save.") != true)
                return;
            row.PropertyChanged -= Clip_PropertyChanged;
            Clips.Remove(row);
            Renumber();
        }

        [RelayCommand]
        private void RevertClip(ClipPromptRow? row) => row?.Revert();

        [RelayCommand]
        private void MoveClipUp(ClipPromptRow? row) => Move(row, -1);

        [RelayCommand]
        private void MoveClipDown(ClipPromptRow? row) => Move(row, +1);

        private void Move(ClipPromptRow? row, int by)
        {
            if (row == null) return;
            var from = Clips.IndexOf(row);
            var to = from + by;
            if (from < 0 || to < 0 || to >= Clips.Count) return;
            Clips.Move(from, to);
            Renumber();
            // Order is part of the story; a reorder alone is an edit.
            foreach (var c in Clips) c.RefreshEdited();
            RaiseEditorState();
        }

        [ObservableProperty]
        private bool _isImporting;

        [RelayCommand]
        private async Task ImportAsync()
        {
            IsImporting = true;
            try
            {
                var added = await _store.ImportAllLegacyAsync();
                _log(added == 0
                    ? "📚 Import: every story in the other H3 tabs' prompt libraries is already here."
                    : $"📚 Import: {added} stor{(added == 1 ? "y" : "ies")} adopted from the other H3 tabs' prompt libraries.");
                await LoadAsync();
            }
            catch (Exception ex)
            {
                _log($"📚 Import failed: {ex.Message}");
            }
            finally
            {
                IsImporting = false;
            }
        }

        private bool HasSource => _editing != null && !string.IsNullOrWhiteSpace(_editing.SourcePath);

        [RelayCommand(CanExecute = nameof(HasSource))]
        private void OpenSource()
        {
            var path = _editing?.SourcePath;
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            }
            catch (Exception ex)
            {
                _log($"Could not open the story's folder: {ex.Message}");
            }
        }

        [RelayCommand]
        private void OpenStoreFolder()
        {
            try
            {
                Directory.CreateDirectory(_store.RootFolder);
                System.Diagnostics.Process.Start("explorer.exe", _store.RootFolder);
            }
            catch (Exception ex)
            {
                _log($"Could not open the story prompts folder: {ex.Message}");
            }
        }

        /// <summary>
        /// Settles unsaved edits before the editor moves on. True when it may move: the edits were saved,
        /// or thrown away. The save is synchronous from the caller's point of view only in that it has
        /// started — a failure is logged and the edits are gone from the editor but not from the store.
        /// </summary>
        public bool ResolveUnsaved()
        {
            if (!IsDirty) return true;
            var choice = ConfirmUnsaved?.Invoke(
                $"\"{EditorTitle}\" has unsaved changes ({DirtySummary.Replace("Unsaved: ", string.Empty)}).") ?? UnsavedChoice.Stay;
            switch (choice)
            {
                case UnsavedChoice.Save:
                    if (!CanSave) return false;
                    // Everything the save needs is read before its first await, so the editor may move on.
                    _ = SaveCoreAsync(stayOnStory: false);
                    return true;
                case UnsavedChoice.Discard:
                    LoadEditor(_selectedStory?.Entry);
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>One saved story in the list.</summary>
    public sealed class StoryRow
    {
        public StoryRow(SavedStoryPrompts entry, bool isInRunList)
        {
            Entry = entry;
            IsInRunList = isInRunList;
        }

        public SavedStoryPrompts Entry { get; }

        public bool IsInRunList { get; }

        public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? "Untitled story" : Entry.Title;

        public int ClipCount => Entry.Clips.Count;

        public string Meta
        {
            get
            {
                var len = Entry.LengthSeconds > 0
                    ? $"{Entry.LengthSeconds.ToString("0.#", CultureInfo.CurrentCulture)} s clips · "
                    : string.Empty;
                return $"{len}{Entry.ModifiedAt:d MMM}";
            }
        }

        /// <summary>A line of the story itself, so two stories with similar names can be told apart.</summary>
        public string Excerpt
        {
            get
            {
                var t = string.Join(' ', (Entry.StoryText ?? string.Empty)
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                return t.Length > 110 ? t[..110].TrimEnd() + "…" : t;
            }
        }

        public bool IsEdited => Entry.EditedByHand;

        public bool IsImported => Entry.Origin.StartsWith("Imported", StringComparison.OrdinalIgnoreCase);

        public bool Matches(string query) =>
            Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            Entry.SourceFileName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            Entry.StoryText.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            Entry.Clips.Any(c => c.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>One clip's prompt in the editor.</summary>
    public sealed partial class ClipPromptRow : ObservableObject
    {
        /// <summary>A shot marker with the whitespace in front of it.</summary>
        private static readonly Regex ShotWithLead = new(@"(\s*)(\[\s*Shot\s*\d+\s*\])", RegexOptions.Compiled);

        private readonly string _stored;
        private readonly string _original;
        private readonly int _originalIndex;

        /// <summary>Per shot marker in the stored clip, in order: the exact whitespace in front of it, and
        /// whether the editor had to break the line there (false when the marker already started a line).</summary>
        private readonly List<(string Lead, bool Inserted)> _leads = new();

        /// <param name="text">The clip as stored.</param>
        public ClipPromptRow(int index, string text, bool isNew)
        {
            _originalIndex = index;
            _stored = (text ?? string.Empty).Trim();
            _original = ToDisplay(_stored);
            IsNew = isNew;
            Index = index;
            Text = _original;
        }

        /// <summary>
        /// What is saved. The box shows every <c>[Shot n]</c> on a line of its own, because a clip is a list of
        /// shots and a 500-word paragraph cannot be edited. Each line break the editor put in goes back to exactly
        /// the whitespace it replaced, so a clip whose words were not touched saves byte for byte as it was
        /// stored; a shot typed in new is joined with one space. An untouched clip is saved exactly as stored.
        /// </summary>
        public string StoredText => !IsNew && !CanRevert ? _stored : FromDisplay(Normalize(Text));

        /// <summary>Puts a line break in front of every shot marker that is not already at the start of a line.</summary>
        private string ToDisplay(string stored)
        {
            _leads.Clear();
            return ShotWithLead.Replace(stored, m =>
            {
                var lead = m.Groups[1].Value;
                var onItsOwnLine = m.Index == 0 || lead.Contains('\n');
                _leads.Add((lead, !onItsOwnLine));
                return (onItsOwnLine ? lead : "\n") + m.Groups[2].Value;
            });
        }

        /// <summary>The reverse of <see cref="ToDisplay"/>, marker by marker in order.</summary>
        private string FromDisplay(string display)
        {
            var k = 0;
            return ShotWithLead.Replace(display, m =>
            {
                var lead = m.Groups[1].Value;
                var i = k++;
                if (lead != "\n" || m.Index == 0) return m.Value;
                if (i < _leads.Count && _leads[i].Inserted) return _leads[i].Lead + m.Groups[2].Value;
                return i < _leads.Count ? m.Value : " " + m.Groups[2].Value;
            }).Trim();
        }

        /// <summary>True for a clip added in this edit — it has no saved version to revert to.</summary>
        public bool IsNew { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        private int _index;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsEdited))]
        [NotifyPropertyChangedFor(nameof(Stats))]
        [NotifyPropertyChangedFor(nameof(CanRevert))]
        [NotifyPropertyChangedFor(nameof(NamesCharacter1))]
        [NotifyPropertyChangedFor(nameof(NamesCharacter2))]
        private string _text = string.Empty;

        public string Header => $"CLIP {Index:00}";

        public bool IsEdited => IsNew || !string.Equals(Normalize(Text), Normalize(_original), StringComparison.Ordinal)
                                || Index != _originalIndex;

        public bool CanRevert => !IsNew && !string.Equals(Normalize(Text), Normalize(_original), StringComparison.Ordinal);

        public bool NamesCharacter1 => Text.Contains("<Picture 1>", StringComparison.Ordinal);

        public bool NamesCharacter2 => Text.Contains("<Picture 2>", StringComparison.Ordinal);

        public string Stats
        {
            get
            {
                var shots = 0;
                var i = Text.IndexOf("[Shot ", StringComparison.OrdinalIgnoreCase);
                while (i >= 0)
                {
                    shots++;
                    i = Text.IndexOf("[Shot ", i + 6, StringComparison.OrdinalIgnoreCase);
                }
                var words = Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
                return $"{shots} shot{(shots == 1 ? string.Empty : "s")} · {words:N0} words";
            }
        }

        public void Revert()
        {
            if (!CanRevert) return;
            Text = _original;
        }

        public void RefreshEdited() => OnPropertyChanged(nameof(IsEdited));

        private static string Normalize(string? s) => (s ?? string.Empty).Replace("\r\n", "\n").Trim();
    }
}
