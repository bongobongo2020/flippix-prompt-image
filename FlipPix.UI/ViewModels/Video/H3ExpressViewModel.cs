using System;
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

            // The summaries below are computed; say when what they are computed from moves.
            PropertyChanged += (_, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(IsBatchRunning):
                        OnPropertyChanged(nameof(RunButtonText));
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

            await FinishSweepAsync(token);
        }

        /// <summary>The ordinary finish, captioned for a tab that has no takes: "the-oasis · Clip 3 / 12".</summary>
        protected override async Task FinishAsync(ErosHuntClip row, double progressFrom, double progressTo,
            CancellationToken token)
        {
            await base.FinishAsync(row, progressFrom, progressTo, token);
            if (row.OutputPath == null) return;

            var story = CurrentStory?.Title;
            Application.Current.Dispatcher.Invoke(() =>
                ShowInPlayer(row.OutputPath, story == null ? row.Title : $"{story} · {row.Title}"));
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
