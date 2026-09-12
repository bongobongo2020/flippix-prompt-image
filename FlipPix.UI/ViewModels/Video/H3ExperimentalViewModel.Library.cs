using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// 🧪 H3 Experimental's prompt library — the same store and picker the 🌀 MiniMax I2V tab uses
    /// (<see cref="ScenePromptLibrary"/> / <see cref="ScenePromptLibraryWindow"/>), with its own folder
    /// under <c>%APPDATA%\FlipPix\prompts\h3experimental\</c> so the two tabs never show each other's
    /// entries.
    ///
    /// <para><b>What a saved take is.</b> One whole clip chain — all twelve clips, headers and all — plus
    /// the story it was written from and the plan it was written against (per-clip seconds, total
    /// duration, aspect). Writing a chain costs a long llama-server turn and is not reproducible run to
    /// run, so every chain the writer produces is filed automatically; so is every chain that reaches the
    /// queue, which is what catches one edited by hand in the prompt box.</para>
    ///
    /// <para><b>The invariant that makes recall useful: the stored chain has no cast preamble.</b> Each
    /// clip is stored as <see cref="CastPromptStamp.Strip"/> left it — canonical
    /// <c>&lt;Picture 1&gt;</c>/<c>&lt;Picture 2&gt;</c> body, no reference line, no wardrobe block. The
    /// preamble is written again on recall from the characters loaded <i>at that moment</i>, which is what
    /// lets a story saved today be rendered next month with a different cast, a different panel split and
    /// a different wardrobe. Nothing here may store a stamped chain.</para>
    ///
    /// <para>The wardrobe is recorded on the entry but deliberately <b>not</b> pushed back into the tab:
    /// the outfits belong to the cast that wore them. Recall restores the story instead, and the tab's own
    /// wardrobe pass dresses whoever is loaded now.</para>
    /// </summary>
    public partial class H3ExperimentalViewModel
    {
        private readonly ScenePromptLibrary _chainLibrary;
        private readonly SemaphoreSlim _chainLibraryLock = new(1, 1);
        private List<ScenePrompt>? _savedChains;
        private int _savedChainCount;

        /// <summary>Set while a recall is putting a saved take back on the form. The video-time setter
        /// arms the automatic writer run (see <see cref="OnLengthSecondsChanged"/>), and restoring a
        /// take's length would otherwise start a fresh chain two seconds later that overwrites the one
        /// just recalled. Protected for ⚡ H3 Express, which sets the length a saved story was written at
        /// around its queue-add for the same reason.</summary>
        protected bool _restoringChain;

        public RelayCommand OpenChainLibraryCommand { get; }
        public RelayCommand SaveChainCommand { get; }

        /// <summary>How many chains are filed. Drives the button caption, so the library advertises itself
        /// without needing a panel of its own.</summary>
        public int SavedChainCount
        {
            get => _savedChainCount;
            private set
            {
                if (_savedChainCount == value) return;
                _savedChainCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ChainLibraryLabel));
            }
        }

        public string ChainLibraryLabel =>
            SavedChainCount > 0 ? $"📚 Prompt Library ({SavedChainCount})" : "📚 Prompt Library";

        /// <summary>Reads the index in the background so the button can show a count from the first paint.
        /// Never on the constructor's thread — see the tab-open regressions this app has had for exactly
        /// that.</summary>
        private async Task PrimeChainLibraryAsync()
        {
            try
            {
                await EnsureChainsLoadedAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Prompt library unavailable: {ex.Message}");
            }
        }

        private async Task EnsureChainsLoadedAsync()
        {
            if (_savedChains != null) return;
            await _chainLibraryLock.WaitAsync();
            try
            {
                if (_savedChains != null) return;
                _savedChains = await _chainLibrary.LoadAsync();
                SavedChainCount = _savedChains.Count;
            }
            finally
            {
                _chainLibraryLock.Release();
            }
        }

        /// <summary>
        /// Files whatever is in the prompt box right now. Called automatically after every chain the
        /// writer finishes and on every Add to Queue, and by the 💾 Save button — which is the only caller
        /// that reports back when there was nothing new to file.
        /// </summary>
        private Task SaveCurrentChainAsync(bool manual) =>
            SaveChainAsync(Prompt, StoryText, CastWardrobe, ClampLength(LengthSeconds),
                           StoryDurationSeconds, ResolvedAspectRatio, manual);

        /// <summary>
        /// Files one take. The chain is stripped back to its bodies first — see the type remarks; that is
        /// the whole reason the entry is worth keeping.
        /// </summary>
        private async Task SaveChainAsync(string prompt, string story, string wardrobe, double lengthSeconds,
                                          double storySeconds, string aspectRatio, bool manual)
        {
            var body = StripChain(prompt);
            if (body.Length == 0)
            {
                if (manual) AddLog("Nothing to save — the prompt box is empty.");
                return;
            }

            var held = false;
            try
            {
                await EnsureChainsLoadedAsync();
                await _chainLibraryLock.WaitAsync();
                held = true;

                var saved = _savedChains!;

                var nameSource = ChainNameSource();

                // The thumbnail: the scene image when there is one, otherwise character 1's photo. This
                // tab's takes are usually story-only, and the lead's face is what makes a row in the
                // picker recognisable — it says which cast the chain was written for, not which cast it
                // must be rendered with.
                var thumbSource = HasSceneImage && File.Exists(SceneImagePath)
                    ? SceneImagePath
                    : Character1.SourcePath is { Length: > 0 } photo && File.Exists(photo)
                        ? photo
                        : string.Empty;

                var draft = new ScenePrompt
                {
                    Name = ScenePromptLibrary.SuggestName(nameSource, body, saved),
                    Prompt = body,
                    SceneImagePath = thumbSource,
                    StoryText = (story ?? string.Empty).Trim(),
                    Wardrobe = (wardrobe ?? string.Empty).Trim(),
                    AspectRatio = aspectRatio,
                    LengthSeconds = ClampLength(lengthSeconds),
                    StoryDurationSeconds = storySeconds,
                    Shots = CountShots(body),
                };

                // Thumbnail encoding runs inside AddOrRefresh — keep the whole thing off the UI thread.
                var (entry, isNew) = await Task.Run(() => _chainLibrary.AddOrRefresh(saved, draft));
                await _chainLibrary.SaveAsync(saved);
                SavedChainCount = saved.Count;

                var clips = SplitClips(body).Count;
                AddLog(isNew
                    ? $"Saved to the prompt library as \"{entry.Name}\" ({clips} clip(s), " +
                      $"{SavedChainCount} take(s) filed)."
                    : $"Already in the prompt library as \"{entry.Name}\" — timestamp refreshed.");
            }
            catch (Exception ex)
            {
                // Never let a library problem fail the Analyze or the queue-add that triggered it.
                AddLog($"Could not save to the prompt library: {ex.Message}");
            }
            finally
            {
                if (held) _chainLibraryLock.Release();
            }
        }

        /// <summary>
        /// Opens the picker and, on a pick, puts the take back on the form: the chain re-stamped for the
        /// cast loaded right now, the story it was written from, and the clip plan it was written against.
        ///
        /// <para>The cast is <b>not</b> restored — recalling a story against different characters is the
        /// point of the library. The wardrobe is not restored either: it dressed the old cast, and the
        /// story the recall just put back is what the tab dresses the new one from.</para>
        /// </summary>
        private async Task OpenChainLibraryAsync()
        {
            try
            {
                await EnsureChainsLoadedAsync();

                var window = new ScenePromptLibraryWindow(_chainLibrary, _savedChains!, new ScenePromptLibraryChrome
                {
                    WindowTitle = "Prompt Library — H3 Experimental",
                    Heading = "Saved story chains",
                    Subtitle = "Every chain the H3 Prompt Writer finishes, and every chain that reaches the "
                             + "queue, is filed here. Pick one to put it back in the prompt box — written "
                             + "for whichever characters are loaded now.",
                    PromptNote = "Stored as clip bodies with no cast preamble: the reference line and the "
                               + "wardrobe block are written fresh from the characters loaded in the tab, "
                               + "which is what lets one story be re-cast. Editable — your changes are "
                               + "saved to the library when you click away.",
                    EmptyText = "No saved chains yet.\nWrite one with Analyze and it lands here.",
                    NoMatchText = "No chain matches that search.",
                    UseButtonText = "✓ Use This Chain",
                    LibraryNoun = "prompt library",
                    AccentColorKey = "AccentAmberColor",
                    PromptEditable = true,
                });

                window.Owner = Application.Current?.Windows
                    .OfType<System.Windows.Window>()
                    .FirstOrDefault(w => w.IsActive);
                // CenterOwner with no owner lands the window in the top-left corner.
                if (window.Owner == null)
                    window.WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;

                var picked = window.ShowDialog() == true ? window.SelectedScene : null;
                SavedChainCount = _savedChains!.Count;
                if (picked == null) return;

                RestoreChain(picked);
            }
            catch (Exception ex)
            {
                AddLog($"Prompt library failed to open: {ex.Message}");
                MessageBox.Show($"Could not open the prompt library:\n{ex.Message}",
                    "Prompt Library", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Puts one saved take back on the form. Everything restored here feeds the video-time setter or
        /// the story setter, both of which arm background passes — hence <see cref="_restoringChain"/>:
        /// the automatic writer run is held off so the recalled chain is not overwritten by a fresh one
        /// two seconds later. The story's own cast and wardrobe passes are left to run, because dressing
        /// the newly loaded cast is exactly what they are for.
        /// </summary>
        private void RestoreChain(ScenePrompt picked)
        {
            _restoringChain = true;
            try
            {
                // Any run already armed by a length change belongs to the chain being replaced.
                _autoAnalyzeCts?.Cancel();

                if (picked.LengthSeconds > 0) LengthSeconds = ClampLength(picked.LengthSeconds);

                // Older entries predate the total; fall back to the clip length so the planner agrees with
                // the single clip that was actually saved.
                StoryDurationSeconds = picked.StoryDurationSeconds > 0
                    ? picked.StoryDurationSeconds
                    : ClampLength(picked.LengthSeconds);

                if (!string.IsNullOrEmpty(picked.AspectRatio) && AspectRatioOptions.Contains(picked.AspectRatio))
                    SelectedAspectRatio = picked.AspectRatio;

                if (!string.IsNullOrWhiteSpace(picked.StoryText)) StoryText = picked.StoryText;

                // Last, so it is not touched by anything the setters above set going.
                Prompt = StampChain(picked.Prompt);
            }
            finally
            {
                _restoringChain = false;
            }

            var cast = !HasCharacter1
                ? "No cast is loaded, so the clips carry no reference line yet"
                : $"The cast preamble was written for the {(HasCharacter2 ? "2 characters" : "1 character")} " +
                  "loaded now";

            AddLog($"Loaded \"{picked.Name}\" from the prompt library — {PromptClipCount} clip(s), " +
                   $"{picked.Shots} shots, {ClampLength(picked.LengthSeconds):0.#}s per clip, " +
                   $"{ResolvedAspectRatio}. {cast}.");

            if (!HasCharacter1)
                AddLog("Load the cast and build the sheets — Add to Queue re-stamps every clip on the way " +
                       "into the queue, so the chain does not have to be written again.");

            // The stored bodies name their cast by number, so a chain written for two fighters recalled onto
            // one leaves a <Picture 2> with no photograph behind it — said now rather than discovered in a
            // render. Read off the stored form: after stamping the tags no longer say Picture N.
            var writtenForTwo = SplitClips(picked.Prompt)
                .Any(c => c.Contains("<Picture 2>", StringComparison.Ordinal));
            if (writtenForTwo && !HasCharacter2)
                AddLog("WARNING: this chain was written for two characters and only one is loaded. The clips " +
                       "still name <Picture 2> and H3 will be handed no picture for it — load a second " +
                       "character and re-open the library, or edit those references out of the prompt box.");
            else if (!writtenForTwo && HasCharacter2)
                AddLog("NOTE: this chain was written for one character and two are loaded. Character 2's " +
                       "references are attached to every clip but no clip names them — re-run Analyze to " +
                       "write them into the story.");

            if (!string.IsNullOrWhiteSpace(picked.Wardrobe) &&
                !string.Equals(picked.Wardrobe.Trim(), CastWardrobe.Trim(), StringComparison.Ordinal))
                AddLog($"This chain was written with a wardrobe of its own — kept on the entry, not " +
                       $"restored, because it dressed the old cast:\n{picked.Wardrobe.Trim()}");
        }

        /// <summary>
        /// Files the chain on its way into the queue as well as when the writer finishes it — this is the
        /// call that catches a chain edited by hand in the prompt box, which is the version actually worth
        /// keeping. De-duplicated on the body, so queueing an unchanged chain only refreshes its timestamp.
        /// </summary>
        protected override void AddToQueue()
        {
            var queued = CanGenerate;
            base.AddToQueue();
            if (queued) _ = SaveCurrentChainAsync(manual: false);
        }

        /// <summary>
        /// What the entry is called in the picker. A story .txt gives its file name; a scene image gives
        /// its; otherwise the <b>story's</b> own opening sentence — which is what a human recognises a
        /// month later. Not the prompt's opening: an H3 body starts "[Shot 1] Cinematic fight
        /// choreography, evening…", so every chain this tab writes would be filed under nearly the same
        /// name.
        ///
        /// <para><see cref="ScenePromptLibrary.SuggestName"/> reads this as a file name, so what comes back
        /// carries nothing a path cannot hold — a full stop above all, which it would take for an
        /// extension and cut the name at.</para>
        /// </summary>
        private string ChainNameSource()
        {
            if (!string.IsNullOrEmpty(StoryFileName)) return StoryFileName;
            if (HasSceneImage) return SceneImagePath;

            var story = (StoryText ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (story.Length == 0) return string.Empty;

            // The first sentence, unless it is too short to say anything.
            var stop = story.IndexOfAny(new[] { '.', '!', '?' });
            if (stop > 12) story = story[..stop];
            if (story.Length > 48) story = story[..48];

            var illegal = Path.GetInvalidFileNameChars();
            return new string(story.Where(c => c != '.' && !illegal.Contains(c)).ToArray()).Trim();
        }

        // ── Chain ⇄ stored-body conversions ────────────────────────────────────────────────────────

        /// <summary>The chain as the library stores it: every clip through
        /// <see cref="CastPromptStamp.Strip"/>, headers rebuilt around the bodies.</summary>
        private static string StripChain(string? chain) =>
            JoinClips(SplitClips(chain)
                .Select(CastPromptStamp.Strip)
                .Where(c => c.Length > 0)
                .ToList());

        /// <summary>The chain as the prompt box wants it, stamped for the cast loaded right now.
        /// <c>selectiveCast: false</c> mirrors what the writer does on this tab — both fighters are on
        /// screen throughout a two-hander, and clipping either one's references is what renders a fighter
        /// against a duplicate of themselves.</summary>
        protected string StampChain(string? chain) =>
            JoinClips(SplitClips(chain)
                .Select(c => CastPromptStamp.Apply(c, Panels1, Panels2, CastWardrobe,
                                                   selectiveCast: false, CastDescriptor))
                .Where(c => c.Length > 0)
                .ToList());
    }
}
