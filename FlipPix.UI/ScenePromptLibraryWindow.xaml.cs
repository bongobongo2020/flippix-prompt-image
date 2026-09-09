using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace FlipPix.UI
{
    /// <summary>
    /// The wording and accent one tab wants on the shared library picker. Every value defaults to the
    /// Character tab's, because that is the tab this window was built for; another tab passes its own so
    /// the window does not describe a feature that tab does not have (the Character tab's reference-line
    /// rewriting, in particular, would be a lie on the I2V tab).
    /// </summary>
    public sealed class ScenePromptLibraryChrome
    {
        public string WindowTitle { get; init; } = "Scene Library";
        public string Heading { get; init; } = "Saved scenes";

        public string Subtitle { get; init; } =
            "Every prompt this tab analyzes is saved here. Pick one to drop it into the H3 prompt box — "
            + "the reference line is rewritten for whichever character images are loaded.";

        public string PromptNote { get; init; } =
            "Stored without its reference line — that line is written fresh from the character images "
            + "loaded in the tab.";

        public string EmptyText { get; init; } =
            "No saved scenes yet.\nAnalyze a scene image and it lands here.";

        public string NoMatchText { get; init; } = "No scene matches that search.";
        public string UseButtonText { get; init; } = "✓ Use This Scene";

        /// <summary>Confirmation copy: "Delete \"name\" from the {LibraryNoun}?"</summary>
        public string LibraryNoun { get; init; } = "scene library";

        /// <summary>Key of a <see cref="System.Windows.Media.Color"/> in the app resources — the owning tab's accent. Null
        /// leaves the window on the Character violet its XAML declares.</summary>
        public string? AccentColorKey { get; init; }

        /// <summary>
        /// Lets the preview pane be typed in, saving the edit back to the entry when it loses focus. Only
        /// for libraries whose entries are a single body — an entry with continuations is shown as a
        /// composed preview with <c>=== SEGMENT n ===</c> headers the window would have to take apart
        /// again, so those stay read-only however this is set.
        /// </summary>
        public bool PromptEditable { get; init; }
    }

    /// <summary>
    /// Picker over one tab's saved prompt library: search, preview, rename, delete, and hand one back to
    /// the tab. <c>DialogResult == true</c> means <see cref="SelectedScene"/> should be loaded into the
    /// prompt box. Which library it shows is decided entirely by the <see cref="ScenePromptLibrary"/>
    /// instance the caller passes; <see cref="ScenePromptLibraryChrome"/> supplies the wording.
    ///
    /// <para>The window owns the persisted copy while it is open — renames and deletes are written straight
    /// through, so closing with Close (rather than Use) still keeps them.</para>
    /// </summary>
    public partial class ScenePromptLibraryWindow : Window
    {
        private readonly ScenePromptLibrary _library;
        private readonly List<ScenePrompt> _entries;
        private readonly ScenePromptLibraryChrome _chrome;
        private readonly ObservableCollection<Row> _rows = new();

        /// <summary>The scene the user chose, set only when the dialog returns true.</summary>
        public ScenePrompt? SelectedScene { get; private set; }

        public ScenePromptLibraryWindow(
            ScenePromptLibrary library, List<ScenePrompt> entries, ScenePromptLibraryChrome? chrome = null)
        {
            InitializeComponent();
            _library = library;
            _entries = entries;
            _chrome = chrome ?? new ScenePromptLibraryChrome();

            ApplyChrome();

            ScenesList.ItemsSource = _rows;
            Rebuild(string.Empty);
            _ = LoadThumbnailsAsync();
        }

        /// <summary>Retitles the window for whichever tab opened it, and repaints its accent.</summary>
        private void ApplyChrome()
        {
            Title = _chrome.WindowTitle;
            HeadingText.Text = _chrome.Heading;
            SubtitleText.Text = _chrome.Subtitle;
            PromptNote.Text = _chrome.PromptNote;
            UseButton.Content = _chrome.UseButtonText;
            PromptBox.IsReadOnly = !_chrome.PromptEditable;

            // AccentButtonStyle reads TabAccentBrush dynamically, so replacing the window-level entry the
            // XAML declares is enough - no style needs to know a second tab exists.
            if (_chrome.AccentColorKey != null &&
                System.Windows.Application.Current?.TryFindResource(_chrome.AccentColorKey)
                    is System.Windows.Media.Color accent)
            {
                Resources["TabAccentBrush"] = new SolidColorBrush(accent);
            }
        }

        /// <summary>
        /// What the preview pane shows: the whole take, not just its opening. An entry with continuations
        /// is several prompts, and a pane showing only the base pass would make two takes that share an
        /// opening look identical.
        /// </summary>
        private static string ComposePreview(ScenePrompt entry)
        {
            if (entry.ContinuationPrompts.Count == 0) return entry.Prompt;

            var text = new System.Text.StringBuilder();
            text.Append("=== SEGMENT 1 ===\n").Append(entry.Prompt);
            for (var i = 0; i < entry.ContinuationPrompts.Count; i++)
            {
                text.Append("\n\n=== SEGMENT ").Append(i + 2).Append(" ===\n")
                    .Append(entry.ContinuationPrompts[i]);
            }
            return text.ToString();
        }

        /// <summary>Refills the list from <see cref="_entries"/>, keeping only rows matching the filter.</summary>
        private void Rebuild(string filter)
        {
            var previous = (ScenesList.SelectedItem as Row)?.Entry;
            _rows.Clear();

            IEnumerable<ScenePrompt> source = _entries.OrderByDescending(e => e.LastUsed);
            if (!string.IsNullOrWhiteSpace(filter))
            {
                var needle = filter.Trim();
                source = source.Where(e =>
                    e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    e.Prompt.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                    e.ContinuationPrompts.Any(p => p.Contains(needle, StringComparison.OrdinalIgnoreCase)));
            }

            foreach (var entry in source)
                _rows.Add(new Row(entry, _library.ResolveThumbnail(entry) != null));

            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Text = _entries.Count == 0 ? _chrome.EmptyText : _chrome.NoMatchText;

            if (previous != null)
                ScenesList.SelectedItem = _rows.FirstOrDefault(r => ReferenceEquals(r.Entry, previous));

            _ = LoadThumbnailsAsync();
        }

        /// <summary>
        /// Decodes thumbnails off the UI thread and pushes them into the rows as they arrive, so opening the
        /// window with a few hundred saved scenes is not a stall.
        /// </summary>
        private async Task LoadThumbnailsAsync()
        {
            var pending = _rows.Where(r => r.HasThumbnail && r.Thumbnail == null).ToList();
            if (pending.Count == 0) return;

            foreach (var row in pending)
            {
                var bitmap = await Task.Run(() => _library.LoadThumbnail(row.Entry));
                if (bitmap != null) row.Thumbnail = bitmap;
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Rebuild(SearchBox.Text);

        private void ScenesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var row = ScenesList.SelectedItem as Row;

            NameBox.Text = row?.Entry.Name ?? string.Empty;
            PromptBox.Text = row == null ? string.Empty : ComposePreview(row.Entry);

            NameBox.IsEnabled = row != null;
            PromptBox.IsEnabled = row != null;
            UseButton.IsEnabled = row != null;
            DeleteButton.IsEnabled = row != null;
        }

        private void ScenesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (ScenesList.SelectedItem is Row) Use();
        }

        private void NameBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (ScenesList.SelectedItem is not Row row) return;

            var name = NameBox.Text.Trim();
            if (name.Length == 0 || name == row.Entry.Name)
            {
                NameBox.Text = row.Entry.Name;
                return;
            }

            row.Entry.Name = name;
            row.Refresh();
            _ = _library.SaveAsync(_entries);
        }

        /// <summary>
        /// Saves an edited prompt back to the entry. Only reachable when the chrome asked for an editable
        /// pane, and refused for an entry with continuations: what the pane shows there is a <i>composed</i>
        /// preview, and writing it back whole would flatten several segments into the first one.
        /// </summary>
        private void PromptBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (!_chrome.PromptEditable) return;
            if (ScenesList.SelectedItem is not Row row) return;
            if (row.Entry.ContinuationPrompts.Count > 0) return;

            var text = PromptBox.Text.Trim();
            if (text.Length == 0 || text == row.Entry.Prompt)
            {
                // An emptied box is a slip, not an instruction to blank the entry.
                PromptBox.Text = row.Entry.Prompt;
                return;
            }

            row.Entry.Prompt = text;
            row.Entry.Shots = CountShots(text);
            row.Entry.LastUsed = DateTime.Now;
            row.Refresh();
            _ = _library.SaveAsync(_entries);
        }

        /// <summary>Mirrors the tabs' own shot counter, so an edited entry's meta line stays true.</summary>
        private static int CountShots(string prompt) =>
            Regex.Matches(prompt, @"\[\s*Shot\s+\d+", RegexOptions.IgnoreCase).Count;

        /// <summary>Clip headers in a stored chain — <c>=== CLIP 3 of 12 ===</c> and the looser shapes the
        /// tabs accept. 0 for a library whose entries are single prompts.</summary>
        private static int CountClips(string prompt) =>
            Regex.Matches(prompt, @"^[ 	]*[=#*\-–—\[]{0,6}[ 	]*CLIP[ 	]+\d+\b",
                          RegexOptions.IgnoreCase | RegexOptions.Multiline).Count;

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ScenesList.SelectedItem is not Row row) return;

            var confirm = MessageBox.Show(
                $"Delete \"{row.Entry.Name}\" from the {_chrome.LibraryNoun}?\n\nThis cannot be undone.",
                "Delete Scene", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            _library.DeleteThumbnail(row.Entry.ThumbnailFile);
            _entries.Remove(row.Entry);
            _rows.Remove(row);

            _library.Save(_entries);
            Rebuild(SearchBox.Text);
        }

        private void UseButton_Click(object sender, RoutedEventArgs e) => Use();

        private void Use()
        {
            if (ScenesList.SelectedItem is not Row row) return;

            row.Entry.LastUsed = DateTime.Now;
            row.Entry.UseCount++;
            _library.Save(_entries);

            SelectedScene = row.Entry;
            DialogResult = true;
            Close();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>One list row. The thumbnail arrives after construction, hence the change notification.</summary>
        private sealed class Row : INotifyPropertyChanged
        {
            private BitmapImage? _thumbnail;

            public Row(ScenePrompt entry, bool hasThumbnail)
            {
                Entry = entry;
                HasThumbnail = hasThumbnail;
            }

            public ScenePrompt Entry { get; }
            public bool HasThumbnail { get; }

            public string Name => Entry.Name;

            /// <summary>Second line: enough to tell two similar scenes apart at a glance.</summary>
            public string Meta
            {
                get
                {
                    var parts = new List<string> { Entry.LastUsed.ToString("d MMM yyyy") };
                    if (Entry.ContinuationPrompts.Count > 0)
                        parts.Add($"{Entry.ContinuationPrompts.Count + 1} passes");
                    var clips = CountClips(Entry.Prompt);
                    if (clips > 1) parts.Add($"{clips} clips");
                    if (Entry.Shots > 0) parts.Add($"{Entry.Shots} shots");
                    if (Entry.LengthSeconds > 0) parts.Add($"{Entry.LengthSeconds:0.#}s");
                    if (!string.IsNullOrEmpty(Entry.AspectRatio)) parts.Add(Entry.AspectRatio);
                    if (Entry.UseCount > 0) parts.Add($"used {Entry.UseCount}×");
                    return string.Join(" · ", parts);
                }
            }

            public BitmapImage? Thumbnail
            {
                get => _thumbnail;
                set { _thumbnail = value; OnPropertyChanged(); }
            }

            /// <summary>Re-reads the entry after a rename.</summary>
            public void Refresh()
            {
                OnPropertyChanged(nameof(Name));
                OnPropertyChanged(nameof(Meta));
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
