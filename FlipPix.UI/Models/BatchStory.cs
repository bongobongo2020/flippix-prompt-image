using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace FlipPix.UI.Models
{
    /// <summary>Where one story is in the batch. Ordered as it is walked, so a glance down the column
    /// reads as progress.</summary>
    public enum BatchStoryState
    {
        Waiting,
        Processing,
        Done,
        Failed,
        Skipped
    }

    /// <summary>
    /// One <c>.txt</c> in the 🗂️ H3 Batch folder, and what happened to it.
    ///
    /// <para>It carries no story text. A folder can hold a hundred files and only one of them is ever
    /// being rendered — the text is read off disk at the moment its turn comes and lives in the tab's own
    /// story box from then on, exactly as if it had been pasted there. What is kept here is what the list
    /// has to show after the run has moved on: the name, the outcome, and the film it produced.</para>
    /// </summary>
    public partial class BatchStory : ObservableObject
    {
        public BatchStory(string path)
        {
            FilePath = path;
            FileName = Path.GetFileName(path);
            Title = Path.GetFileNameWithoutExtension(path);
        }

        /// <summary>What marks a <see cref="FilePath"/> as a saved story rather than a file.</summary>
        public const string LibraryPrefix = "library:";

        /// <summary>
        /// A story added from ⚡ H3 Express's 📚 Story prompts instead of found in the folder. Its text comes with
        /// it — it may have been imported from another tab and have no file anywhere — so unlike a folder row it
        /// carries the story itself (<see cref="InlineText"/>). Its <see cref="FilePath"/> is only a unique key.
        /// </summary>
        public BatchStory(string title, string storyText, string storyHash)
        {
            Title = string.IsNullOrWhiteSpace(title) ? "story" : title.Trim();
            FileName = Title + ".txt";
            FilePath = LibraryPrefix + storyHash;
            InlineText = storyText;
            StoryHash = storyHash;
        }

        /// <summary>Full path on disk; the file is not read until this story's turn. For a saved story, a key.</summary>
        public string FilePath { get; }

        /// <summary>The story's text for a saved story; null for a folder row, which is read when its turn comes.</summary>
        public string? InlineText { get; }

        public bool IsFromLibrary => InlineText != null;

        public string FileName { get; }

        /// <summary>The name without its extension — what the clips and the joined film are named after,
        /// and what the list shows.</summary>
        public string Title { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(IsWaiting))]
        [NotifyPropertyChangedFor(nameof(IsProcessing))]
        [NotifyPropertyChangedFor(nameof(IsDone))]
        [NotifyPropertyChangedFor(nameof(IsFailed))]
        private BatchStoryState _state = BatchStoryState.Waiting;

        /// <summary>The live phase inside a story — "3/6 · Photographing character 1…" — or the reason it
        /// failed. Whatever the row has to say beyond its state.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private string _detail = string.Empty;

        /// <summary>The joined film, once there is one.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOutput))]
        private string _outputPath = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private TimeSpan _elapsed;

        /// <summary>How many clips this story was written as, once Analyze has said.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        private int _clipCount;

        /// <summary>The story's text hash once a tab that files prompts by story has read it (⚡ H3 Express),
        /// or empty. Filled in the background after a scan — see <c>StoryPromptStore.HashStory</c>.</summary>
        [ObservableProperty]
        private string _storyHash = string.Empty;

        /// <summary>How many clip prompts are saved for this story — nonzero means it renders without the
        /// clip writer.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasSavedPrompts))]
        [NotifyPropertyChangedFor(nameof(SavedPromptsTip))]
        private int _savedClipCount;

        public bool HasSavedPrompts => SavedClipCount > 0;

        public string SavedPromptsTip =>
            $"{SavedClipCount} clip prompt{(SavedClipCount == 1 ? string.Empty : "s")} saved for this story — it " +
            "renders from them without running the clip writer. Click to open them.";

        public bool IsWaiting => State == BatchStoryState.Waiting;
        public bool IsProcessing => State == BatchStoryState.Processing;
        public bool IsDone => State == BatchStoryState.Done;
        public bool IsFailed => State == BatchStoryState.Failed;
        public bool HasOutput => !string.IsNullOrEmpty(OutputPath) && File.Exists(OutputPath);

        /// <summary>The one line the row shows: the state, and whatever is worth knowing beside it.</summary>
        public string StatusText
        {
            get
            {
                var clips = ClipCount > 0 ? $" · {ClipCount} clip{(ClipCount == 1 ? string.Empty : "s")}" : string.Empty;
                var took = Elapsed > TimeSpan.Zero ? $" · {Elapsed.TotalMinutes:0.#} min" : string.Empty;
                var detail = Detail.Length > 0 ? $" · {Detail}" : string.Empty;
                return State switch
                {
                    BatchStoryState.Waiting => IsFromLibrary ? "waiting · saved story" : "waiting",
                    BatchStoryState.Processing => "processing" + clips + detail,
                    BatchStoryState.Done => "done" + clips + took,
                    BatchStoryState.Failed => "failed" + detail,
                    _ => "skipped" + detail
                };
            }
        }

        /// <summary>Puts the row back to Waiting so a stopped or failed batch can be run again without
        /// rescanning the folder — and without losing what the finished ones produced.</summary>
        public void Reset()
        {
            State = BatchStoryState.Waiting;
            Detail = string.Empty;
            Elapsed = TimeSpan.Zero;
            ClipCount = 0;
        }
    }
}
