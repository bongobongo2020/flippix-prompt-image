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

        /// <summary>Full path on disk; the file is not read until this story's turn.</summary>
        public string FilePath { get; }

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
                    BatchStoryState.Waiting => "waiting",
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
