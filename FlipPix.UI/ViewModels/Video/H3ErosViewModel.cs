using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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
    /// "H3 Eros" tab — the 🧪 H3 Experimental flow (story in, the beat sheet and the per-clip writer, the
    /// cast sheets, the wardrobe lock, the clip queue, the FFmpeg join) rendered through the author's
    /// <b>MiniMax SEEDHUNTER v122 EROS-Hybrid</b> graph, which turns every clip into a seed hunt.
    ///
    /// <para><b>Three sweeps, not twelve interruptions.</b> The graph builds one
    /// <c>MiniMaxH3ReferenceToVideo</c> conditioning and latent and samples it three times, on three noise
    /// seeds, at a small draft canvas. The tab runs that for <i>every clip in the queue, back to back,
    /// without ever stopping to ask a question</i>, and only then hands over a board of drafts to choose
    /// from. So one ▶ Generate is:</para>
    /// <list type="number">
    /// <item><b>Hunt sweep.</b> Down the queue, three drafts per clip, at the cheapest canvas that still
    /// lets two takes be told apart. A twelve-clip story lands as thirty-six drafts.</item>
    /// <item><b>Pick.</b> The board. Click a tile to watch it and make it that clip's take; 🎲 re-rolls one
    /// draft or a whole clip's three; ✕ throws one away. Nothing is on the GPU while this happens, so the
    /// rest of the app — and every other tab — keeps working.</item>
    /// <item><b>Finish sweep.</b> Every picked clip, in story order: the picked latent is lifted to the
    /// finished megapixels by <c>MinimaxH3LatentUpscaler3D</c>, re-sampled by a short fixed-sigma pass,
    /// RIFE'd and muxed. When the last clip of a chain lands, the inherited FFmpeg concat joins them.</item>
    /// </list>
    ///
    /// <para><b>Why the finish re-samples the first pass.</b> When a hunt and its finish were adjacent
    /// submissions, ComfyUI's execution cache handed the picked latent straight back. Hunting the whole
    /// queue first gives that up — eleven other clips have been through the sampler by then. What replaces
    /// it is determinism: the same seed, model, steps and canvas produce the same latent, so the finish
    /// re-samples <i>one</i> draft branch (the picked one; the other two are pruned out) and gets the take
    /// that was chosen. A third of a hunt, once, to buy an uninterrupted queue.</para>
    ///
    /// <para><b>Two megapixel dials, not one.</b> <see cref="PreviewMegapixels"/> is what the drafts are
    /// sampled at; <c>Megapixels</c> — the tab's usual quality dropdown — is what the picked one is
    /// finished at. They are independent because the upscaler takes a target, not a factor.</para>
    ///
    /// <para>Everything before the render is inherited unchanged from
    /// <see cref="H3ExperimentalViewModel"/>: the story and scene inputs, the wardrobe derived once and
    /// locked, the two character cards and their panel-split sheets, the two-step writer that turns a story
    /// into a clip chain, the queue, and the join.</para>
    /// </summary>
    public partial class H3ErosViewModel : H3ExperimentalViewModel, IErosBoardHost
    {
        /// <summary>How many drafts one hunt produces. The graph has exactly three sampler branches.</summary>
        public const int SampleCount = 3;

        // ── Workflow node ids (locked to h3-minimax/h3-eros.json; see tools/convert_h3_eros.py) ──
        protected const string NodePrompt = "22:11";        // PrimitiveStringMultiline
        protected const string NodeSeconds = "22:23";       // PrimitiveFloat → the frame-count expression
        protected const string NodeSteps = "22:8";          // INTConstant → BasicScheduler steps (first pass)
        protected const string NodeResolution = "22:9";     // ResolutionSelector — the *draft* canvas
        protected const string NodeRef2V = "5";             // MiniMaxH3ReferenceToVideo
        protected const string NodeLatentSplit = "242";     // LTXVSeparateAVLatent — reads the picked latent
        protected const string NodeUpscaler = "243";        // MinimaxH3LatentUpscaler3D — the finished canvas
        protected const string NodeUpscaleSampler = "135:26";  // SamplerCustomAdvanced — the 2nd pass
        protected const string NodeUpscaleNoise = "135:27"; // RandomNoise — the 2nd pass's own seed
        protected const string NodeSinglePassVideo = "259"; // VAEDecode of the picked latent, upscale off
        protected const string NodeSinglePassAudio = "258"; // VAEDecodeAudio of the same
        protected const string NodeUpscaledVideo = "189";   // VAEDecode of the 2nd pass
        protected const string NodeUpscaledAudio = "190";   // VAEDecodeAudio of the 2nd pass
        protected const string NodeRife = "165";            // RIFEInterpolation — 24 → 48 fps
        protected const string NodeFinalSave = "34";        // VHS_VideoCombine — the finished clip
        protected const string NodeUnet = "171:4";          // UNETLoader — the model both sweeps sample with

        /// <summary>The ComfyUI models folder the model dropdown offers, under <c>diffusion_models</c>.
        /// Everything the server reports there is listed; nothing outside it is.</summary>
        private const string ModelFolder = "h3-minimax/";

        /// <summary>What this tab's workflow file names in its UNETLoader. Kept here so the dropdown still
        /// has the shipped model in it when the server cannot be reached to enumerate the folder — and
        /// overridable, because a fork of this tab renders on a different checkpoint.</summary>
        protected virtual string ShippedModel =>
            "h3-minimax/10Eros_Max_h3_TURBO-hybrid_beta4_int8_convrot.safetensors";

        // Added to the graph, not present in the file: the two INT sources that stand in for
        // ResolutionSelector's outputs when the chosen aspect is one its combo does not accept.
        protected const string NodeCanvasWidth = "eros_canvas_w";
        protected const string NodeCanvasHeight = "eros_canvas_h";

        /// <summary><see cref="H3CastQueueItem.ErosStage"/> values. A hunted clip is still a Pending queue
        /// item — there is GPU work left on it — so the stage is tracked beside the status, not in it.</summary>
        protected const string StageHunted = "hunted";
        protected const string StageFinished = "finished";

        /// <summary>ManualSigmas schedules, by step count. The graph ships all three; the render links one.</summary>
        protected static readonly Dictionary<int, string> SigmaSchedules = new()
        {
            [3] = "222",   // 0.9035, 0.6316, 0.3158, 0.0000
            [4] = "221",   // 0.9035, 0.8000, 0.6316, 0.3158, 0.0000
            [5] = "220",   // 0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000
        };

        /// <summary>Draft slot (1-based) → the sampler that produced it, the sink that saved it, its noise.</summary>
        protected static readonly (string Sampler, string Sink, string Noise)[] SampleBranches =
        {
            ("125:12", "18", "125:17"),
            ("133:129", "134", "133:128"),
            ("143:139", "144", "143:138"),
        };

        /// <summary>The sampler's <c>denoised_output</c>. Slot 0 is <c>output</c>, which is what the
        /// previews decode; the upscale reads the denoised one, exactly as the authored graph does.</summary>
        protected const int DenoisedSlot = 1;

        /// <summary>Frames per second the preview sinks and the un-interpolated final are muxed at.</summary>
        protected const int DraftFrameRate = 24;

        /// <summary>
        /// Steps on the first pass — the one that produces the drafts. A constant rather than a dial: a hunt
        /// is a comparison between seeds, and the finish re-samples the picked branch, so this number has to
        /// mean the same thing in both submissions or the finish is not the take that was picked.
        /// </summary>
        protected virtual int FirstPassSteps => 12;

        /// <summary>Prefix on every filename this tab writes and on the name it takes the workflow lease
        /// under. A fork renders into its own folder, so its drafts and finals must not collide with this
        /// tab's — and a log line saying which tab is holding the GPU has to name the right one.</summary>
        protected virtual string RunTokenPrefix => "h3eros";

        private readonly ObservableCollection<ErosHuntClip> _board = new();

        /// <summary>
        /// Re-rolls asked for while a sweep owned the GPU, in the order they were clicked. Slot 0 means the
        /// whole clip; anything else is that one take.
        ///
        /// <para>A plain list rather than the queue of <see cref="H3CastQueueItem"/>s the sweeps walk: these
        /// are requests against rows that are already <i>in</i> that queue, they are answered between the
        /// sweeps rather than by one, and a request the user takes back has to be removable from the middle.
        /// Not persisted — a re-roll waiting on a run that the app did not outlive is not something to
        /// silently start on the next launch.</para>
        ///
        /// <para><c>Generation</c> and <c>Prompt</c> are what the row looked like when the button was
        /// pressed, and are read once at the front of the queue: see <see cref="DrainRerollQueueAsync"/>.</para>
        /// </summary>
        private readonly List<(ErosHuntClip Row, int Slot, int Generation, string Prompt)> _rerollQueue = new();

        private double _previewMegapixels = 0.15;
        private string _selectedDiffusionModel = string.Empty;
        private bool _isLoadingDiffusionModels;
        private int _upscaleSteps = 4;
        private bool _useRife = true;
        private bool _autoPickSample;
        private int _autoPickSlot = 1;
        private bool _autoFinishWhenPicked = true;
        private bool _isSidePanelVisible = true;
        private bool _isTopPanelVisible = true;
        private bool _topPanelAutoFolded;
        private string _huntStatus = string.Empty;
        private string? _activePreviewUri;
        private string _activePreviewLabel = string.Empty;
        private double _tileSize = 104;
        private bool _isPreviewMuted = true;
        private bool _thumbnailSweepRunning;

        public H3ErosViewModel(
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
            // The drafts are the whole point of this graph, and the finished canvas is the upscaler's own
            // target rather than a multiple of the draft — so the Duo tab's draft/finish switch has no
            // meaning here and stays off.
            UseLatentUpscale = false;
            RtxUpscale = false;

            ToggleSidePanelCommand = new RelayCommand(() => IsSidePanelVisible = !IsSidePanelVisible);
            ToggleTopPanelCommand = new RelayCommand(() => IsTopPanelVisible = !IsTopPanelVisible);
            FinishPickedCommand = new RelayCommand(() => _ = ProcessQueueAsync(), () => HasUnfinishedPicks && !IsProcessingQueue);
            PickFirstEverywhereCommand = new RelayCommand(PickFirstEverywhere, () => HasBoard && !IsProcessingQueue);
            ClearPicksCommand = new RelayCommand(ClearPicks, () => HasAnyPick && !IsProcessingQueue);
            ToggleAllPromptsCommand = new RelayCommand(ToggleAllPrompts, () => HasBoard);
            ToggleMuteCommand = new RelayCommand(() => IsPreviewMuted = !IsPreviewMuted);
            ZoomTilesInCommand = new RelayCommand(() => TileSize += TileSizeStep, () => TileSize < MaxTileSize);
            ZoomTilesOutCommand = new RelayCommand(() => TileSize -= TileSizeStep, () => TileSize > MinTileSize);
            RefreshDiffusionModelsCommand = new RelayCommand(
                () => _ = LoadDiffusionModelsAsync(), () => !_isLoadingDiffusionModels);

            Queue.CollectionChanged += (_, _) => Application.Current.Dispatcher.Invoke(SyncBoard);
            SyncBoard();

            // The dropdown starts with what was chosen last run and the model the file ships, so the tab
            // is usable before — and if — the server answers. Enumerating the folder is a network round
            // trip, so it happens off the constructor's thread; this tab is on the window's startup path.
            _selectedDiffusionModel = NormalizeModel(RecallDiffusionModel(_settingsService.Settings));
            DiffusionModelOptions.Add(new DiffusionModelOption(ShippedModel, LabelFor(ShippedModel) + " (shipped)"));
            if (_selectedDiffusionModel.Length > 0 && _selectedDiffusionModel != ShippedModel)
                DiffusionModelOptions.Add(new DiffusionModelOption(_selectedDiffusionModel, LabelFor(_selectedDiffusionModel)));
            if (_selectedDiffusionModel.Length == 0) _selectedDiffusionModel = ShippedModel;
            _ = LoadDiffusionModelsAsync();

            AddLog("H3 Eros initialized — one ▶ Generate hunts three drafts for every clip in the queue " +
                   "back to back, then you pick the good ones and only those are upscaled and joined");
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The EROS-Hybrid seed-hunter graph, flattened to API format by
        /// <c>tools/convert_h3_eros.py</c>.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-eros.json";

        protected override string OutputSubfolder => "h3_eros";

        protected override string OutputFileStem => "H3Eros";

        protected override string OutputFolderName => "H3Eros";

        protected override string TabDisplayName => "H3 Eros";

        protected override string ChainLibraryFolder => "h3eros";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3eros_queue.json");

        /// <summary>
        /// The dropdown names the <b>finished</b> canvas — the megapixel target handed to the 3D latent
        /// upscaler — not the draft the previews are sampled at. Sizes are what the upscaler's
        /// <c>keep_proportion</c> + 32px alignment really produces at 16:9.
        /// </summary>
        public override IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.5, "0.5 MP — fast finish (960×544)"),
            new MegapixelOption(0.8, "0.8 MP — balanced (1216×672)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (1376×768)"),
            new MegapixelOption(1.5, "1.5 MP — high (1664×928)"),
            new MegapixelOption(2.0, "2.0 MP — 2K (1920×1088)"),
        };

        // ── The tab's own dials ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The diffusion model both sweeps load — the point of the dropdown being to run the same story
        /// through the different H3 checkpoints in <c>diffusion_models/h3-minimax</c> and compare them.
        /// Stored as ComfyUI names it, so it drops straight into the UNETLoader's <c>unet_name</c>.
        ///
        /// <para>Persisted the moment it changes, so a comparison run survives a restart. It is also
        /// frozen onto every queue item at Add to Queue: the finish re-samples the picked draft's branch,
        /// so a hunt and a finish on different models would not be the take that was chosen.</para>
        /// </summary>
        public string SelectedDiffusionModel
        {
            get => _selectedDiffusionModel;
            set
            {
                var name = NormalizeModel(value);
                if (name.Length == 0 || _selectedDiffusionModel == name) return;
                _selectedDiffusionModel = name;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiffusionModelSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    StoreDiffusionModel(settings, name);
                    _settingsService.SaveSettings(settings);
                }
                AddLog($"Model: {LabelFor(name)} — the next hunt and everything queued after it sample with this.");
            }
        }

        /// <summary>Where the dropdown's choice lives between runs. A fork of this tab renders on its own
        /// checkpoint, so it keeps its own slot rather than fighting this one over a shared field.</summary>
        protected virtual string? RecallDiffusionModel(ComfyUISettings? settings) =>
            settings?.H3ErosDiffusionModel;

        protected virtual void StoreDiffusionModel(ComfyUISettings settings, string name) =>
            settings.H3ErosDiffusionModel = name;

        /// <summary>Every model the connected ComfyUI reports under <see cref="ModelFolder"/>, plus the
        /// one the workflow ships and whatever was picked last run, so the list is never empty.</summary>
        public ObservableCollection<DiffusionModelOption> DiffusionModelOptions { get; } = new();

        /// <summary>Re-reads the folder from the server — for after a model has been dropped into it.</summary>
        public RelayCommand RefreshDiffusionModelsCommand { get; }

        /// <summary>The line under the dropdown: what is loaded, and the one caveat that bites.</summary>
        public string DiffusionModelSummary =>
            _isLoadingDiffusionModels
                ? "Reading diffusion_models/h3-minimax from ComfyUI…"
                : _selectedDiffusionModel.Contains("ref2va", StringComparison.OrdinalIgnoreCase) ||
                  _selectedDiffusionModel.Contains("hybrid", StringComparison.OrdinalIgnoreCase)
                    ? $"{DiffusionModelOptions.Count} model(s) in diffusion_models/h3-minimax."
                    : "This graph feeds MiniMaxH3ReferenceToVideo — a model trained only for first/last-frame " +
                      "work may render noise through it. Hunt one clip before queueing a story.";

        /// <summary>
        /// Fills <see cref="DiffusionModelOptions"/> from /object_info/UNETLoader, keeping only what lives
        /// in <see cref="ModelFolder"/>. Asking the server rather than reading the disk is what makes this
        /// work against a remote ComfyUI, where the models folder is not a path this machine has. A server
        /// that cannot be reached leaves the list exactly as the constructor seeded it.
        /// </summary>
        private async Task LoadDiffusionModelsAsync()
        {
            if (_isLoadingDiffusionModels) return;
            _isLoadingDiffusionModels = true;
            Application.Current.Dispatcher.Invoke(() =>
            {
                RefreshDiffusionModelsCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(DiffusionModelSummary));
            });
            try
            {
                var all = await _comfyUIService.HttpClient.GetUnetFilenamesAsync();
                var found = all
                    .Where(n => n.Replace('\\', '/').StartsWith(ModelFolder, StringComparison.OrdinalIgnoreCase))
                    .Select(n => n.Replace('\\', '/'))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (found.Count == 0)
                {
                    AddLog("Model list: ComfyUI reported nothing under diffusion_models/h3-minimax — " +
                           "keeping the model the workflow ships.");
                    return;
                }

                // The chosen model survives the rebuild even when the server no longer has it: it stays in
                // the list, labelled, so the tab does not silently start sampling with something else.
                var keep = _selectedDiffusionModel;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    DiffusionModelOptions.Clear();
                    foreach (var n in found)
                        DiffusionModelOptions.Add(new DiffusionModelOption(
                            n, LabelFor(n) + (n == ShippedModel ? " (shipped)" : string.Empty)));

                    // The server's own spelling wins where it differs only in case, so the value written
                    // into unet_name is byte-for-byte what ComfyUI offered.
                    var match = found.FirstOrDefault(n => string.Equals(n, keep, StringComparison.OrdinalIgnoreCase));
                    if (match == null && keep.Length > 0)
                        DiffusionModelOptions.Add(new DiffusionModelOption(keep, LabelFor(keep) + " (not on server)"));

                    // Written through the field, not the property: Clear() has already pushed null back
                    // through the ComboBox's two-way SelectedValue binding, so the notification below is
                    // what puts the selection back on screen — and the setter, seeing no change, would
                    // never raise it. Going round the setter also skips a settings write on every startup.
                    _selectedDiffusionModel = match ?? keep;
                    OnPropertyChanged(nameof(SelectedDiffusionModel));
                    OnPropertyChanged(nameof(DiffusionModelSummary));
                });
                AddLog($"Model list: {found.Count} model(s) in diffusion_models/h3-minimax.");
            }
            catch (Exception ex)
            {
                AddLog($"Model list could not be read ({ex.Message}) — keeping {LabelFor(_selectedDiffusionModel)}.");
            }
            finally
            {
                _isLoadingDiffusionModels = false;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    RefreshDiffusionModelsCommand.NotifyCanExecuteChanged();
                    OnPropertyChanged(nameof(DiffusionModelSummary));
                });
            }
        }

        /// <summary>Slashes one way and no stray whitespace, so a name from settings, from the server and
        /// from the workflow file all compare equal.</summary>
        private static string NormalizeModel(string? name) =>
            (name ?? string.Empty).Trim().Replace('\\', '/');

        /// <summary>What the dropdown shows: the filename, without the folder or the extension.</summary>
        protected static string LabelFor(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "(workflow default)";
            var file = name.Replace('\\', '/');
            var slash = file.LastIndexOf('/');
            if (slash >= 0) file = file[(slash + 1)..];
            var dot = file.LastIndexOf('.');
            return dot > 0 ? file[..dot] : file;
        }

        /// <summary>
        /// The megapixels the drafts are sampled at. Cheap on purpose — a queue of N clips costs 3N of
        /// these before anything is picked, and their only job is to let you tell compositions apart. The
        /// picked one is finished at <c>Megapixels</c>.
        /// </summary>
        public double PreviewMegapixels
        {
            get => _previewMegapixels;
            set
            {
                var clamped = Math.Clamp(value, 0.1, 1.0);
                if (Math.Abs(_previewMegapixels - clamped) < 0.0001) return;
                _previewMegapixels = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
            }
        }

        public virtual IReadOnlyList<MegapixelOption> PreviewMegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.15, "0.15 MP — default, quickest (512×288)"),
            new MegapixelOption(0.2, "0.2 MP — a little clearer (608×352)"),
            new MegapixelOption(0.3, "0.3 MP — clearer (736×416)"),
            new MegapixelOption(0.4, "0.4 MP — closest to the finish (864×480)"),
        };

        /// <summary>Fixed sigmas on the upscale pass: 3, 4 or 5. Four is what the graph ships live.</summary>
        public int UpscaleSteps
        {
            get => _upscaleSteps;
            set
            {
                var clamped = SigmaSchedules.ContainsKey(value) ? value : 4;
                if (_upscaleSteps == clamped) return;
                _upscaleSteps = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
            }
        }

        public IReadOnlyList<int> UpscaleStepOptions { get; } = new[] { 3, 4, 5 };

        /// <summary>RIFE on the finished frames, 24 → 48 fps. On is how the authored graph runs.</summary>
        public bool UseRife
        {
            get => _useRife;
            set { if (_useRife == value) return; _useRife = value; OnPropertyChanged(); OnPropertyChanged(nameof(HuntSummary)); }
        }

        /// <summary>
        /// Picks a slot for every clip the moment its hunt lands, so ▶ Generate runs the whole story —
        /// hunt, finish and join — without a human. Off by default: choosing is what the tab is for.
        /// </summary>
        public bool AutoPickSample
        {
            get => _autoPickSample;
            set
            {
                if (_autoPickSample == value) return;
                _autoPickSample = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HuntSummary));
                OnCanExecuteChanged();
            }
        }

        /// <summary>Which draft <see cref="AutoPickSample"/> takes, 1-3.</summary>
        public int AutoPickSlot
        {
            get => _autoPickSlot;
            set
            {
                var clamped = Math.Clamp(value, 1, SampleCount);
                if (_autoPickSlot == clamped) return;
                _autoPickSlot = clamped;
                OnPropertyChanged();
            }
        }

        public IReadOnlyList<int> AutoPickSlotOptions { get; } = new[] { 1, 2, 3 };

        /// <summary>
        /// Starts the finish sweep on its own once <i>every</i> hunted clip has a take picked, rather than
        /// waiting for ✨ Finish picked. On by default, so the ordinary run is: press Generate, watch the
        /// board fill, click your way down it, and the last click starts the upscales and the join.
        ///
        /// <para>It waits for all of them on purpose. Finishing clip 1 the instant it is picked would put
        /// the GPU under the board while clip 2 is still being watched, and would make going back to
        /// re-roll clip 1 a race against its own upscale.</para>
        /// </summary>
        public bool AutoFinishWhenPicked
        {
            get => _autoFinishWhenPicked;
            set { if (_autoFinishWhenPicked == value) return; _autoFinishWhenPicked = value; OnPropertyChanged(); }
        }

        /// <summary>What the sweeps will cost and produce, in one line under the controls.</summary>
        public virtual string HuntSummary
        {
            get
            {
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var fps = UseRife ? $"RIFE {DraftFrameRate}→{DraftFrameRate * 2} fps" : $"{DraftFrameRate} fps";
                var pick = AutoPickSample
                    ? $"take {AutoPickSlot} picked automatically"
                    : "you pick one per clip";
                return $"Hunt sweep: {SampleCount} drafts per clip at ≈{dw}×{dh} ({PreviewMegapixels:0.##} MP), " +
                       $"the whole queue without stopping, then {pick}. " +
                       $"Finish sweep: each picked latent upscaled to ≈{fw}×{fh} ({Megapixels:0.0} MP), " +
                       $"{UpscaleSteps} fixed sigmas, {fps}, then joined.";
            }
        }

        /// <summary>
        /// What the two sweeps produce, in the words of this graph rather than the Duo tab's. The base class
        /// describes a draft that is doubled by a fixed factor; here the finished canvas is the upscaler's
        /// own target and the draft is whatever the hunt was told to cost.
        /// </summary>
        public override string UpscaleSummary => HuntSummary;

        /// <summary>
        /// The peak frame stack, reported at the size the <i>finish</i> holds — the drafts are sampled one
        /// after another and are a fraction of it, so the finished canvas is the number that decides whether
        /// the server survives the clip.
        /// </summary>
        public override string LoadSummary
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var draftGb = FrameStackGb(frames, dw, dh);
                var finishGb = FrameStackGb(frames, fw, fh);
                var rife = UseRife ? FrameStackGb(frames * 2, fw, fh) : finishGb;

                var text = $"{frames} frames: ≈{draftGb:0.#} GB per draft at {dw}×{dh}, " +
                           $"≈{finishGb:0.#} GB at the finished {fw}×{fh}" +
                           (UseRife ? $", ≈{rife:0.#} GB after RIFE doubles them." : ".");
                return rife >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, " +
                             "drop the finished quality, or turn RIFE off."
                    : text;
            }
        }

        public override bool HasLoadWarning
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                return FrameStackGb(UseRife ? frames * 2 : frames, fw, fh) >= HeavyFrameStackGb;
            }
        }

        // ── The board ───────────────────────────────────────────────────────────────────────────────

        /// <summary>One row per queued clip, each holding that clip's drafts. A view of the queue, not a
        /// copy of it: picks are written straight through onto the queue items and saved with them.</summary>
        public ObservableCollection<ErosHuntClip> HuntBoard => _board;

        public RelayCommand FinishPickedCommand { get; }
        public RelayCommand PickFirstEverywhereCommand { get; }
        public RelayCommand ClearPicksCommand { get; }
        public RelayCommand ToggleAllPromptsCommand { get; }

        public bool HasBoard => _board.Count > 0;

        /// <summary>Any drafts anywhere — what makes the board worth showing.</summary>
        public bool HasDrafts => _board.Any(c => c.HasDrafts);

        public bool HasAnyPick => _board.Any(c => c.HasPick);

        /// <summary>Whether any row has its prompt box open — what the board-wide toggle does next.</summary>
        public bool ArePromptsOpen => _board.Any(c => c.IsDescriptionOpen);

        public string PromptsToggleLabel => ArePromptsOpen ? "✎ Hide prompts" : "✎ Show prompts";

        /// <summary>
        /// Whether the board is worth showing. As soon as anything is queued: before the hunt it is a list of
        /// the clips with their descriptions open for editing, which is the cheapest moment to fix a beat that
        /// came out wrong; during the hunt the tiles fill in; after it, it is what the tab is for.
        /// </summary>
        public bool ShowBoard => HasBoard;

        /// <summary>Picked clips that have not been upscaled yet — what ✨ Finish picked would run.</summary>
        public bool HasUnfinishedPicks => _board.Any(c => c.HasPick && !c.IsFinished);

        /// <summary>Hunted clips still waiting for a take to be chosen.</summary>
        public int UnpickedCount => _board.Count(c => c.HasDrafts && !c.HasPick && !c.IsFinished);

        /// <summary>The one-line story of where the tab is: hunting, waiting on picks, finishing, done.</summary>
        public string HuntStatus
        {
            get => _huntStatus;
            private set { if (_huntStatus == value) return; _huntStatus = value; OnPropertyChanged(); }
        }

        /// <summary>The clip the shared player is showing — a draft while choosing, a finished clip after.</summary>
        public string? ActivePreviewUri
        {
            get => _activePreviewUri;
            set
            {
                if (_activePreviewUri == value) return;
                _activePreviewUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActivePreview));
            }
        }

        /// <summary>Whether the shared player has anything loaded. Separate from <c>HasResult</c>: a draft is
        /// playable long before any clip has a finished file.</summary>
        public bool HasActivePreview => !string.IsNullOrEmpty(ActivePreviewUri);

        /// <summary>
        /// What the pinned player is showing, in one line — "Clip 3 / 12 · Take 2 · seed 5956…".
        ///
        /// <para>The board's tiles no longer print their seed under themselves: three seed lines per row,
        /// twelve rows deep, was a wall of digits nobody reads. The seed that matters is the one you have
        /// just chosen, and that one is here, above the video it belongs to.</para>
        /// </summary>
        public string ActivePreviewLabel
        {
            get => _activePreviewLabel;
            private set { if (_activePreviewLabel == value) return; _activePreviewLabel = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Whether the docked player is silent. It is, until you say otherwise.
        ///
        /// <para>The player loops whatever take it is showing, and picking down a board means loading a new
        /// one every few seconds. A hunt is judged on what the shot looks like — the audio is the same
        /// generated bed on all three takes — so sound here is a dozen clips of overlapping noise while you
        /// work. The button is next to the transport for when you do want to hear one.</para>
        /// </summary>
        public bool IsPreviewMuted
        {
            get => _isPreviewMuted;
            set
            {
                if (_isPreviewMuted == value) return;
                _isPreviewMuted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MuteGlyph));
                OnPropertyChanged(nameof(MuteTip));
            }
        }

        public RelayCommand ToggleMuteCommand { get; }

        /// <summary>Shows the state the player is in, not the state the button would put it in — a speaker
        /// with a line through it means you are not hearing this.</summary>
        public string MuteGlyph => IsPreviewMuted ? "🔇" : "🔊";

        public string MuteTip => IsPreviewMuted
            ? "The preview is muted. Click to hear the take that is playing."
            : "The preview has sound. Click to mute it — the player loops, so a board full of picks is a "
              + "lot of overlapping audio.";

        /// <summary>Sets what the player shows and what the caption above it says, together — they are one
        /// fact, and a caption left over from the last take is worse than none.</summary>
        protected void ShowInPlayer(string? uri, string label)
        {
            ActivePreviewUri = uri;
            ActivePreviewLabel = string.IsNullOrEmpty(uri) ? string.Empty : label;
        }

        /// <summary>"4 / 12 picked" — the board's own progress, which its status line cannot carry during a
        /// run because that line belongs to whatever is on the GPU.</summary>
        public string PickProgress
        {
            get
            {
                if (_board.Count == 0) return string.Empty;
                var picked = _board.Count(c => c.HasPick || c.IsFinished);
                return $"{picked} / {_board.Count} picked";
            }
        }

        // ── Tile size ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The side of a draft tile, in pixels. Square on purpose: the drafts are portrait in one story and
        /// landscape in the next, and a box shaped for one wastes half its width on the other.
        ///
        /// <para>A dial rather than a constant because the board is used two ways — scanning a whole story
        /// for the beat that went wrong wants small tiles, telling three near-identical takes apart wants
        /// large ones.</para>
        /// </summary>
        public double TileSize
        {
            get => _tileSize;
            private set
            {
                var clamped = Math.Clamp(value, MinTileSize, MaxTileSize);
                if (Math.Abs(_tileSize - clamped) < 0.5) return;
                _tileSize = clamped;
                OnPropertyChanged();
                ZoomTilesInCommand.NotifyCanExecuteChanged();
                ZoomTilesOutCommand.NotifyCanExecuteChanged();
                // The tiles read their size off their own draft, not off the tab — see
                // IErosBoardHost.BoardTileSize — so the board has to be told the dial moved.
                RefreshBoardState();
            }
        }

        /// <summary>The board's zoom, as the tiles see it.</summary>
        double IErosBoardHost.BoardTileSize => _tileSize;

        private const double MinTileSize = 68;
        private const double MaxTileSize = 208;
        private const double TileSizeStep = 28;

        public RelayCommand ZoomTilesInCommand { get; }
        public RelayCommand ZoomTilesOutCommand { get; }

        // ── Room for the board ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Whether the tab's input side — the scene, the story, the cast cards, the dials — is on screen.
        ///
        /// <para>It is a form you fill in once and a board you then look at for a long time, and the board is
        /// the larger job: a twelve-clip story is thirty-six videos to tell apart. Folding the inputs away
        /// gives their 340px to the tiles and drops the reference-sheet strip above them, which together is
        /// most of a screen. Nothing is lost — every input is exactly where it was when it comes back.</para>
        /// </summary>
        public bool IsSidePanelVisible
        {
            get => _isSidePanelVisible;
            set
            {
                if (_isSidePanelVisible == value) return;
                _isSidePanelVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SidePanelToggleGlyph));
                OnPropertyChanged(nameof(SidePanelToggleTip));
            }
        }

        public RelayCommand ToggleSidePanelCommand { get; }

        /// <summary>Which way the fold arrow points — towards where the panel will go.</summary>
        public string SidePanelToggleGlyph => IsSidePanelVisible ? "◀" : "▶";

        public string SidePanelToggleTip => IsSidePanelVisible
            ? "Fold the inputs away and give their width to the hunt board — the takes get half again as "
              + "wide. Nothing is lost; everything comes back exactly as you left it."
            : "Bring the scene, story, cast and dials back.";

        /// <summary>
        /// Whether the results pane's top half — the reference-sheet strip and the list of queued clips —
        /// is on screen. Both are things you check before pressing Generate and then stop needing: together
        /// they are most of the height above the board, and twelve queue rows alone are taller than the
        /// tiles they are pushing down. The queue's own header, with its buttons and its counts, stays.
        /// </summary>
        public bool IsTopPanelVisible
        {
            get => _isTopPanelVisible;
            set
            {
                if (_isTopPanelVisible == value) return;
                _isTopPanelVisible = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TopPanelToggleLabel));
                OnPropertyChanged(nameof(TopPanelToggleTip));
            }
        }

        public RelayCommand ToggleTopPanelCommand { get; }

        public string TopPanelToggleLabel => IsTopPanelVisible
            ? "▲  Hide the sheets and the queue list"
            : "▼  Show the sheets and the queue list";

        public string TopPanelToggleTip => IsTopPanelVisible
            ? "Folds away the character-sheet strip and the list of queued clips, and gives their height to "
              + "the hunt board. The queue's header — its counts and its buttons — stays where it is."
            : "Brings the character sheets and the queued clips back.";

        /// <summary>The window's <c>MediaFailed</c> handler, routed into the tab's log. A preview that will
        /// not open is the symptom this tab has had twice — a black frame with nothing said about it — so
        /// the failure is written down rather than swallowed.</summary>
        public void ReportPreviewFailed(string reason) =>
            AddLog($"Preview failed to open: {reason}");

        /// <summary>
        /// Rebuilds the board from the queue, keeping the rows that are already there so a queue change does
        /// not throw away drafts that are on screen. Rows for items that have gone are dropped; new items get
        /// a row hydrated from whatever the queue file remembered about their hunt.
        /// </summary>
        private void SyncBoard()
        {
            var byItem = _board.ToDictionary(c => c.Item);

            for (var i = 0; i < Queue.Count; i++)
            {
                var item = Queue[i];
                if (!byItem.TryGetValue(item, out var row))
                {
                    row = new ErosHuntClip(item, SampleCount, this);
                    Hydrate(row);
                }
                else
                {
                    byItem.Remove(item);
                }

                var at = _board.IndexOf(row);
                if (at < 0) _board.Insert(Math.Min(i, _board.Count), row);
                else if (at != i) _board.Move(at, i);
            }

            // Whatever is left in the map no longer has a queue item behind it.
            foreach (var orphan in byItem.Values) _board.Remove(orphan);

            // The first time there is anything to hunt, the sheet strip and the queue list get out of the
            // board's way. Both are things you check before pressing Generate and then stop needing, and
            // together they are 250px of a pane whose whole job from here on is showing takes. Once only,
            // and the labelled toggle brings them straight back — so a later choice of yours sticks.
            if (_board.Count > 0 && !_topPanelAutoFolded)
            {
                _topPanelAutoFolded = true;
                IsTopPanelVisible = false;
            }

            RefreshBoardState();
            StartThumbnailSweep();
        }

        /// <summary>Fills a fresh row from its queue item — the drafts the queue file remembered, the pick
        /// that was made against them, and whether the clip has already been finished.</summary>
        private void Hydrate(ErosHuntClip row)
        {
            var item = row.Item;
            row.Title = item.IsStoryClip ? $"Clip {item.ClipIndex} / {item.ClipCount}" : "Single clip";
            row.Summary = Shorten(CastPromptStamp.ExtractDescription(item.Prompt));
            row.OutputPath = !string.IsNullOrEmpty(item.OutputVideoPath) && File.Exists(item.OutputVideoPath)
                ? item.OutputVideoPath : null;
            row.IsFinished = item.ErosStage == StageFinished && row.OutputPath != null;

            for (var slot = 1; slot <= SampleCount; slot++)
            {
                var draft = row.Drafts[slot - 1];
                var path = slot <= item.HuntSamplePaths.Count ? item.HuntSamplePaths[slot - 1] : string.Empty;
                var seed = slot <= item.HuntSampleSeeds.Count ? item.HuntSampleSeeds[slot - 1] : -1;
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    draft.Status = "not hunted yet";
                    continue;
                }
                draft.VideoPath = path;
                draft.Seed = seed;
                draft.Status = $"take {draft.Slot}";
            }

            // Set before the pick: the setter runs DescriptionEdited, which stales a row whose prompt has
            // moved on, and a stale row must not come back from the queue file already picked.
            row.Description = CastPromptStamp.ShotLines(CastPromptStamp.ExtractDescription(item.Prompt));
            row.IsStale = row.HasDrafts && !string.IsNullOrEmpty(item.HuntPromptStamp)
                          && item.HuntPromptStamp != item.Prompt;

            row.ApplyPick(!row.IsStale && row.Drafts.Any(d => d.Slot == item.ChosenSampleSlot && d.HasVideo)
                ? item.ChosenSampleSlot : 0);
            // A queue file written before the per-slot seeds existed carries a pick with no seed behind it.
            // The finish needs that number, so it is taken from the draft the pick points at rather than
            // left at -1, which would fail the clip the moment the finish sweep reached it.
            if (row.HasPick && item.ChosenSeed < 0)
                item.ChosenSeed = row.Drafts[row.PickedSlot - 1].Seed;
            if (row.HasPick && item.ChosenSeed < 0)
            {
                row.ApplyPick(0);
                item.ChosenSampleSlot = 0;
            }

            row.Status = DescribeRow(row);
        }

        /// <summary>What a row is waiting for, in the two or three words the header has space for.</summary>
        private static string DescribeRow(ErosHuntClip row) =>
            row.IsFinished ? "finished"
            : row.IsRerollQueued ? "queued to re-roll"
            : row.IsStale ? "prompt edited — 🎲 to hunt it"
            : !row.HasDrafts ? "not hunted"
            : row.HasPick ? "picked"
            : "pick a take";

        /// <summary>
        /// The clip's description as one paragraph for its row on the board.
        ///
        /// <para>It used to be cut at 140 characters, which on a wide board ended every row in an ellipsis
        /// with most of the band left white — a hard limit deciding what fits when only the layout knows.
        /// The cut belongs to the <c>TextBlock</c>, against the width and the tile height it actually has;
        /// this flattens the line breaks and guards against a pathological prompt, nothing more.</para>
        /// </summary>
        private static string Shorten(string prompt)
        {
            // Split-and-join flattens the line breaks and the runs of spaces in one pass. A shot list
            // reads as a paragraph here, and ragged gaps in it look like broken text.
            var line = string.Join(" ", (prompt ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return line.Length <= 900 ? line : line[..897] + "…";
        }

        /// <summary>Re-raises everything the board's buttons and headers are bound to. Marshalled, because
        /// the sweeps call it from the render thread between submissions and <c>NotifyCanExecuteChanged</c>
        /// touches WPF command state.</summary>
        private void RefreshBoardState()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(RefreshBoardState);
                return;
            }

            foreach (var row in _board) row.RaiseState();
            OnPropertyChanged(nameof(HasBoard));
            OnPropertyChanged(nameof(HasDrafts));
            OnPropertyChanged(nameof(ShowBoard));
            OnPropertyChanged(nameof(HasAnyPick));
            OnPropertyChanged(nameof(ArePromptsOpen));
            OnPropertyChanged(nameof(PromptsToggleLabel));
            OnPropertyChanged(nameof(HasUnfinishedPicks));
            OnPropertyChanged(nameof(UnpickedCount));
            OnPropertyChanged(nameof(PickProgress));
            OnPropertyChanged(nameof(RerollQueueNote));
            OnPropertyChanged(nameof(HasQueuedRerolls));
            FinishPickedCommand.NotifyCanExecuteChanged();
            PickFirstEverywhereCommand.NotifyCanExecuteChanged();
            ClearPicksCommand.NotifyCanExecuteChanged();
            ToggleAllPromptsCommand.NotifyCanExecuteChanged();
        }

        // ── Board actions ───────────────────────────────────────────────────────────────────────────

        /// <summary>Nothing on the board may submit while the queue — or another re-roll — owns the GPU.</summary>
        bool IErosBoardHost.CanStartBoardJob => !IsProcessingQueue;

        /// <summary>But it may ask, and be answered when the GPU comes free: while a sweep is running the
        /// two 🎲 buttons park their work instead of being dead.</summary>
        bool IErosBoardHost.CanQueueBoardJob => IsProcessingQueue;

        /// <summary>"🎲 2 queued" for the board header — the one place a re-roll parked behind a long sweep
        /// is visible without hunting for the row it belongs to.</summary>
        public string RerollQueueNote =>
            _rerollQueue.Count == 0 ? string.Empty : $"🎲 {_rerollQueue.Count} queued";

        public bool HasQueuedRerolls => _rerollQueue.Count > 0;

        /// <summary>Clicking a tile does the obvious thing: it plays that take <i>and</i> makes it the one the
        /// clip will be finished from. The ✓ badge is a readout, not a second step.</summary>
        public void PickDraft(ErosSeedDraft? draft)
        {
            if (draft == null || !draft.HasVideo || !draft.Clip.CanAct) return;

            var row = draft.Clip;
            row.ApplyPick(draft.Slot);
            row.Status = "picked";
            row.Item.ChosenSampleSlot = draft.Slot;
            row.Item.ChosenSeed = draft.Seed;
            SaveQueueToFile();

            ShowInPlayer(draft.VideoPath, $"{row.Title} · Take {draft.Slot} · seed {draft.Seed}");
            RefreshBoardState();
            UpdateHuntStatus();
            MaybeAutoFinish();
        }

        /// <summary>Throws one draft away — the tile and the file. The clip keeps its other takes, and the
        /// empty slot can be re-rolled into something better.</summary>
        public void DeleteDraft(ErosSeedDraft? draft)
        {
            if (draft == null || !draft.Clip.CanAct) return;

            var row = draft.Clip;
            var path = draft.VideoPath;
            if (ActivePreviewUri == path) ShowInPlayer(null, string.Empty);

            draft.Clear();
            draft.Status = "deleted";
            StoreDraft(row.Item, draft.Slot, null, -1);

            if (row.PickedSlot == draft.Slot)
            {
                row.ApplyPick(0);
                row.Item.ChosenSampleSlot = 0;
                row.Item.ChosenSeed = -1;
            }
            row.Status = row.HasDrafts ? (row.HasPick ? "picked" : "pick a take") : "no takes left — 🎲 to hunt again";
            if (!row.HasDrafts) row.Item.ErosStage = string.Empty;
            SaveQueueToFile();

            TryDelete(path);
            RefreshBoardState();
            UpdateHuntStatus();
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { /* the tile is gone either way */ }
        }

        /// <summary>
        /// An edited description, spliced back into the clip's full prompt — preamble, wardrobe lock and
        /// sound fields untouched.
        ///
        /// <para><b>And the takes go stale.</b> The finish pass re-samples the picked branch from the prompt
        /// rather than reading a cached latent, so a prompt edited after the hunt would finish a video nobody
        /// has ever seen. The old takes stay on screen to compare against, but the pick is dropped and
        /// nothing can be picked again until the clip is re-rolled — which is the button this box exists
        /// to be used before.</para>
        /// </summary>
        public void DescriptionEdited(ErosHuntClip? row, string description)
        {
            if (row == null) return;

            var item = row.Item;
            // Hydrating a row assigns the box the shot-per-line spelling of the description it already
            // holds. That is a re-layout, not an edit, and staling every clip on it would cost a board.
            if (CastPromptStamp.SameDescription(CastPromptStamp.ExtractDescription(item.Prompt), description)) return;

            item.Prompt = CastPromptStamp.ReplaceDescription(item.Prompt, description);
            row.Summary = Shorten(description ?? string.Empty);

            if (row.HasDrafts && !row.IsFinished)
            {
                row.IsStale = !string.IsNullOrEmpty(item.HuntPromptStamp) && item.HuntPromptStamp != item.Prompt;
                if (row.IsStale && row.HasPick)
                {
                    row.ApplyPick(0);
                    item.ChosenSampleSlot = 0;
                    item.ChosenSeed = -1;
                }
            }

            row.Status = DescribeRow(row);
            SaveQueueToFile();
            RefreshBoardState();
            UpdateHuntStatus();
            AddLog($"{row.Title}: description edited" +
                   (row.IsStale ? " — its takes are of the old wording, so re-roll the clip to see the new one." : "."));
        }

        public void PlayClipResult(ErosHuntClip? row)
        {
            if (row?.OutputPath == null) return;
            ShowInPlayer(row.OutputPath, $"{row.Title} · finished clip");
        }

        /// <summary>Hunts one slot again on a fresh seed. A board job, not a queue pass — it takes the GPU
        /// for one submission and gives it straight back.</summary>
        public void RerollDraft(ErosSeedDraft? draft)
        {
            if (draft == null || !draft.Clip.CanAct) return;
            if (IsProcessingQueue) { ToggleQueuedReroll(draft.Clip, draft.Slot); return; }
            RunBoardJob(() => RerollDraftAsync(draft));
        }

        /// <summary>Hunts a whole clip again on a fresh base seed.</summary>
        public void RerollClip(ErosHuntClip? row)
        {
            if (row is not { CanAct: true }) return;
            if (IsProcessingQueue) { ToggleQueuedReroll(row, 0); return; }
            RunBoardJob(() => RerollClipAsync(row));
        }

        // ── Re-rolls asked for mid-sweep ────────────────────────────────────────────────────────────

        /// <summary>
        /// Puts a re-roll in the queue behind the running sweep, or takes it back out if it is already there.
        ///
        /// <para>Re-wording a beat and pressing 🎲 is a thing done <i>while</i> the hunt comes in — that is
        /// when the wrong beat is spotted — and until now the sweep simply held the button greyed. The click
        /// went nowhere and said nothing, which reads as a broken button rather than a busy GPU.</para>
        ///
        /// <para>A second click on the same 🎲 cancels: the request is a toggle, because the only thing to do
        /// about a mis-clicked one is take it back, and there is nowhere else to do that from.</para>
        /// </summary>
        private void ToggleQueuedReroll(ErosHuntClip row, int slot)
        {
            var what = slot == 0 ? "the whole clip" : $"take {slot}";
            var at = _rerollQueue.FindIndex(r => r.Row == row && r.Slot == slot);

            if (at >= 0)
            {
                _rerollQueue.RemoveAt(at);
                MarkQueued(row, slot, false);
                AddLog($"{row.Title}: {what} taken back out of the re-roll queue " +
                       $"({_rerollQueue.Count} still waiting).");
            }
            else if (slot != 0 && _rerollQueue.Any(r => r.Row == row && r.Slot == 0))
            {
                // The whole clip is already going to be re-hunted; queuing one of its takes as well would
                // spend a submission producing a draft the clip re-roll is about to overwrite.
                AddLog($"{row.Title}: the whole clip is already queued to re-roll — take {slot} is part of it.");
                return;
            }
            else
            {
                if (slot == 0)
                {
                    // ...and the same the other way round: a whole-clip re-roll swallows the single takes
                    // queued before it.
                    foreach (var superseded in _rerollQueue.Where(r => r.Row == row).ToList())
                    {
                        _rerollQueue.Remove(superseded);
                        MarkQueued(row, superseded.Slot, false);
                    }
                }
                _rerollQueue.Add((row, slot, row.HuntGeneration, row.Item.Prompt));
                MarkQueued(row, slot, true);
                AddLog($"{row.Title}: {what} queued to re-roll — it starts when the sweep on the GPU " +
                       $"finishes ({_rerollQueue.Count} waiting). Click 🎲 again to take it back out.");
            }

            row.Status = DescribeRow(row);
            RefreshBoardState();
        }

        /// <summary>Puts the waiting badge on — or takes it off — the tiles a request covers, and on the row
        /// that holds them. A whole-clip request badges every take: all three are being replaced.</summary>
        private static void MarkQueued(ErosHuntClip row, int slot, bool queued)
        {
            if (slot == 0)
                foreach (var d in row.Drafts) d.IsRerollQueued = queued;
            else
            {
                var draft = row.Drafts.FirstOrDefault(d => d.Slot == slot);
                if (draft != null) draft.IsRerollQueued = queued;
            }
            row.IsRerollQueued = queued || row.Drafts.Any(d => d.IsRerollQueued);
        }

        /// <summary>
        /// Runs everything parked while the sweep held the GPU, in the order it was clicked.
        ///
        /// <para>Called between the two sweeps and again after the finish sweep, and it re-reads its own list
        /// on every pass — a 🎲 pressed while the drain itself is running is still a request made during a
        /// run, and joins the back of the same queue.</para>
        /// </summary>
        private async Task DrainRerollQueueAsync(CancellationToken token)
        {
            var done = 0;
            while (_rerollQueue.Count > 0)
            {
                token.ThrowIfCancellationRequested();

                var (row, slot, generation, prompt) = _rerollQueue[0];
                _rerollQueue.RemoveAt(0);
                MarkQueued(row, slot, false);
                RefreshBoardState();

                // The board is rebuilt whenever the queue list changes, so a row queued a minute ago can
                // have been removed, or finished by the sweep that was running when it was asked for.
                if (!_board.Contains(row) || !row.CanAct)
                {
                    AddLog($"{row.Title}: queued re-roll dropped — the clip is no longer waiting for one.");
                    continue;
                }

                // Already answered. Re-wording a clip the hunt sweep had not reached yet and pressing 🎲 is
                // the obvious thing to do, and the sweep then renders it from that very wording — so the
                // request is granted before it is drained, and running it would be a second hunt of the
                // same prompt. Both halves matter: a re-roll asked for without an edit leaves the stamp
                // equal too, and is a request for different noise that must still be honoured.
                if (row.HuntGeneration > generation && row.Item.HuntPromptStamp == prompt)
                {
                    AddLog($"{row.Title}: queued re-roll already covered — the sweep hunted it from the " +
                           "wording that was asked for.");
                    continue;
                }

                done++;
                HuntStatus = $"Re-rolling {row.Title}" + (slot == 0 ? string.Empty : $" — take {slot}") +
                             (_rerollQueue.Count > 0 ? $" ({_rerollQueue.Count} more queued)" : string.Empty);

                try
                {
                    if (slot == 0) await RerollClipAsync(row);
                    else
                    {
                        var draft = row.Drafts.FirstOrDefault(d => d.Slot == slot);
                        if (draft != null) await RerollDraftAsync(draft);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // One failed re-roll must not strand the rest of the queue, nor the finish sweep behind it.
                    row.Status = $"re-roll failed: {ex.Message}";
                    AddLog($"{row.Title} FAILED to re-roll: {ex.Message}");
                }

                UpdateQueueStatus();
                SaveQueueToFile();
                RefreshBoardState();
            }

            if (done > 0) AddLog($"=== {done} queued re-roll(s) done. ===");
        }

        /// <summary>Empties the queue and clears its badges — for a run that was stopped, where the parked
        /// re-rolls would otherwise sit badged with nothing left running to answer them.</summary>
        private void ClearRerollQueue(string why)
        {
            if (_rerollQueue.Count == 0) return;
            var n = _rerollQueue.Count;
            foreach (var (row, slot, _, _) in _rerollQueue.ToList()) MarkQueued(row, slot, false);
            _rerollQueue.Clear();
            foreach (var row in _board) row.Status = DescribeRow(row);
            AddLog($"{n} queued re-roll(s) dropped — {why}. Press 🎲 again to run one now.");
        }

        /// <summary>
        /// "These are all fine" — take 1 everywhere, and take the hands off. It picks the first take that
        /// rendered on every clip that has one, and then <b>runs the rest of the queue to completion</b>:
        /// clips still to be hunted are hunted, their take 1 is picked as it lands, every pick is upscaled,
        /// and the story is joined. One press, no further clicks.
        ///
        /// <para><b>Why it turns three switches on rather than working around them.</b> Picking take 1 on a
        /// half-hunted board used to do almost nothing visible: the clips with drafts were picked, and then
        /// <see cref="MaybeAutoFinish"/> refused to start — correctly, by its own rule, because clips that
        /// have not been hunted have no pick and it will not upscale half a story. So the board sat there,
        /// and the only way on was to press ▶ Generate, wait out the hunt, and come back and press this
        /// again. The button's name promised an unattended run and the machinery underneath was built for a
        /// board somebody was standing at.</para>
        ///
        /// <para>So the press sets <see cref="AutoPickSample"/> (at slot 1) and
        /// <see cref="AutoFinishWhenPicked"/> rather than sidestepping them — the two checkboxes visibly
        /// tick, which is the honest way to show that a button just changed how the rest of the run will
        /// behave. They are the tab's own unattended switches; this is the one-press way of setting them.</para>
        /// </summary>
        /// <summary>
        /// What the hunt board needs before an unattended run: take 1 answered automatically as each clip
        /// lands, and the finish sweep started once they all have. Without these two the queue hunts the
        /// whole story and then parks at a board nobody is standing at.
        /// </summary>
        protected override void PrepareForUnattendedRun()
        {
            AutoPickSlot = 1;
            AutoPickSample = true;
            AutoFinishWhenPicked = true;
            AddLog("Unattended: take 1 is picked as each clip lands, and the finish sweep starts on its own.");
        }

        private void PickFirstEverywhere()
        {
            // Set before the picks, so a hunt that is somehow already running answers its own board.
            AutoPickSlot = 1;
            AutoPickSample = true;
            AutoFinishWhenPicked = true;

            var picked = 0;
            foreach (var row in _board.Where(c => c.CanAct && c.HasDrafts && !c.HasPick && !c.IsStale))
            {
                var first = row.Drafts.FirstOrDefault(d => d.HasVideo);
                if (first == null) continue;
                row.ApplyPick(first.Slot);
                row.Status = "picked";
                row.Item.ChosenSampleSlot = first.Slot;
                row.Item.ChosenSeed = first.Seed;
                picked++;
            }
            SaveQueueToFile();
            RefreshBoardState();
            UpdateHuntStatus();

            var toHunt = _board.Count(c => !c.IsFinished && !c.HasDrafts &&
                                           c.Item.ItemStatus == QueueItemStatus.Pending);
            var stale = _board.Count(c => c.IsStale && !c.IsFinished);

            AddLog($"Take 1 everywhere: {picked} clip(s) picked now" +
                   (toHunt > 0 ? $", {toHunt} still to hunt — they will be hunted, picked and finished " +
                                 "without stopping" : string.Empty) + ".");
            // Said out loud because both sweeps skip a stale clip, so it is the one thing that can make an
            // unattended run quietly deliver a short story. It has drafts, so the hunt sweep passes over it;
            // it cannot be picked, so the finish sweep passes over it too.
            if (stale > 0)
                AddLog($"WARNING: {stale} clip(s) are stale — their takes are of older wording, so they are " +
                       "skipped by both sweeps and will be missing from the join. 🎲 Re-roll them.");

            // Not MaybeAutoFinish(): that waits for every clip to have a pick, which is right for a click on
            // a single tile and wrong here — this press is the instruction to go and get the rest.
            if (!IsProcessingQueue && (picked > 0 || toHunt > 0)) _ = ProcessQueueAsync();
        }

        /// <summary>Opens every row's prompt box, or shuts them all. They are shut by default: on a
        /// twelve-clip board twelve open boxes are taller than the takes they belong to.</summary>
        private void ToggleAllPrompts()
        {
            var open = !ArePromptsOpen;
            foreach (var row in _board) row.IsDescriptionOpen = open;
            OnPropertyChanged(nameof(ArePromptsOpen));
            OnPropertyChanged(nameof(PromptsToggleLabel));
        }

        private void ClearPicks()
        {
            foreach (var row in _board.Where(c => c.CanAct && c.HasPick))
            {
                row.ApplyPick(0);
                row.Status = row.HasDrafts ? "pick a take" : "not hunted";
                row.Item.ChosenSampleSlot = 0;
                row.Item.ChosenSeed = -1;
            }
            SaveQueueToFile();
            RefreshBoardState();
            UpdateHuntStatus();
        }

        /// <summary>Starts the finish sweep by itself once every hunted clip has been chosen — the last
        /// click on the board is what sets the upscales going.</summary>
        private void MaybeAutoFinish()
        {
            if (!AutoFinishWhenPicked || IsProcessingQueue) return;
            if (!HasUnfinishedPicks) return;
            // A clip whose hunt failed has neither drafts nor a pick and never will until it is retried;
            // waiting on it would strand every clip that did land.
            if (_board.Any(c => !c.IsFinished && !c.HasPick && !c.IsStale &&
                                c.Item.ItemStatus != QueueItemStatus.Failed)) return;

            AddLog("Every clip has a take picked — starting the finish sweep.");
            _ = ProcessQueueAsync();
        }

        // ── The queue: hunt sweep, then finish sweep ────────────────────────────────────────────────

        /// <summary>
        /// This tab's drain loop, and the reason it is not the base class's. The base drains one item from
        /// Pending to Completed and moves on; here the queue is crossed twice — every clip is hunted before
        /// any is picked, and every pick is finished in one pass afterwards — so a long story is one
        /// uninterrupted stretch of GPU, one sitting at the board, and one more stretch of GPU.
        ///
        /// <para>Pressing ▶ Generate again after picking runs only the second half: the hunt sweep skips
        /// clips that already have drafts.</para>
        /// </summary>
        protected override async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            try
            {
                await HuntSweepAsync(token);
                // Before the finish sweep, not after: a clip re-worded during the hunt is re-hunted while
                // the board is still being picked over, so its new takes are there to choose between rather
                // than arriving after everything else has already been upscaled.
                await DrainRerollQueueAsync(token);
                await FinishSweepAsync(token);
                await DrainRerollQueueAsync(token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Queue stopped.");
            }
            catch (Exception ex)
            {
                AddLog($"Queue error: {ex.Message}");
            }
            finally
            {
                if (token.IsCancellationRequested) ClearRerollQueue("the queue was stopped");
                IsProcessingQueue = false;
                IsProcessing = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                UpdateQueueStatus();
                RefreshBoardState();
                UpdateHuntStatus();
                SaveQueueToFile();
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Down the whole queue, three drafts per clip, without stopping. Clips that already have drafts are
        /// skipped, so this is a no-op on the second press of ▶ Generate.
        /// </summary>
        private async Task HuntSweepAsync(CancellationToken token)
        {
            var todo = _board.Where(c => !c.IsFinished && !c.HasDrafts &&
                                         c.Item.ItemStatus == QueueItemStatus.Pending).ToList();
            if (todo.Count == 0) return;

            AddLog($"=== Hunt sweep: {todo.Count} clip(s) × {SampleCount} drafts " +
                   $"= {todo.Count * SampleCount} takes at {PreviewMegapixels:0.##} MP ===");

            for (var i = 0; i < todo.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = todo[i];
                var item = row.Item;
                var from = 100.0 * i / todo.Count;
                var to = 100.0 * (i + 1) / todo.Count;

                item.Status = "Hunting";
                item.StartedAt ??= DateTime.Now;
                UpdateQueueStatus();
                HuntStatus = $"Hunting {row.Title} — {i + 1} of {todo.Count}";

                try
                {
                    var baseSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                    item.HuntBaseSeed = baseSeed;
                    var seeds = Enumerable.Range(0, SampleCount).Select(n => baseSeed + n).ToArray();

                    await HuntAsync(row, Enumerable.Range(1, SampleCount).ToArray(), seeds, from, to, token);

                    item.Status = "Hunted";
                    item.ErosStage = StageHunted;

                    if (AutoPickSample) AutoPick(row);
                }
                catch (OperationCanceledException)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    throw;
                }
                catch (Exception ex)
                {
                    if (await TryHandleCrashAndRetryAsync(item, ex))
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog($"{row.Title}: reset to Pending — will hunt again after the ComfyUI restart.");
                    }
                    else
                    {
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        row.Status = $"hunt failed: {ex.Message}";
                        AddLog($"{row.Title} FAILED to hunt: {ex.Message}");
                    }
                }

                UpdateQueueStatus();
                SaveQueueToFile();
                RefreshBoardState();
            }

            var unpicked = UnpickedCount;
            AddLog(unpicked > 0
                ? $"=== Hunt sweep done. {unpicked} clip(s) are waiting for a take to be picked — " +
                  "click a tile to watch it and choose it. The GPU is free until then. ==="
                : "=== Hunt sweep done. ===");
            UpdateHuntStatus();
        }

        /// <summary>Answers the board for an unattended run, taking the configured slot or the first take
        /// that actually rendered.</summary>
        private void AutoPick(ErosHuntClip row)
        {
            var draft = row.Drafts.FirstOrDefault(d => d.Slot == AutoPickSlot && d.HasVideo)
                        ?? row.Drafts.FirstOrDefault(d => d.HasVideo);
            if (draft == null) return;

            Application.Current.Dispatcher.Invoke(() =>
            {
                row.ApplyPick(draft.Slot);
                row.Status = "picked (auto)";
            });
            row.Item.ChosenSampleSlot = draft.Slot;
            row.Item.ChosenSeed = draft.Seed;
            AddLog($"{row.Title}: auto-picked take {draft.Slot} (seed {draft.Seed}).");
        }

        /// <summary>
        /// Every clip that has a take picked and has not been finished, in queue order: the picked latent
        /// upscaled, re-sampled, RIFE'd, muxed — and, as each story's last clip lands, joined.
        /// </summary>
        private async Task FinishSweepAsync(CancellationToken token)
        {
            var todo = _board.Where(c => c.HasPick && !c.IsFinished && !c.IsStale &&
                                         c.Item.ItemStatus == QueueItemStatus.Pending).ToList();
            if (todo.Count == 0)
            {
                if (UnpickedCount > 0)
                    AddLog("Nothing to finish yet — pick a take on the board and the upscales start " +
                           (AutoFinishWhenPicked ? "on their own." : "when you press ✨ Finish picked."));
                return;
            }

            AddLog($"=== Finish sweep: {todo.Count} picked clip(s) → {Megapixels:0.0} MP ===");

            for (var i = 0; i < todo.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = todo[i];
                var item = row.Item;
                var from = 100.0 * i / todo.Count;
                var to = 100.0 * (i + 1) / todo.Count;

                item.ItemStatus = QueueItemStatus.Processing;
                item.StartedAt = DateTime.Now;
                row.IsBusy = true;
                UpdateQueueStatus();
                RefreshBoardState();
                HuntStatus = $"Finishing {row.Title} — {i + 1} of {todo.Count}";

                try
                {
                    await FinishAsync(row, from, to, token);
                    item.ItemStatus = QueueItemStatus.Completed;
                    item.CompletedAt = DateTime.Now;
                    item.ErosStage = StageFinished;
                    row.IsFinished = true;
                    row.Status = "finished";
                    AddLog($"Completed: {item.DisplayText}");
                    // Never throws — a join problem must not push a rendered clip back to Pending.
                    await CompleteStoryAsync(item, token);
                }
                catch (OperationCanceledException)
                {
                    item.ItemStatus = QueueItemStatus.Pending;
                    row.Status = "stopped";
                    throw;
                }
                catch (Exception ex)
                {
                    if (await TryHandleCrashAndRetryAsync(item, ex))
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        row.Status = "will retry after the ComfyUI restart";
                        AddLog($"{row.Title}: reset to Pending — the pick is kept, only the upscale is redone.");
                    }
                    else
                    {
                        item.ItemStatus = QueueItemStatus.Failed;
                        item.ErrorMessage = ex.Message;
                        row.Status = $"finish failed: {ex.Message}";
                        AddLog($"{row.Title} FAILED to finish: {ex.Message}");
                    }
                }
                finally
                {
                    row.IsBusy = false;
                }

                UpdateQueueStatus();
                SaveQueueToFile();
                RefreshBoardState();
            }
        }

        /// <summary>Runs one board button's work off the queue loop — a single re-roll, which is a GPU job
        /// but not a queue pass.</summary>
        private void RunBoardJob(Func<Task> job) => _ = RunBoardJobAsync(job);

        private async Task RunBoardJobAsync(Func<Task> job)
        {
            if (IsProcessingQueue) return;
            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            try
            {
                await job();
                // A re-roll holds the GPU for a submission, and a 🎲 pressed while it did was parked rather
                // than refused — so the same drain runs here as at the end of a sweep.
                await DrainRerollQueueAsync(_queueCts!.Token);
            }
            catch (OperationCanceledException)
            {
                AddLog("Re-roll stopped.");
                ClearRerollQueue("the re-roll was stopped");
            }
            catch (Exception ex) { AddLog($"Re-roll failed: {ex.Message}"); }
            finally
            {
                IsProcessingQueue = false;
                IsProcessing = false;
                ProcessingStatus = "Ready";
                RefreshBoardState();
                UpdateHuntStatus();
                SaveQueueToFile();
                OnCanExecuteChanged();
            }
        }

        /// <summary>Hunts a single slot again on a fresh seed, leaving the clip's other takes alone. This is
        /// what makes an empty or a bad tile cheap to replace — a third of a hunt, not a whole one.</summary>
        private async Task RerollDraftAsync(ErosSeedDraft draft)
        {
            var row = draft.Clip;
            var seed = System.Random.Shared.NextInt64(0, long.MaxValue);
            AddLog($"{row.Title}: re-rolling take {draft.Slot} on seed {seed}.");

            if (row.PickedSlot == draft.Slot)
            {
                row.ApplyPick(0);
                row.Item.ChosenSampleSlot = 0;
                row.Item.ChosenSeed = -1;
            }
            TryDelete(draft.VideoPath);
            row.IsBusy = true;
            try
            {
                await HuntAsync(row, new[] { draft.Slot }, new[] { seed }, 0, 100, _queueCts!.Token);
                if (row.Item.ErosStage != StageFinished) row.Item.ErosStage = StageHunted;
            }
            finally { row.IsBusy = false; }
        }

        /// <summary>Throws a clip's three takes away and hunts it again on a fresh base seed. The prompt, the
        /// cast and the canvas are unchanged — only the noise is.</summary>
        private async Task RerollClipAsync(ErosHuntClip row)
        {
            var baseSeed = System.Random.Shared.NextInt64(0, long.MaxValue);
            AddLog($"{row.Title}: re-rolling all {SampleCount} takes on base seed {baseSeed}.");

            row.ApplyPick(0);
            row.Item.ChosenSampleSlot = 0;
            row.Item.ChosenSeed = -1;
            row.Item.HuntBaseSeed = baseSeed;
            foreach (var d in row.Drafts) TryDelete(d.VideoPath);

            row.IsBusy = true;
            try
            {
                await HuntAsync(row,
                    Enumerable.Range(1, SampleCount).ToArray(),
                    Enumerable.Range(0, SampleCount).Select(n => baseSeed + n).ToArray(),
                    0, 100, _queueCts!.Token);
                if (row.Item.ItemStatus == QueueItemStatus.Failed)
                {
                    row.Item.ItemStatus = QueueItemStatus.Pending;
                    row.Item.ErrorMessage = null;
                }
                row.Item.Status = "Hunted";
                row.Item.ErosStage = StageHunted;
            }
            finally { row.IsBusy = false; }
        }

        // ── Phase 1 — the hunt ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// One submission producing one draft per requested slot. The graph is pruned to just those slots'
        /// preview sinks, so nothing downstream of the picker — the upscaler, the second pass, RIFE, the
        /// final mux — is even in the prompt, and the requested samplers are all the GPU spends time on.
        /// </summary>
        private async Task HuntAsync(
            ErosHuntClip row, IReadOnlyList<int> slots, IReadOnlyList<long> seeds,
            double progressFrom, double progressTo, CancellationToken token)
        {
            var item = row.Item;
            IsProcessing = true;

            var uploaded = await UploadPanelsAsync(item);
            var prompt = DetaggedPrompt(item);
            var len = ClampLength(item.LengthSeconds);
            var (dw, dh) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            var clipTag = item.IsStoryClip ? $"c{item.ClipIndex:00}" : "c01";
            var huntToken = $"{RunTokenPrefix}_hunt_{stamp}_{clipTag}";

            AddLog($"{row.Title}: hunting take(s) {string.Join(", ", slots)} at ≈{dw}×{dh} " +
                   $"({item.PreviewMegapixels:0.##} MP), {len:0.#}s / {FramesForSeconds(len)} frames, " +
                   $"seed(s) {string.Join(", ", seeds)}, model {LabelFor(item.DiffusionModel)}.");

            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = ParseGraph(json);
            ApplyCommonInputs(root, item, uploaded, prompt, len);

            // What these takes are of. The finish re-samples from the prompt, so an edit after this point
            // has to be visible as staleness rather than quietly changing what gets finished.
            item.HuntPromptStamp = item.Prompt;

            for (var i = 0; i < slots.Count; i++)
            {
                var (_, sink, noise) = SampleBranches[slots[i] - 1];
                SetInput(root, noise, "noise_seed", seeds[i]);
                SetInput(root, sink, "frame_rate", DraftFrameRate);
                SetInput(root, sink, "save_output", true);
                SetInput(root, sink, "filename_prefix", $"{OutputSubfolder}/{huntToken}_p{slots[i]}");
            }

            var keep = slots.Select(s => SampleBranches[s - 1].Sink).ToList();
            json = PruneToOutputs(root.ToJsonString(), keep, out var pruned);
            AddLog($"  Hunt graph: pruned to {keep.Count} preview sink(s), {pruned} node(s) removed.");

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var s in slots)
                {
                    var d = row.Drafts[s - 1];
                    d.Clear();
                    d.IsRendering = true;
                    d.Status = "rendering…";
                }
                // Whatever was stale is being re-hunted against the prompt as it now reads.
                row.IsStale = false;
                row.Status = "hunting…";
                RefreshBoardState();
            });

            string promptId;
            var lease = await AcquireLeaseAsync($"{TabDisplayName} hunt", token);
            try
            {
                ProcessingStatus = $"Hunting {row.Title}...";
                promptId = await SubmitAsync(json, progressFrom, progressFrom + (progressTo - progressFrom) * 0.9, token);
            }
            finally
            {
                // Released the moment the submission returns: the picking that follows must not hold the GPU.
                lease.Dispose();
            }

            ProcessingStatus = "Collecting the drafts...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);

            var found = 0;
            for (var i = 0; i < slots.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var slot = slots[i];
                var (_, sink, _) = SampleBranches[slot - 1];

                string? local = null;
                if (byNode.TryGetValue(sink, out var outs) && outs.Count > 0)
                {
                    var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                    local = await ResolveOutputToLocalAsync(pick);
                }
                local ??= FindTokenVideoOnDisk($"{huntToken}_p{slot}");

                if (local != null && File.Exists(local))
                {
                    SetDraft(row, slot, local, seeds[i]);
                    found++;
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        row.Drafts[slot - 1].IsRendering = false;
                        row.Drafts[slot - 1].Status = "no output — 🎲 to try again";
                    });
                    StoreDraft(item, slot, null, -1);
                    AddLog($"  Take {slot}: no output produced.");
                }
            }

            if (found == 0)
                throw new Exception("The hunt produced no drafts.");

            ProcessingProgress = progressTo;
            row.HuntGeneration++;
            Application.Current.Dispatcher.Invoke(() =>
            {
                row.Status = DescribeRow(row);
                RefreshBoardState();
            });
            AddLog($"  {row.Title}: {found}/{slots.Count} take(s) ready.");
        }

        /// <summary>Uploads this clip's panels — the same policy as H3 Cast: one reference per view, never
        /// the assembled sheet, and character 2's only when the clip names them.</summary>
        protected async Task<IReadOnlyList<string>> UploadPanelsAsync(H3CastQueueItem item)
        {
            ProcessingStatus = "Uploading character references...";

            var panels1 = ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1);
            var includesCharacter2 = CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2);
            var panels2 = includesCharacter2
                ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2)
                : (IReadOnlyList<string>)Array.Empty<string>();

            var uploaded = new List<string>();
            foreach (var panel in panels1.Concat(panels2))
                uploaded.Add(await EnsureUploadedAsync(panel));
            if (uploaded.Count == 0)
                throw new Exception("No reference images to wire — the cast has no panels.");
            if (uploaded.Count > MaxReferenceImages)
                throw new Exception($"{uploaded.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                    $"takes at most {MaxReferenceImages}. Split the sheets into fewer panels.");
            return uploaded;
        }

        /// <summary>The clip's prompt with the picture-numbering tags resolved against the panels this item
        /// actually uploads. Identical in both sweeps — a difference here is a different video.</summary>
        protected string DetaggedPrompt(H3CastQueueItem item) => CastPromptStamp.Detag(
            item.Prompt,
            ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1).Count,
            CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2)
                ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2).Count
                : 0);

        // ── Phase 3 — the finish ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The picked take, at full size. The same file and the same first-pass inputs as the hunt, with the
        /// picked branch's own seed written back and everything the final sink cannot reach pruned away —
        /// the other two samplers, their decodes and their sinks among them.
        ///
        /// <para>Sampling is deterministic, so re-running that one branch reproduces the latent the draft was
        /// decoded from; what comes out is the take that was chosen, upscaled, not a new take of the same
        /// seed. When a hunt and its finish happen to be adjacent submissions ComfyUI's execution cache
        /// skips even that.</para>
        /// </summary>
        protected virtual Task FinishAsync(ErosHuntClip row, double progressFrom, double progressTo,
            CancellationToken token) => UpscaleFinishAsync(row, progressFrom, progressTo, token);

        /// <inheritdoc cref="FinishAsync"/>
        protected async Task UpscaleFinishAsync(ErosHuntClip row, double progressFrom, double progressTo,
            CancellationToken token)
        {
            var item = row.Item;
            IsProcessing = true;
            HasResult = false;

            var chosen = item.ChosenSampleSlot;
            var seed = item.ChosenSeed;
            if (chosen is < 1 or > SampleCount || seed < 0)
                throw new Exception("This clip has no usable pick — choose a take on the board first.");

            var uploaded = await UploadPanelsAsync(item);
            var prompt = DetaggedPrompt(item);
            var len = ClampLength(item.LengthSeconds);
            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
            var runToken = $"{RunTokenPrefix}_{ts}{clipTag}";
            var (fw, fh) = H3Canvas.Resolve(item.AspectRatio, item.Megapixels, 32);

            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = ParseGraph(json);
            ApplyCommonInputs(root, item, uploaded, prompt, len);

            var (sampler, _, noise) = SampleBranches[chosen - 1];
            SetInput(root, noise, "noise_seed", seed);

            Link(root, NodeLatentSplit, "av_latent", sampler, DenoisedSlot);
            // The graph's single-pass decode of the same latent — the author's "skip upscale" option. Nothing
            // consumes it here, so the prune below removes it; it is repointed anyway so the graph never
            // carries a link into a sampler that is about to be deleted.
            Link(root, NodeSinglePassVideo, "samples", sampler, DenoisedSlot);
            Link(root, NodeSinglePassAudio, "samples", sampler, DenoisedSlot);

            // The upscale pass. Its own noise seed is fresh every finish — re-rolling it is how the authored
            // graph offers variations of the same picked composition.
            SetInput(root, NodeUpscaler, "mode", "megapixels");
            SetInput(root, NodeUpscaler, "mode.megapixels", item.Megapixels);
            SetInput(root, NodeUpscaleNoise, "noise_seed", System.Random.Shared.NextInt64(0, long.MaxValue));
            Link(root, NodeUpscaleSampler, "sigmas",
                 SigmaSchedules.TryGetValue(item.UpscaleSteps, out var sigmas) ? sigmas : SigmaSchedules[4], 0);

            // Which decode feeds the mux, and whether RIFE stands between them.
            var video = NodeUpscaledVideo;
            var audio = NodeUpscaledAudio;
            if (item.UseRife)
            {
                SetInput(root, NodeRife, "source_fps", (double)DraftFrameRate);
                SetInput(root, NodeRife, "target_fps", (double)(DraftFrameRate * 2));
                Link(root, NodeRife, "images", video, 0);
                Link(root, NodeFinalSave, "images", NodeRife, 0);
                SetInput(root, NodeFinalSave, "frame_rate", DraftFrameRate * 2);
            }
            else
            {
                Link(root, NodeFinalSave, "images", video, 0);
                SetInput(root, NodeFinalSave, "frame_rate", DraftFrameRate);
            }
            Link(root, NodeFinalSave, "audio", audio, 0);
            SetInput(root, NodeFinalSave, "save_output", true);
            SetInput(root, NodeFinalSave, "filename_prefix", $"{OutputSubfolder}/{runToken}_final");

            json = PruneToOutputs(root.ToJsonString(), new[] { NodeFinalSave }, out var pruned);
            AddLog($"{row.Title}: finishing take {chosen} (seed {seed}, {item.UpscaleSteps} fixed sigmas " +
                   $"at {fw}×{fh}, {(item.UseRife ? $"RIFE → {DraftFrameRate * 2}fps" : $"{DraftFrameRate}fps")}). " +
                   $"Finish graph: the picked branch kept, {pruned} node(s) removed.");

            var lease = await AcquireLeaseAsync($"{TabDisplayName} finish", token);
            try
            {
                ProcessingStatus = $"Upscaling {row.Title} take {chosen} to {fw}×{fh}...";
                var local = await SubmitAndRetrieveAsync(json, $"{runToken}_final", NodeFinalSave,
                                                         progressFrom, progressTo, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);
                Directory.CreateDirectory(outputDir);
                var finalName = item.IsStoryClip
                    ? $"{OutputFileStem}_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}{OutputFileSuffix}.mp4"
                    : $"{OutputFileStem}_{ts}{OutputFileSuffix}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    row.OutputPath = finalPath;
                    ResultVideoPath = finalPath;
                    ShowInPlayer(finalPath, $"{row.Title} · finished · take {chosen}");
                    ResultVideoInfo = $"{TabDisplayName} • " +
                                      $"{(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"take {chosen} • ≈{fw}×{fh} • {item.AspectRatio} • " +
                                      $"{len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                AddLog($"=== {row.Title} complete: {finalPath} ===");
            }
            finally
            {
                lease.Dispose();
            }
        }

        // ── Graph patching ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Everything both sweeps must agree on, written identically into both submissions. The prompt, the
        /// references, the draft canvas, the length and the first-pass step count all feed the samplers, so a
        /// single difference between hunt and finish is a different latent — and a finished clip that is not
        /// the take that was picked.
        /// </summary>
        protected virtual void ApplyCommonInputs(
            JsonObject root, H3CastQueueItem item, IReadOnlyList<string> uploaded,
            string prompt, double lengthSeconds)
        {
            RequireClass(root, NodeRef2V, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeFinalSave, "VHS_VideoCombine");

            // ── The model ─────────────────────────────────────────────────────
            // The item's own, not the dropdown's: the finish re-samples the picked draft's branch, so
            // hunting and finishing on different checkpoints would produce a clip that is not the take
            // that was picked. Empty is a queue item written before the dropdown existed — leave the
            // workflow file's own model alone.
            if (!string.IsNullOrWhiteSpace(item.DiffusionModel))
            {
                RequireClass(root, NodeUnet, "UNETLoader");
                SetInput(root, NodeUnet, "unet_name", item.DiffusionModel);
            }

            // ── References ────────────────────────────────────────────────────
            // The graph ships none at all — the author's were LoadImageCrop nodes, a custom node this
            // server does not have — so every one is injected beside the reference node.
            var loaders = new List<string>();
            for (var i = 0; i < uploaded.Count; i++)
            {
                var id = $"eros_ref_{i}";
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeRef2V, loaders);
            SetInput(root, NodeRef2V, "ref_image_size", item.MaxFidelityReferences ? "max" : "match");

            // ── Prompt, length, first-pass steps ──────────────────────────────
            SetInput(root, NodePrompt, "value", prompt);
            SetInput(root, NodeSeconds, "value", lengthSeconds);
            SetInput(root, NodeSteps, "value", FirstPassSteps);

            // ── The draft canvas ──────────────────────────────────────────────
            // ResolutionSelector sizes the drafts only; the finished size is the upscaler's target and is set
            // in the finish. Aspects the node's combo does not accept are resolved here and fed in as two
            // PrimitiveInt nodes standing where its width and height outputs were.
            if (H3Canvas.RequiresLiteralCanvas(item.AspectRatio))
            {
                var (cw, ch) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
                root[NodeCanvasWidth] = IntNode(cw, "Draft width");
                root[NodeCanvasHeight] = IntNode(ch, "Draft height");
                Retarget(root, NodeResolution, 0, NodeCanvasWidth);
                Retarget(root, NodeResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, NodeResolution, "aspect_ratio", item.AspectRatio);
                SetInput(root, NodeResolution, "megapixels", item.PreviewMegapixels);
                SetInput(root, NodeResolution, "multiple", 32);
            }
        }

        protected static JsonObject ParseGraph(string json) =>
            JsonNode.Parse(json)?.AsObject()
            ?? throw new Exception("Workflow JSON could not be parsed.");

        protected async Task<WorkflowQueueCoordinator.WorkflowLease> AcquireLeaseAsync(
            string label, CancellationToken token)
        {
            ProcessingStatus = "Waiting for other workflows to finish...";
            AddLog($"{label}: waiting for other workflows to finish...");
            var lease = await _workflowCoordinator.AcquireAsync(TabDisplayName, token);
            try
            {
                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }
                return lease;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        protected static void SetInput(JsonObject root, string nodeId, string input, object value)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString())
            };
        }

        /// <summary>Points one node's input at another node's output.</summary>
        protected static void Link(JsonObject root, string nodeId, string input, string sourceId, int slot)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            if (root[sourceId] == null)
                throw new Exception($"Workflow node '{sourceId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = new JsonArray(sourceId, slot);
        }

        protected static JsonObject IntNode(int value, string title) => new()
        {
            ["inputs"] = new JsonObject { ["value"] = value },
            ["class_type"] = "PrimitiveInt",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        /// <summary>
        /// Repoints every link that reads <paramref name="slot"/> of <paramref name="sourceId"/> at slot 0
        /// of <paramref name="newId"/> instead.
        /// </summary>
        protected static void Retarget(JsonObject root, string sourceId, int slot, string newId)
        {
            foreach (var node in root)
            {
                if (node.Value?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs.ToList())
                {
                    if (input.Value is not JsonArray link || link.Count != 2) continue;
                    if (link[0]?.ToString() != sourceId) continue;
                    if (link[1] is not JsonValue index || !index.TryGetValue<int>(out var i) || i != slot)
                        continue;

                    inputs[input.Key] = new JsonArray(newId, 0);
                }
            }
        }

        // ── Draft bookkeeping ───────────────────────────────────────────────────────────────────────

        /// <summary>Writes a landed draft onto both the board tile and the queue item, so it is on screen now
        /// and still there after a restart.</summary>
        protected virtual void SetDraft(ErosHuntClip row, int slot, string localPath, long seed)
        {
            StoreDraft(row.Item, slot, localPath, seed);

            Application.Current.Dispatcher.Invoke(() =>
            {
                var draft = row.Drafts[slot - 1];
                draft.IsRendering = false;
                draft.VideoPath = localPath;
                draft.Seed = seed;
                draft.Status = $"take {draft.Slot}";
                RefreshBoardState();
                AddLog($"  Take {slot} ready: {Path.GetFileName(localPath)}");
            });

            // The first draft of the whole run auto-loads into the player, so there is something to watch
            // the moment the sweep starts. After that the player follows the clicks.
            if (string.IsNullOrEmpty(ActivePreviewUri))
                Application.Current.Dispatcher.Invoke(
                    () => ShowInPlayer(localPath, $"{row.Title} · Take {slot} · seed {seed}"));

            // FFmpeg, on a background thread: the sweep's continuations can land on the UI thread, and a
            // second of blocked dispatcher per draft is a frozen window for the length of a hunt.
            _ = Task.Run(() =>
            {
                var thumb = ExtractFirstFrame(localPath);
                if (thumb != null)
                    Application.Current?.Dispatcher.Invoke(() => row.Drafts[slot - 1].Thumbnail = thumb);
            });
        }

        /// <summary>The persisted half of a draft: path and seed at the slot's index, lists grown as needed
        /// so a queue file written before the board existed still round-trips.</summary>
        protected static void StoreDraft(H3CastQueueItem item, int slot, string? path, long seed)
        {
            while (item.HuntSamplePaths.Count < SampleCount) item.HuntSamplePaths.Add(string.Empty);
            while (item.HuntSampleSeeds.Count < SampleCount) item.HuntSampleSeeds.Add(-1);
            item.HuntSamplePaths[slot - 1] = path ?? string.Empty;
            item.HuntSampleSeeds[slot - 1] = seed;
        }

        /// <summary>Fills in tile images for drafts restored from the queue file, one at a time in the
        /// background — a twelve-clip board is thirty-six FFmpeg calls, and none of them is urgent.</summary>
        private void StartThumbnailSweep()
        {
            if (_thumbnailSweepRunning) return;
            var missing = _board.SelectMany(c => c.Drafts)
                                .Where(d => d.HasVideo && d.Thumbnail == null)
                                .ToList();
            if (missing.Count == 0) return;

            _thumbnailSweepRunning = true;
            _ = Task.Run(() =>
            {
                try
                {
                    foreach (var draft in missing)
                    {
                        var path = draft.VideoPath;
                        if (string.IsNullOrEmpty(path)) continue;
                        var thumb = ExtractFirstFrame(path);
                        if (thumb == null) continue;
                        Application.Current?.Dispatcher.Invoke(() => draft.Thumbnail = thumb);
                    }
                }
                finally { _thumbnailSweepRunning = false; }
            });
        }

        private void UpdateHuntStatus()
        {
            if (IsProcessingQueue) return;
            var stale = _board.Count(c => c.IsStale);
            var unpicked = UnpickedCount;
            var pending = _board.Count(c => c.HasPick && !c.IsFinished);

            // A stale clip is the one state that goes nowhere on its own: ▶ Generate skips it (it has
            // drafts), and the finish sweep refuses it. Say so, or it looks like the queue simply stopped.
            var staleNote = stale > 0
                ? $"{stale} clip(s) reworded and waiting to be re-rolled"
                : string.Empty;

            var main = unpicked > 0
                ? $"{unpicked} clip(s) waiting for a take — click a tile to watch it and choose it"
                : pending > 0
                    ? $"{pending} clip(s) picked and ready to finish"
                    : _board.Count > 0 && _board.All(c => c.IsFinished)
                        ? "every clip finished"
                        : string.Empty;

            HuntStatus = string.IsNullOrEmpty(staleNote) ? main
                       : string.IsNullOrEmpty(main) ? staleNote
                       : $"{staleNote} · {main}";
        }

        /// <summary>
        /// The tile image. Several simultaneous WPF <c>MediaElement</c>s render as solid black — with a whole
        /// story on the board it would be dozens — which is why the tiles are still frames and the one being
        /// watched plays in the single shared player.
        /// </summary>
        protected BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"h3eros_thumb_{Guid.NewGuid():N}.png");
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = $"-y -i \"{videoPath}\" -frames:v 1 -q:v 3 \"{outPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return null;
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(20000);
                }
                if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0) return null;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(outPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                AddLog($"Thumbnail extract failed: {ex.Message}");
                return null;
            }
        }

        // ── Queue ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The stock queue-add, with this tab's own settings frozen onto every item and any previous hunt
        /// cleared — a re-queued chain hunts again rather than showing the drafts of whatever run put those
        /// numbers there.
        /// </summary>
        protected override void AddToQueue()
        {
            var before = Queue.Count;
            base.AddToQueue();
            for (var i = before; i < Queue.Count; i++)
            {
                Queue[i].PreviewMegapixels = PreviewMegapixels;
                Queue[i].DiffusionModel = SelectedDiffusionModel;
                Queue[i].UpscaleSteps = UpscaleSteps;
                Queue[i].UseRife = UseRife;
                Queue[i].ChosenSampleSlot = 0;
                Queue[i].ChosenSeed = -1;
                Queue[i].HuntBaseSeed = -1;
                Queue[i].HuntSamplePaths = new List<string>();
                Queue[i].HuntSampleSeeds = new List<long>();
                Queue[i].ErosStage = string.Empty;
            }
            if (Queue.Count > before) SaveQueueToFile();
            SyncBoard();
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(HuntSummary));
            OnPropertyChanged(nameof(UpscaleSummary));
            OnPropertyChanged(nameof(LoadSummary));
            OnPropertyChanged(nameof(HasLoadWarning));
            RefreshBoardState();
        }
    }

    /// <summary>One entry in the H3 Eros model dropdown. <c>Value</c> is what goes into the UNETLoader —
    /// the server's own name, folder and extension included; <c>Label</c> is only what is shown.</summary>
    public record DiffusionModelOption(string Value, string Label);
}
