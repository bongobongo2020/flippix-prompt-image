using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    /// "H3 Batch" tab — point it at a folder of story <c>.txt</c> files and it makes a film of each one,
    /// in turn, until the folder is done.
    ///
    /// <para><b>It is 🍀 I'm Feeling Lucky in a loop, and deliberately nothing more.</b> Each story runs
    /// the same six steps that button runs — read the cast out of it, dress them, photograph them with
    /// Krea2-Spicy, build their sheets, write the clips, queue them, hunt three takes each, take the
    /// first, upscale it and join the film. There is no second pipeline here and no batch-only shortcut:
    /// the value of a batch is that a hundred stories go through the path that was already trusted for
    /// one, so a fix to that path is a fix to this tab too.</para>
    ///
    /// <para><b>🥽 The VR checkbox.</b> <see cref="RenderAsVr"/> puts the whole 🥽🎯 H3 VR workflow under
    /// the same loop instead: every story becomes a VR180 side-by-side stereo film, on the
    /// <c>h3-vr180-sbs-lora</c> LoRA, with the 21:9 canvas, the solo-cast first-person rules and the
    /// <c>_LR_180</c> file naming headset players read. That is why this class now sits under
    /// <see cref="H3VrViewModel"/> rather than <see cref="H3ErosViewModel"/>: the VR pipeline is the
    /// same Eros pipeline with one LoRA, one preamble and one canvas on top, and the checkbox simply
    /// switches those in or out (<see cref="H3VrViewModel.VrPipelineActive"/>). Unticked, every story
    /// runs the ordinary Eros workflow exactly as this tab always did — no preamble, no LoRA node, no
    /// stereo rule, the flat canvas — because a gate that leaves anything behind is a flat film with
    /// "VR" in its name.</para>
    ///
    /// <para><b>✴️ The Singularity checkbox.</b> <see cref="UseSingularity"/> swaps the <i>stack</i> the
    /// same loop samples on: <c>h3-singularity.json</c>, built from the author's own Singularity render —
    /// its ref2va checkpoint, its attention/chunking/sigma-shift patches and its euler/simple 10-step
    /// first pass — in place of <c>h3-eros.json</c>. Nothing else moves. That is possible because the
    /// built graph carries <see cref="H3ErosViewModel"/>'s node ids, so the cast, the reference panels,
    /// the hunt, the pick, the upscale finish, the join and the VR splice all drive it unchanged; the
    /// authored graph could not have rendered a batch story at all, having no reference slots to put a
    /// cast into (see <c>tools/build_h3_singularity.py</c>). The two checkboxes are independent axes —
    /// which stack, and flat or stereo — and any of the four combinations is a valid run.</para>
    ///
    /// <para><b>Each story is a clean slate.</b> Between files the queue is emptied and the cast is torn
    /// down — both cards, their photos, their sheets, their Parts and the wardrobe with them — because a
    /// folder of stories is a folder of different stories, and the surest way to make the fourth film
    /// wrong is to leave the third one's cast standing behind it. The cost of that decision is the one
    /// thing worth knowing about this tab: <b>every story pays for two portraits and two character
    /// sheets of its own.</b> Where a folder really is episodes about the same people, run them on
    /// 🌹🎯 H3 Eros instead, where the cast stays put between presses.</para>
    ///
    /// <para><b>Naming.</b> A story's clips and its joined film are named after its file, not after a
    /// timestamp — <c>H3Batch_the-oasis_clip03.mp4</c>, <c>H3Batch_the-oasis_..._joined.mp4</c>. With a
    /// hundred films in one folder the timestamp is the least useful thing that could be in the name, and
    /// <see cref="OutputFileStem"/> is read at exactly the two moments the current story is known. In
    /// VR mode the films carry the <c>_LR_180</c> suffix on top of that, so a headset player reads the
    /// stereo layout from the file name.</para>
    /// </summary>
    public class H3BatchViewModel : H3VrViewModel
    {
        /// <summary>What counts as a story. <c>.md</c> and <c>.text</c> are included for the same reason
        /// the 📄 Load .txt dialog accepts them.</summary>
        private static readonly string[] StoryExtensions = { ".txt", ".md", ".text" };

        /// <summary>Anything a file name may not carry into an output path.</summary>
        private static readonly Regex UnsafeForFileName = new(@"[^\w\-. ]+", RegexOptions.Compiled);

        private readonly ObservableCollection<BatchStory> _stories = new();

        private string _batchFolder = string.Empty;
        private bool _isBatchRunning;
        private string _batchStatus = string.Empty;
        private bool _renderAsVr;
        private bool _useSingularity;
        private BatchStory? _current;
        private CancellationTokenSource? _batchCts;

        public H3BatchViewModel(
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
            PickFolderCommand = new RelayCommand(async () => await PickFolderAsync(), () => !IsBatchRunning);
            RescanCommand = new RelayCommand(() => Rescan(reportEmpty: true),
                                             () => !IsBatchRunning && HasFolder);
            StartBatchCommand = new RelayCommand(async () => await RunBatchAsync(), () => CanStartBatch);
            StopBatchCommand = new RelayCommand(StopBatch, () => IsBatchRunning);
            ResetBatchCommand = new RelayCommand(ResetBatch, () => !IsBatchRunning && HasStories);
            RemoveStoryCommand = new RelayCommand<BatchStory>(RemoveStory, s => s != null && !IsBatchRunning);
            OpenStoryFolderCommand = new RelayCommand(OpenStoryFolder, () => HasFolder);

            _batchFolder = _settingsService.Settings?.H3BatchFolder ?? string.Empty;
            // Read into the field, like the VR flag below: the base constructors have already chosen a
            // checkpoint by the time this class exists, so all that is left is to put the mode's own one
            // back when the last run left the box ticked — and only when the dropdown is still sitting on
            // a default, never over a checkpoint that was chosen on purpose.
            _useSingularity = _settingsService.Settings?.H3BatchUseSingularity ?? false;
            var storedModel = (_settingsService.Settings?.H3BatchDiffusionModel ?? string.Empty)
                .Trim().Replace('\\', '/');
            if (_useSingularity)
            {
                OfferShippedModel();
                if (storedModel.Length == 0 || storedModel == base.ShippedModel)
                    SelectedDiffusionModel = SingularityModel;
            }
            // The VR checkbox is read here, not in the base constructor, because the base cannot see it —
            // its own gate ran before this class's fields existed (see H3VrViewModel.VrPipelineActive).
            // True means the canvas defaults the VR constructor skipped have to be put on by hand.
            _renderAsVr = _settingsService.Settings?.H3BatchRenderAsVr ?? false;
            if (_renderAsVr)
            {
                SelectedAspectRatio = "21:9 (Ultrawide)";
                Megapixels = NativeMegapixels;
                PreviewMegapixels = 0.3;
            }
            // The scan is disk work and this view model is built on the window's startup path, so it waits
            // for the dispatcher to be idle rather than running in the constructor — see the recurring
            // slow-open bug. Nothing on screen needs the list before then.
            if (HasFolder)
                Application.Current?.Dispatcher.InvokeAsync(
                    () => Rescan(reportEmpty: false),
                    System.Windows.Threading.DispatcherPriority.Background);

            // The row's detail column is 🍀's own phase line, so "3/6 · Photographing character 1…"
            // shows against the story it belongs to rather than only in the log.
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LuckyPhase) && _current is { } row)
                    row.Detail = LuckyPhase;
            };

            AddLog("H3 Batch initialized — point it at a folder of story .txt files and press ▶ Run batch. " +
                   "Each story gets its own cast, sheets, clips, takes and joined film, one after another." +
                   (_renderAsVr
                        ? "  🥽 VR is ON: every film is rendered through the H3 VR workflow as a VR180 " +
                          "side-by-side stereo pair, named ..._LR_180.mp4."
                        : string.Empty) +
                   (_useSingularity
                        ? "  ✴️ Singularity is ON: every film is sampled on the Singularity stack " +
                          "(h3-singularity.json) instead of the Eros one."
                        : string.Empty));
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The author's Singularity stack, in the topology this tab drives — see
        /// <c>tools/build_h3_singularity.py</c> for what was kept, what was replaced and why.
        ///
        /// <para>Its node ids are <c>h3-eros.json</c>'s, deliberately, so switching to it is a change of
        /// file name and nothing else: the same <c>ApplyCommonInputs</c>, the same three-draft hunt, the
        /// same latent-upscale finish, the same VR LoRA splice.</para>
        /// </summary>
        private const string SingularityWorkflow = "workflow/video/h3-minimax/h3-singularity.json";

        /// <summary>The checkpoint that graph is built around, and what the dropdown moves to when the
        /// box is ticked. It is a <b>ref2va</b> model — which is what makes the swap honest, because the
        /// whole pipeline conditions on cast reference photographs.</summary>
        public const string SingularityModel =
            "h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors";

        /// <summary>The Singularity graph's own first pass: ten steps of <c>euler</c>/<c>simple</c>,
        /// which is what the author measured that checkpoint at. Eros's twelve is for the hybrid.</summary>
        private const int SingularityFirstPassSteps = 10;

        /// <summary>Whichever stack this batch is being rendered on. Both files are driven by the same
        /// node ids, so this is the entire switch.</summary>
        protected override string WorkflowFileName =>
            UseSingularity ? SingularityWorkflow : base.WorkflowFileName;

        /// <summary>What the chosen workflow file names in its UNETLoader — the model the dropdown labels
        /// "(shipped)", and what a fresh install starts on.</summary>
        protected override string ShippedModel =>
            UseSingularity ? SingularityModel : base.ShippedModel;

        /// <summary>
        /// The first pass's step count, which belongs to the stack rather than to the tab: the Singularity
        /// checkpoint is sampled at ten steps, the Eros hybrid at twelve. Read live by both sweeps, which
        /// is why <see cref="CanChangeWorkflow"/> freezes the checkbox while anything is rendering — a
        /// finish at a different step count is not the take that was hunted.
        /// </summary>
        protected override int FirstPassSteps =>
            UseSingularity ? SingularityFirstPassSteps : base.FirstPassSteps;

        protected override string OutputSubfolder => "h3_batch";

        /// <summary>
        /// Named after the story being rendered, not after the clock. Read at exactly two moments — the
        /// finish naming a clip, and the join naming the film — and the current story is set at both.
        /// Falls back to the bare stem outside a run, which is what a manual press on this tab would use.
        /// </summary>
        protected override string OutputFileStem =>
            _current == null ? "H3Batch" : $"H3Batch_{SafeName(_current.Title)}";

        protected override string OutputFolderName => "H3Batch";

        protected override string TabDisplayName => "H3 Batch";

        protected override string ChainLibraryFolder => "h3batch";

        protected override string RunTokenPrefix => "h3batch";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3batch_queue.json");

        protected override string? RecallDiffusionModel(ComfyUISettings? settings) =>
            settings?.H3BatchDiffusionModel;

        protected override void StoreDiffusionModel(ComfyUISettings settings, string name) =>
            settings.H3BatchDiffusionModel = name;

        /// <summary>The batch's own VR gate — the checkbox, nothing else. See
        /// <see cref="H3VrViewModel.VrPipelineActive"/> for everything that reads it.</summary>
        protected override bool VrPipelineActive => RenderAsVr;

        /// <summary>The headset players' stereo marker, on in VR mode only — a flat film named
        /// <c>_LR_180</c> would be played as stereo it does not contain.</summary>
        protected override string OutputFileSuffix => RenderAsVr ? "_LR_180" : string.Empty;

        /// <summary>A file name safe to build an output path from, and short enough to stay readable.</summary>
        private static string SafeName(string title)
        {
            var clean = UnsafeForFileName.Replace(title, "_").Trim().Replace(' ', '_');
            if (clean.Length > 48) clean = clean[..48];
            return clean.Length == 0 ? "story" : clean;
        }

        // ── The folder ──────────────────────────────────────────────────────────────────────────────

        public RelayCommand PickFolderCommand { get; }
        public RelayCommand RescanCommand { get; }
        public RelayCommand StartBatchCommand { get; }
        public RelayCommand StopBatchCommand { get; }
        public RelayCommand ResetBatchCommand { get; }
        public RelayCommand<BatchStory> RemoveStoryCommand { get; }
        public RelayCommand OpenStoryFolderCommand { get; }

        /// <summary>The folder of stories. Persisted, because a batch folder is a place you come back to.</summary>
        public string BatchFolder
        {
            get => _batchFolder;
            private set
            {
                if (_batchFolder == value) return;
                _batchFolder = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasFolder));
                OnPropertyChanged(nameof(FolderSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3BatchFolder = value;
                    _settingsService.SaveSettings(settings);
                }
            }
        }

        public bool HasFolder => !string.IsNullOrWhiteSpace(BatchFolder) && Directory.Exists(BatchFolder);

        public ObservableCollection<BatchStory> Stories => _stories;

        public bool HasStories => _stories.Count > 0;

        public string FolderSummary =>
            !HasFolder
                ? "No folder chosen. Pick one holding your story .txt files."
                : $"{BatchFolder} — {_stories.Count} story file(s) " +
                  $"({_stories.Count(s => s.IsWaiting)} waiting, {_stories.Count(s => s.IsDone)} done" +
                  (_stories.Any(s => s.IsFailed) ? $", {_stories.Count(s => s.IsFailed)} failed" : string.Empty) + ").";

        public bool IsBatchRunning
        {
            get => _isBatchRunning;
            private set
            {
                if (_isBatchRunning == value) return;
                _isBatchRunning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BatchButtonText));
                OnCanExecuteChanged();
            }
        }

        public string BatchButtonText => IsBatchRunning ? "▶ Running…" : "▶ Run batch";

        /// <summary>The line above the list: which story, how far in, how long left to go.</summary>
        public string BatchStatus
        {
            get => _batchStatus;
            private set { if (_batchStatus == value) return; _batchStatus = value; OnPropertyChanged(); }
        }

        public bool CanStartBatch =>
            !IsBatchRunning && !IsFeelingLucky && !IsProcessingQueue && !IsBuildingSheets &&
            _stories.Any(s => s.IsWaiting);

        // ── The VR switch ─────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the batch runs every story through the 🥽🎯 H3 VR workflow — the same Eros hunt with the
        /// VR180 SBS LoRA, the stereo prompt rules and the 21:9 canvas — instead of the ordinary flat one.
        /// This is the one flag <see cref="H3VrViewModel.VrPipelineActive"/> reads, so everything
        /// VR-shaped on this tab hangs off it: the LoRA splice at submit time, the first-person rules for
        /// a solo cast, the per-eye quality lists, and the <c>_LR_180</c> suffix on every file.
        ///
        /// <para><b>Unticked, it is the Eros workflow exactly as this tab always ran it.</b> Not "the VR
        /// pipeline with the LoRA turned down" — the preamble, the stereo scene rule and the LoRA node are
        /// simply not applied, and the canvas goes back to the flat defaults. There is no third mode.</para>
        ///
        /// <para>Persisted, and switched per batch rather than per story: a folder of stories is usually
        /// all one kind of film. Changing it mid-run is refused (see <see cref="CanChangeVrMode"/>) — the
        /// checkbox is read at Add to Queue like every other dial, but a run that changes its mind halfway
        /// is a folder of films that do not match each other.</para>
        /// </summary>
        public bool RenderAsVr
        {
            get => _renderAsVr;
            set
            {
                if (_renderAsVr == value) return;
                _renderAsVr = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RenderAsVrSummary));

                // The canvas switches with the mode, to the mode's own defaults — the LoRA's 21:9 at
                // native on, the flat Eros figures off — because a stereo pair on a 16:9 canvas does not
                // split, and a flat film on an ultrawide one is just a flat film with black bars' worth
                // of wasted width. The user can still change them afterwards; this is only the sensible
                // starting point for the mode.
                if (value)
                {
                    SelectedAspectRatio = "21:9 (Ultrawide)";
                    Megapixels = NativeMegapixels;
                    PreviewMegapixels = 0.3;
                }
                else
                {
                    SelectedAspectRatio = H3Canvas.AutoAspect;
                    Megapixels = 1.0;
                    PreviewMegapixels = 0.15;
                }

                // Everything computed off the gate has to be told it moved.
                OnPropertyChanged(nameof(MegapixelOptions));
                OnPropertyChanged(nameof(PreviewMegapixelOptions));
                OnPropertyChanged(nameof(HuntSummary));
                OnPropertyChanged(nameof(VrLoraSummary));
                OnPropertyChanged(nameof(HasVrLoraWarning));
                OnPropertyChanged(nameof(SoloPovSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3BatchRenderAsVr = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? "H3 Batch: 🥽 VR ON — every story in the folder will be rendered through the H3 VR " +
                      "workflow as a VR180 stereo pair (21:9, LoRA on top, films named ..._LR_180.mp4)."
                    : "H3 Batch: VR off — every story in the folder will be an ordinary flat film, as before.");
            }
        }

        /// <summary>The line under the checkbox: what the next run will make, not what the last one made.</summary>
        public string RenderAsVrSummary => RenderAsVr
            ? "Every story becomes a VR180 side-by-side stereo film — the 🥽🎯 H3 VR workflow (the " +
              "vr180-sbs LoRA, 21:9 canvas, first-person for a solo cast), on the same hunt/finish/join " +
              "loop. Films are named ..._LR_180.mp4, which is what a headset player reads the layout from."
            : "Every story becomes an ordinary flat film — the 🌹🎯 H3 Eros workflow, exactly as this tab " +
              "has always run it.";

        /// <summary>The checkbox is frozen while anything is rendering, so a folder cannot come out half
        /// VR and half flat.</summary>
        public bool CanChangeVrMode => CanChangeWorkflow;

        /// <summary>
        /// Whether a switch of render mode is allowed at all right now — the gate both this tab's
        /// checkboxes sit behind.
        ///
        /// <para>Neither of them is a dial that is frozen onto a queue item at Add to Queue: the workflow
        /// file, the step count and the LoRA are read <i>live</i>, by both sweeps. A hunt on one stack and
        /// a finish on the other is not the take that was picked, and a folder switched halfway is a
        /// folder of films that do not match each other — so the answer is no while anything is
        /// rendering, rather than a migration.</para>
        /// </summary>
        public bool CanChangeWorkflow =>
            !IsBatchRunning && !IsFeelingLucky && !IsProcessingQueue && !IsBuildingSheets;

        // ── The Singularity switch ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether every story in the folder is sampled on the <b>Singularity</b> stack —
        /// <c>h3-singularity.json</c>, built from the author's own Singularity render — instead of the
        /// 🌹🎯 H3 Eros one.
        ///
        /// <para><b>What actually changes.</b> The checkpoint
        /// (<c>Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8</c>), its CLIP and video VAE, the model
        /// patches the author measured it with (comfy-kitchen attention, chunked feed-forward at 2/4096,
        /// fp16 accumulation, sigma shift 12/3) and the first pass's sampler and step count —
        /// <c>euler</c>/<c>simple</c> at ten steps rather than <c>er_sde</c>/<c>beta</c> at twelve.</para>
        ///
        /// <para><b>What deliberately does not change.</b> Everything a story is: the cast, the wardrobe,
        /// the portraits and sheets, the @char tags and the reference panels, the clip writer, the queue,
        /// the three-draft hunt, the board, the latent-upscale finish, RIFE and the join. The authored
        /// Singularity graph could not have run a batch story at all — it conditions on a first and last
        /// frame, with no reference slots to put a cast into — so the build swaps its
        /// <c>MiniMaxH3ImageToVideo</c> for <c>MiniMaxH3ReferenceToVideo</c> and keeps the tab's own
        /// topology around it. That is also why this is one property: the graph carries
        /// <see cref="H3ErosViewModel"/>'s node ids, so nothing else in the render path knows which stack
        /// it is driving.</para>
        ///
        /// <para>It composes with 🥽 VR: the graph ships the same empty Power Lora Loader seat, so the
        /// VR180 SBS LoRA splices onto the Singularity checkpoint exactly as it does onto the Eros one.
        /// Both boxes ticked is a folder of VR180 films sampled on this stack.</para>
        ///
        /// <para>Persisted, and refused mid-run for the reason <see cref="CanChangeWorkflow"/> gives.</para>
        /// </summary>
        public bool UseSingularity
        {
            get => _useSingularity;
            set
            {
                if (_useSingularity == value) return;
                _useSingularity = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UseSingularitySummary));
                OnPropertyChanged(nameof(HuntSummary));

                // The checkpoint is the stack. A Singularity graph sampling the Eros hybrid is neither
                // tab's render, so the dropdown moves with the checkbox — and says so, because it is
                // still a dropdown and the next thing the user does may be to change it back.
                OfferShippedModel();
                SelectedDiffusionModel = ShippedModel;

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3BatchUseSingularity = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? "H3 Batch: ✴️ Singularity ON — every story is sampled on h3-singularity.json " +
                      $"({LabelFor(SingularityModel)}, euler/simple at {SingularityFirstPassSteps} steps). " +
                      "The cast, the sheets, the hunt, the finish and the join are unchanged."
                    : "H3 Batch: Singularity off — every story is sampled on h3-eros.json again, as this " +
                      "tab has always run it.");

                // The board is the one thing this switch can quietly invalidate. A draft on it was
                // hunted on the other stack, and the finish re-samples the picked branch on whichever
                // stack is set when it runs — so finishing one now would produce a clip that is not the
                // take that was chosen. Said out loud rather than migrated: the fix is one re-roll.
                if (HasBoard)
                    AddLog("  Note: the board still holds drafts hunted on the other stack. Finish one and " +
                           "it is re-sampled on this one, which is not the take you picked — 🎲 re-roll " +
                           "those clips, or clear the queue, before pressing ▶.");
            }
        }

        /// <summary>
        /// Puts the current mode's own checkpoint in the dropdown if it is not there already.
        ///
        /// <para>The list is normally the server's answer to /object_info, which has both models in it.
        /// This is for the case where the server could not be reached: without it, ticking the box would
        /// select a model the ComboBox has no row for, and the dropdown would go blank while the render
        /// path used a checkpoint nothing on screen names.</para>
        /// </summary>
        private void OfferShippedModel()
        {
            var name = ShippedModel;
            if (DiffusionModelOptions.Any(o => string.Equals(o.Value, name, StringComparison.OrdinalIgnoreCase)))
                return;
            DiffusionModelOptions.Add(new DiffusionModelOption(name, LabelFor(name) + " (shipped)"));
        }

        /// <summary>The line under the checkbox: which stack the next run samples on, and what it does
        /// not change.</summary>
        public string UseSingularitySummary => UseSingularity
            ? "Every story is sampled on the Singularity stack — the Singularity ref2va checkpoint with " +
              "the author's own attention, chunking and sigma-shift patches, euler/simple at " +
              $"{SingularityFirstPassSteps} steps. The cast, the character reference sheets, the three " +
              "drafts per clip, the upscale finish and the join are exactly as they are on H3 Eros."
            : "Every story is sampled on the H3 Eros stack — the 10Eros hybrid checkpoint, er_sde/beta " +
              $"at {base.FirstPassSteps} steps — exactly as this tab has always run it.";

        private async Task PickFolderAsync()
        {
            var start = HasFolder ? BatchFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var folder = await _fileDialogService.OpenFolderDialogAsync("Select a folder of story .txt files", start);
            if (string.IsNullOrWhiteSpace(folder)) return;
            BatchFolder = folder;
            Rescan(reportEmpty: true);
        }

        /// <summary>
        /// Re-reads the folder. Stories already in the list keep their state, so rescanning after dropping
        /// two new files in does not put the eighty finished ones back to Waiting; files that have
        /// disappeared drop out unless they have already produced something.
        /// </summary>
        private void Rescan(bool reportEmpty)
        {
            if (!HasFolder) return;

            List<string> found;
            try
            {
                found = Directory.EnumerateFiles(BatchFolder)
                    .Where(f => StoryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                AddLog($"H3 Batch: the folder could not be read ({ex.Message}).");
                return;
            }

            var known = _stories.ToDictionary(s => s.FilePath, StringComparer.OrdinalIgnoreCase);
            var keep = new HashSet<string>(found, StringComparer.OrdinalIgnoreCase);

            for (var i = _stories.Count - 1; i >= 0; i--)
                if (!keep.Contains(_stories[i].FilePath) && !_stories[i].IsDone)
                    _stories.RemoveAt(i);

            var added = 0;
            for (var i = 0; i < found.Count; i++)
            {
                if (known.ContainsKey(found[i])) continue;
                _stories.Add(new BatchStory(found[i]));
                added++;
            }

            OnPropertyChanged(nameof(HasStories));
            OnPropertyChanged(nameof(FolderSummary));
            OnCanExecuteChanged();

            if (_stories.Count == 0 && reportEmpty)
                AddLog($"H3 Batch: no .txt, .md or .text files in {BatchFolder}.");
            else if (added > 0)
                AddLog($"H3 Batch: {added} story file(s) added — {_stories.Count(s => s.IsWaiting)} waiting.");
        }

        private void RemoveStory(BatchStory? story)
        {
            if (story == null) return;
            _stories.Remove(story);
            OnPropertyChanged(nameof(HasStories));
            OnPropertyChanged(nameof(FolderSummary));
            OnCanExecuteChanged();
        }

        private void ResetBatch()
        {
            foreach (var s in _stories) s.Reset();
            OnPropertyChanged(nameof(FolderSummary));
            OnCanExecuteChanged();
            AddLog("H3 Batch: every story back to waiting.");
        }

        private void OpenStoryFolder()
        {
            if (!HasFolder) return;
            try { System.Diagnostics.Process.Start("explorer.exe", BatchFolder); }
            catch (Exception ex) { AddLog($"Could not open the folder: {ex.Message}"); }
        }

        private void StopBatch()
        {
            _batchCts?.Cancel();
            // The story in flight is cancelled too — 🍀 owns its own token, and Cancel reaches it.
            CancelEverythingPublic();
            AddLog("H3 Batch: stopping after the current story is cut short.");
        }

        /// <summary>The tab's ✕ Cancel, reachable from the batch loop. Named apart from the command so it
        /// is obvious at the call site that this cancels the <i>story</i>, not the batch.</summary>
        private void CancelEverythingPublic() => CancelCommand.Execute(null);

        // ── The loop ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Every waiting story, in folder order, each one through the whole 🍀 pipeline.
        ///
        /// <para><b>One story's failure is not the batch's.</b> A file that comes back with no film is
        /// marked Failed with the reason on its row and the loop moves to the next one — a folder left
        /// running overnight must not be stopped at 3 a.m. by one story the model would not write. Only
        /// ✕ Stop ends the run early.</para>
        /// </summary>
        private async Task RunBatchAsync()
        {
            if (!CanStartBatch) return;

            _batchCts?.Dispose();
            _batchCts = new CancellationTokenSource();
            var token = _batchCts.Token;

            IsBatchRunning = true;
            var startedAll = DateTime.Now;
            var done = 0;
            var failed = 0;

            try
            {
                var todo = _stories.Where(s => s.IsWaiting).ToList();
                AddLog($"=== 🗂️ H3 Batch{(RenderAsVr ? " · VR180" : string.Empty)}: " +
                       $"{todo.Count} story file(s) from {BatchFolder} ===");

                for (var i = 0; i < todo.Count; i++)
                {
                    if (token.IsCancellationRequested) break;

                    var story = todo[i];
                    _current = story;
                    var startedOne = DateTime.Now;
                    story.State = BatchStoryState.Processing;
                    story.Detail = string.Empty;
                    BatchStatus = $"Story {i + 1} of {todo.Count}: {story.Title}";

                    try
                    {
                        var text = (await File.ReadAllTextAsync(story.FilePath, token)).Trim();
                        if (text.Length == 0)
                        {
                            story.State = BatchStoryState.Skipped;
                            story.Detail = "the file is empty";
                            AddLog($"🗂️ {story.Title}: skipped, the file is empty.");
                            continue;
                        }

                        AddLog($"=== 🗂️ story {i + 1}/{todo.Count}: {story.FileName} ({text.Length:N0} chars) ===");
                        ResetForNextStory();
                        SetStory(text, story.FileName);

                        // The one line that makes this a batch tab rather than a second pipeline.
                        await FeelLuckyAsync();
                        token.ThrowIfCancellationRequested();

                        story.ClipCount = Queue.Count;
                        story.Elapsed = DateTime.Now - startedOne;

                        var completed = Queue.Count(q => q.ItemStatus == QueueItemStatus.Completed);
                        if (Queue.Count > 0 && completed == Queue.Count)
                        {
                            story.OutputPath = ResultVideoPath ?? string.Empty;
                            story.State = BatchStoryState.Done;
                            done++;
                            AddLog($"=== 🗂️ {story.Title}: done — {completed} clip(s) in " +
                                   $"{story.Elapsed.TotalMinutes:0.#} min ===");
                        }
                        else
                        {
                            story.State = BatchStoryState.Failed;
                            story.Detail = Queue.Count == 0
                                ? "nothing reached the queue"
                                : $"{Queue.Count - completed} of {Queue.Count} clip(s) did not finish";
                            failed++;
                            AddLog($"=== 🗂️ {story.Title}: FAILED — {story.Detail} ===");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        story.State = BatchStoryState.Failed;
                        story.Detail = "stopped";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        story.State = BatchStoryState.Failed;
                        story.Detail = ex.Message;
                        story.Elapsed = DateTime.Now - startedOne;
                        failed++;
                        AddLog($"=== 🗂️ {story.Title}: FAILED — {ex.Message} ===");
                    }
                    finally
                    {
                        OnPropertyChanged(nameof(FolderSummary));
                    }
                }

                BatchStatus = token.IsCancellationRequested
                    ? $"Stopped — {done} done, {failed} failed."
                    : $"Batch finished — {done} done, {failed} failed, " +
                      $"{(DateTime.Now - startedAll).TotalMinutes:0.#} min.";
                AddLog($"=== 🗂️ H3 Batch {(token.IsCancellationRequested ? "stopped" : "finished")}: " +
                       $"{done} done, {failed} failed, {(DateTime.Now - startedAll).TotalMinutes:0.#} min ===");
            }
            catch (OperationCanceledException)
            {
                BatchStatus = $"Stopped — {done} done, {failed} failed.";
                AddLog("🗂️ H3 Batch stopped.");
            }
            catch (Exception ex)
            {
                BatchStatus = $"Stopped: {ex.Message}";
                AddLog($"🗂️ H3 Batch stopped: {ex.Message}");
            }
            finally
            {
                _current = null;
                IsBatchRunning = false;
                _batchCts?.Dispose();
                _batchCts = null;
                OnPropertyChanged(nameof(FolderSummary));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Tears the previous story down completely: the queue, the board, both cast cards with their
        /// photos and sheets, the wardrobe, and the prompt.
        ///
        /// <para>Nothing here is an optimisation to be reconsidered later. A card that keeps its photo is
        /// a card 🍀 will not re-photograph, so the next story would be cast with the last one's people;
        /// a wardrobe left standing dresses them in the last one's clothes; a prompt left in the box is
        /// what Add to Queue would snapshot if Analyze came back empty. Each of those produces a film that
        /// looks finished and is wrong, which is the worst outcome an unattended batch can have.</para>
        /// </summary>
        private void ResetForNextStory()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ClearQueueCommand.Execute(null);
                Character1.Clear();
                Character2.Clear();
                ClearWardrobeCommand.Execute(null);
                ClearDerivedCastCommand.Execute(null);
                Prompt = string.Empty;
                StoryText = string.Empty;
                OnCanExecuteChanged();
            });
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanStartBatch));
            OnPropertyChanged(nameof(HasStories));
            OnPropertyChanged(nameof(FolderSummary));
            OnPropertyChanged(nameof(CanChangeVrMode));
            OnPropertyChanged(nameof(CanChangeWorkflow));
            StartBatchCommand.NotifyCanExecuteChanged();
            StopBatchCommand.NotifyCanExecuteChanged();
            ResetBatchCommand.NotifyCanExecuteChanged();
            PickFolderCommand.NotifyCanExecuteChanged();
            RescanCommand.NotifyCanExecuteChanged();
            RemoveStoryCommand.NotifyCanExecuteChanged();
        }
    }
}
