using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "⚡ H3 Express" — 🗂️ H3 Batch without the seed hunt. A folder of story <c>.txt</c> files goes in,
    /// and each one comes out as a joined film, rendered one clip at a time.
    ///
    /// <para><b>What it leaves out.</b> Batch hunts three drafts for every clip, takes the first, then
    /// re-samples that branch and upscales it — so each clip is a hunt submission <i>and</i> a finish
    /// submission, and two of the three drafts are thrown away unseen. Here there is nothing to choose
    /// between and nobody choosing, so the hunt is skipped outright: every clip is given its seed up front
    /// and goes straight to the finish graph — one submission per clip, composed at the draft canvas and
    /// latent-upscaled to the finished one inside the same graph.</para>
    ///
    /// <para><b>What it keeps.</b> Everything else is Batch's loop, unchanged: a fresh cast per story, the
    /// wardrobe, the Krea2-Spicy portraits, the sheets, the clip writer, the queue, the finish sweep and the
    /// join. It only swaps <see cref="H3ErosViewModel.RunSweepsAsync"/>, so a fix to the render path is a
    /// fix here too.</para>
    ///
    /// <para><b>Defaults.</b> ✴️ Singularity and 📚 Researched prompts both start on — the quickest stack
    /// and the prompt build written from the MiniMax-H3 guides. Singularity is remembered in its own
    /// settings slot; researched prompts are switched on at every launch, since that build is what this tab
    /// is for. There is no VR mode.</para>
    /// </summary>
    public class H3ExpressViewModel : H3BatchViewModel
    {
        /// <summary>The sampler branch every clip renders on. A finish needs a branch to keep; with no hunt,
        /// branch 1 is simply the one kept.</summary>
        private const int RenderSlot = 1;

        public H3ExpressViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider,
                   workflowCoordinator, fileDialogService)
        {
            ResearchPrompts = true;

            PlayStoryCommand = new RelayCommand<BatchStory>(PlayStory);
            SelectClipCommand = new RelayCommand<ErosHuntClip>(SelectClip);
            CloseClipEditorCommand = new RelayCommand(() => SelectedClip = null);
            RegenerateClipCommand = new RelayCommand(RegenerateSelectedClip, () => CanRegenerateSelectedClip);
            RevertClipPromptCommand = new RelayCommand(
                () => { if (SelectedClip != null) ClipPromptDraft = SelectedClip.Item.Prompt; },
                () => IsClipPromptEdited);
            UnqueueClipCommand = new RelayCommand(UnqueueSelectedClip,
                                                  () => SelectedClip != null && IsQueued(SelectedClip));

            // A story's clips leave the board when the next story starts. The editor and any regenerate
            // still waiting for one of them go with it.
            HuntBoard.CollectionChanged += (_, _) => OnBoardChanged();

            // The summaries below are computed; say when what they are computed from moves.
            PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(IsBatchRunning):
                        OnPropertyChanged(nameof(RunButtonText));
                        TryStartQueuedRegenerations();
                        break;
                    case nameof(IsProcessingQueue):
                    case nameof(IsFeelingLucky):
                    case nameof(IsBuildingSheets):
                        TryStartQueuedRegenerations();
                        break;
                    case nameof(UseSingularity):
                        OnPropertyChanged(nameof(StackSummary));
                        break;
                    case nameof(ResearchPrompts):
                        OnPropertyChanged(nameof(PromptBuildSummary));
                        break;
                    case nameof(Megapixels):
                    case nameof(SelectedAspectRatio):
                        OnPropertyChanged(nameof(HuntSummary));
                        break;
                    case nameof(ProcessingProgress):
                    case nameof(HasBoard):
                        OnPropertyChanged(nameof(ClipProgressText));
                        break;
                }
            };
        }

        protected override void LogIntro() =>
            AddLog("H3 Express initialized — point it at a folder of story .txt files and press ⚡ Render. " +
                   "Each story gets its own cast, sheets and clips, then ONE render per clip — no seed hunt — " +
                   "and the joined film. ✴️ Singularity and 📚 researched prompts are on by default.");

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        protected override string OutputSubfolder => "h3_express";

        protected override string FileStemPrefix => "H3Express";

        protected override string OutputFolderName => "H3Express";

        protected override string TabDisplayName => "H3 Express";

        protected override string ChainLibraryFolder => "h3express";

        protected override string RunTokenPrefix => "h3express";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3express_queue.json");

        protected override string? RecallDiffusionModel(ComfyUISettings? settings) =>
            settings?.H3ExpressDiffusionModel;

        protected override void StoreDiffusionModel(ComfyUISettings settings, string name) =>
            settings.H3ExpressDiffusionModel = name;

        protected override string? RecallBatchFolder(ComfyUISettings? settings) => settings?.H3ExpressFolder;

        protected override void StoreBatchFolder(ComfyUISettings settings, string folder) =>
            settings.H3ExpressFolder = folder;

        /// <summary>On unless it has been switched off here — the default the tab was asked for.</summary>
        protected override bool RecallUseSingularity(ComfyUISettings? settings) =>
            settings?.H3ExpressUseSingularity ?? true;

        protected override void StoreUseSingularity(ComfyUISettings settings, bool value) =>
            settings.H3ExpressUseSingularity = value;

        // No VR mode: never read on, never stored, never active.
        protected override bool RecallRenderAsVr(ComfyUISettings? settings) => false;

        protected override void StoreRenderAsVr(ComfyUISettings settings, bool value) { }

        protected override bool VrPipelineActive => false;

        // ── The render: no hunt ─────────────────────────────────────────────────────────────────────

        /// <summary>Nothing to arrange. The Eros version turns on auto-pick so a hunt does not park at the
        /// board; with no hunt there is no board to park at.</summary>
        protected override void PrepareForUnattendedRun() { }

        /// <summary>A clip here is a render, not a picked take.</summary>
        protected override bool NamesTakes => false;

        protected override string LuckyRenderPhase => "Rendering and joining…";

        /// <summary>
        /// Every waiting clip gets its seed and its branch now, then the finish sweep renders them in queue
        /// order — each one a single submission, and the story joined when its last clip lands.
        ///
        /// <para>The seed is the clip's own when it has one, as the hunt would have used for take 1, so a
        /// story's clips still share a seed. The prompt stamp is written the way a hunt writes it, so a clip
        /// is never read as reworded-since-hunted and skipped.</para>
        /// </summary>
        protected override async Task RunSweepsAsync(CancellationToken token)
        {
            var toSeed = HuntBoard.Where(c => !c.IsFinished && !c.HasPick &&
                                              c.Item.ItemStatus == QueueItemStatus.Pending).ToList();
            foreach (var row in toSeed)
            {
                var item = row.Item;
                var seed = item.Seed >= 0 ? item.Seed : Random.Shared.NextInt64(0, long.MaxValue);
                item.HuntBaseSeed = seed;
                item.ChosenSampleSlot = RenderSlot;
                item.ChosenSeed = seed;
                item.HuntPromptStamp = item.Prompt;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.ApplyPick(RenderSlot);
                    row.Status = "waiting";
                });
            }

            if (toSeed.Count > 0)
            {
                SaveQueueToFile();
                AddLog($"=== ⚡ Express: {toSeed.Count} clip(s), one render each — no seed hunt ===");
            }

            try
            {
                await FinishSweepAsync(token);
                // Regenerates asked for while the story rendered, before the batch moves on and clears
                // the board — after that there is no clip left to re-render.
                await DrainRegenerationsAsync(token);
            }
            catch (OperationCanceledException)
            {
                ClearRegenerations("the render was stopped");
                throw;
            }
        }

        /// <summary>
        /// Named after the story on the board, not only the one in flight. A clip regenerated after the run
        /// has ended is still one of that story's clips, and has to overwrite its own file and re-join the
        /// same film, not start a new one under the bare prefix.
        /// </summary>
        protected override string OutputFileStem =>
            (CurrentStory ?? StoryOnBoard) is { } story ? $"{FileStemPrefix}_{SafeName(story.Title)}" : FileStemPrefix;

        /// <summary>The ordinary finish, captioned for a tab that has no takes: "the-oasis · Clip 3 / 12".</summary>
        protected override async Task FinishAsync(ErosHuntClip row, double progressFrom, double progressTo,
            CancellationToken token)
        {
            await base.FinishAsync(row, progressFrom, progressTo, token);
            if (row.OutputPath == null) return;

            Application.Current.Dispatcher.Invoke(() => ShowInPlayer(row.OutputPath, ClipCaption(row)));
        }

        private string ClipCaption(ErosHuntClip row) =>
            (CurrentStory ?? StoryOnBoard)?.Title is { } story ? $"{story} · {row.Title}" : row.Title;

        // ── One clip: its prompt, edited and re-rendered ────────────────────────────────────────────

        /// <summary>A regenerate waiting for the GPU: the clip, the prompt to render it from, and whether
        /// to roll it a new seed.</summary>
        private sealed record ClipRegeneration(ErosHuntClip Row, string Prompt, bool NewSeed);

        /// <summary>Everything a regenerate changes on a clip, so a failed or stopped one can put it back.
        /// The old file is only overwritten when the new render lands, so until then the clip on disk is
        /// still the one this describes.</summary>
        private sealed record ClipSnapshot(
            string Prompt, string HuntPromptStamp, long HuntBaseSeed, int ChosenSampleSlot, long ChosenSeed,
            QueueItemStatus ItemStatus, string? ErrorMessage, string ErosStage, DateTime? StartedAt,
            DateTime? CompletedAt, int PickedSlot, bool IsFinished, string? OutputPath, string Summary,
            string Status, bool HasResult);

        private readonly List<ClipRegeneration> _regenerations = new();
        private ErosHuntClip? _selectedClip;
        private string _clipPromptDraft = string.Empty;
        private bool _regenerateWithNewSeed;

        public RelayCommand<ErosHuntClip> SelectClipCommand { get; }
        public RelayCommand CloseClipEditorCommand { get; }
        public RelayCommand RegenerateClipCommand { get; }
        public RelayCommand RevertClipPromptCommand { get; }
        public RelayCommand UnqueueClipCommand { get; }

        /// <summary>The clip whose prompt the editor under the chips is showing, or null when it is shut.</summary>
        public ErosHuntClip? SelectedClip
        {
            get => _selectedClip;
            set
            {
                if (_selectedClip == value) return;
                if (_selectedClip != null)
                {
                    _selectedClip.IsSelected = false;
                    _selectedClip.PropertyChanged -= SelectedClip_PropertyChanged;
                }
                _selectedClip = value;
                if (value != null)
                {
                    value.IsSelected = true;
                    value.PropertyChanged += SelectedClip_PropertyChanged;
                }

                // A clip waiting to be regenerated shows the prompt it is waiting with, not the one it has.
                _clipPromptDraft = value == null
                    ? string.Empty
                    : _regenerations.FirstOrDefault(r => r.Row == value)?.Prompt ?? value.Item.Prompt;

                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedClip));
                OnPropertyChanged(nameof(ClipPromptDraft));
                RaiseClipEditorState();
            }
        }

        public bool HasSelectedClip => _selectedClip != null;

        /// <summary>
        /// The editor's text. It starts as the clip's full prompt — exactly what its render was given, the
        /// reference line and wardrobe lock included — and nothing is written back to the clip until
        /// Regenerate is pressed, so an edit can be abandoned by picking another clip.
        /// </summary>
        public string ClipPromptDraft
        {
            get => _clipPromptDraft;
            set
            {
                if (_clipPromptDraft == value) return;
                _clipPromptDraft = value ?? string.Empty;
                OnPropertyChanged();
                RaiseClipEditorState();
            }
        }

        /// <summary>True when the editor no longer says what the clip was rendered from.</summary>
        public bool IsClipPromptEdited => _selectedClip != null && _clipPromptDraft != _selectedClip.Item.Prompt;

        /// <summary>
        /// Off, a regenerate keeps the clip's seed, so the new render differs only where the prompt does.
        /// On, it rolls a new one — a different take of the same words, for a clip whose prompt was fine and
        /// whose render was not.
        /// </summary>
        public bool RegenerateWithNewSeed
        {
            get => _regenerateWithNewSeed;
            set { if (_regenerateWithNewSeed == value) return; _regenerateWithNewSeed = value; OnPropertyChanged(); }
        }

        /// <summary>"the-oasis · Clip 3 / 12 · seed 4815162342".</summary>
        public string SelectedClipHeader
        {
            get
            {
                if (_selectedClip == null) return string.Empty;
                var seed = _selectedClip.Item.ChosenSeed;
                return ClipCaption(_selectedClip) + (seed >= 0 ? $" · seed {seed}" : string.Empty);
            }
        }

        /// <summary>Where the selected clip is, in the words the editor shows beside its header.</summary>
        public string SelectedClipStatus
        {
            get
            {
                var row = _selectedClip;
                if (row == null) return string.Empty;
                if (row.IsBusy) return "rendering…";
                if (IsQueued(row)) return "regenerate queued — it runs when the GPU is free";
                if (!string.IsNullOrEmpty(row.Status)) return row.Status;
                return row.IsFinished ? "finished" : "waiting to render";
            }
        }

        /// <summary>The button says which of its three things a press will do.</summary>
        public string RegenerateButtonText
        {
            get
            {
                var row = _selectedClip;
                if (row != null && IsQueued(row)) return "⏳ Update queued regenerate";
                if (row != null && SweepWillRender(row)) return "✓ Render with this prompt";
                return IsProcessingQueue ? "⏳ Queue regenerate" : "⚡ Regenerate clip";
            }
        }

        public string RegenerateTip =>
            IsBatchRunning && !IsProcessingQueue
                ? "Available once the story's clips are rendering — the cast, portraits and sheets are being made now."
                : "Renders this one clip again from the prompt in the box, overwrites its file and re-joins the story's " +
                  "film. The other clips are not touched.\n\nWhile something is rendering it waits its turn. A clip that " +
                  "has not rendered yet simply renders from the new prompt when the run reaches it.";

        private bool CanRegenerateSelectedClip
        {
            get
            {
                var row = _selectedClip;
                if (row == null || row.IsBusy || !HuntBoard.Contains(row)) return false;
                if (string.IsNullOrWhiteSpace(_clipPromptDraft)) return false;
                // A render holds the GPU and drains the queue when it ends. Outside one, only the cast and
                // sheet phases of a run — which have no board of their own to render — keep it shut.
                return IsProcessingQueue || (!IsBatchRunning && !IsFeelingLucky && !IsBuildingSheets);
            }
        }

        private bool IsQueued(ErosHuntClip row) => _regenerations.Any(r => r.Row == row);

        /// <summary>A clip the sweep in flight has yet to reach: seeded, waiting, not on the GPU. There is
        /// nothing to regenerate — the sweep renders it from whatever its prompt says when it gets there.</summary>
        private bool SweepWillRender(ErosHuntClip row) =>
            IsBatchRunning && IsProcessingQueue && !row.IsBusy && !row.IsFinished && row.HasPick &&
            row.Item.ItemStatus == QueueItemStatus.Pending;

        /// <summary>A chip click opens its prompt, and plays the clip when there is one to play. A second
        /// click on the open clip shuts the editor.</summary>
        private void SelectClip(ErosHuntClip? row)
        {
            if (row == null) return;
            if (row == _selectedClip)
            {
                SelectedClip = null;
                return;
            }
            SelectedClip = row;
            if (row.OutputPath != null) ShowInPlayer(row.OutputPath, ClipCaption(row));
        }

        private void SelectedClip_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
            RaiseClipEditorState();

        private void RaiseClipEditorState()
        {
            OnPropertyChanged(nameof(IsClipPromptEdited));
            OnPropertyChanged(nameof(SelectedClipHeader));
            OnPropertyChanged(nameof(SelectedClipStatus));
            OnPropertyChanged(nameof(RegenerateButtonText));
            OnPropertyChanged(nameof(RegenerateTip));
            RegenerateClipCommand?.NotifyCanExecuteChanged();
            RevertClipPromptCommand?.NotifyCanExecuteChanged();
            UnqueueClipCommand?.NotifyCanExecuteChanged();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            RaiseClipEditorState();
        }

        private void RegenerateSelectedClip()
        {
            var row = _selectedClip;
            if (row == null || !CanRegenerateSelectedClip) return;
            var prompt = _clipPromptDraft;

            if (SweepWillRender(row))
            {
                ApplyPrompt(row, prompt, RegenerateWithNewSeed);
                SaveQueueToFile();
                AddLog($"{ClipCaption(row)}: prompt edited — it renders from the new one when the run reaches it.");
                RaiseClipEditorState();
                return;
            }

            // One request per clip: pressing again while it waits replaces what it is waiting with.
            var at = _regenerations.FindIndex(r => r.Row == row);
            var request = new ClipRegeneration(row, prompt, RegenerateWithNewSeed);
            if (at >= 0) _regenerations[at] = request;
            else _regenerations.Add(request);
            row.IsRerollQueued = true;

            if (IsProcessingQueue)
                AddLog($"{ClipCaption(row)}: regenerate {(at >= 0 ? "updated" : "queued")} — it runs when the " +
                       $"render on the GPU finishes ({_regenerations.Count} waiting).");
            else
                _ = RunRegenerationsAsync();

            RaiseClipEditorState();
        }

        private void UnqueueSelectedClip()
        {
            var row = _selectedClip;
            if (row == null || _regenerations.RemoveAll(r => r.Row == row) == 0) return;
            row.IsRerollQueued = false;
            AddLog($"{ClipCaption(row)}: regenerate taken back out of the queue.");
            RaiseClipEditorState();
        }

        /// <summary>Writes a prompt onto a clip the way a render expects to find it: the stamp matching, so
        /// the finish sweep never reads it as reworded-since-seeded and skips it.</summary>
        private static void ApplyPrompt(ErosHuntClip row, string prompt, bool newSeed)
        {
            var item = row.Item;
            item.Prompt = prompt;
            item.HuntPromptStamp = prompt;
            if (newSeed || item.ChosenSeed < 0)
            {
                var seed = Random.Shared.NextInt64(0, long.MaxValue);
                item.HuntBaseSeed = seed;
                item.ChosenSeed = seed;
            }
            item.ChosenSampleSlot = RenderSlot;
            row.ApplyPick(RenderSlot);
            row.IsStale = false;
            row.Summary = Shorten(CastPromptStamp.ExtractDescription(prompt));
        }

        /// <summary>A regenerate pressed with the GPU free: its own short run, holding the queue flag so
        /// ⚡ Render and the stack switches wait for it, and draining whatever is pressed meanwhile.</summary>
        private async Task RunRegenerationsAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            try
            {
                await DrainRegenerationsAsync(token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Regenerate stopped.");
            }
            catch (Exception ex)
            {
                AddLog($"Regenerate error: {ex.Message}");
            }
            finally
            {
                if (token.IsCancellationRequested) ClearRegenerations("the regenerate was stopped");
                IsProcessingQueue = false;
                IsProcessing = false;
                ProcessingStatus = token.IsCancellationRequested ? "Stopped" : "Ready";
                UpdateQueueStatus();
                RefreshBoardState();
                SaveQueueToFile();
                OnCanExecuteChanged();
            }
        }

        /// <summary>Runs every waiting regenerate in the order they were pressed. It re-reads its list each
        /// pass, so one pressed while another renders joins the back of the same run.</summary>
        private async Task DrainRegenerationsAsync(CancellationToken token)
        {
            while (_regenerations.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var request = _regenerations[0];
                _regenerations.RemoveAt(0);
                request.Row.IsRerollQueued = false;
                RaiseClipEditorState();

                if (!HuntBoard.Contains(request.Row) || request.Row.IsBusy)
                {
                    AddLog($"{request.Row.Title}: regenerate dropped — the clip is no longer on the board.");
                    continue;
                }

                await RegenerateAsync(request, token);
            }
        }

        /// <summary>
        /// One clip, rendered again from the requested prompt. Its file is overwritten in place and the story
        /// re-joined, so the film on the STORIES list is the one with the new clip in it.
        ///
        /// <para>A render that fails or is stopped puts the clip back exactly as it was — prompt, seed, state —
        /// because its old file is still the one on disk, and the prompt shown for a clip must be the prompt
        /// that made it. The edit is not lost: it stays in the box to be tried again.</para>
        /// </summary>
        private async Task RegenerateAsync(ClipRegeneration request, CancellationToken token)
        {
            var row = request.Row;
            var item = row.Item;
            var before = new ClipSnapshot(
                item.Prompt, item.HuntPromptStamp, item.HuntBaseSeed, item.ChosenSampleSlot, item.ChosenSeed,
                item.ItemStatus, item.ErrorMessage, item.ErosStage, item.StartedAt, item.CompletedAt,
                row.PickedSlot, row.IsFinished, row.OutputPath, row.Summary, row.Status, HasResult);

            var reworded = request.Prompt != item.Prompt;
            ApplyPrompt(row, request.Prompt, request.NewSeed);
            AddLog($"=== ⚡ Regenerating {ClipCaption(row)} — {(reworded ? "edited prompt" : "same prompt")}, " +
                   $"{(item.ChosenSeed == before.ChosenSeed ? "same" : "new")} seed {item.ChosenSeed} ===");

            // The player may have this clip or the joined film open, and both files are about to be rewritten.
            Application.Current.Dispatcher.Invoke(() => ShowInPlayer(null, string.Empty));

            item.ItemStatus = QueueItemStatus.Processing;
            item.ErrorMessage = null;
            item.StartedAt = DateTime.Now;
            row.IsFinished = false;
            row.IsBusy = true;
            row.Status = "regenerating…";
            UpdateQueueStatus();
            RefreshBoardState();

            try
            {
                await FinishAsync(row, 0, 100, token);
                item.ItemStatus = QueueItemStatus.Completed;
                item.CompletedAt = DateTime.Now;
                item.ErosStage = StageFinished;
                row.IsFinished = true;
                row.Status = "finished · regenerated";
                AddLog($"{ClipCaption(row)}: regenerated.");

                // Never throws. Joins only when every clip of the story is done, as after a run.
                await CompleteStoryAsync(item, token);
                UpdateStoryAfterRegenerate();
            }
            catch (Exception ex)
            {
                item.Prompt = before.Prompt;
                item.HuntPromptStamp = before.HuntPromptStamp;
                item.HuntBaseSeed = before.HuntBaseSeed;
                item.ChosenSampleSlot = before.ChosenSampleSlot;
                item.ChosenSeed = before.ChosenSeed;
                item.ItemStatus = before.ItemStatus;
                item.ErrorMessage = before.ErrorMessage;
                item.ErosStage = before.ErosStage;
                item.StartedAt = before.StartedAt;
                item.CompletedAt = before.CompletedAt;
                row.ApplyPick(before.PickedSlot);
                row.IsFinished = before.IsFinished;
                row.OutputPath = before.OutputPath;
                row.Summary = before.Summary;
                HasResult = before.HasResult;

                if (ex is OperationCanceledException)
                {
                    row.Status = before.Status;
                    AddLog($"{ClipCaption(row)}: regenerate stopped — the clip is as it was.");
                    throw;
                }

                row.Status = $"regenerate failed: {ex.Message}";
                AddLog($"{ClipCaption(row)} FAILED to regenerate: {ex.Message} — the clip is as it was, " +
                       "and the edited prompt is still in the box.");
            }
            finally
            {
                row.IsBusy = false;
                UpdateQueueStatus();
                SaveQueueToFile();
                RefreshBoardState();
                RaiseClipEditorState();
            }
        }

        /// <summary>
        /// A story whose every clip is now done gets the re-joined film on its row — and, outside a run, is
        /// Done, even if the run had marked it Failed for the clip that has just been fixed. Inside a run the
        /// batch loop makes that call itself when the story's render returns.
        /// </summary>
        private void UpdateStoryAfterRegenerate()
        {
            if (StoryOnBoard is not { } story || Queue.Count == 0) return;
            if (Queue.Any(q => q.ItemStatus != QueueItemStatus.Completed)) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (!string.IsNullOrEmpty(ResultVideoPath)) story.OutputPath = ResultVideoPath;
                if (IsBatchRunning) return;
                story.State = BatchStoryState.Done;
                story.Detail = string.Empty;
                story.ClipCount = Queue.Count;
            });
            OnPropertyChanged(nameof(FolderSummary));
        }

        /// <summary>Starts regenerates that were waiting on something other than a render — the tail of a
        /// run, or a press that landed just as the last render let go of the GPU.</summary>
        private void TryStartQueuedRegenerations()
        {
            if (_regenerations.Count == 0) return;
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (_regenerations.Count == 0 || IsProcessingQueue || IsBatchRunning ||
                    IsFeelingLucky || IsBuildingSheets) return;
                _ = RunRegenerationsAsync();
            });
        }

        private void ClearRegenerations(string why)
        {
            if (_regenerations.Count == 0) return;
            var n = _regenerations.Count;
            foreach (var r in _regenerations) r.Row.IsRerollQueued = false;
            _regenerations.Clear();
            AddLog($"{n} queued regenerate(s) dropped — {why}. Press Regenerate again to run one.");
            RaiseClipEditorState();
        }

        private void OnBoardChanged()
        {
            if (_selectedClip != null && !HuntBoard.Contains(_selectedClip)) SelectedClip = null;

            var gone = _regenerations.RemoveAll(r => !HuntBoard.Contains(r.Row));
            if (gone > 0)
                AddLog($"{gone} queued regenerate(s) dropped — their story's clips have left the board.");
        }

        /// <summary>What one clip costs and produces, in one line under the settings.</summary>
        public override string HuntSummary
        {
            get
            {
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var fps = UseRife ? $"RIFE → {DraftFrameRate * 2} fps" : $"{DraftFrameRate} fps";
                return $"One render per clip: composed at ≈{dw}×{dh}, upscaled to ≈{fw}×{fh} " +
                       $"({UpscaleSteps} finishing steps, {fps}), then joined.";
            }
        }

        // ── What the page shows ─────────────────────────────────────────────────────────────────────

        public string RunButtonText => IsBatchRunning ? "⚡ Rendering…" : "⚡ Render every story";

        public string StackSummary => UseSingularity
            ? $"Singularity ref2va checkpoint · euler/simple · {FirstPassSteps} steps"
            : $"H3 Eros hybrid checkpoint · er_sde/beta · {FirstPassSteps} steps";

        public string PromptBuildSummary => ResearchPrompts
            ? "MiniMax-H3 guide build · 3–5 shots per clip · medium-or-closer framing"
            : "Shipped build · a cut roughly every 1.25 s";

        /// <summary>"3 of 12 clips rendered" for the story in flight.</summary>
        public string ClipProgressText
        {
            get
            {
                var total = HuntBoard.Count;
                if (total == 0) return string.Empty;
                var done = HuntBoard.Count(c => c.IsFinished);
                return $"{done} of {total} clip{(total == 1 ? string.Empty : "s")} rendered";
            }
        }

        /// <summary>Plays a finished story's joined film in the player.</summary>
        public RelayCommand<BatchStory> PlayStoryCommand { get; }

        private void PlayStory(BatchStory? story)
        {
            if (story?.HasOutput != true) return;
            ShowInPlayer(story.OutputPath, $"{story.Title} · joined film");
        }
    }
}
