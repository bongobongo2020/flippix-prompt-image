using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Cast" tab — reference-to-video the way the Minimax-h3 reference-to-video thread does it, with the
    /// two preparation steps the thread leaves to the user done in the app:
    ///
    /// <list type="number">
    /// <item><b>The wardrobe.</b> Written from the story (or the scene image) a couple of seconds after the
    /// user stops typing, one outfit per character, and then held still — see <see cref="CastWardrobe"/>. It
    /// is decided <i>first</i> because both of the steps below are generated against it, which is the order
    /// the panel is laid out in.</item>
    /// <item><b>Character sheets.</b> Each character arrives as one ordinary photo. Qwen-Image-Edit-2511
    /// (<c>qwen_image_edit_2511_int8_convrot</c>, 8-step Lightning LoRA) turns it into a three-panel
    /// reference sheet on a plain studio background — full-body front, full-body back, face close-up —
    /// <i>wearing the locked wardrobe</i> rather than whatever the photo showed. That composite is what H3 is
    /// actually handed: the front view fixes silhouette and outfit, the back view explains hair and clothing
    /// through turns, and the close-up carries the facial identity. One image per character, several views
    /// inside it.</item>
    /// <item><b>The prompt.</b> Written from a "scene" image, from a story, or from both. The scene image is
    /// never uploaded — only the llama-server sees it — and supplies setting, lighting and art style; the
    /// story (typed, pasted or loaded from a .txt) supplies what happens. Either one is enough on its own:
    /// with no image the request goes to the llama-server as plain text and the setting is written out of the
    /// prose instead. Both paths produce the same dense multi-shot H3 prompt the 🌀📝 tab writes
    /// (<c>texttovideoH3.md</c>), in which the cast is named only as <c>&lt;Picture 1&gt;</c> /
    /// <c>&lt;Picture 2&gt;</c> — the story's own character names are replaced by those tags — and the
    /// wardrobe is quoted into it as settled fact rather than left for the writer to invent per clip.</item>
    /// <item><b>The video.</b> <c>h3facerefiner.json</c> — the turbo ref2va graph the 🎭👥 tab uses, plus a
    /// second H3 pass that tracks and crops every face, re-generates the crops at a low denoise against the
    /// same conditioning, and stitches them back into the full frames. Faces are the first thing H3 loses at
    /// distance, and the sheets are what the refiner has to work from, so the two halves of this tab are the
    /// same idea applied twice.</item>
    /// </list>
    ///
    /// <para><b>Length.</b> H3 tops out at ~15 seconds, so <see cref="StoryDurationSeconds"/> (5–120 s) is
    /// delivered as a <i>chain</i>: Analyze asks the LLM for <see cref="PlannedClipCount"/> complete H3
    /// prompts in one reply, separated by <c>=== CLIP n of N ===</c> headers, each one a consecutive beat of
    /// the same story. The prompt box holds the whole chain and stays editable; "Add to Queue" splits it on
    /// those headers and enqueues one job per clip — same sheets, same reference line, rendered in order.
    /// Once every clip has landed, <see cref="CompleteStoryAsync"/> FFmpeg-concatenates them into one
    /// continuous video, which becomes the tab's result.</para>
    ///
    /// <para><b>Queued, not blocking.</b> Sheet building is an explicit up-front step (its result is on screen
    /// before any video GPU time is spent); "Add to Queue" then snapshots the sheets, the prompt and the
    /// settings into an <see cref="H3CastQueueItem"/> and the queue drains in the background, one ComfyUI
    /// submission at a time via the workflow coordinator.</para>
    ///
    /// <para><b>Custom nodes.</b> The refine pass needs the H3-FaceRefine node pack
    /// (<c>H3FaceTrackCrop</c>, <c>H3InjectVideoLatent</c>, <c>H3PerFrameDenoise</c>, <c>H3FaceStitch</c>) and
    /// <c>MiniMaxH3NativeAudioLock</c>. If the server does not have them the submit fails with a missing-node
    /// error and the app's node resolver offers to install them; turning <see cref="FaceRefine"/> off prunes
    /// that whole branch out and the tab runs as a plain sheet-driven ref2video.</para>
    /// </summary>
    public partial class H3CastViewModel : VideoProcessingBaseViewModel
    {
        /// <summary>The render graph this tab submits. Virtual so the H3 Duo tab — the same cast
        /// machinery on the MiniMax I2V turbo pipeline — can render through its own copy of that graph.</summary>
        protected virtual string WorkflowFileName => "workflow/video/h3-minimax/h3facerefiner.json";
        private const string SheetWorkflowFileName = "workflow/image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json";
        /// <summary>Where ComfyUI writes this tab's runs, under the output folder. Virtual for
        /// <see cref="H3DuoViewModel"/>, which renders into its own subfolder.</summary>
        protected virtual string OutputSubfolder => "h3_cast";
        /// <summary>The stem of this tab's output files and joined stories — "H3Cast" / "H3Duo".</summary>
        protected virtual string OutputFileStem => "H3Cast";

        /// <summary>Appended to every finished clip and to the joined story, immediately before the
        /// extension. Empty for every tab but 🥽 H3 VR, which has to end its files in <c>_LR_180</c>
        /// because that is where a headset player looks to decide the clip is a stereo pair — see
        /// <see cref="H3VrViewModel"/>. A suffix rather than part of <see cref="OutputFileStem"/>
        /// precisely because it only works as the last thing before ".mp4".</summary>
        protected virtual string OutputFileSuffix => string.Empty;
        private const string SystemPromptFile = "texttovideoH3.md";
        private const string SheetPromptFile = "h3-charsheet-2511.md";

        /// <summary>Appended to <see cref="SystemPromptFile"/> when more than one clip is being written, so
        /// the H3 format itself stays defined in exactly one place.</summary>
        private const string StorySystemPromptFile = "texttovideoH3_story.md";

        // ── Video node ids (locked from h3-minimax/h3facerefiner.json) ────────────────────────
        private const string NodeCharacter1 = "44";     // LoadImage → ref_image_0
        private const string NodeReference = "23";      // MiniMaxH3ReferenceToVideo (base pass)
        private const string NodeRefineReference = "101"; // MiniMaxH3ReferenceToVideo (face-crop canvas)
        private const string NodePrompt = "48";         // PrimitiveStringMultiline → both reference nodes
        private const string NodeResolution = "45";     // ResolutionSelector → canvas + resize 13
        private const string NodeFrames = "5";          // ComfyMathExpression seconds → frames (output slot 1)
        private const string NodeDuration = "56";       // PrimitiveFloat seconds → node 5
        private const string NodeSeed = "46";           // RandomNoise noise_seed (base pass)
        private const string NodeScheduler = "57";      // BasicScheduler (steps — read, never written)
        private const string NodeModelLoader = "36";    // UNETLoader — the raw model, before LoRA/shift/patches
        private const string NodeSigmaShift = "59";     // MiniMaxH3SigmaShift — last patch before the guiders
        private const string NodeSolAttn = "53";        // SolAttnPatch — the predecessor of SLA
        private const string NodeSampler = "12";        // SamplerCustomAdvanced — the base pass (the draft, upscaling)
        private const string NodeBaseAudio = "2";       // VAEDecodeAudio — reads the same latent as node 3

        // ── Attention: one H3SLAAttention per branch, each last on its own MODEL wire ─────────
        private const string NodeSlaBase = "66";        // after the sigma shift, feeding guider 10 + scheduler 57
        /// <summary>The face refine's own patch, after the audio lock. Unlike 🪪👥⚡ H3 Cast Hybrid there is no
        /// free id inside the 100–111 window <see cref="AddSecondRefinePass"/> clones (107 is the refine
        /// pass's KSamplerSelect), so the clone pair is seeded into that map explicitly.</summary>
        private const string NodeSlaRefine = "67";
        private const string NodeSlaRefine2 = "267";    // character 2's copy of it

        // ── Latent upscale: the draft → 2× → finish scheme, nodes 70–80 ───────────────────────
        // Node 23 keeps the cast's panels at the *finished* canvas; node 72 is a bare
        // MiniMaxH3ReferenceToVideo with no references at all, supplying only the draft's empty AV latent.
        private const string NodeDraftWidth = "70";     // ComfyMathExpression — finished width / 2
        private const string NodeDraftHeight = "71";    // ComfyMathExpression — finished height / 2
        private const string NodeDraftLatent = "72";    // MiniMaxH3ReferenceToVideo — empty AV latent, no refs
        private const string NodeDraftSigmas = "74";    // SplitSigmas.high_sigmas — the first 4 of 8 steps
        private const string NodeLatentUpscaler = "77"; // MinimaxH3LatentUpscaler3D
        private const string NodeLatentSwitch = "80";   // ComfySwitchNode — draft latent or finished latent
        private const string NodeBaseFrames = "3";      // VAEDecode — the base pass's finished frames
        private const string NodeFaceTrack = "100";     // H3FaceTrackCrop — tracks and crops one subject
        private const string NodeRefineDenoise = "106"; // BasicScheduler of the refine pass (denoise + steps)
        private const string NodeRefineSeed = "108";    // RandomNoise of the refine pass
        private const string NodeFaceStitch = "111";    // H3FaceStitch — refined crops back into the frames

        // ── Character 2's refine pass — the 100-block cloned into the 200s at submit time ──────
        /// <summary>What nodes 100–111 become in the clone. See <see cref="AddSecondRefinePass"/>.</summary>
        private const int RefinePass2IdOffset = 100;

        /// <summary>
        /// How much bigger the finished canvas is than the sampled draft, per side. Must stay an integer:
        /// the draft is derived as <c>round(finished / factor / 32) * 32</c> and the upscaler multiplies
        /// back by the same factor, and only an integer keeps those two on the same number.
        /// </summary>
        private const double LatentUpscaleFactor = 2.0;

        /// <summary>
        /// What ResolutionSelector rounds the finished canvas to. <b>64 with the latent upscale on</b>: the
        /// draft is the finished canvas halved, and a merely 32-aligned finish does not survive the round
        /// trip — 17 of the 24 aspect × quality combinations land 32px away from where they started.
        /// </summary>
        private const int ResolutionMultiple = 32;
        private const int UpscaledResolutionMultiple = 64;

        /// <summary>
        /// SLA's attention block size, pinned rather than exposed. H3 packs audio at 80 rows per second, so
        /// a 128-row block forces 1.6s of audio through one attention pattern and speech comes back robotic.
        /// </summary>
        protected const string SlaBlockSize = "64";

        private const string NodeFaceTrack2 = "200";       // H3FaceTrackCrop tracking character 2
        private const string NodeRefineReference2 = "201"; // MiniMaxH3ReferenceToVideo — their panels only
        private const string NodeRefineDenoise2 = "206";   // BasicScheduler of the second pass
        private const string NodeRefineSeed2 = "208";      // RandomNoise of the second pass
        private const string NodeFaceStitch2 = "211";      // H3FaceStitch — over character 1's stitched frames
        private const string NodeRtxUpscale = "64";     // RTXVideoSuperResolution (images ← stitch, or base)
        private const string NodeVideoCombine = "65";   // VHS_VideoCombine — the graph's only output

        /// <summary>The autogrow input the reference nodes collect their images from.</summary>
        private const string RefImagePrefix = "ref_images.ref_image_";

        /// <summary>Ids for the injected <c>LoadImage</c> nodes, one per reference beyond the first. Well
        /// clear of every id the export uses (which top out at 111).</summary>
        private const int ReferenceNodeIdBase = 900;

        /// <summary><c>MiniMaxH3ReferenceToVideo</c>'s autogrow cap — nine <c>ref_image_N</c> slots, which is
        /// three panels each for two characters with room to spare.</summary>
        protected const int MaxReferenceImages = 9;

        // ── Tagged references (MiniMaxH3-Contex-Loop ≥ v0.4.0) ─────────────────
        /// <summary>The reference node that resolves <c>@tags</c> instead of fixed <c>ref_image_N</c> slots.</summary>
        private const string TaggedReferenceClass = "MiniMaxH3TaggedReferenceToVideo";

        /// <summary>Registers one image under one <c>@tag</c>; chained through <c>previous</c>.</summary>
        private const string TaggedPictureClass = "MiniMaxH3TaggedPictureReference";

        /// <summary>Ids for the injected <c>MiniMaxH3TaggedPictureReference</c> chain, one per panel. Kept
        /// clear of both the export's ids and the <see cref="ReferenceNodeIdBase"/> loaders.</summary>
        private const int TaggedNodeIdBase = 920;

        /// <summary>The face-refine pass's own prompt primitive, injected whenever that pass runs — it stays on
        /// the core reference node and so needs the prompt in picture numbers rather than aliases, and it is
        /// written for one character where the clip's own prompt describes the whole cast.</summary>
        private const string NodeRefinePrompt = "931";

        /// <summary>Character 2's pass has its own, for the same reasons.</summary>
        private const string NodeRefinePrompt2 = "932";

        // ── Sheet node ids (locked from image/qwen-edit/Qwen_Edit_2511_INT8_Convrot_WF.json) ───
        private const string SheetLoadImage = "78";     // LoadImage (the character photo)
        private const string SheetPositive = "115:111"; // TextEncodeQwenImageEditPlus (the sheet instruction)
        private const string SheetSampler = "115:3";    // KSampler (seed + latent rewire)
        private const string SheetLatent = "115:112";   // EmptySD3LatentImage — the sheet canvas
        private const string SheetSave = "60";          // SaveImage

        /// <summary>
        /// The canvas the sheet is generated on. Three full-height panels side by side need width, and 16:9
        /// is deliberate: it is the aspect the video canvas usually is, so the sheet survives the reference
        /// resize with the least reframing.
        /// </summary>
        private const int SheetWidth = 1536;
        private const int SheetHeight = 864;

        // ── SAM face mask: what stops the composite being a moving rectangle ─────────────────
        /// <summary>The SAM model loader, shared by both characters' mask passes — it only loads a model,
        /// so it is deliberately not in <see cref="AddSecondRefinePass"/>'s clone map.</summary>
        private const string NodeSamLoader = "90";
        /// <summary><c>H3FaceMaskSAM</c>. Its id is outside the 100–111 clone window, so its clone pair is
        /// seeded into that map by hand — character 2's mask has to come from character 2's own crops.</summary>
        private const string NodeFaceMask = "91";
        private const string NodeFaceMask2 = "291";

        /// <summary>
        /// Feather on the paste mask, in source pixels. The two values are not a preference: a rectangle
        /// needs a wide blend to hide its edge, and a mask that already follows the jaw and hairline needs
        /// a narrow one or it eats into the face. The node's own guidance is 4–8 with a SAM mask.
        /// </summary>
        private const int FeatherWithMask = 6;
        private const int FeatherWithBox = 24;

        /// <summary>H3FaceTrackCrop.canvas_mode: clamp the refine canvas to H3's native 768 short edge, or
        /// let it follow the largest crop in the clip. See <see cref="RefineNoDownscale"/>.</summary>
        private const string CanvasModeCapped = "auto_capped_768";
        private const string CanvasModeNoDownscale = "auto_no_downscale";

        /// <summary>H3 renders at 24 fps and the duration maths below is built on it; written on every submit
        /// so an export at another rate cannot desync what the tab reports from what lands on disk.</summary>
        protected const int OutputFrameRate = 24;

        /// <summary>RTX Video Super Resolution factor — node 64's <c>scale</c>, mirrored here so the tab can
        /// say what size the file will be before it renders.</summary>
        private const double RtxScale = 2.0;

        // ── Character state ────────────────────────────────────────────────────
        private readonly CharacterSlot _character1;
        private readonly CharacterSlot _character2;

        // ── Scene / prompt state ───────────────────────────────────────────────
        private string _sceneImagePath = string.Empty;
        private BitmapImage? _sceneImagePreview;
        private string _sceneImageInfo = string.Empty;
        private string _prompt = string.Empty;
        private int _promptClipCount;
        private string _castWardrobe = string.Empty;
        private bool _isWardrobeLocked = true;
        /// <summary>Set once the user unlocks the box: their outfits are never overwritten by the watcher.</summary>
        private bool _wardrobeIsManual;
        /// <summary>The story/scene the wardrobe in the box was written from — so identical material does not
        /// re-ask the llama-server, and changed material re-dresses the whole cast.</summary>
        private string _wardrobeStoryStamp = string.Empty;
        /// <summary>The sexes it was written for, in cast order: a switch there re-dresses only that
        /// character.</summary>
        private string _wardrobeCastStamp = string.Empty;
        private CancellationTokenSource? _wardrobeCts;
        private bool _isDerivingWardrobe;
        private string _storyText = string.Empty;
        private string _storyFileName = string.Empty;
        private double _storyDurationSeconds = 15;
        private H3VisualStyle _visualStyle = H3VisualStyles.Auto;
        private string _selectedAspectRatio = H3Canvas.AutoAspect;
        private double _megapixels = 1.0;
        private double _lengthSeconds = 10;
        private long _seed = -1;
        private bool _isAnalyzing;
        private bool _isWritingPrompt;
        private bool _faceRefine = true;
        private double _refineDenoise = 0.45;
        private double _refineBlend = 1.0;
        private bool _refineNoDownscale;
        private bool _useSamFaceMask = true;
        // Off by default: it is the graph's largest allocation and the observed cause of ComfyUI dying
        // mid-render on a long clip. Opt in for short ones; otherwise upscale the finished file afterwards.
        private bool _rtxUpscale;
        private bool _useLatentUpscale = true;
        private bool _useSla = true;
        // 0.85, not the faster 0.90: lightx2v's shipped value, and what the turbo LoRA was distilled
        // against. 0.90 measured hotter and flatter in the speech band on this pipeline.
        private double _slaSparsity = 0.85;
        private bool _useSparseAttention;
        private bool _maxFidelityReferences;
        private bool _useAudioEnhancement = true;
        private bool _isBuildingSheets;
        private string _sheetPhase = string.Empty;

        protected readonly IFileDialogService _fileDialogService;
        // Protected so derived tabs can issue their own llama-server calls (the H3 Experimental
        // fork's MCP tool loop) against the same configured server.
        protected readonly LMStudioService _lmStudioService;
        // Protected so derived tabs (the H3 Experimental fork) can run their own analyze loop with the
        // same cancel plumbing.
        protected CancellationTokenSource? _analyzeCts;
        private CancellationTokenSource? _sheetCts;

        /// <summary>path → ComfyUI input-folder filename; each file is uploaded once per session.</summary>
        private readonly Dictionary<string, string> _uploadCache = new(StringComparer.OrdinalIgnoreCase);

        // ── Queue ──────────────────────────────────────────────────────────────
        private readonly ObservableCollection<H3CastQueueItem> _queue = new();
        /// <summary>The drain loop's own cancellation. Protected so a fork that drives the queue in more
        /// than one pass (🌹 H3 Eros hunts every clip, then finishes the picked ones) can own it too.</summary>
        protected CancellationTokenSource? _queueCts;
        private bool _isProcessingQueue;
        private string _queueStatus = string.Empty;

        /// <summary>This tab's persisted queue. Virtual so the H3 Duo tab keeps its own queue file.</summary>
        protected virtual string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3cast_queue.json");

        public H3CastViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, logger, settingsService, serviceProvider, workflowCoordinator)
        {
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _fileDialogService = fileDialogService ?? throw new ArgumentNullException(nameof(fileDialogService));

            _character1 = new CharacterSlot(1, LoadImagePreview, OnCharacterChanged);
            _character2 = new CharacterSlot(2, LoadImagePreview, OnCharacterChanged);

            SelectCharacter1Command = new RelayCommand(async () => await PickCharacterAsync(_character1));
            SelectCharacter2Command = new RelayCommand(async () => await PickCharacterAsync(_character2));
            ClearCharacter2Command = new RelayCommand(() => _character2.Clear());
            GenerateCastZimageLoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "zimage", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastFamegridLoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "famegrid", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastKrea2LoraCommand = new RelayCommand<CastPhotoWorkflows.CastLora>(
                async lora => await GenerateCastPhotoAsync(CastPhotoMenuSlot, "krea2", lora),
                _ => CanGenerateCastPhoto(CastPhotoMenuSlot));
            GenerateCastQwenCommand = new RelayCommand<CharacterSlot>(
                async slot => await GenerateCastPhotoAsync(slot, "qwen"), slot => CanGenerateCastPhoto(slot));
            GenerateCastKrea2SpicyCommand = new RelayCommand<CharacterSlot>(
                async slot => await GenerateCastPhotoAsync(slot, "krea2spicy"), slot => CanGenerateCastPhoto(slot));
            SelectSceneImageCommand = new RelayCommand(async () => await SelectSceneImageAsync());
            ClearSceneImageCommand = new RelayCommand(() => SceneImagePath = string.Empty);
            LoadStoryCommand = new RelayCommand(async () => await LoadStoryFileAsync());
            ClearStoryCommand = new RelayCommand(() => StoryText = string.Empty, () => HasStoryText);
            DeriveWardrobeCommand = new RelayCommand(async () => await RederiveWardrobeAsync(), () => CanAnalyze);
            ClearWardrobeCommand = new RelayCommand(ClearWardrobe, () => HasCastWardrobe);
            ClearDerivedCastCommand = new RelayCommand(ClearDerivedCast, () => HasDerivedCast);
            ToggleWardrobeLockCommand = new RelayCommand(() => IsWardrobeLocked = !IsWardrobeLocked);
            BuildSheetsCommand = new RelayCommand(async () => await BuildSheetsAsync(), () => CanBuildSheets);
            FeelLuckyCommand = new RelayCommand(async () => await FeelLuckyAsync(), () => CanFeelLucky);
            AnalyzeCommand = new RelayCommand(async () => await AnalyzeAsync(), () => CanAnalyze);
            GenerateCommand = new RelayCommand(AddToQueue, () => CanGenerate);
            CancelCommand = new RelayCommand(CancelEverything, () => IsProcessingQueue || IsProcessing || IsBuildingSheets);
            RemoveQueueItemCommand = new RelayCommand<H3CastQueueItem>(RemoveQueueItem);
            ClearQueueCommand = new RelayCommand(ClearQueue, () => HasQueueItems);
            StartQueueCommand = new RelayCommand(() => _ = ProcessQueueAsync(), () => HasPendingItems && !IsProcessingQueue);
            StopQueueCommand = new RelayCommand(StopQueue, () => IsProcessingQueue);
            ReprocessAllFailedCommand = new RelayCommand(ReprocessAllFailed, () => HasFailedItems);
            PlayVideoCommand = new RelayCommand(PlayVideo, () => HasResult);
            OpenResultFolderCommand = new RelayCommand(OpenResultFolder, () => HasResult);
            RandomSeedCommand = new RelayCommand(() => Seed = System.Random.Shared.NextInt64(0, long.MaxValue));

            _queue.CollectionChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(HasQueueItems));
                UpdateQueueStatus();
            };

            AddLog("H3 Cast initialized");
            ScheduleQueueLoad();
        }

        #region Commands

        public ICommand SelectCharacter1Command { get; }
        public ICommand SelectCharacter2Command { get; }
        public ICommand ClearCharacter2Command { get; }
        /// <summary>Renders this card's character photo with the Image Generator's Z-Image base
        /// workflow and the LoRA picked in the ✨ menu (or the workflow's own when "as authored" is
        /// picked). Runs for the card whose menu was last opened — see
        /// <see cref="CastPhotoMenuSlot"/>. See H3CastViewModel.Cast.cs.</summary>
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastZimageLoraCommand { get; }
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastFamegridLoraCommand { get; }
        public RelayCommand<CastPhotoWorkflows.CastLora> GenerateCastKrea2LoraCommand { get; }
        public RelayCommand<CharacterSlot> GenerateCastQwenCommand { get; }
        /// <summary>The ✨ menu's Krea2-Spicy entry — the famegrid spicy selfie workflow, its
        /// LoRAs baked in, no picking. Takes the card directly, like Qwen.</summary>
        public RelayCommand<CharacterSlot> GenerateCastKrea2SpicyCommand { get; }
        public ICommand SelectSceneImageCommand { get; }
        public RelayCommand ClearSceneImageCommand { get; }
        /// <summary>Reads a .txt/.md story off disk into <see cref="StoryText"/>.</summary>
        public RelayCommand LoadStoryCommand { get; }
        public RelayCommand ClearStoryCommand { get; }
        /// <summary>Re-asks the llama-server for the cast's outfits, replacing whatever is in the box.</summary>
        public RelayCommand DeriveWardrobeCommand { get; }
        public RelayCommand ClearWardrobeCommand { get; }
        /// <summary>🧹 — throws away everything the story wrote about the cast (both Parts, the sexes no
        /// photo has answered for, and the wardrobe with them) so a newly pasted story writes them from
        /// scratch. See <see cref="ClearDerivedCast"/> in H3CastViewModel.Cast.cs.</summary>
        public RelayCommand ClearDerivedCastCommand { get; }
        /// <summary>🔒/🔓 — hands the wardrobe box between the story watcher and the user.</summary>
        public RelayCommand ToggleWardrobeLockCommand { get; }
        public RelayCommand BuildSheetsCommand { get; }

        /// <summary>The whole chain from a pasted story, unattended. See <see cref="FeelLuckyAsync"/>.</summary>
        public RelayCommand FeelLuckyCommand { get; }
        public RelayCommand AnalyzeCommand { get; }
        /// <summary>Named for the button it drives; it enqueues rather than running inline.</summary>
        public RelayCommand GenerateCommand { get; }
        public RelayCommand CancelCommand { get; }
        public RelayCommand<H3CastQueueItem> RemoveQueueItemCommand { get; }
        public RelayCommand ClearQueueCommand { get; }
        public RelayCommand StartQueueCommand { get; }
        public RelayCommand StopQueueCommand { get; }
        public RelayCommand ReprocessAllFailedCommand { get; }
        public RelayCommand PlayVideoCommand { get; }
        public RelayCommand OpenResultFolderCommand { get; }
        public RelayCommand RandomSeedCommand { get; }

        #endregion

        #region Characters

        /// <summary>Character 1 — its sheet is <c>ref_image_0</c> / <c>&lt;Picture 1&gt;</c>. Required.</summary>
        public CharacterSlot Character1 => _character1;

        /// <summary>Character 2 — optional. Without it the graph runs with a single reference sheet.</summary>
        public CharacterSlot Character2 => _character2;

        public bool HasCharacter1 => _character1.HasSource;
        public bool HasCharacter2 => _character2.HasSource;

        /// <summary>Both slots that have a photo loaded, in cast order.</summary>
        private IEnumerable<CharacterSlot> LoadedCharacters =>
            new[] { _character1, _character2 }.Where(c => c.HasSource);

        /// <summary>True once every loaded character has a sheet to send — what Generate waits for.</summary>
        public bool AllSheetsReady => HasCharacter1 && LoadedCharacters.All(c => c.HasSheet);

        /// <summary>
        /// How many <c>ref_image_N</c> slots character 1 occupies — one per panel its sheet was cut into.
        /// Falls back to 1 before a sheet exists, so a prompt can be written while the sheets are still
        /// building and still be renumbered correctly when the job is queued.
        /// </summary>
        protected int Panels1 => Math.Max(1, _character1.PanelCount);

        /// <summary>The same for character 2, or 0 when the run has a single character.</summary>
        protected int Panels2 => HasCharacter2 ? Math.Max(1, _character2.PanelCount) : 0;

        /// <summary>
        /// What the cast costs in reference slots and how the picture tags map onto them — the tab's answer to
        /// "why does the prompt say &lt;Picture 4&gt;".
        /// </summary>
        public string PanelPlanSummary
        {
            get
            {
                if (!AllSheetsReady) return string.Empty;
                var total = Panels1 + Panels2;
                if (total <= (Panels2 > 0 ? 2 : 1))
                    return "Sheets go to H3 whole, one reference each — if the panel layout shows up in the " +
                           "video, that is why; set the split to Auto or 3.";

                var second = Panels2 > 0
                    ? $", Character 2 is <Picture {Panels1 + 1}>–<Picture {total}>"
                    : string.Empty;
                return $"{total} reference images: Character 1 is <Picture 1>–<Picture {Panels1}>{second}. " +
                       "H3 never sees the panels side by side, so it has no grid to copy into the frames.";
            }
        }

        public string CastSummary
        {
            get
            {
                if (!HasCharacter1) return "Load a photo of character 1 to start.";
                var missing = LoadedCharacters.Count(c => !c.HasSheet);
                var cast = HasCharacter2 ? "Two characters" : "One character";
                if (missing == 0)
                    return $"{cast} — sheets ready; H3 sees the sheets, not the photos.";

                var queued = IsProcessing || IsProcessingQueue
                    ? " A render is in flight, so the build waits for the GPU and starts when that job finishes."
                    : string.Empty;
                return $"{cast} — {missing} sheet(s) still to build. Build Sheets runs Qwen-Image-Edit-2511 " +
                       $"once per character.{queued}";
            }
        }

        private void OnCharacterChanged()
        {
            OnPropertyChanged(nameof(HasCharacter1));
            OnPropertyChanged(nameof(HasCharacter2));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            OnPropertyChanged(nameof(PanelPlanSummary));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(SheetsShowWardrobe));
            OnPropertyChanged(nameof(WardrobeSummary));
            OnCanExecuteChanged();
            // Who is in the cast, and their sex, are half of what the outfits are written from — casting a
            // second character or switching one to a woman re-opens the wardrobe question.
            ScheduleWardrobeDerive();
        }

        private async Task PickCharacterAsync(CharacterSlot slot)
        {
            var path = await PickImageAsync($"Select Character {slot.Index}", $"h3cast.char{slot.Index}");
            if (path == null) return;
            slot.SourcePath = path;
            AddLog($"Character {slot.Index}: {Path.GetFileName(path)}");
            await AdoptSheetFromLibraryAsync(slot);
        }

        private async Task SelectSceneImageAsync()
        {
            var path = await PickImageAsync("Select Scene Image", "h3cast.scene");
            if (path == null) return;
            SceneImagePath = path;
            AddLog($"Scene image: {Path.GetFileName(path)}");
        }

        private async Task<string?> PickImageAsync(string title, string persistKey)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            return await _fileDialogService.OpenFileDialogAsync(
                title,
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: persistKey);
        }

        private BitmapImage? LoadImagePreview(string path, out string info)
        {
            info = string.Empty;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
                var fi = new FileInfo(path);
                info = $"{bitmap.PixelWidth}×{bitmap.PixelHeight} • {fi.Length / 1024}KB";
                return bitmap;
            }
            catch (Exception ex)
            {
                AddLog($"Error loading image preview: {ex.Message}");
                info = "Error loading image";
                return null;
            }
        }

        #endregion

        #region Character sheets (Qwen-Image-Edit-2511 ConvRot)

        /// <summary>
        /// Deliberately <i>not</i> gated on a render being in flight. The sheet builder needs the GPU, but so
        /// does everything else in the app, and the workflow coordinator already serializes that: a build
        /// started mid-render simply waits for the lease and runs when the render lets go.
        ///
        /// <para>Gating it on <c>IsProcessing</c> instead — which is what it did at first — produced a dead
        /// end. Loading a new photo invalidates that character's sheet, and Add to Queue needs every loaded
        /// character to have one; with the builder disabled for the duration of a 20-minute render there was
        /// no way to prepare the next job while the current one ran, which is the whole point of the
        /// queue.</para>
        /// </summary>
        public bool CanBuildSheets => HasCharacter1 && !IsBuildingSheets &&
                                      LoadedCharacters.Any(c => !c.UseSourceAsSheet);

        // ── The sheet library ───────────────────────────────────────────────────────────────────────

        private CastSheetLibrary? _sheetLibrary;
        private Task? _sheetLibrarySync;
        private bool _reuseSheets = true;

        /// <summary>
        /// Load a photograph this app has already built a sheet from, and take that sheet back instead of
        /// paying for it again. Off turns the library into a write-only archive: sheets are still filed as
        /// they are built, nothing is ever adopted.
        ///
        /// <para>On by default because building the same sheet twice buys nothing — Qwen is sampled with a
        /// fresh seed each time, so a rebuild is a <i>different</i> sheet of the same person, and a chain
        /// already queued against the old one is now referencing a face that no longer exists on disk.</para>
        /// </summary>
        public bool ReuseSheetsFromLibrary
        {
            get => _reuseSheets;
            set
            {
                if (_reuseSheets == value) return;
                _reuseSheets = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SheetLibrarySummary));
            }
        }

        public string SheetLibrarySummary =>
            ReuseSheetsFromLibrary
                ? "Loading a photo this app has already built a sheet from brings that sheet straight back — " +
                  "no render, no GPU. New sheets are filed as they are built."
                : "Sheets are filed but never reused — every photo is sent to Qwen again, even one whose " +
                  "sheet is already on disk.";

        /// <summary>The library, built on first use rather than in the constructor — it touches disk, and
        /// this view model is constructed on the window's startup path. See [[project_ui_thread_io]].</summary>
        protected CastSheetLibrary SheetLibrary
        {
            get
            {
                if (_sheetLibrary != null) return _sheetLibrary;
                var folder = _settingsService.Settings?.CastSheetLibraryFolder;
                _sheetLibrary = new CastSheetLibrary(folder, AddLog);
                // The one expensive pass — reading the build log back — runs once, in the background, and
                // nothing waits on it. A photo loaded before it finishes simply misses; the next one hits.
                _sheetLibrarySync = _sheetLibrary.SyncAsync();
                return _sheetLibrary;
            }
        }

        /// <summary>
        /// Called the moment a card is given a photograph. If a sheet has already been built from that
        /// exact picture, it is put back on the card as though the build had just finished.
        ///
        /// <para>Fire-and-forget on purpose: the caller is a click handler, the lookup hashes a file, and a
        /// card that has no sheet yet is the state the user is already looking at. Failure is silent beyond
        /// the log — the ordinary 🪪 Build Sheets path is untouched and still there.</para>
        /// </summary>
        protected async Task AdoptSheetFromLibraryAsync(CharacterSlot slot)
        {
            if (!ReuseSheetsFromLibrary || slot.UseSourceAsSheet || slot.HasSheet) return;
            var source = slot.SourcePath;
            if (string.IsNullOrEmpty(source)) return;

            try
            {
                var library = SheetLibrary;
                if (_sheetLibrarySync != null) await _sheetLibrarySync.ConfigureAwait(false);

                var match = await library.FindAsync(source).ConfigureAwait(false);
                if (match == null) return;

                // The card may have moved on while the disk was being read — a second photo picked, or the
                // slot cleared. Adopting then would put a stranger's sheet on someone else's card.
                var stillThere = false;
                Application.Current.Dispatcher.Invoke(() =>
                    stillThere = string.Equals(slot.SourcePath, source, StringComparison.OrdinalIgnoreCase)
                                 && !slot.HasSheet && !slot.UseSourceAsSheet);
                if (!stillThere) return;

                Application.Current.Dispatcher.Invoke(() =>
                {
                    slot.SetSheet(match.SheetPath, match.Entry.Wardrobe);
                    OnCharacterChanged();
                });

                var how = match.Confidence switch
                {
                    CastSheetLibrary.MatchKind.SourcePath => "the same file",
                    CastSheetLibrary.MatchKind.SourceHash => "the same picture under a different name",
                    _ => "the same file name — from the build log, so check the face is right"
                };
                AddLog($"Character {slot.Index}: a sheet for this photo already existed and has been " +
                       $"loaded ({Path.GetFileName(match.SheetPath)}, matched on {how}). " +
                       (string.IsNullOrWhiteSpace(match.Entry.Wardrobe)
                            ? "No wardrobe was recorded with it."
                            : $"It was built wearing: {match.Entry.Wardrobe}") +
                       " Press 🪪 Build Sheets to replace it with a new one.");
            }
            catch (Exception ex)
            {
                AddLog($"Character {slot.Index}: the sheet library could not be checked ({ex.Message}) — " +
                       "build the sheet as usual.");
            }
        }

        /// <summary>True while the sheet builder is waiting for the GPU or generating. Its own flag, kept
        /// apart from <see cref="VideoProcessingBaseViewModel.IsProcessing"/> so the two can overlap without
        /// fighting over the progress bar a render owns.</summary>
        public bool IsBuildingSheets
        {
            get => _isBuildingSheets;
            private set
            {
                if (_isBuildingSheets == value) return;
                _isBuildingSheets = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        /// <summary>What the sheet builder is doing right now, shown beside its button — it cannot use the
        /// tab's status line, which belongs to whatever is rendering.</summary>
        public string SheetPhase
        {
            get => _sheetPhase;
            private set { if (_sheetPhase != value) { _sheetPhase = value; OnPropertyChanged(); } }
        }

        public string BuildSheetsButtonText
        {
            get
            {
                var n = LoadedCharacters.Count(c => !c.UseSourceAsSheet);
                return n > 1 ? $"🪪 Build {n} Character Sheets" : "🪪 Build Character Sheet";
            }
        }

        /// <summary>
        /// Runs Qwen-Image-Edit-2511 once per loaded character, turning each photo into the three-panel
        /// reference sheet H3 is handed. A character marked <see cref="CharacterSlot.UseSourceAsSheet"/>
        /// is skipped — its image already <i>is</i> a sheet.
        ///
        /// <para>The export's KSampler samples the VAE-encoded source photo, which would pin the sheet to
        /// that photo's framing and aspect; <see cref="UseSheetCanvas"/> repoints it at the graph's own
        /// (otherwise disconnected) empty latent so the three panels get a wide canvas of their own. The
        /// photo still reaches the model — <c>TextEncodeQwenImageEditPlus</c> carries it as the edit
        /// reference, which is what keeps the face on model.</para>
        /// </summary>
        /// <param name="onlyMissing">Build only the characters that have no sheet, or whose sheet was
        /// photographed in a different wardrobe. False - what the sheet button passes - rebuilds the lot,
        /// which is what a button called "Build Sheets" has to mean. I'm Feeling Lucky passes true, so a
        /// sheet the library just handed back for free is not immediately paid for again.</param>
        private async Task BuildSheetsAsync(bool onlyMissing = false)
        {
            if (!CanBuildSheets) return;

            IsBuildingSheets = true;
            SheetPhase = "Preparing…";

            _sheetCts?.Dispose();
            _sheetCts = new CancellationTokenSource();
            var token = _sheetCts.Token;

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var todo = LoadedCharacters
                    .Where(c => !c.UseSourceAsSheet)
                    .Where(c => !onlyMissing || !c.HasSheet || !c.SheetMatchesWardrobe)
                    .ToList();
                if (todo.Count == 0)
                {
                    AddLog("Sheets: every character already has one in the locked wardrobe - nothing to build.");
                    SheetPhase = "Sheets ready.";
                    return;
                }
                AddLog($"=== H3 Cast: building {todo.Count} character sheet(s) with Qwen-Image-Edit-2511 ===");

                // Settled before the GPU is even queued for — the sheet is the reference H3 dresses the video
                // from, so it has to be photographed in the wardrobe the prompts will quote rather than in
                // whatever the source photo happened to be wearing. It is a llama-server round trip, so it is
                // done outside the workflow lease: nothing else should be waiting on the GPU for this.
                SheetPhase = "Deciding the wardrobe…";
                if (!await EnsureWardrobeAsync(token) && (HasStoryText || HasSceneImage))
                    AddLog("WARNING: no wardrobe could be derived, so the sheets keep the clothes in the source " +
                           "photos and each clip will describe an outfit of its own.");

                // Only ever a wait, never a refusal: a render in flight holds the lease until it finishes.
                SheetPhase = IsProcessing || IsProcessingQueue
                    ? "Waiting for the current render to finish…"
                    : "Waiting for the GPU…";
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Cast", token);

                SheetPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    SheetPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                var instruction = (await LoadFileAsync(Path.Combine("prompts", "prompt2json", SheetPromptFile), token)).Trim();

                for (var i = 0; i < todo.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var progress = todo.Count > 1 ? $" ({i + 1}/{todo.Count})" : string.Empty;
                    await BuildOneSheetAsync(todo[i], instruction, progress, token);
                }

                SheetPhase = AllSheetsReady ? "Sheets ready." : "Sheets built.";
            }
            catch (OperationCanceledException)
            {
                AddLog("Sheet building cancelled");
                SheetPhase = "Cancelled";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (character sheets): {ex.Message}");
                SheetPhase = $"Error: {ex.Message}";
                MessageBox.Show($"Building the character sheets failed:\n{ex.Message}",
                    "H3 Cast", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                IsBuildingSheets = false;
                _sheetCts?.Dispose();
                _sheetCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Builds one character's sheet: upload the photo → Qwen-Image-Edit-2511 → retrieve →
        /// <see cref="CharacterSlot.SetSheet"/>. The loop body of <see cref="BuildSheetsAsync"/> in its
        /// own method, so the ✨ Generate button can run it for the character it just photographed —
        /// inside the workflow lease its caller already holds, with the wardrobe already settled.
        /// </summary>
        private async Task BuildOneSheetAsync(CharacterSlot slot, string instruction, string progress, CancellationToken token)
        {
            var outfit = CastPromptStamp.OutfitFor(CastWardrobe, slot.Index);

            SheetPhase = $"Uploading character {slot.Index}…{progress}";
            var uploaded = await EnsureUploadedAsync(slot.SourcePath);

            var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
            var runToken = $"sheet_{slot.Index}_{ts}";

            var json = await LoadFileAsync(SheetWorkflowFileName, token);
            json = UseSheetCanvas(json);
            SetInput(ref json, SheetLoadImage, "image", uploaded);
            SetInput(ref json, SheetPositive, "prompt", BuildSheetInstruction(instruction, slot, outfit));
            SetInput(ref json, SheetSampler, "seed", System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L));
            SetInput(ref json, SheetLatent, "width", SheetWidth);
            SetInput(ref json, SheetLatent, "height", SheetHeight);
            SetInput(ref json, SheetSave, "filename_prefix", $"{OutputSubfolder}/{runToken}");

            SheetPhase = $"Generating character {slot.Index}'s sheet…{progress}";
            AddLog($"Character {slot.Index} ({slot.Noun}): generating a {SheetWidth}×{SheetHeight} sheet " +
                   $"from {Path.GetFileName(slot.SourcePath)}...");
            if (outfit.Length > 0)
                AddLog($"Character {slot.Index} is being dressed in the locked wardrobe: {outfit}");
            var promptId = await SubmitSheetAsync(json, token);

            string? local = null;
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(SheetSave, out var outs) && outs.Count > 0)
                local = await ResolveImageToLocalAsync(outs[0]);
            local ??= FindTokenImageOnDisk(runToken);
            if (local == null || !File.Exists(local))
                throw new Exception($"Character {slot.Index}'s sheet was not produced.");

            // Uploaded now so the video graph's LoadImage can read it as a ComfyUI input later.
            await EnsureUploadedAsync(local);
            var applied = local;
            var wornInSheet = outfit;
            Application.Current.Dispatcher.Invoke(() => slot.SetSheet(applied, wornInSheet));
            AddLog($"Character {slot.Index}: sheet ready — {Path.GetFileName(local)}");

            // Filed after the card has it, never before: the sheet is this run's whatever happens next, and
            // a library write that failed must not read as a build that failed.
            await SheetLibrary.RecordAsync(slot.SourcePath, applied, slot.Index, slot.Noun, wornInSheet, token);
        }

        /// <summary>
        /// The sheet instruction for one character: the shipped three-panel brief, plus who they are and —
        /// when a wardrobe is locked — the outfit the sheet must show them in.
        ///
        /// <para>This is where wardrobe consistency is actually won. Everything downstream is a description
        /// competing with a picture, and the picture wins: while the sheets showed the source photo's clothes,
        /// every clip's prompt was asking H3 to ignore what its references plainly show and dress the cast from
        /// prose instead — which it does differently in each clip, because each clip is generated on its own.
        /// Photographing the cast in the locked outfit removes the disagreement rather than arbitrating it, and
        /// the reference line then tells H3 to copy the clothing rather than discard it (see
        /// <see cref="CastPromptStamp.CastInfo.SheetsShowWardrobe"/>).</para>
        /// </summary>
        private static string BuildSheetInstruction(string baseInstruction, CharacterSlot slot, string outfit)
        {
            var sb = new StringBuilder(baseInstruction);
            sb.Append($" The person is a {slot.Noun}.");
            if (outfit.Length == 0) return sb.ToString();

            sb.Append(" Dress them in exactly this outfit, replacing whatever clothing the input image shows: ")
              .Append(outfit.TrimEnd('.', ' '))
              .Append(". Every one of the three panels must show that same outfit, complete and clearly visible, " +
                      "with the same garments, colours and materials — the front view from the front, the back " +
                      "view from behind, and whatever of it reaches the shoulders in the close-up. Change only " +
                      "the clothing: the face, hair, skin tone, build and age stay exactly as they are in the " +
                      "input image.");
            return sb.ToString();
        }

        /// <summary>
        /// Points the sheet workflow's sampler at its empty latent instead of the VAE-encoded source photo,
        /// so the sheet is composed on a canvas of our choosing rather than inheriting the photo's framing.
        /// Idempotent: a workflow already wired that way is returned unchanged.
        /// </summary>
        private static string UseSheetCanvas(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Sheet workflow JSON could not be parsed.");

            RequireClass(root, SheetSampler, "KSampler");
            RequireClass(root, SheetLatent, "EmptySD3LatentImage");
            RequireClass(root, SheetPositive, "TextEncodeQwenImageEditPlus");
            RequireClass(root, SheetLoadImage, "LoadImage");
            RequireClass(root, SheetSave, "SaveImage");

            json = root.ToJsonString();
            SetInput(ref json, SheetSampler, "latent_image", new JsonArray(SheetLatent, 0));
            return json;
        }

        private async Task<string?> ResolveImageToLocalAsync(string imageFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var srcPath = Path.Combine(outputFolder, imageFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(srcPath))
                        {
                            await WaitForFileStableAsync(srcPath);
                            return srcPath;
                        }
                    }
                }

                var parts = imageFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadViewFileAsync(filename, subfolder, "output");
                if (bytes is { Length: > 0 })
                {
                    var tmp = Path.Combine(Path.GetTempPath(), $"h3cast_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tmp, bytes);
                    return tmp;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve sheet image failed: {ex.Message}");
            }
            return null;
        }

        private string? FindTokenImageOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.png")
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>Uploads a file to ComfyUI once, caching the returned input-folder name by path.</summary>
        protected async Task<string> EnsureUploadedAsync(string path)
        {
            if (_uploadCache.TryGetValue(path, out var cached) && !string.IsNullOrEmpty(cached))
                return cached;
            if (!File.Exists(path))
                throw new FileNotFoundException($"Image is gone: {path}");

            var name = await _comfyUIService.UploadImageAsync(path);
            if (string.IsNullOrEmpty(name)) throw new Exception($"Failed to upload {Path.GetFileName(path)}.");
            _uploadCache[path] = name;
            AddLog($"Uploaded: {name}");
            return name;
        }

        /// <summary>Reads a file shipped next to the exe (workflow JSON or prompt), relative to BaseDirectory.</summary>
        protected static async Task<string> LoadFileAsync(string relativePath, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, relativePath);
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        #endregion

        #region Scene, prompt and settings

        /// <summary>
        /// Scene image — never uploaded to ComfyUI. It is the only image Analyze looks at, and the prompt it
        /// produces is what the referenced cast ends up acting out.
        /// </summary>
        public string SceneImagePath
        {
            get => _sceneImagePath;
            set
            {
                if (_sceneImagePath == value) return;
                _sceneImagePath = value;
                _sceneImagePreview = LoadImagePreview(value, out _sceneImageInfo);
                OnPropertyChanged();
                OnPropertyChanged(nameof(SceneImagePreview));
                OnPropertyChanged(nameof(SceneImageInfo));
                OnPropertyChanged(nameof(HasSceneImage));
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                OnPropertyChanged(nameof(StorySourceSummary));
                OnCanExecuteChanged();
                // A scene image outranks the story as a wardrobe source, so swapping it re-dresses the cast.
                ScheduleWardrobeDerive();
            }
        }

        public BitmapImage? SceneImagePreview => _sceneImagePreview;
        public string SceneImageInfo => _sceneImageInfo;
        public bool HasSceneImage => !string.IsNullOrEmpty(SceneImagePath) && File.Exists(SceneImagePath);

        /// <summary>
        /// The full H3 prompt: reference line + the model's own fields. Past one clip's worth of duration it
        /// holds the <i>whole chain</i> — one such prompt per clip, separated by <c>=== CLIP n of N ===</c>
        /// headers — and stays editable, because it is what "Add to Queue" splits.
        /// </summary>
        public string Prompt
        {
            get => _prompt;
            set
            {
                if (_prompt == value) return;
                _prompt = value;
                // Cached: the box updates on every keystroke and a chain is tens of kilobytes.
                _promptClipCount = SplitClips(_prompt).Count;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PromptClipCount));
                OnPropertyChanged(nameof(HasPromptSequence));
                OnPropertyChanged(nameof(PromptClipSummary));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// The cast's outfits, decided once and stamped into every clip verbatim.
        ///
        /// <para>This exists because a chain's clips are written as N separate blocks by a model that never
        /// sees its own earlier output, so a wardrobe left to the prose gets re-invented every few clips and
        /// the characters change clothes mid-story. Deciding it once and repeating it verbatim is the only
        /// thing that survives that.</para>
        ///
        /// <para>It is written from the story (or the scene image) automatically — see
        /// <see cref="ScheduleWardrobeDerive"/> — and then used in two places at once, which is what makes it
        /// hold: the character sheets are <i>generated</i> with the cast wearing it, and every clip's prompt
        /// carries it as a code-built <see cref="CastPromptStamp.WardrobeLockHeader"/> block that outranks
        /// anything the model puts in the body. Picture and text then agree; while they disagreed, H3 settled
        /// the argument itself, differently in each clip. Editing this box (after unlocking it) and pressing
        /// Add to Queue re-stamps every clip — but the sheets have to be rebuilt to match.</para>
        /// </summary>
        public string CastWardrobe
        {
            get => _castWardrobe;
            set
            {
                if (_castWardrobe == value) return;
                _castWardrobe = value;
                PushWardrobeToCast();
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCastWardrobe));
                OnPropertyChanged(nameof(WardrobeSummary));
                OnPropertyChanged(nameof(SheetsShowWardrobe));
                OnPropertyChanged(nameof(CastSummary));
                ClearWardrobeCommand.NotifyCanExecuteChanged();
                ClearDerivedCastCommand.NotifyCanExecuteChanged();
            }
        }

        public bool HasCastWardrobe => !string.IsNullOrWhiteSpace(CastWardrobe);

        /// <summary>
        /// The box is read-only until it is deliberately unlocked. The wardrobe is meant to be one decision
        /// taken from the story and then held still — everything downstream (the sheets, every clip's prompt)
        /// is generated against it, so an accidental keystroke here is a costume change halfway through a
        /// render queue. Unlocking also stops the story watcher rewriting what was typed by hand.
        /// </summary>
        public bool IsWardrobeLocked
        {
            get => _isWardrobeLocked;
            set
            {
                if (_isWardrobeLocked == value) return;
                _isWardrobeLocked = value;
                // Unlocking is the user taking over: from here the wardrobe is theirs, and a story edit no
                // longer overwrites it. Re-locking hands it back to the story.
                _wardrobeIsManual = !value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(WardrobeLockButtonText));
                OnPropertyChanged(nameof(WardrobeSummary));
            }
        }

        public string WardrobeLockButtonText => IsWardrobeLocked ? "🔒 Locked" : "🔓 Editing";

        /// <summary>
        /// True when every loaded character's sheet was generated wearing the wardrobe that is locked right
        /// now. It is what lets the prompts tell H3 to copy the clothing out of the references instead of
        /// disowning it — and, when false, what the cast card complains about.
        /// </summary>
        public bool SheetsShowWardrobe =>
            HasCastWardrobe && HasCharacter1 && LoadedCharacters.All(c => c.SheetMatchesWardrobe);

        /// <summary>Who the preamble names and whether the references can be trusted for clothing.</summary>
        protected CastPromptStamp.CastInfo CastDescriptor => new(
            _character1.Noun,
            HasCharacter2 ? _character2.Noun : null,
            SheetsShowWardrobe);

        /// <summary>Hands each character their own line of the wardrobe, so the cast card can say whether the
        /// sheet on screen still matches it.</summary>
        private void PushWardrobeToCast()
        {
            _character1.ExpectedWardrobe = CastPromptStamp.OutfitFor(_castWardrobe, 1);
            _character2.ExpectedWardrobe = CastPromptStamp.OutfitFor(_castWardrobe, 2);
        }

        public string WardrobeSummary
        {
            get
            {
                if (!HasCastWardrobe)
                    return "Empty — written automatically from the story (or the scene image) a moment after " +
                           "you stop typing. Unlock to write your own.";

                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                var sheets = !HasCharacter1
                    ? " Load the cast below and build their sheets to have them photographed in it."
                    : stale.Count == 0
                        ? " The character sheets show this outfit, so the references and the prompt agree."
                        : $" Character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet does not show " +
                          "this outfit yet — rebuild the sheets, or the references and the prompt will be " +
                          "dressing them differently.";

                return (IsWardrobeLocked
                    ? "Locked. This exact text is written into every clip's prompt ahead of the description, so " +
                      "the cast cannot change clothes between clips."
                    : "Unlocked — your edits stand and the story no longer rewrites it. Re-lock when you are done.")
                    + sheets;
            }
        }

        /// <summary>
        /// Length of the <i>finished</i> video, 5–120 s in 5 s steps. H3 renders at most ~15 s in one pass, so
        /// anything longer than <see cref="LengthSeconds"/> is written as a chain of
        /// <see cref="PlannedClipCount"/> clips and queued one job per clip, rendered back to back with the
        /// same sheets and joined when the last one lands.
        /// </summary>
        public double StoryDurationSeconds
        {
            get => _storyDurationSeconds;
            set
            {
                var snapped = Math.Clamp(Math.Round(value / 5.0) * 5.0, 5, 120);
                if (Math.Abs(_storyDurationSeconds - snapped) < 0.0001) return;
                _storyDurationSeconds = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
            }
        }

        /// <summary>How many clips Analyze will be asked for: the target duration divided by the per-clip
        /// length, rounded up. 1 means a single ordinary H3 pass.</summary>
        public int PlannedClipCount =>
            Math.Max(1, (int)Math.Ceiling(StoryDurationSeconds / ClampLength(LengthSeconds) - 0.0001));

        public bool IsStorySequence => PlannedClipCount > 1;

        public virtual string ClipPlanSummary
        {
            get
            {
                var clip = ClampLength(LengthSeconds);
                var n = PlannedClipCount;
                if (n <= 1) return $"One clip of {clip:0.#}s — a single H3 pass.";
                return $"{n} clips × {clip:0.#}s → {n * clip:0.#}s of video. Analyze writes all {n} in one " +
                       "reply, Add to Queue enqueues one job per clip, and they are joined into a single " +
                       "file when the last one lands.";
            }
        }

        /// <summary>How many clips the prompt box <i>actually</i> holds — what Add to Queue will enqueue.</summary>
        public int PromptClipCount => _promptClipCount;

        public bool HasPromptSequence => PromptClipCount > 1;

        public string PromptClipSummary =>
            PromptClipCount > 1
                ? $"This prompt holds {PromptClipCount} clips — Add to Queue enqueues {PromptClipCount} jobs, in order."
                : string.Empty;

        /// <summary>
        /// The story the video tells — typed, pasted, or loaded from a .txt. It is the second way into this
        /// tab: with a scene image it is the brief that image is dressed with, and <b>without</b> one it is
        /// the whole input, sent to the llama-server on its own so a story alone can become an H3 prompt with
        /// the loaded cast written into it.
        /// </summary>
        public string StoryText
        {
            get => _storyText;
            set
            {
                if (_storyText == value) return;
                _storyText = value;
                // Typing over a loaded file makes the file name a lie.
                if (!string.IsNullOrEmpty(_storyFileName)) _storyFileName = string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasStoryText));
                OnPropertyChanged(nameof(StorySourceSummary));
                ClearStoryCommand.NotifyCanExecuteChanged();
                OnCanExecuteChanged();
                // The story names the cast before anybody browses for a photo — read the two leads
                // out of it and fill the cards, then the wardrobe pass dresses them.
                ScheduleCastDerive();
                // The wardrobe comes out of the story, so it follows the story rather than waiting to be asked.
                ScheduleWardrobeDerive();
            }
        }

        public bool HasStoryText => !string.IsNullOrWhiteSpace(StoryText);

        /// <summary>File name of the story .txt currently loaded, or empty when the story was typed or
        /// pasted. Derived tabs use it to name what they save.</summary>
        protected string StoryFileName => _storyFileName;

        /// <summary>Says which of the two inputs Analyze will actually use, and what it will do with them.</summary>
        public string StorySourceSummary
        {
            get
            {
                var words = HasStoryText
                    ? StoryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length
                    : 0;
                var loaded = string.IsNullOrEmpty(_storyFileName) ? string.Empty : $" from {_storyFileName}";

                if (HasSceneImage && HasStoryText)
                    return $"Analyze will use both: the scene image for the setting, lighting and wardrobe, " +
                           $"and these {words:N0} words{loaded} for what happens.";
                if (HasSceneImage)
                    return "Analyze will read the scene image alone and invent a story that suits it.";
                if (HasStoryText)
                    return $"No scene image — Analyze will work from these {words:N0} words{loaded} alone, " +
                           "writing the setting, lighting and wardrobe out of the story itself.";
                return "Load a scene image, write a story, or both — Analyze needs at least one of them.";
            }
        }

        /// <summary>
        /// Reads a story off disk. Text only: the point is a story someone already wrote, in the file they
        /// wrote it in.
        /// </summary>
        /// <summary>Virtual so a derived tab can react to a story file landing (the H3 Experimental
        /// fork auto-runs its prompt writer the moment a .txt is loaded).</summary>
        /// <summary>Puts a story into the box and remembers which file it came from. Shared by the
        /// 📄 Load .txt button and by 🗂️ H3 Batch, which walks a folder of them — the file name is not
        /// cosmetic, it is what names that story's clips and its joined film.</summary>
        protected void SetStory(string text, string fileName)
        {
            StoryText = text;
            _storyFileName = fileName;
            OnPropertyChanged(nameof(StorySourceSummary));
        }

        protected virtual async Task LoadStoryFileAsync()
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

            var path = await _fileDialogService.OpenFileDialogAsync(
                "Select a story (.txt)",
                "Text Files|*.txt;*.md;*.text|All Files|*.*",
                initialDir,
                persistKey: "h3cast.story");
            if (path == null) return;

            try
            {
                var text = (await File.ReadAllTextAsync(path)).Trim();
                if (text.Length == 0)
                {
                    AddLog($"Story file is empty: {Path.GetFileName(path)}");
                    return;
                }

                SetStory(text, Path.GetFileName(path));
                AddLog($"Story loaded: {_storyFileName} ({text.Length:N0} chars)");
            }
            catch (Exception ex)
            {
                AddLog($"Could not read the story file: {ex.Message}");
                MessageBox.Show($"Could not read that file:\n{ex.Message}",
                    "H3 Cast", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// The medium the prompt writer must work in. Left on Auto the writer picks — which is what it did
        /// before this existed, and it kept picking the same high-production gacha anime whatever the story
        /// was, because that was the first example the system prompt showed it.
        /// </summary>
        public IReadOnlyList<H3VisualStyle> VisualStyleOptions { get; } = H3VisualStyles.All;

        public H3VisualStyle VisualStyle
        {
            get => _visualStyle;
            set
            {
                if (value == null || ReferenceEquals(_visualStyle, value)) return;
                _visualStyle = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VisualStyleSummary));
            }
        }

        /// <summary>The line the clips will actually open with, so the choice is visible before Analyze runs.</summary>
        public string VisualStyleSummary => VisualStyle.IsAuto
            ? "The writer picks the medium off the scene image, or off the story when there is no image."
            : "[Shot 1] opens: " + VisualStyle.Clause;

        public IReadOnlyList<string> AspectRatioOptions { get; } =
            new[] { H3Canvas.AutoAspect }
                .Concat(H3Canvas.AspectRatios.Select(a => a.Option)).ToList();

        public string SelectedAspectRatio
        {
            get => _selectedAspectRatio;
            set
            {
                if (_selectedAspectRatio == value) return;
                _selectedAspectRatio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResolvedAspectRatio));
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>The aspect actually sent to ComfyUI — the picked one, or the scene image's closest match.</summary>
        public string ResolvedAspectRatio =>
            SelectedAspectRatio == H3Canvas.AutoAspect
                ? ClosestAspectRatio(SceneImagePath)
                : SelectedAspectRatio;

        /// <summary>The same ResolutionSelector presets as the other H3 tabs — H3's native canvas is a 768px short edge.
        /// Virtual so H3 Duo can offer the wider range its I2V pipeline can afford (only three steps ever see
        /// the full canvas there).</summary>
        public virtual IReadOnlyList<MegapixelOption> MegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.4, "0.4 MP — fast draft (≈864×480)"),
            new MegapixelOption(0.7, "0.7 MP — balanced (≈1120×640)"),
            new MegapixelOption(1.0, "1.0 MP — full quality (≈1344×768)"),
        };

        public double Megapixels
        {
            get => _megapixels;
            set
            {
                if (Math.Abs(_megapixels - value) <= 0.0001) return;
                _megapixels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        public double LengthSeconds
        {
            get => _lengthSeconds;
            set
            {
                if (Math.Abs(_lengthSeconds - value) <= 0.0001) return;
                _lengthSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LengthSummary));
                // Frame count is the multiplier on every whole-clip tensor in the graph.
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
                // The per-clip length is the divisor behind the story split, so the plan moves with it.
                OnPropertyChanged(nameof(PlannedClipCount));
                OnPropertyChanged(nameof(IsStorySequence));
                OnPropertyChanged(nameof(ClipPlanSummary));
                OnLengthSecondsChanged();
            }
        }

        /// <summary>Fires after the per-clip length — the divisor behind the story split — changes. A
        /// no-op here; the H3 Experimental tab overrides it to run its prompt writer once the video time
        /// is set, rather than the moment a story lands.</summary>
        protected virtual void OnLengthSecondsChanged() { }

        public string LengthSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                return $"{len:0.#}s → {FramesForSeconds(len)} frames @ 24 fps";
            }
        }

        public long Seed
        {
            get => _seed;
            set { if (_seed != value) { _seed = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Whether the second H3 pass runs. On, every frame's faces are tracked, cropped, re-generated at
        /// <see cref="RefineDenoise"/> against the same conditioning and stitched back in. Off, that whole
        /// branch is pruned and the base pass's frames go straight to the RTX upscale.
        /// </summary>
        public bool FaceRefine
        {
            get => _faceRefine;
            set
            {
                if (_faceRefine == value) return;
                _faceRefine = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        /// <summary>
        /// Denoise of the refine pass — how far a cropped face is allowed to move away from what the base
        /// pass rendered. Low keeps the original performance and only cleans it up; high re-draws the face
        /// from the sheet and risks fighting the lip-sync the audio lock is protecting.
        /// </summary>
        /// <summary>
        /// Opacity of the refined face when it is composited back — <c>H3FaceStitch.blend</c>, node 111.
        ///
        /// <para><b>This, not the denoise, is the floor on how much the pass can change a face.</b> Every
        /// crop is resized to the refine canvas, VAE-encoded, sampled, VAE-decoded, resized back to source
        /// and colour-matched to the region it replaces. All of that happens at <i>any</i> denoise,
        /// including zero: a VAE round trip is lossy and a resample is lossy, so at a low denoise setting
        /// what reaches the frame is not "the same face, gently cleaned" but "a re-encoded copy of the
        /// face". Dropping the denoise to 0.15 removes the model's contribution and leaves that residue
        /// untouched.</para>
        ///
        /// <para>Blend is what attenuates it: at 0.6, six parts refined face to four parts the pixels H3
        /// originally rendered, so the round-trip error is scaled down along with the refinement. 1.00 is
        /// what the workflow shipped and stays the default, since anything else silently changes what an
        /// existing job renders; 0.5–0.7 is where a face that is "slightly off at the lowest denoise"
        /// usually stops being off.</para>
        /// </summary>
        /// <summary>
        /// Sizes the face-refine canvas from the largest crop in the clip instead of clamping it to 768 —
        /// <c>H3FaceTrackCrop.canvas_mode</c>, <c>auto_no_downscale</c> rather than <c>auto_capped_768</c>.
        ///
        /// <para><b>It only does anything on close-ups.</b> The two modes are the same until a crop exceeds
        /// 768px; below that nothing is being downscaled and there is nothing to recover. When a crop is
        /// bigger — a 2.5x crop of a 350px face is 875px — the capped mode shrinks it on the way in, and
        /// that lost detail cannot come back at the stitch no matter what the denoise or the blend is set
        /// to. This removes the resample half of the round-trip loss described on
        /// <see cref="RefineBlend"/>.</para>
        ///
        /// <para><b>Uncapped, and the cost is quadratic.</b> A 1200px canvas is 2.4x the latent tokens and
        /// 2.4x the crop stack of a 768px one, and the canvas follows whatever the biggest face in the clip
        /// happens to be. The node's own note is "can get expensive on clips that include close-ups", so
        /// this stays off by default and the estimates below stop being an upper bound when it is on.</para>
        /// </summary>
        /// <summary>
        /// Composites the refined face through a SAM mask that follows the actual face, instead of through
        /// the detected face box.
        ///
        /// <para><b>This is the fix for "visible moving layers on the face".</b> With
        /// <c>paste_region: face_only</c> the stitch pastes an arbitrary rectangle back into the frame —
        /// re-detected every frame, so it moves and resizes on its own, while the pixels inside it came
        /// from a different pass and differ subtly from everything outside. A subtly-different rectangle
        /// that shifts every frame reads exactly as a layer sliding over the face, and neither the denoise
        /// nor <see cref="RefineBlend"/> can touch it: one does not affect compositing at all, the other
        /// only lowers the layer's contrast without removing its edge.</para>
        ///
        /// <para>Supplying <c>H3FaceStitch.masks</c> overrides <c>paste_region</c> outright, so the blend
        /// falls on the jaw and hairline. <c>H3FaceMaskSAM.temporal_smooth</c> damps what is left of the
        /// frame-to-frame movement, and the feather drops from <see cref="FeatherWithBox"/> to
        /// <see cref="FeatherWithMask"/> because a mask that already follows the face does not need a wide
        /// blend and is harmed by one.</para>
        ///
        /// <para>Costs a SAM pass over the crops. Off, the mask nodes are unwired and pruned and the stitch
        /// goes back to the face box.</para>
        /// </summary>
        public bool UseSamFaceMask
        {
            get => _useSamFaceMask;
            set
            {
                if (_useSamFaceMask == value) return;
                _useSamFaceMask = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public bool RefineNoDownscale
        {
            get => _refineNoDownscale;
            set
            {
                if (_refineNoDownscale == value) return;
                _refineNoDownscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public double RefineBlend
        {
            get => _refineBlend;
            set
            {
                if (Math.Abs(_refineBlend - value) < 0.0001) return;
                _refineBlend = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public double RefineDenoise
        {
            get => _refineDenoise;
            set
            {
                var snapped = Math.Clamp(Math.Round(value * 20) / 20.0, 0.15, 0.75);
                if (Math.Abs(_refineDenoise - snapped) <= 0.0001) return;
                _refineDenoise = snapped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RefineSummary));
            }
        }

        public string RefineSummary => FaceRefine
            ? $"Second H3 pass on the tracked face crops at denoise {RefineDenoise:0.00}, blend " +
              $"{RefineBlend:0.00}, stitched back with the stage-1 audio locked so lip-sync survives. " +
              (RefineNoDownscale
                  ? "Crop canvas uncapped — sized from the largest crop, so no frame is downscaled; costs "
                    + "area squared on a close-up. "
                  : "Crop canvas capped at 768. ") +
              (UseSamFaceMask
                  ? "Composited through a SAM mask that follows the jaw and hairline. "
                  : "Composited through the detected face box — a rectangle that moves every frame. ") +
              "Needs the H3-FaceRefine + NativeAudioLock nodes."
            : "Off — the base H3 frames go straight to the RTX upscale. Faces stay as H3 rendered them.";

        /// <summary>
        /// The RTX Video Super Resolution ×2 finish. <b>Off by default</b>: it is the graph's single largest
        /// allocation — a whole doubled frame stack, materialized at once, right at the end of a run that has
        /// already spent its minutes — so at 15 seconds it is what takes the server down rather than the
        /// diffusion. Turning it on for a short clip is cheap; for a long one, render at the H3 canvas and
        /// upscale the finished file in ✨ Enhance Video, where nothing else is holding memory.
        /// See <see cref="LoadSummary"/>.
        /// </summary>
        /// <summary>
        /// The draft-then-finish scheme on the base pass: four sampler steps at half the width and half the
        /// height, a 2x pass through the MiniMax H3 3D latent upscaler, then three fixed-sigma steps at the
        /// finished size. Only those last three ever see the full canvas.
        ///
        /// <para><b>The cast's panels are not sampled at the draft canvas.</b> Node 23 keeps every panel at
        /// the finished canvas exactly as with this off, and a second, reference-free
        /// MiniMaxH3ReferenceToVideo supplies the draft's empty latent - see
        /// <see cref="WireLatentUpscale"/>. On a tab whose whole job is holding a face, encoding the
        /// identity references at a quarter of the area would cost more than the scheme saves.</para>
        ///
        /// <para>With it on the tab no longer runs its own 6-step shifted schedule: the draft takes the
        /// first four steps of an unshifted 8-step ramp and the finish takes three fixed sigmas, which is
        /// the recipe validated on the MiniMax I2V tab. Off, node 57's 6 steps come back and the whole
        /// 70-80 block is pruned.</para>
        /// </summary>
        public bool UseLatentUpscale
        {
            get => _useLatentUpscale;
            set
            {
                if (_useLatentUpscale == value) return;
                _useLatentUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>
        /// Block-sparse attention for MiniMax H3, patched onto both passes - the base render and the face
        /// refine. Measured 1.4-1.6x on this pipeline. Anything under the kernel's minimum sequence length
        /// falls back to dense on its own, so a short clip at 0.4 MP may show no change at all.
        /// </summary>
        public bool UseSla
        {
            get => _useSla;
            set { if (_useSla != value) { _useSla = value; OnPropertyChanged(); } }
        }

        /// <summary>The fraction of key blocks SLA skips. Break-even is around 0.60 - below that the kernel
        /// is <i>slower</i> than dense attention, so a low setting is a loss, not a safe fallback.</summary>
        public IReadOnlyList<SlaSparsityOption> SlaSparsityOptions { get; } = new[]
        {
            new SlaSparsityOption(0.80, "0.80 - conservative"),
            new SlaSparsityOption(0.85, "0.85 - lightx2v default"),
            new SlaSparsityOption(0.90, "0.90 - validated, ~15% faster"),
            new SlaSparsityOption(0.95, "0.95 - maximum"),
        };

        public double SlaSparsity
        {
            get => _slaSparsity;
            set { if (Math.Abs(_slaSparsity - value) > 0.0001) { _slaSparsity = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Sol-Attn, the same author's earlier general-purpose sparse attention - node 53, which this
        /// workflow shipped with wired in and always on. <b>Off by default now</b>: SLA supersedes it for
        /// H3 and stacking the two bought nothing measurable, so leaving both on would be two
        /// approximations for one speedup. Off, the node is unwired and pruned.
        /// </summary>
        public bool UseSparseAttention
        {
            get => _useSparseAttention;
            set { if (_useSparseAttention != value) { _useSparseAttention = value; OnPropertyChanged(); } }
        }

        public bool RtxUpscale
        {
            get => _rtxUpscale;
            set
            {
                if (_rtxUpscale == value) return;
                _rtxUpscale = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
                OnPropertyChanged(nameof(LoadSummary));
                OnPropertyChanged(nameof(HasLoadWarning));
            }
        }

        /// <summary>What the saved file will be.</summary>
        public virtual string UpscaleSummary
        {
            get
            {
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var sampled = string.Empty;
                if (UseLatentUpscale)
                {
                    var (dw, dh) = DraftCanvas(ResolvedAspectRatio, Megapixels);
                    sampled = $" Sampled as a {dw}×{dh} draft, upscaled ×{LatentUpscaleFactor:0.#}; " +
                              "the cast's panels stay at the finished canvas.";
                }
                if (!RtxUpscale)
                    return $"Output: the H3 canvas as rendered, ≈{cw}×{ch}. No upscale pass.{sampled}";
                var (w, h) = UpscaleSize(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                return $"Output: RTX ×2 super-resolution → ≈{w}×{h}.{sampled}";
            }
        }

        /// <summary>
        /// The frame stack the run will have to hold in one piece, and a warning when that is the size that
        /// kills ComfyUI mid-render. Every image node here works on the whole clip at once, so the cost is
        /// frames × pixels × 3 channels, and the RTX pass quadruples the pixel count — at 15 seconds and
        /// 1.0 MP that is roughly 17 GB in fp32, allocated at the very end of a run that has already taken
        /// twenty minutes. The failure looks nothing like an error in the graph: the server process simply
        /// disappears and the job is neither queued nor in the history.
        /// </summary>
        public virtual string LoadSummary
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (cw, ch) = CanvasSize(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var baseGb = FrameStackGb(frames, cw, ch);

                if (!RtxUpscale)
                    return $"{frames} frames × {cw}×{ch} ≈ {baseGb:0.#} GB of frames held at once.";

                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                var upGb = FrameStackGb(frames, uw, uh);
                var text = $"{frames} frames: ≈{baseGb:0.#} GB at the H3 canvas, ≈{upGb:0.#} GB after RTX ×2, " +
                           "both live at the same time during the upscale.";
                return upGb >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten the clip, " +
                             "drop the quality to 0.7 MP, or turn RTX off and upscale afterwards in ✨ Enhance Video."
                    : text;
            }
        }

        /// <summary>True when <see cref="LoadSummary"/> is carrying a warning, so the UI can colour it.</summary>
        public virtual bool HasLoadWarning
        {
            get
            {
                if (!RtxUpscale) return false;
                var (uw, uh) = UpscaleSize(ResolvedAspectRatio, Megapixels, UseLatentUpscale);
                return FrameStackGb(FramesForSeconds(ClampLength(LengthSeconds)), uw, uh) >= HeavyFrameStackGb;
            }
        }

        /// <summary>Where a whole-clip frame stack starts being the thing that fails, in gigabytes.</summary>
        protected const double HeavyFrameStackGb = 8.0;

        /// <summary>An uncompressed fp32 RGB frame stack, in GB — what an image tensor of this clip costs.</summary>
        protected static double FrameStackGb(int frames, int width, int height) =>
            (double)frames * width * height * 3 * 4 / (1024.0 * 1024.0 * 1024.0);

        /// <summary>
        /// Switches the reference pipeline from 'match' — references scaled to the generation's pixel area,
        /// which on the MiniMax I2V graph this tab's <see cref="H3DuoViewModel"/> renders through is the
        /// <i>draft</i> canvas, a quarter of the chosen megapixels — to 'max', a 2048px short edge. The H3
        /// Cast graph keeps its panels at the finished canvas by construction and ignores this; on the Duo
        /// tab it is the lever for identity fidelity, and it is on there by default.
        /// </summary>
        public bool MaxFidelityReferences
        {
            get => _maxFidelityReferences;
            set
            {
                if (_maxFidelityReferences == value) return;
                _maxFidelityReferences = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(UpscaleSummary));
            }
        }

        /// <summary>
        /// The audio-enhancement pass over the saved clip, on the Duo render's I2V graph. The H3 Cast graph
        /// has no such switch and ignores this.
        /// </summary>
        public bool UseAudioEnhancement
        {
            get => _useAudioEnhancement;
            set { if (_useAudioEnhancement != value) { _useAudioEnhancement = value; OnPropertyChanged(); } }
        }

        public bool IsAnalyzing
        {
            get => _isAnalyzing;
            set
            {
                if (_isAnalyzing == value) return;
                _isAnalyzing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AnalyzeBusyText));
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// True only while the <i>prompt writer</i> is running — the pass that is about to overwrite the
        /// prompt box.
        ///
        /// <para>Kept apart from <see cref="IsAnalyzing"/> on purpose. That flag is the llama-server mutex:
        /// the automatic wardrobe pass and the automatic cast pass borrow it too, because only one turn may
        /// be in flight at a time. Neither of those touches the prompt box, so neither is a reason to
        /// refuse a queue-add — and they are exactly the passes that fire on a debounce after the story
        /// changes, reschedule themselves when they collide with a real Analyze run, and then take a long
        /// time because the GPU is busy rendering the queue. Gating
        /// <see cref="CanGenerate"/> on <see cref="IsAnalyzing"/> is what left Add to Queue greyed out for
        /// minutes after the chain had visibly landed in the box.</para>
        /// </summary>
        public bool IsWritingPrompt
        {
            get => _isWritingPrompt;
            protected set
            {
                if (_isWritingPrompt == value) return;
                _isWritingPrompt = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        /// <summary>What the spinner beside Analyze says. The wardrobe and cast watchers borrow the same busy
        /// flag — it is the same server and the same "one turn at a time" — so they name themselves rather
        /// than claiming a prompt is being written.</summary>
        public string AnalyzeBusyText =>
            _isDerivingWardrobe ? "Writing the wardrobe…" :
            _isDerivingCast ? "Reading the cast…" :
            "Analyzing…";

        /// <summary>
        /// Analyze needs <i>something</i> to work from — the scene image, the story text, or both. Neither is
        /// individually required: an image alone gets a story invented for it, a story alone gets its setting
        /// written out of the prose. Deliberately <i>not</i> gated on
        /// <see cref="VideoProcessingBaseViewModel.IsProcessing"/>: it talks to the llama-server, so the next
        /// scene can be written while the GPU is busy.
        /// </summary>
        public bool CanAnalyze => (HasSceneImage || HasStoryText) && !IsAnalyzing;

        /// <summary>
        /// Queueing needs a prompt and a sheet for every loaded character. A render in flight does not block
        /// it — that is what the queue is for — but the prompt writer does, since it is about to overwrite
        /// the prompt box that would be snapshotted. The background wardrobe and cast passes deliberately do
        /// <i>not</i>: see <see cref="IsWritingPrompt"/>.
        /// </summary>
        public bool CanGenerate => !string.IsNullOrWhiteSpace(Prompt) && AllSheetsReady && !IsWritingPrompt;

        /// <summary>Maps the scene image's own aspect to the nearest ratio the ResolutionSelector offers.</summary>
        private string ClosestAspectRatio(string path)
        {
            int w = 0, h = 0;
            if (SceneImagePreview is { } preview && string.Equals(path, SceneImagePath, StringComparison.OrdinalIgnoreCase))
            {
                w = preview.PixelWidth; h = preview.PixelHeight;
            }
            if ((w <= 0 || h <= 0) && !string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try
                {
                    using var fs = File.OpenRead(path);
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    w = frame.PixelWidth; h = frame.PixelHeight;
                }
                catch { /* fall through to the 16:9 default */ }
            }
            return H3Canvas.ClosestAspectRatio(w, h);
        }

        /// <summary>
        /// The canvas node 45 will pick, for display only — the graph takes it straight from
        /// ResolutionSelector. Mirrors that node's maths: the aspect's area at this megapixel count, the
        /// width snapped to a multiple of 32, and the height derived from the <i>snapped</i> width.
        /// </summary>
        private static (int Width, int Height) CanvasSize(
            string aspectOption, double megapixels, bool latentUpscale)
        {
            var ratio = H3Canvas.AspectRatios
                .FirstOrDefault(a => a.Option == aspectOption).Ratio;
            if (ratio <= 0) ratio = 16.0 / 9.0;

            // The alignment follows the latent upscale, because that is what the graph does. This stays an
            // approximation of ResolutionSelector rather than H3Canvas.Resolve's exact reproduction of it,
            // as it always has; the graph does not depend on it, since nodes 70/71 derive the draft from
            // the selector's real output.
            var multiple = latentUpscale ? UpscaledResolutionMultiple : ResolutionMultiple;
            var area = Math.Max(0.1, megapixels) * 1_000_000.0;
            var w = Round(Math.Sqrt(area * ratio));
            return (w, Round(w / ratio));

            int Round(double v) => Math.Max(multiple, (int)Math.Round(v / multiple) * multiple);
        }

        /// <summary>The canvas the base pass actually samples at - the finished frame with the upscale off,
        /// a quarter of its area with it on. What nodes 70/71 compute.</summary>
        private static (int Width, int Height) DraftCanvas(string aspectOption, double megapixels)
        {
            var (w, h) = CanvasSize(aspectOption, megapixels, true);
            return ((int)(w / LatentUpscaleFactor), (int)(h / LatentUpscaleFactor));
        }

        /// <summary>The size that reaches the file: <see cref="CanvasSize"/> through the graph's RTX ×2 pass.</summary>
        private static (int Width, int Height) UpscaleSize(
            string aspectOption, double megapixels, bool latentUpscale)
        {
            var (w, h) = CanvasSize(aspectOption, megapixels, latentUpscale);
            return ((int)(w * RtxScale), (int)(h * RtxScale));
        }

        /// <summary>H3's supported clip length is 4–15 seconds at 24 fps.</summary>
        protected static double ClampLength(double seconds) =>
            Math.Clamp(seconds <= 0 ? 10 : seconds, 4, 15);

        /// <summary>Mirrors node 5's expression: 24 fps snapped onto the model's 17k+5 frame grid.</summary>
        protected static int FramesForSeconds(double seconds)
        {
            var frames = Math.Max(5, (int)Math.Round(seconds * 24));
            return frames + (5 - frames % 17 + 17) % 17;
        }

        #endregion

        #region Analysis (scene image → multi-shot H3 prompt)

        /// <summary>Virtual so a derived tab can replace the writer flow outright (the H3 Experimental
        /// fork routes story runs through the MCP prompt-writer tool loop instead).</summary>
        protected virtual async Task AnalyzeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            IsWritingPrompt = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;
                var fromImage = HasSceneImage;
                var source = fromImage
                    ? HasStoryText ? "the scene image + the story text" : "the scene image"
                    : "the story text";
                AddLog(clipCount > 1
                    ? $"Writing a {clipCount}-clip chain ({clipCount} × {len:0.#}s = {clipCount * len:0.#}s) " +
                      $"from {source} — sending to {_lmStudioService.DescribeTarget(model)}"
                    : $"Writing a {len:0.#}s multi-shot H3 prompt from {source} " +
                      $"— sending to {_lmStudioService.DescribeTarget(model)}");
                if (!VisualStyle.IsAuto)
                    AddLog($"Visual style locked: {VisualStyle.Name}");


                // A whole novel will not fit a local model's context; it truncates silently, which looks like
                // the model ignoring the ending.
                if (HasStoryText && StoryText.Length > 20000)
                    AddLog($"WARNING: the story is {StoryText.Length:N0} characters — a local model will very " +
                           "likely truncate it. Cut it down to the beats you want on screen.");

                // Decided before the chain is written, so all N clips are dressed from one answer rather than
                // N independent ones — and kept if it is already filled in, because the box is editable and a
                // hand-written wardrobe is a deliberate one.
                // Not gated on "the box is empty": a box holding one outfit for a two-hander is exactly the
                // case that needs topping up, and it is the one a "there is already a wardrobe" check misses.
                if (!await EnsureWardrobeAsync(token, model))
                    AddLog("WARNING: the wardrobe could not be written — the clips will each describe the " +
                           "outfits themselves, which is where between-clip costume changes come from. " +
                           "Fill the wardrobe box in by hand, or press 🎽 Derive again.");

                var systemPrompt = await ReadSystemPromptAsync(SystemPromptFile, token);
                if (clipCount > 1)
                {
                    systemPrompt += "\n\n" + await ReadSystemPromptAsync(StorySystemPromptFile, token);
                    if (!fromImage)
                        // That guide sources the wardrobe from a scene image this run does not have.
                        systemPrompt += "\n\nNOTE FOR THIS RUN: there is no scene image. Wherever the rules " +
                                        "above say to read the setting or the wardrobe off the scene image, " +
                                        "read them off the STORY instead — decide each of them once, and " +
                                        "then repeat that wording verbatim in every clip.";
                }

                // A chain already in the box is far too long to feed back in as a "draft".
                var draft = PromptClipCount > 1
                    ? "(the prompt box holds a previous sequence — ignore it and write a fresh one)"
                    : string.IsNullOrWhiteSpace(Prompt)
                        ? "(none — invent a sequence that suits the material above)"
                        : CastPromptStamp.Strip(Prompt).Trim();

                var lengthBlock = clipCount > 1
                    ? $"Story sequence: write {clipCount} clips that together tell ONE continuous story " +
                      $"running about {clipCount * len:0.##} seconds in total. Each clip is {len:0.##} " +
                      "seconds long and is rendered separately, so each one must be a complete, " +
                      "self-contained H3 prompt. Separate them with a line spelled exactly " +
                      $"\"=== CLIP n of {clipCount} ===\", numbered 1 to {clipCount} in order. The same " +
                      "characters appear throughout — the same reference sheets are attached to every clip.\n"
                    : $"Target duration: {len:0.##} seconds.\n";

                // The cast reaches the generator as reference SHEETS, so identity is named rather than
                // described — anything the LLM invents about a face it has never seen overrides the sheet.
                // Wardrobe is the deliberate exception: the sheets show studio clothing that has nothing to
                // do with this video, so the outfit has to come from somewhere else and be written out. With
                // a scene image that somewhere is the image; with a story alone it is the prose, and failing
                // that whatever the setting calls for — stated once and then repeated verbatim.
                //
                // Once the wardrobe has been decided it is quoted here instead, as settled fact. That is the
                // difference between asking a model to be consistent — which it cannot be across N blocks it
                // writes independently — and handing it the answer.
                var wardrobeRule = HasCastWardrobe
                    ? "CLOTHING IS ALREADY DECIDED AND IS NOT YOURS TO CHOOSE. The cast wears exactly this, in " +
                      "every shot of every clip:\n" + CastWardrobe.Trim() + "\n" +
                      "Attach that outfit to the character's tag the first time they appear in each clip — " +
                      "\"<Picture 1>, wearing …\" — copying the wording above rather than rephrasing it, and " +
                      "keep it identical everywhere else it is mentioned. " +
                      (SheetsShowWardrobe
                          ? "Their reference sheets were photographed in exactly these clothes, so the pictures " +
                            "and the words above agree — do not contradict either. "
                          : "Their reference sheets are studio photographs and whatever those show them wearing " +
                            "is irrelevant. ") +
                      "Never put them in anything else and never invent a costume change; the only clothing " +
                      "that may change is a change the user's story explicitly asks for."
                    : fromImage
                    ? "CLOTHING IS THE ONE EXCEPTION, and it is a hard requirement: the cast must be dressed exactly as the people in the SCENE image are dressed, NOT as their reference sheets show them — a sheet is a studio photograph and its clothing is irrelevant to this video. Read the wardrobe off the scene image and write it out explicitly the first time each character appears — garments, colours, materials, footwear, headwear and worn accessories — attached to their tag, e.g. \"<Picture 1>, wearing a <full outfit description from the scene image>,\". Restate that same outfit in exactly the same words every later time it is mentioned. If the scene image shows no people, dress them in what the setting plainly calls for and keep that wording identical throughout."
                    : "CLOTHING IS THE ONE EXCEPTION, and it is a hard requirement: the cast must NOT be dressed as their reference sheets show them — a sheet is a studio photograph and its clothing is irrelevant to this video. Take the wardrobe from the STORY where the story describes it, and where it does not, dress them in what the period, place and situation plainly call for. Either way write the outfit out explicitly the first time each character appears — garments, colours, materials, footwear, headwear and worn accessories — attached to their tag, e.g. \"<Picture 1>, wearing a <full outfit description>,\", and restate that same outfit in exactly the same words every later time it is mentioned.";

                // The sexes are stated because they are the one thing about the cast the writer may put in
                // words: everything else about how they look is carried by the sheets and must not be
                // described, but "he"/"she" has to be right in the prose or the tag is fighting the sentence
                // around it.
                var cast = HasCharacter2
                    ? $"Two character reference sheets will additionally be given to the video model and are addressed as <Picture 1> (Character 1, a {_character1.Noun}) and <Picture 2> (Character 2, a {_character2.Noun}). Each sheet is one person shown from several angles on a plain studio background. You are NOT shown those sheets — the video model is. Write both characters into the action and refer to them ONLY by those tags — wherever the story names its people, cast <Picture 1> and <Picture 2> in those roles and use the tags in place of the names. Their sexes are as stated here, so use the matching pronouns; apart from that, do not write any word for their hair, face, skin, build or age, since the tag already carries all of it. " + wardrobeRule
                    : $"One character reference sheet will additionally be given to the video model and is addressed as <Picture 1> (Character 1, a {_character1.Noun}). The sheet is one person shown from several angles on a plain studio background. You are NOT shown that sheet — the video model is. Write them into the action and refer to them ONLY by that tag — wherever the story names its protagonist, cast <Picture 1> in that role and use the tag in place of the name. Their sex is as stated here, so use the matching pronouns; apart from that, do not write any word for their hair, face, skin, build or age, since the tag already carries all of it. " + wardrobeRule + SoloCastDirective;

                // Ahead of the story rather than after it: the writer decides the medium in its first
                // sentence, and a rule that arrives after the material has already been read is one the
                // opening of [Shot 1] has stopped listening to.
                var styleRule = H3VisualStyles.Rule(VisualStyle);

                const string faceRule =
                    "Faces matter: keep the cast's faces visible and readable — favour medium and close shots " +
                    "over wide ones where the story allows, since a face that is a handful of pixels wide " +
                    "cannot hold an identity.\n";

                // The story's events are the whole plot. A short story is stretched by budgeting the clips
                // across its own action — several clips per exchange of the fight — never by inventing new
                // events around it; without an explicit budget the model plays the story out at its natural
                // pace, then pads the remaining clips with journeys and epilogues of its own.
                const string storyFidelityRule =
                    "THE STORY IS THE COMPLETE PLOT — expand its action, never invent events. Show only what " +
                    "the story narrates, in its order, from its first line to its last: no new events, " +
                    "journeys, locations or outcomes the story does not contain, and nothing past its ending — " +
                    "no walking away, no epilogue, no aftermath the prose has not written.\n" +
                    "BUDGET THE CLIPS FIRST: list the story's events in order (in a fight, every strike, " +
                    "dodge, grab, throw and fall is one event) and share the clips between them — several " +
                    "clips per exchange when there are fewer exchanges than clips — so that the story's " +
                    "FINAL event is what the LAST clip shows. Render each share at full detail: wind-up, " +
                    "strike, contact, recoil, fall, recovery, each its own shots, angles and impact detail. " +
                    "If your plan finishes the story before the last clip, the plan is wrong — you have gone " +
                    "too fast; re-split and give each exchange more clips. Never bridge the gap with new " +
                    "events.\n";

                string userMessage;
                if (fromImage)
                {
                    var story = HasStoryText
                        ? StoryText.Trim()
                        : "(none — invent a story that suits the scene and carry it from beginning to end)";

                    userMessage =
                        "Image role: REFERENCE ONLY — this image is the SCENE (setting, lighting, art style, mood " +
                        "and the wardrobe the cast wears). The video does not start on it and the generator will " +
                        "never see it, so describe the environment — and the clothing — explicitly.\n" +
                        $"{cast}\n" +
                        lengthBlock +
                        styleRule +
                        faceRule +
                        (HasStoryText ? storyFidelityRule : string.Empty) +
                        $"Story the video must tell:\n{story}\n" +
                        $"Draft idea from the user:\n{draft}";
                }
                else
                {
                    // No image at all: the story carries the setting as well as the action, so the model is
                    // told to establish it rather than assume a picture it was never given.
                    var wholeStory = clipCount > 1
                        ? $"Together the {clipCount} clips must tell the whole story below, beginning to end — " +
                          "budget the clips across its events before writing anything: one event per clip " +
                          "when the story has that many, and several clips per event when it has fewer.\n"
                        : $"The whole story has to be told inside {len:0.##} seconds, so pick the beats that " +
                          "carry it and compress the rest; do not stop halfway through.\n";

                    userMessage =
                        "There is NO reference image of the scene. The story below is the only source: read the " +
                        "setting, period, time of day, weather, lighting and mood out of it and write them into " +
                        "the prompt explicitly, inventing whatever the story leaves unsaid and keeping it " +
                        "consistent from the first shot to the last. The generator sees only your text.\n" +
                        $"{cast}\n" +
                        lengthBlock +
                        wholeStory +
                        storyFidelityRule +
                        styleRule +
                        faceRule +
                        $"The story:\n{StoryText.Trim()}\n" +
                        $"Draft idea from the user:\n{draft}";
                }

                // A chain needs headroom for N prompts, not one. A single H3 prompt runs ~700 tokens, so 6000
                // is already generous; each extra clip only needs a fraction of that, and the total is capped
                // so the request cannot exceed a modest local context window.
                var maxTokens = Math.Min(32000, 6000 + 2500 * (Math.Max(1, clipCount) - 1));

                // A chain is written one clip at a time — one beat-sheet call, then one call per clip.
                // See H3CastViewModel.ChainWriter.cs for what asking for all N in one reply did to the
                // cast tags, and what that cost at render time. A lone clip was never that failure and
                // keeps the request this tab has always sent.
                string cleaned;
                if (clipCount > 1)
                {
                    cleaned = await WriteChainClipByClipAsync(model, len, clipCount, token);
                }
                else
                {
                    var result = fromImage
                        ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                            model,
                            SceneImagePath,
                            userMessage,
                            systemPrompt,
                            maxTokens: maxTokens,
                            cancellationToken: token)
                        : await _lmStudioService.SendTextChatAsync(
                            model,
                            systemPrompt,
                            userMessage,
                            maxTokens: maxTokens,
                            cancellationToken: token);

                    cleaned = ApplyReferenceLineToChain(
                        CleanOutput(result), Panels1, Panels2, CastWardrobe, CastDescriptor);
                }
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written ({written} clips, {cleaned.Length} chars, {CountShots(cleaned)} shots total)"
                        : $"Prompt written ({cleaned.Length} chars, {CountShots(cleaned)} shots)");

                    if (written != clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the model returned {written}. " +
                               "Add to Queue enqueues what is in the prompt box — re-run Analyze, or edit the " +
                               "headers by hand.");

                    // Checked on the bodies alone: the reference line is code-written and identical in every
                    // clip, so including it would only ever mask a real disagreement.
                    var drift = DescribeWardrobeDrift(SplitClips(cleaned).Select(CastPromptStamp.Strip).ToList());
                    if (drift != null)
                        AddLog(HasCastWardrobe
                            ? $"Note: the clip bodies describe the cast's appearance and {drift}. Every clip " +
                              "carries the same wardrobe block ahead of its description and that block outranks " +
                              "them, so the outfits should still hold — but if a clip comes out wrong, " +
                              "harmonise those words too."
                            : $"WARNING: the clips describe the cast's appearance and {drift}, and there is no " +
                              "wardrobe locked to override them — they will change outfits between clips. Fill " +
                              "the wardrobe box in (🎽 Derive) and press Add to Queue again.");
                }
                else
                {
                    AddLog("WARNING: Analysis returned empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"Analysis failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsWritingPrompt = false;
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        protected static async Task<string> ReadSystemPromptAsync(string fileName, CancellationToken token)
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "prompts", "prompt2json", fileName);
            if (!File.Exists(path))
                throw new FileNotFoundException($"System prompt not found: {path}");
            return await File.ReadAllTextAsync(path, token);
        }

        /// <summary>
        /// Strips the wrappers small vision models like to add (code fences, bold markers, a leading
        /// "prompt:" label, surrounding quotes) without touching the H3 field structure.
        /// </summary>
        protected static string CleanOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            text = text.Replace("**", "").Trim();

            if (text.StartsWith("```"))
            {
                var firstBreak = text.IndexOf('\n');
                if (firstBreak > 0) text = text[(firstBreak + 1)..];
                var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
                if (lastFence >= 0) text = text[..lastFence];
                text = text.Trim();
            }

            if (text.StartsWith("prompt:", StringComparison.OrdinalIgnoreCase))
                text = text[7..].TrimStart();
            if (text.Length > 1 && text[0] == '"' && text[^1] == '"')
                text = text[1..^1].Trim();

            return text.Trim();
        }

        #endregion

        #region Wardrobe (decided once, stamped into every clip)

        /// <summary>
        /// Resolves the llama-server target, or null after telling the user why it could not. Shared by
        /// Analyze and the wardrobe pass so the two cannot end up talking to different servers.
        /// </summary>
        /// <param name="quiet">Set by the automatic wardrobe pass: nobody pressed anything, so a modal
        /// "no model available" two and a half seconds after a keystroke would be an interruption rather than
        /// an answer. It logs and gives up instead.</param>
        protected async Task<string?> ResolveLlmModelAsync(CancellationToken token, bool quiet = false)
        {
            var baseUrl = _settingsService.Settings?.LMStudioSettings?.BaseUrl ?? "http://alien:8080";
            await _lmStudioService.SetBaseUrlAsync(baseUrl);

            var models = await _lmStudioService.GetAvailableModelsAsync(token);
            var model = _settingsService.Settings?.LMStudioSettings?.SelectedModel ?? string.Empty;
            if (string.IsNullOrEmpty(model) && models.Count > 0)
                model = models[0].Id ?? models[0].Name ?? string.Empty;
            if (!string.IsNullOrEmpty(model)) return model;

            if (quiet)
                AddLog("The wardrobe could not be written: no llama-server model is available. Start the " +
                       "server, then press 🎽 Derive.");
            else
                MessageBox.Show("No LM Studio / llama-server model available. Ensure the server is running and a model is loaded.",
                    "LM Studio Unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        /// <summary>
        /// The 🎽 button: re-derives the wardrobe on its own, replacing whatever is in the box. Useful when the
        /// outfits are the only thing wrong with an otherwise good chain — the wardrobe is re-stamped into
        /// every clip at Add to Queue, so a chain already in the prompt box does not have to be rewritten.
        /// </summary>
        private async Task RederiveWardrobeAsync()
        {
            if (!CanAnalyze) return;

            IsAnalyzing = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;

            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                // The button is a deliberate re-roll, so it always writes the whole cast — both characters,
                // whether or not their photos have been picked yet. The story is what is being costumed here;
                // which files have been browsed for is a separate question, and asking it at this point is what
                // used to leave a two-hander with one outfit.
                var dress = WardrobeCast;
                AddLog($"Writing outfits for {dress.Count} characters — sending to {_lmStudioService.DescribeTarget(model)}");
                var derived = await DeriveWardrobeAsync(model, dress, token);
                if (string.IsNullOrWhiteSpace(derived))
                {
                    AddLog("WARNING: the wardrobe came back empty — the box is unchanged.");
                    return;
                }

                SetDerivedWardrobe(derived, dress);
                if (PromptClipCount > 0)
                    AddLog("Add to Queue re-stamps this wardrobe into every clip already in the prompt box — " +
                           "no need to re-run Analyze.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR writing the wardrobe: {ex.Message}");
                MessageBox.Show($"Writing the wardrobe failed:\n{ex.Message}",
                    "H3 Cast", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }

        /// <summary>
        /// Empties the box and puts the wardrobe back under the story's control — clearing it is how you ask
        /// for a different outfit, so a fresh one is derived rather than waiting to be asked for.
        /// </summary>
        private void ClearWardrobe()
        {
            CastWardrobe = string.Empty;
            _wardrobeStoryStamp = string.Empty;
            _wardrobeCastStamp = string.Empty;
            // Locking is what hands the box back to the story — see IsWardrobeLocked.
            IsWardrobeLocked = true;
            _wardrobeIsManual = false;
            ScheduleWardrobeDerive();
        }

        /// <summary>
        /// Puts a derived wardrobe in the box and records what it was written from, so the watcher below can
        /// tell "the story has not changed since" from "it has". Also warns when the sheets already on screen
        /// were photographed in different clothes.
        /// </summary>
        /// <param name="dressed">Who this pass wrote for. When that is not the whole cast the lines are
        /// merged into the block already in the box rather than replacing it — the point of a top-up being
        /// that an outfit a character sheet has already been built in survives one.</param>
        private void SetDerivedWardrobe(string wardrobe, IReadOnlyList<CharacterSlot> dressed)
        {
            // Merged whenever what came back does not cover the whole cast — which is the deliberate top-up,
            // and also the small model that was asked for two outfits and returned one. Either way the answer
            // is the same: keep the outfit already in the box rather than losing a character to a short reply.
            var covers = WardrobeCast.All(c => CastPromptStamp.OutfitFor(wardrobe, c.Index).Length > 0);
            var partial = HasCastWardrobe && !covers;
            CastWardrobe = partial ? CastPromptStamp.MergeWardrobe(CastWardrobe, wardrobe) : wardrobe;
            _wardrobeStoryStamp = StorySourceStamp();
            _wardrobeCastStamp = CastSexStamp();
            AddLog(partial
                ? $"Wardrobe: character {string.Join(" and ", dressed.Select(c => c.Index))} dressed; the rest "
                  + $"of the cast keeps what they had:\n{CastWardrobe.Trim()}"
                : $"Wardrobe locked:\n{CastWardrobe.Trim()}");

            // Said plainly, because an undressed character is invisible until a video comes back wrong: the
            // clips would describe the outfit of whoever the model did answer for and improvise the other.
            var undressed = WardrobeCast
                .Where(c => CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0).ToList();
            if (undressed.Count > 0)
                AddLog($"WARNING: character {string.Join(" and ", undressed.Select(c => c.Index))} came back " +
                       "with no outfit. The next Analyze or Build Sheets writes one; press 🎽 Derive to do it now.");

            var stale = LoadedCharacters.Where(c => c.HasSheet && !c.SheetMatchesWardrobe).ToList();
            if (stale.Count > 0)
                AddLog($"Character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet was built in " +
                       "other clothes — press Build Character Sheet(s) again so the references H3 gets are " +
                       "wearing this wardrobe.");
        }

        /// <summary>Separates a stamp's fields — a control character, so no story text can forge one.</summary>
        private const char StampSeparator = (char)31;   // Unit Separator

        /// <summary>The material the outfits are read out of. A change here re-dresses everybody.</summary>
        private string StorySourceStamp() =>
            StoryText.Trim() + StampSeparator + (HasSceneImage ? SceneImagePath : string.Empty);

        /// <summary>The sexes the outfits in the box were written for, in cast order. A change here re-dresses
        /// only the character whose sex moved, which leaves the other one's sheet valid.</summary>
        private string CastSexStamp() =>
            string.Join(StampSeparator, WardrobeCast.Select(c => c.Noun));

        /// <summary>
        /// The story is the source of the wardrobe, so a change to the story (or to the scene image, or to who
        /// is in the cast) has to reach the outfits by itself — a wardrobe box the user has to remember to
        /// refresh is a wardrobe box that quietly describes the previous story.
        ///
        /// <para>Debounced rather than immediate, because this box is typed into: the request goes out once
        /// the typing stops. A wardrobe the user unlocked and wrote themselves is never touched, and neither is
        /// one already derived from exactly this material.</para>
        /// </summary>
        private void ScheduleWardrobeDerive()
        {
            if (_wardrobeIsManual) return;

            // Cancels the previous keystroke's pending pass, and any request already in flight against text
            // that has since changed. Disposal is left to that pass's own finally — it may still be inside an
            // await holding the token.
            _wardrobeCts?.Cancel();
            _wardrobeCts = null;

            if (!HasStoryText && !HasSceneImage) return;

            var cts = new CancellationTokenSource();
            _wardrobeCts = cts;
            _ = AutoDeriveWardrobeAsync(cts);
        }

        private async Task AutoDeriveWardrobeAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);

                var dress = CharactersNeedingWardrobe();
                if (dress.Count == 0) return;

                // Analyze derives the wardrobe itself as its first step; two passes at once would race for the
                // box and burn a request for the loser. Rescheduled rather than dropped — a need abandoned
                // here is a character who is never dressed at all, which is exactly how a second character
                // ended up with no outfit.
                if (IsAnalyzing || _isDerivingWardrobe)
                {
                    ScheduleWardrobeDerive();
                    return;
                }

                _isDerivingWardrobe = true;
                IsAnalyzing = true;
                try
                {
                    var model = await ResolveLlmModelAsync(token, quiet: true);
                    if (model == null) return;

                    AddLog(dress.Count < WardrobeCast.Count
                        ? $"Writing an outfit for character {string.Join(" and ", dress.Select(c => c.Index))}..."
                        : HasCastWardrobe
                            ? "Story changed — rewriting the cast's wardrobe from it..."
                            : "Deriving the cast's wardrobe from the story...");
                    var derived = await DeriveWardrobeAsync(model, dress, token);
                    token.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(derived))
                    {
                        AddLog("The wardrobe could not be written from this material — press 🎽 Derive to retry, " +
                               "or unlock the box and write it yourself.");
                        return;
                    }
                    SetDerivedWardrobe(derived, dress);
                }
                finally
                {
                    _isDerivingWardrobe = false;
                    // Not unconditionally: Analyze cancels this pass from inside EnsureWardrobeAsync, and
                    // clearing the shared busy flag there would re-arm the Analyze button on top of the run
                    // that just cancelled us.
                    if (!IsWritingPrompt) IsAnalyzing = false;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Nothing the user asked for is blocked on this, so it reports and stays out of the way.
                AddLog($"Automatic wardrobe pass failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_wardrobeCts, cts)) _wardrobeCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// Makes sure the wardrobe covers the whole cast before something that depends on it runs — the sheet
        /// builder, which photographs the cast in it, and Analyze, which quotes it. Tops up a character who has
        /// no outfit rather than rewriting the ones that already have one, so the debounce being late, or
        /// having been skipped while something else was talking to the llama-server, cannot leave a character
        /// undressed at the moment their sheet is built.
        ///
        /// <para>Returns whether there is a wardrobe to work with at all.</para>
        /// </summary>
        protected async Task<bool> EnsureWardrobeAsync(CancellationToken token, string? llmModel = null)
        {
            var dress = CharactersNeedingWardrobe();
            if (dress.Count == 0)
            {
                if (HasCastWardrobe) AddLog("Wardrobe: using the outfits already in the wardrobe box.");
                return HasCastWardrobe;
            }
            if (!HasStoryText && !HasSceneImage) return HasCastWardrobe;

            // A pending debounce would otherwise fire straight afterwards and derive a second, different one.
            _wardrobeCts?.Cancel();

            var model = llmModel ?? await ResolveLlmModelAsync(token);
            if (model == null) return HasCastWardrobe;

            AddLog(dress.Count < WardrobeCast.Count
                ? $"Wardrobe: character {string.Join(" and ", dress.Select(c => c.Index))} has no outfit yet — " +
                  "writing one, leaving the rest of the cast dressed as they are..."
                : "Wardrobe: deciding the cast's outfits once, so every clip — and every character sheet — " +
                  "can be dressed identically...");
            var derived = await DeriveWardrobeAsync(model, dress, token);
            if (string.IsNullOrWhiteSpace(derived)) return HasCastWardrobe;

            SetDerivedWardrobe(derived, dress);
            return true;
        }

        /// <summary>
        /// Asks the llama-server for one outfit per loaded character, as its own small request rather than as
        /// part of the chain. Kept separate on purpose: it is a short, narrow question a local model answers
        /// well, whereas the same decision buried inside a request for eight full H3 prompts is the first
        /// thing that gets re-improvised per clip.
        ///
        /// <para>Returns the normalized block (<c>Character 1 wears …</c>, one line each), or an empty string
        /// if nothing usable came back — the caller then falls back to the old describe-it-per-clip rule.</para>
        /// </summary>
        /// <summary>
        /// The cast the wardrobe is written for: <b>both</b> slots, always — not just the ones whose photo is
        /// loaded.
        ///
        /// <para>The panel is worked top-down, so the story is typed and the wardrobe derived <i>before</i>
        /// anyone has browsed for a photo. Sizing the request by loaded photos therefore wrote one outfit for a
        /// story with two people in it, and the second character was silently never dressed. The outfits are a
        /// costume decision about the story, not about which files have been picked yet, so both are written
        /// up front and character 2's line is simply dropped — by
        /// <see cref="CastPromptStamp.Apply"/> — from any clip they are not in.</para>
        /// </summary>
        private IReadOnlyList<CharacterSlot> WardrobeCast => new[] { _character1, _character2 };

        /// <summary>The cast as the wardrobe text helpers want it — index and noun, no view model.</summary>
        private static IReadOnlyList<CastPromptStamp.CastRole> Roles(IEnumerable<CharacterSlot> cast) =>
            cast.Select(c => new CastPromptStamp.CastRole(c.Index, c.Noun)).ToList();

        /// <summary>
        /// Which characters the next pass has to dress. Everyone when the story, the scene image or the box
        /// itself has changed; otherwise only those with no outfit yet or whose <i>sex</i> has been switched
        /// since theirs was written — re-rolling an outfit that a character sheet has already been built in
        /// would invalidate that sheet for nothing.
        /// </summary>
        private IReadOnlyList<CharacterSlot> CharactersNeedingWardrobe()
        {
            if (!HasCastWardrobe || StorySourceStamp() != _wardrobeStoryStamp) return WardrobeCast;

            var wroteFor = _wardrobeCastStamp.Split(StampSeparator);
            return WardrobeCast.Where(c =>
                CastPromptStamp.OutfitFor(CastWardrobe, c.Index).Length == 0 ||
                wroteFor.Length < c.Index ||
                !string.Equals(wroteFor[c.Index - 1], c.Noun, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <param name="dress">The characters this pass writes outfits for. The others' outfits are quoted to
        /// the model as already decided, so a topped-up wardrobe still looks like one production.</param>
        private async Task<string> DeriveWardrobeAsync(
            string model, IReadOnlyList<CharacterSlot> dress, CancellationToken token)
        {
            if (dress.Count == 0) return string.Empty;

            const string systemPrompt =
                "You are a costume supervisor writing the wardrobe bible for a short film. You reply with " +
                "nothing but the wardrobe lines you were asked for — no preamble, no headings, no markdown, " +
                "no notes, no explanation.";

            // The sex is stated in the shape itself rather than left to the model's reading of the story: it is
            // what decides half of what a costume supervisor writes, and it is also what the cast card, the
            // sheet builder and the reference line are all working from, so all four have to agree.
            var shape = string.Join("\n", dress.Select(c => $"CHARACTER {c.Index} ({c.Noun}): <outfit>"));
            var who = dress.Count == 2
                ? $"both characters — Character 1 is a {dress[0].Noun}, Character 2 is a {dress[1].Noun}"
                : $"Character {dress[0].Index}, who is a {dress[0].Noun}";

            // What is already decided, quoted back so a topped-up outfit is designed against the one it will
            // share every frame with rather than beside it.
            var settled = WardrobeCast
                .Where(c => dress.All(d => d.Index != c.Index))
                .Select(c => (c, Outfit: CastPromptStamp.OutfitFor(CastWardrobe, c.Index)))
                .Where(x => x.Outfit.Length > 0)
                .Select(x => $"Character {x.c.Index} ({x.c.Noun}) is already dressed and must not be changed: {x.Outfit}")
                .ToList();
            var settledBlock = settled.Count == 0
                ? string.Empty
                : string.Join("\n", settled) + "\nDress the character(s) below to belong in the same production " +
                  "as that, without copying it and without writing a line for anyone already dressed.\n";

            var rules =
                settledBlock +
                $"Decide the outfit for {who} in this video. " +
                "Reply with exactly these lines and nothing else:\n" + shape + "\n\n" +
                "Each <outfit> is ONE sentence of at most 45 words naming every visible garment and worn item — " +
                "top, bottom or dress, outer layer, footwear, headwear, gloves, eyewear, jewellery, bag, belt — " +
                "each with its colour and its material. Write only clothing and worn accessories: no face, hair, " +
                "skin, build, age, name, pose, expression, background, weather or action. The outfit must be " +
                "practical for everything the character does in the story and must stay wearable from beginning " +
                "to end, because they will wear it in every shot of the finished video. " +
                // Both slots are dressed before anyone knows whether the second will be used, so the model is
                // told what to do when the story only has one person in it rather than left to refuse or to
                // repeat character 1's outfit.
                "Write a line for every character listed above even if the story features fewer people than " +
                "that — an unused outfit costs nothing, whereas a missing one leaves that character undressed.";

            string userMessage;
            if (HasSceneImage)
            {
                var story = HasStoryText
                    ? $"The story they act out:\n{StoryText.Trim()}\n"
                    : string.Empty;
                userMessage =
                    "Image role: REFERENCE ONLY — this is the SCENE the video is set in, and it is where the " +
                    "wardrobe comes from. Read the clothing off the people in it. If it shows no people, dress " +
                    "the cast in what the setting, period and situation plainly call for.\n" +
                    story + rules;
            }
            else
            {
                userMessage =
                    "There is no reference image. The story below is the only source: take the wardrobe from it " +
                    "where it describes clothing, and where it does not, dress the cast in what the period, " +
                    "place and situation plainly call for.\n" +
                    $"The story:\n{StoryText.Trim()}\n" +
                    rules;
            }

            var result = HasSceneImage
                ? await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model, SceneImagePath, userMessage, systemPrompt, maxTokens: 600, cancellationToken: token)
                : await _lmStudioService.SendTextChatAsync(
                    model, systemPrompt, userMessage, maxTokens: 600, cancellationToken: token);

            return CastPromptStamp.NormalizeWardrobe(CleanOutput(result), Roles(WardrobeCast), Roles(dress));
        }


        #endregion

        #region Clip chain (story mode)

        /// <summary>The header written between clips. The LLM is told to emit exactly this shape.</summary>
        private const string ClipHeaderFormat = "=== CLIP {0} of {1} ===";

        /// <summary>
        /// Matches a clip header on a line of its own. Deliberately loose about the decoration around it
        /// (<c>===</c>, <c>##</c>, <c>[CLIP 3]</c>, <c>Clip 3:</c> — small models produce all of them) but
        /// capped at 60 characters so a line of prompt body that happens to start with the word can never be
        /// mistaken for a header.
        /// </summary>
        private static readonly Regex ClipHeaderRegex = new(
            @"^[ \t]*[=#*\-–—\[]{0,6}[ \t]*CLIP[ \t]+(\d+)\b[^\r\n]{0,60}$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Splits a prompt chain into its individual clip prompts, headers removed. Text with no headers is
        /// one clip, so every caller can treat the single-clip case as a chain of length 1.
        /// </summary>
        protected static List<string> SplitClips(string? text)
        {
            // Normalized first: `$` in a .NET multiline match sits *before* the \n, so a CRLF header line
            // would never match — and the prompt box hands back CRLF the moment it is edited by hand.
            var t = (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();
            if (t.Length == 0) return new List<string>();

            var headers = ClipHeaderRegex.Matches(t);
            if (headers.Count == 0) return new List<string> { t };

            var clips = new List<string>();
            // Anything ahead of the first header is a preamble the model added; it belongs to clip 1.
            var preamble = t[..headers[0].Index].Trim();

            for (var i = 0; i < headers.Count; i++)
            {
                var start = headers[i].Index + headers[i].Length;
                var end = i + 1 < headers.Count ? headers[i + 1].Index : t.Length;
                var body = t[start..end].Trim();

                if (i == 0 && preamble.Length > 0)
                    body = body.Length > 0 ? $"{preamble}\n\n{body}" : preamble;

                if (body.Length > 0) clips.Add(body);
            }

            return clips.Count > 0 ? clips : new List<string> { t };
        }

        /// <summary>Reassembles clip prompts into one chain. A single clip is returned bare — headers only
        /// appear once there is actually a sequence.</summary>
        protected static string JoinClips(IReadOnlyList<string> clips)
        {
            if (clips.Count == 0) return string.Empty;
            if (clips.Count == 1) return clips[0].Trim();

            var sb = new StringBuilder();
            for (var i = 0; i < clips.Count; i++)
            {
                if (i > 0) sb.Append("\n\n");
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
                    ClipHeaderFormat, i + 1, clips.Count);
                sb.Append("\n\n").Append(clips[i].Trim());
            }
            return sb.ToString();
        }

        /// <summary>Chain-aware <see cref="ApplyReferenceLine"/> — every clip needs its own reference line and
        /// its own copy of the wardrobe, because every clip is submitted to H3 as a separate prompt with the
        /// same references attached and no memory of the clip before it. A chain of more than one clip gets the
        /// selective cast: a beat nobody's second character appears in does not carry their photographs.</summary>
        protected static string ApplyReferenceLineToChain(
            string prompt, int panels1, int panels2, string? wardrobe, CastPromptStamp.CastInfo cast)
        {
            var clips = SplitClips(prompt);
            if (clips.Count == 0) return string.Empty;
            var selective = clips.Count > 1;
            return JoinClips(clips.Select(c => CastPromptStamp.Apply(c, panels1, panels2, wardrobe, selective, cast))
                                  .Where(c => c.Length > 0).ToList());
        }

        /// <summary>
        /// Wearable and hairstyle words, used only by <see cref="DescribeWardrobeDrift"/> to spot a chain
        /// whose clips dress the cast differently. Deliberately nouns, not colours — lighting colours
        /// legitimately change from beat to beat.
        /// </summary>
        private static readonly string[] AppearanceTerms =
        {
            "dress", "gown", "skirt", "blouse", "shirt", "t-shirt", "tee", "jacket", "coat", "trenchcoat",
            "hoodie", "sweater", "cardigan", "jumper", "vest", "waistcoat", "blazer", "suit", "uniform",
            "robe", "cloak", "cape", "armor", "armour", "kimono", "leggings", "jeans", "trousers", "pants",
            "shorts", "boots", "heels", "sneakers", "shoes", "sandals", "gloves", "scarf", "hat", "cap",
            "helmet", "mask", "goggles", "glasses", "necklace", "earrings", "bracelet", "belt", "apron",
            "ponytail", "braid", "braids", "bun", "bangs", "fringe", "dreadlocks", "blonde", "brunette",
            "redhead", "bearded", "freckles",
        };

        /// <summary>
        /// Flags wardrobe drift across a chain: an appearance word that appears in some clips but not all.
        /// Returns null when there is nothing to report.
        ///
        /// <para>Each clip is written as its own block and rendered separately, so a wardrobe re-phrased in
        /// clip 4 is a wardrobe <i>changed</i> in clip 4 — and that only becomes visible after minutes of GPU
        /// time per clip. Only <b>inconsistent</b> terms are reported, which is what keeps the check quiet.</para>
        /// </summary>
        protected static string? DescribeWardrobeDrift(IReadOnlyList<string> clips)
        {
            if (clips.Count < 2) return null;

            var perClip = clips
                .Select(c => new HashSet<string>(
                    AppearanceTerms.Where(t => Regex.IsMatch(c, $@"\b{Regex.Escape(t)}\b", RegexOptions.IgnoreCase)),
                    StringComparer.OrdinalIgnoreCase))
                .ToList();

            var inconsistent = perClip
                .SelectMany(s => s)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => perClip.Count(s => s.Contains(t)) != clips.Count)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (inconsistent.Count == 0) return null;

            var shown = string.Join(", ", inconsistent.Take(8));
            var more = inconsistent.Count > 8 ? $" (+{inconsistent.Count - 8} more)" : string.Empty;
            return $"the clips do not agree on: {shown}{more}";
        }

        #endregion

        #region Analysis helpers

        /// <summary>Counts `[Shot n]` markers, purely for the log line.</summary>
        protected static int CountShots(string prompt)
        {
            var count = 0;
            var idx = prompt.IndexOf("[Shot ", StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                count++;
                idx = prompt.IndexOf("[Shot ", idx + 6, StringComparison.OrdinalIgnoreCase);
            }
            return count;
        }

        #endregion

        #region Queue

        public ObservableCollection<H3CastQueueItem> Queue => _queue;

        public bool HasQueueItems => _queue.Count > 0;
        public bool HasPendingItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Pending);
        public bool HasFailedItems => _queue.Any(x => x.ItemStatus == QueueItemStatus.Failed);

        /// <summary>True while the drain loop is alive — one ComfyUI submission at a time.</summary>
        public bool IsProcessingQueue
        {
            get => _isProcessingQueue;
            protected set
            {
                if (_isProcessingQueue == value) return;
                _isProcessingQueue = value;
                OnPropertyChanged();
                OnCanExecuteChanged();
            }
        }

        public string QueueStatus
        {
            get => _queueStatus;
            private set { if (_queueStatus != value) { _queueStatus = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Freezes the form into queue items and starts the drain loop if it is not already running. The
        /// sheets are already built by this point, so nothing here waits on the GPU and the tab stays usable
        /// the moment the button is hit.
        ///
        /// <para>The prompt box — not the duration slider — decides how many items are queued: it is split on
        /// its <c>=== CLIP n of N ===</c> headers and each clip becomes one job, so a chain that has been
        /// hand-edited (a clip deleted, a header added) queues exactly what is on screen. The clips are added
        /// in order and the drain loop always takes the first Pending item, so they render in story order,
        /// against the same character sheets.</para>
        /// </summary>
        /// <summary>Virtual so a derived tab can act on a queue-add — the 🧪 H3 Experimental fork files the
        /// chain in its prompt library there, which is where a hand-edited chain gets caught.</summary>
        protected virtual void AddToQueue()
        {
            if (!CanGenerate) return;

            var clips = SplitClips(Prompt);
            if (clips.Count == 0) return;

            // Shared by every clip of one story: groups the output files and labels the queue rows.
            var storyId = clips.Count > 1
                ? $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}"[..20]
                : string.Empty;

            // One seed for the whole chain rather than a fresh one per clip. The prompts differ, so the clips
            // still differ; what a shared seed removes is the per-clip re-roll of the noise every other frame
            // in H3 is built on, which is one more thing that made the cast look subtly re-cast between beats.
            // An explicit seed is honoured as before, and a lone clip keeps -1 so it rolls at submit time.
            var storySeed = Seed >= 0 || clips.Count == 1
                ? Seed
                : System.Random.Shared.NextInt64(0, long.MaxValue);

            for (var i = 0; i < clips.Count; i++)
            {
                var item = new H3CastQueueItem
                {
                    Character1SheetPath = _character1.SheetPath,
                    Character2SheetPath = HasCharacter2 ? _character2.SheetPath : string.Empty,
                    Character1SourcePath = _character1.SourcePath,
                    Character2SourcePath = HasCharacter2 ? _character2.SourcePath : string.Empty,
                    // The panels, not the sheets, are what gets uploaded — frozen here beside the prompt whose
                    // @tags name them one for one, so the two can never disagree at submit time.
                    Character1PanelPaths = _character1.PanelPaths.ToList(),
                    Character2PanelPaths = HasCharacter2 ? _character2.PanelPaths.ToList() : new List<string>(),
                    SceneImagePath = HasSceneImage ? SceneImagePath : string.Empty,
                    // Baked in now: the reference line has to name this item's cast and the wardrobe block has
                    // to be the one on screen when it was queued, not whichever characters and outfits happen
                    // to be loaded when the item eventually runs. Re-stamped rather than trusted, so editing
                    // the wardrobe box and re-queueing an unchanged chain is enough to redress every clip.
                    Prompt = CastPromptStamp.Apply(clips[i], Panels1, Panels2, CastWardrobe,
                                                   clips.Count > 1, CastDescriptor),
                    AspectRatio = ResolvedAspectRatio,
                    Megapixels = Megapixels,
                    LengthSeconds = ClampLength(LengthSeconds),
                    Seed = storySeed,
                    FaceRefine = FaceRefine,
                    RefineDenoise = RefineDenoise,
                    RefineBlend = RefineBlend,
                    RefineNoDownscale = RefineNoDownscale,
                    UseSamFaceMask = UseSamFaceMask,
                    // The refine passes rebuild a reference line of their own, and it names the cast in the
                    // same words this clip's prompt just did.
                    PerCharacterRefine = true,
                    Sex1 = _character1.Noun,
                    Sex2 = HasCharacter2 ? _character2.Noun : string.Empty,
                    SheetsShowWardrobe = SheetsShowWardrobe,
                    RtxUpscale = RtxUpscale,
                    UseLatentUpscale = UseLatentUpscale,
                    UseSla = UseSla,
                    SlaSparsity = SlaSparsity,
                    UseSparseAttention = UseSparseAttention,
                    MaxFidelityReferences = MaxFidelityReferences,
                    UseAudioEnhancement = UseAudioEnhancement,
                    StoryId = storyId,
                    ClipIndex = i + 1,
                    ClipCount = clips.Count,
                    ItemStatus = QueueItemStatus.Pending,
                };

                _queue.Add(item);
                AddLog($"Queued: {item.DisplayText}");
            }

            var total = Panels1 + Panels2;
            AddLog(total > (Panels2 > 0 ? 2 : 1)
                ? $"References: {total} panel images — Character 1 is {CastPromptStamp.DescribeAliases(1, Panels1)}" +
                  (Panels2 > 0 ? $", Character 2 is {CastPromptStamp.DescribeAliases(2, Panels2)}" : string.Empty) +
                  ". The sheets are never sent whole, so there is no panel layout for H3 to copy."
                : "References: sheets sent whole, one per character. If the panel layout appears in the video, " +
                  "set each character's split to Auto (or 3) and re-queue.");

            if (clips.Count > 1)
            {
                var soloed = _queue.Where(q => q.StoryId == storyId && !CastPromptStamp.IncludesCharacter2(q.Prompt, HasCharacter2))
                                   .Select(q => q.ClipIndex).ToList();
                if (Panels2 > 0 && soloed.Count > 0)
                    AddLog($"Character 2 is not in clip(s) {string.Join(", ", soloed)}, so their " +
                           $"{Panels2} reference(s) are left out of those prompts entirely.");
            }

            SaveQueueToFile();

            // Said once per queue-add, whether it is one clip or twelve: a sheet that does not show the locked
            // outfit is the one remaining way for the wardrobe to drift, because the pictures and the prompt
            // then disagree and H3 settles it per clip.
            if (HasCastWardrobe)
            {
                var stale = LoadedCharacters.Where(c => !c.SheetMatchesWardrobe).ToList();
                AddLog(stale.Count == 0
                    ? "Wardrobe: the character sheets show the locked outfits, so the references and the prompts " +
                      "agree on the clothes."
                    : $"WARNING: character {string.Join(" and ", stale.Select(c => c.Index))}'s sheet does not " +
                      "show the locked wardrobe (it was built earlier, or loaded as-is). The prompt says one " +
                      "thing and the reference photograph shows another, which is where costume drift comes " +
                      "from — rebuild the sheets and re-queue.");
            }

            if (clips.Count > 1)
            {
                AddLog($"Story queued: {clips.Count} clips × {ClampLength(LengthSeconds):0.#}s " +
                       $"→ {clips.Count * ClampLength(LengthSeconds):0.#}s of video, rendered one at a time " +
                       $"and joined when the last one lands. All {clips.Count} share seed {storySeed}.");
                AddLog(HasCastWardrobe
                    ? $"Wardrobe stamped into all {clips.Count} clips:\n{CastWardrobe.Trim()}"
                    : "WARNING: no wardrobe is locked, so each clip dresses the cast from its own description " +
                      "— that is what makes them change clothes between clips. Press 🎽 Derive and re-queue.");
            }
            UpdateQueueStatus();

            // Queueing stages the job; it does not start it. Add to Queue and Generate are separate
            // buttons so a run can be built up prompt by prompt and then rendered in one pass — the
            // GPU is only claimed when ▶ Generate (StartQueueCommand) is pressed.
            AddLog(IsProcessingQueue
                ? "Added to the queue — the queue is already running, so this is picked up when the item " +
                  "on the GPU finishes."
                : "Added to the queue — nothing is rendering yet. Press ▶ Generate to start.");
        }

        private void RemoveQueueItem(H3CastQueueItem? item)
        {
            // A Processing item is mid-submission; removing it would orphan the run, not stop it.
            if (item == null || item.ItemStatus == QueueItemStatus.Processing) return;
            _queue.Remove(item);
            SaveQueueToFile();
            UpdateQueueStatus();
        }

        private void ClearQueue()
        {
            _queueCts?.Cancel();
            _queue.Clear();
            SaveQueueToFile();
            UpdateQueueStatus();
            AddLog("Queue cleared");
        }

        private void StopQueue() => _queueCts?.Cancel();

        /// <summary>Stops whichever half of the tab is on the GPU — the sheet builder or the queue.</summary>
        private void CancelEverything()
        {
            _luckyCts?.Cancel();
            _sheetCts?.Cancel();
            _queueCts?.Cancel();
            // Including the wardrobe pass the story watcher may have queued behind the user's back.
            _wardrobeCts?.Cancel();
        }

        // ── 🍀 I'm Feeling Lucky ────────────────────────────────────────────────────────────────────

        private CancellationTokenSource? _luckyCts;
        private bool _isFeelingLucky;
        private string _luckyPhase = string.Empty;

        /// <summary>True while the whole chain is running itself. Its own flag rather than a reuse of
        /// <see cref="IsBuildingSheets"/> or <see cref="IsProcessingQueue"/>, because it spans both and
        /// outlives each — the button has to stay disabled between the steps, not flicker back on.</summary>
        public bool IsFeelingLucky
        {
            get => _isFeelingLucky;
            private set
            {
                if (_isFeelingLucky == value) return;
                _isFeelingLucky = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(LuckyButtonText));
                OnCanExecuteChanged();
            }
        }

        /// <summary>Which of the six steps is running, for the line under the button.</summary>
        public string LuckyPhase
        {
            get => _luckyPhase;
            private set { if (_luckyPhase == value) return; _luckyPhase = value; OnPropertyChanged(); }
        }

        public string LuckyButtonText => IsFeelingLucky ? "🍀 Running…" : "🍀 I'm Feeling Lucky";

        /// <summary>A story is all it needs; everything else it makes for itself.</summary>
        public bool CanFeelLucky =>
            (HasStoryText || HasSceneImage) && !IsFeelingLucky && !IsBuildingSheets &&
            !IsProcessingQueue && !IsWritingPrompt;

        /// <summary>
        /// Last chance for a fork to put the queue into unattended mode before 🍀 starts it. The base queue
        /// renders each item to completion by itself and needs nothing; the hunt-board tabs stop at a board
        /// full of takes unless auto-pick and auto-finish are on, so <see cref="H3ErosViewModel"/> overrides
        /// this and turns them on.
        /// </summary>
        protected virtual void PrepareForUnattendedRun() { }

        /// <summary>
        /// The whole pipeline, once, from a story: read the cast out of it, dress them, photograph them,
        /// build their sheets, write the clips, queue them, render them, pick the takes and join the film.
        /// Six model-and-GPU steps that are otherwise six buttons pressed in order with waiting in between.
        ///
        /// <para><b>It is a sequence, not a second pipeline.</b> Every step is the same method its own
        /// button calls, in the order the tab already implies, so there is one implementation of each and no
        /// copy to drift. What 🍀 adds is the waiting: the cast and wardrobe passes are normally
        /// <i>debounced</i> — they fire 2.5 s after typing stops — and a run that photographs its characters
        /// cannot hope a debounce has landed. So it forces both and awaits them
        /// (<see cref="DeriveCastNowAsync"/>, <see cref="EnsureWardrobeAsync"/>) before anything renders.
        /// A card with no Part on it yet photographs nobody in particular, and the whole film is then built
        /// on that.</para>
        ///
        /// <para><b>What it will not do.</b> It never overwrites work already there: a card with a photo
        /// keeps it, a character whose sheet already matches the locked wardrobe is not re-sheeted (which is
        /// what makes the sheet library worth having), and it stops rather than guessing when a step comes
        /// back empty. Everything it does make goes through the ordinary setters, so the cards, the queue and
        /// the board end up exactly as they would had the buttons been pressed by hand.</para>
        ///
        /// <para>✕ Cancel stops it between steps, and inside any step that was already cancellable.</para>
        /// </summary>
        protected async Task FeelLuckyAsync()
        {
            if (!CanFeelLucky) return;

            _luckyCts?.Dispose();
            _luckyCts = new CancellationTokenSource();
            var token = _luckyCts.Token;

            IsFeelingLucky = true;
            var started = DateTime.Now;
            try
            {
                AddLog("=== 🍀 I'm Feeling Lucky: cast → wardrobe → photos → sheets → clips → render → join ===");

                // 1. Who is in this story. Forced rather than waited for; see the remarks.
                LuckyPhase = "1/6 · Reading the cast out of the story…";
                await DeriveCastNowAsync(token, quiet: false);
                token.ThrowIfCancellationRequested();
                if (!_character1.IsCast)
                {
                    AddLog("🍀 stopped: no character could be read out of the story, and card 1 is empty. " +
                           "Write a Part on it, or load a photo, and press 🍀 again.");
                    return;
                }

                // 2. What they wear. Before the photos, because the portrait is generated wearing it.
                LuckyPhase = "2/6 · Deciding the wardrobe…";
                await EnsureWardrobeAsync(token);
                token.ThrowIfCancellationRequested();

                // 3. Their photographs — only for the cards that have none.
                var toShoot = new[] { _character1, _character2 }
                    .Where(c => c.IsCast && !c.HasSource).ToList();
                if (toShoot.Count == 0)
                    AddLog("🍀 the cast already have their photos — keeping them.");
                for (var i = 0; i < toShoot.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    LuckyPhase = $"3/6 · Photographing character {toShoot[i].Index}" +
                                 (toShoot.Count > 1 ? $" ({i + 1}/{toShoot.Count})" : string.Empty) + "…";
                    await GenerateCastPhotoAsync(toShoot[i], LuckyPhotoEngine);
                }

                // 4. Their sheets. onlyMissing, so a sheet the library handed back is not paid for twice.
                token.ThrowIfCancellationRequested();
                LuckyPhase = "4/6 · Building the character sheets…";
                await BuildSheetsAsync(onlyMissing: true);
                token.ThrowIfCancellationRequested();
                if (!AllSheetsReady)
                {
                    AddLog("🍀 stopped after the sheets: not every character has one, so the clips would be " +
                           "written against a cast that is not all there.");
                    return;
                }

                // 5. The clips, and the queue.
                LuckyPhase = "5/6 · Writing the clips…";
                await AnalyzeAsync();
                token.ThrowIfCancellationRequested();
                if (!CanGenerate)
                {
                    AddLog("🍀 stopped after Analyze: no prompt was written, so there is nothing to queue.");
                    return;
                }
                AddToQueue();
                if (!HasPendingItems)
                {
                    AddLog("🍀 stopped: nothing reached the queue.");
                    return;
                }

                // 6. Render it. PrepareForUnattendedRun is what stops a hunt-board tab parking at the board.
                LuckyPhase = $"6/6 · {LuckyRenderPhase}";
                PrepareForUnattendedRun();
                await ProcessQueueAsync();

                LuckyPhase = $"Done in {(DateTime.Now - started).TotalMinutes:0.#} min.";
                AddLog($"=== 🍀 finished in {(DateTime.Now - started).TotalMinutes:0.#} min ===");
            }
            catch (OperationCanceledException)
            {
                LuckyPhase = "Cancelled.";
                AddLog("🍀 cancelled.");
            }
            catch (Exception ex)
            {
                LuckyPhase = $"Stopped: {ex.Message}";
                AddLog($"🍀 stopped: {ex.Message}");
            }
            finally
            {
                IsFeelingLucky = false;
                _luckyCts?.Dispose();
                _luckyCts = null;
                OnCanExecuteChanged();
            }
        }

        /// <summary>Which Image Generator base graph 🍀 photographs the cast with. Krea2-Spicy: its LoRAs
        /// are baked in, so there is nothing for an unattended run to have to choose.</summary>
        protected virtual string LuckyPhotoEngine => "krea2spicy";

        /// <summary>What 🍀's last step says it is doing. A tab with no takes to pick says so.</summary>
        protected virtual string LuckyRenderPhase => "Rendering, picking and joining…";

        private void ReprocessAllFailed()
        {
            var failed = _queue.Where(x => x.ItemStatus == QueueItemStatus.Failed).ToList();
            if (failed.Count == 0) return;
            foreach (var item in failed)
            {
                item.ItemStatus = QueueItemStatus.Pending;
                item.ErrorMessage = null;
            }
            UpdateQueueStatus();
            SaveQueueToFile();
            if (!IsProcessingQueue) _ = ProcessQueueAsync();
        }

        protected void UpdateQueueStatus()
        {
            var pending = _queue.Count(x => x.ItemStatus == QueueItemStatus.Pending);
            var running = _queue.Count(x => x.ItemStatus == QueueItemStatus.Processing);
            var done = _queue.Count(x => x.ItemStatus == QueueItemStatus.Completed);
            var failed = _queue.Count(x => x.ItemStatus == QueueItemStatus.Failed);
            QueueStatus = _queue.Count == 0
                ? string.Empty
                : $"{pending} pending • {running} running • {done} done • {failed} failed";

            OnPropertyChanged(nameof(HasPendingItems));
            OnPropertyChanged(nameof(HasFailedItems));
            OnCanExecuteChanged();
        }

        /// <summary>
        /// Drains pending items one at a time. The workflow-coordinator lease is taken <b>per item</b> rather
        /// than around the loop, so a long queue does not lock every other tab out of ComfyUI for its whole
        /// run — and items added mid-drain are picked up on the next pass.
        /// </summary>
        protected virtual async Task ProcessQueueAsync()
        {
            if (IsProcessingQueue) return;

            IsProcessingQueue = true;
            _queueCts?.Dispose();
            _queueCts = new CancellationTokenSource();
            var token = _queueCts.Token;

            AddLog("Starting H3 Cast queue...");
            try
            {
                H3CastQueueItem? item;
                while (!token.IsCancellationRequested &&
                       (item = _queue.FirstOrDefault(x => x.ItemStatus == QueueItemStatus.Pending)) != null)
                {
                    item.ItemStatus = QueueItemStatus.Processing;
                    item.StartedAt = DateTime.Now;
                    UpdateQueueStatus();
                    SaveQueueToFile();

                    try
                    {
                        await GenerateItemAsync(item, token);
                        item.ItemStatus = QueueItemStatus.Completed;
                        item.CompletedAt = DateTime.Now;
                        AddLog($"Completed: {item.DisplayText}");
                        // Never throws — a join problem must not push a rendered clip back to Pending.
                        await CompleteStoryAsync(item, token);
                    }
                    catch (OperationCanceledException)
                    {
                        item.ItemStatus = QueueItemStatus.Pending;
                        AddLog("Queue stopped — the current item is back to Pending.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (await TryHandleCrashAndRetryAsync(item, ex))
                        {
                            item.ItemStatus = QueueItemStatus.Pending;
                            AddLog("Item reset to Pending — will retry after ComfyUI restart");
                        }
                        else
                        {
                            item.ItemStatus = QueueItemStatus.Failed;
                            item.ErrorMessage = ex.Message;
                            AddLog($"FAILED: {ex.Message}");
                        }
                    }

                    UpdateQueueStatus();
                    SaveQueueToFile();
                }
            }
            finally
            {
                IsProcessingQueue = false;
                ProcessingStatus = token.IsCancellationRequested ? "Queue stopped" : "Queue finished";
                AddLog("Queue processing finished.");
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Runs once the <i>last</i> clip of a chain lands: announces the finished story, then FFmpeg-joins
        /// its clips, in <see cref="H3CastQueueItem.ClipIndex"/> order, into one continuous video.
        ///
        /// <para>Deliberately exception-free. It is called from the drain loop straight after an item has
        /// been marked Completed, and that loop's catch would otherwise read a join failure as a render
        /// failure and push an already-rendered clip back to Pending.</para>
        /// </summary>
        protected async Task CompleteStoryAsync(H3CastQueueItem finished, CancellationToken token)
        {
            try
            {
                if (!finished.IsStoryClip || string.IsNullOrEmpty(finished.StoryId)) return;

                var siblings = _queue.Where(x => x.StoryId == finished.StoryId)
                                     .OrderBy(x => x.ClipIndex)
                                     .ToList();

                if (siblings.Any(x => x.ItemStatus != QueueItemStatus.Completed))
                {
                    // Nothing left running means the chain is stuck on a failure rather than still
                    // rendering — say so, otherwise the missing joined file looks like a silent bug.
                    var stalled = siblings.Count(x => x.ItemStatus == QueueItemStatus.Failed);
                    if (stalled > 0 && !siblings.Any(x => x.ItemStatus is QueueItemStatus.Pending or QueueItemStatus.Processing))
                        AddLog($"Story not joined: {stalled} of {siblings.Count} clips failed. " +
                               "Retry them and the join runs when the last one lands.");
                    return;
                }

                var total = siblings.Sum(x => ClampLength(x.LengthSeconds));
                AddLog($"=== Story complete: {siblings.Count} clips, {total:0.#}s total ===");
                foreach (var clip in siblings)
                    AddLog($"  clip {clip.ClipIndex}/{clip.ClipCount}: {clip.OutputVideoPath}");

                await JoinStoryAsync(finished.StoryId, siblings, token);
            }
            catch (Exception ex)
            {
                // Includes cancellation: the clips themselves are already on disk either way.
                AddLog($"Story join skipped: {ex.Message}");
            }
        }

        /// <summary>
        /// Concatenates a finished chain's clips into one MP4 next to them, and makes it the tab's current
        /// result so ▶ Play opens the whole story rather than its last beat. Best-effort — the individual
        /// clips are untouched and remain usable if the join cannot run.
        /// </summary>
        protected async Task JoinStoryAsync(string storyId, IReadOnlyList<H3CastQueueItem> clips,
            CancellationToken token)
        {
            var paths = clips.Select(c => c.OutputVideoPath)
                             .Where(p => !string.IsNullOrEmpty(p) && File.Exists(p))
                             .Select(p => p!)
                             .ToList();

            if (paths.Count < clips.Count)
                AddLog($"Join: {clips.Count - paths.Count} clip file(s) are missing from disk and are left out.");
            if (paths.Count < 2)
            {
                AddLog("Join skipped: fewer than two clip files are available.");
                return;
            }

            var ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg))
            {
                AddLog("Join skipped: FFmpeg not found. The clips are separate files, in playback order.");
                return;
            }

            // Alongside the clips, which already share this stem — the joined file sorts with them.
            var outputDir = Path.GetDirectoryName(paths[0])
                            ?? Path.Combine(_settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3Cast");
            Directory.CreateDirectory(outputDir);

            // Re-running a chain (after retrying a failed clip) re-joins the same story from the same clips,
            // so overwriting this file is a refresh, not a loss.
            var joinedPath = Path.Combine(outputDir, $"{OutputFileStem}_{storyId}_joined{OutputFileSuffix}.mp4");
            var total = clips.Sum(c => ClampLength(c.LengthSeconds));

            ProcessingStatus = $"Joining {paths.Count} clips...";
            AddLog($"Joining {paths.Count} clips with FFmpeg → {Path.GetFileName(joinedPath)}");
            await ConcatClipsAsync(ffmpeg, paths, joinedPath, token);

            if (!File.Exists(joinedPath) || new FileInfo(joinedPath).Length == 0)
            {
                AddLog("Join produced no file — the individual clips are unaffected.");
                return;
            }

            await LocalCopyService.CopyVideoAsync(joinedPath);

            var fi = new FileInfo(joinedPath);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = joinedPath;
                ResultVideoInfo = $"{OutputFileStem} • joined story • {paths.Count} clips • {total:0.#}s • " +
                                  $"{fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            ProcessingStatus = "Clips joined!";
            AddLog($"=== Joined video complete: {joinedPath} ===");
        }

        /// <summary>
        /// FFmpeg concat-demuxer join. Every clip of a chain comes out of the same graph at the same
        /// resolution and frame rate, but it is re-encoded rather than stream-copied for the same reason the
        /// other H3 tabs do it: H3 writes an audio track per clip, and a copy-mode concat of separately
        /// encoded H3 outputs is where the timestamp and codec-parameter edge cases live. veryfast/CRF 18 is
        /// visually lossless and costs seconds on clips this short.
        /// </summary>
        private async Task ConcatClipsAsync(string ffmpeg, IReadOnlyList<string> clips, string outPath,
            CancellationToken token)
        {
            var listPath = Path.Combine(Path.GetTempPath(), $"h3cast_concat_{Guid.NewGuid():N}.txt");
            var sb = new StringBuilder();
            foreach (var clip in clips)
            {
                // The concat demuxer reads a backslash as an escape and a single quote as the delimiter.
                sb.AppendLine($"file '{clip.Replace("\\", "/").Replace("'", @"'\''")}'");
            }
            await File.WriteAllTextAsync(listPath, sb.ToString(), token);

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpeg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                foreach (var a in new[]
                {
                    "-y", "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "192k", "-pix_fmt", "yuv420p", outPath
                }) psi.ArgumentList.Add(a);

                using var p = System.Diagnostics.Process.Start(psi)
                              ?? throw new Exception("Failed to start FFmpeg.");
                // stderr is drained before the wait: FFmpeg logs everything there and blocks once the pipe
                // buffer fills, which would otherwise hang the join.
                var stderr = await p.StandardError.ReadToEndAsync(token);
                await p.WaitForExitAsync(token);
                if (p.ExitCode != 0)
                {
                    var tail = stderr.Length <= 600 ? stderr : stderr[^600..];
                    throw new Exception($"FFmpeg exited {p.ExitCode}: {tail}");
                }
            }
            finally
            {
                try { File.Delete(listPath); } catch { /* temp file: best effort */ }
            }
        }

        #endregion

        #region Queue persistence

        protected void SaveQueueToFile()
        {
            try
            {
                var dir = Path.GetDirectoryName(QueueFilePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Completed items are session history, not pending work — keeping them out stops the queue
                // file (and therefore startup) from growing without bound.
                var pending = _queue.Where(q => q.ItemStatus != QueueItemStatus.Completed).ToList();
                File.WriteAllText(QueueFilePath,
                    JsonSerializer.Serialize(pending, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { AddLog($"Error saving queue: {ex.Message}"); }
        }

        /// <summary>
        /// Defers the persisted queue read to Background dispatcher priority, with the file I/O itself on a
        /// worker thread — this view model is built during app startup and must not do disk work in its
        /// constructor.
        /// </summary>
        private void ScheduleQueueLoad()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                _ = LoadQueueFromFileAsync();
                return;
            }

            dispatcher.InvokeAsync(async () => await LoadQueueFromFileAsync(),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>
        /// Last chance to fix up an item read back from disk, before anything else sees it. Does nothing
        /// here; 🥽🎯 H3 VR uses it to migrate prompts written before its stereo scene rule existed.
        ///
        /// <para>A prompt rewritten here is deliberately allowed to disagree with the item's
        /// <see cref="H3CastQueueItem.HuntPromptStamp"/>, which is what marks an already-hunted clip stale —
        /// the drafts on its board were sampled from the old text, and finishing one would deliver a video
        /// nobody has seen. Staleness is the correct outcome of a migration, not a side effect of it.</para>
        /// </summary>
        protected virtual void OnQueueItemLoaded(H3CastQueueItem item) { }

        private async Task LoadQueueFromFileAsync()
        {
            try
            {
                if (!File.Exists(QueueFilePath)) return;

                var items = await Task.Run(() =>
                    JsonSerializer.Deserialize<List<H3CastQueueItem>>(File.ReadAllText(QueueFilePath)));
                if (items == null || items.Count == 0) return;

                _queue.Clear();
                foreach (var item in items)
                {
                    if (item.ItemStatus == QueueItemStatus.Completed) continue;
                    // Anything left mid-flight by a crash or a close is unfinished work, not a running job.
                    if (item.ItemStatus == QueueItemStatus.Processing) item.ItemStatus = QueueItemStatus.Pending;
                    OnQueueItemLoaded(item);
                    _queue.Add(item);
                }

                UpdateQueueStatus();
                // Deliberately not auto-started: a leftover queue should not seize the GPU the moment the app
                // opens. The ▶ Generate button picks it back up.
                if (HasPendingItems)
                    AddLog($"Queue restored: {_queue.Count} items ({_queue.Count(x => x.ItemStatus == QueueItemStatus.Pending)} pending) — press ▶ Generate to resume.");
                else if (_queue.Count > 0)
                    AddLog($"Queue restored: {_queue.Count} items");
            }
            catch (Exception ex) { AddLog($"Error loading queue: {ex.Message}"); }
        }

        #endregion

        #region Generation

        /// <summary>The render itself. Virtual because the whole point of <see cref="H3DuoViewModel"/> is
        /// this method: the same queued <see cref="H3CastQueueItem"/>, driven through a different graph.</summary>
        protected virtual async Task GenerateItemAsync(H3CastQueueItem item, CancellationToken token)
        {
            IsProcessing = true;
            HasResult = false;
            ResultVideoPath = string.Empty;
            ResultVideoInfo = string.Empty;
            ProcessingProgress = 0;
            ProcessingStatus = "Preparing H3 Cast workflow...";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                var clipLabel = item.IsStoryClip ? $", clip {item.ClipIndex}/{item.ClipCount}" : string.Empty;
                AddLog($"=== H3 Cast ({(item.HasCharacter2 ? "2 sheets" : "1 sheet")}{clipLabel}, " +
                       $"{(item.FaceRefine ? $"face refine {item.RefineDenoise:0.00}" : "no face refine")}) ===");
                AddLog("Waiting for other workflows to finish...");
                lease = await _workflowCoordinator.AcquireAsync("H3Cast", token);

                ProcessingStatus = "Checking ComfyUI...";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    ProcessingStatus = "Connecting to ComfyUI...";
                    await _comfyUIService.ConnectAsync();
                }

                var json = await LoadFileAsync(WorkflowFileName, token);

                ProcessingStatus = "Uploading character references...";
                ProcessingProgress = 5;

                // One reference per view, never the assembled sheet: H3 conditions on each ref_image as a
                // single subject, so a collage handed in whole is a collage it will happily render.
                var panels1 = ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1);

                // A story clip's prompt was stamped for the cast that clip actually uses, so a character it
                // never names is not uploaded, not wired and not encoded — theirs is a face H3 would otherwise
                // be told to keep in a scene it was never asked to put them in.
                var includesCharacter2 = CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2);
                var panels2 = includesCharacter2
                    ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2)
                    : (IReadOnlyList<string>)Array.Empty<string>();
                if (item.HasCharacter2 && !includesCharacter2)
                    AddLog("Character 2 is not named in this clip — their references are left out of it.");

                var uploadedRefs = new List<string>();
                foreach (var panel in panels1.Concat(panels2))
                    uploadedRefs.Add(await EnsureUploadedAsync(panel));

                json = WireReferenceImages(json, uploadedRefs, out var refLoaders);

                // The base pass only: the refine pass is a 0.45-denoise img2img over 768px face crops,
                // with no composition to settle cheaply and nothing to gain from drafting it.
                json = WireLatentUpscale(json, item.UseLatentUpscale, out var upscaleWired);
                if (item.UseLatentUpscale && !upscaleWired)
                    AddLog($"The latent upscale was requested but the workflow file no longer carries the " +
                           $"draft block (nodes {NodeDraftWidth}-{NodeLatentSwitch}) — sampling the base " +
                           "pass in one go at the full canvas instead.");

                // Before AddSecondRefinePass, which clones the model wire verbatim.
                json = WireAttention(json, item.UseSla, item.SlaSparsity, item.UseSparseAttention,
                                     out var slaWired);
                if (item.UseSla && !slaWired)
                    AddLog($"SLA was requested but the workflow file no longer carries nodes {NodeSlaBase} " +
                           $"and {NodeSlaRefine} — sampling with dense attention instead.");
                else if (item.UseSla)
                    AddLog($"SLA block-sparse attention at sparsity {item.SlaSparsity:0.00}, block " +
                           $"{SlaBlockSize} — on the base pass and on each face-refine pass. A short or " +
                           "low-resolution clip falls below the kernel's minimum sequence length and runs " +
                           "dense on its own.");
                AddLog(item.UseSparseAttention
                    ? "Sol-Attn is on as well as SLA — the two are separate approximations of the same "
                      + "attention and stacking them has not measured faster than SLA alone."
                    : "Sol-Attn off: node 53 is unwired and pruned.");

                var runSeed = item.Seed >= 0 ? item.Seed : System.Random.Shared.NextInt64(0, long.MaxValue);
                var len = ClampLength(item.LengthSeconds);
                var aspect = item.AspectRatio;
                var (canvasW, canvasH) = CanvasSize(aspect, item.Megapixels, item.UseLatentUpscale);
                var (upW, upH) = UpscaleSize(aspect, item.Megapixels, item.UseLatentUpscale);
                var (draftW, draftH) = DraftCanvas(aspect, item.Megapixels);
                var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                // Story clips carry their index in the run token so a chain's outputs sort in story order and
                // the disk-scan fallback can still tell two clips apart.
                var clipTag = item.IsStoryClip ? $"_c{item.ClipIndex:00}" : string.Empty;
                var runToken = $"h3cast_{ts}{clipTag}";

                json = EnsureInputPrimitives(json);

                // The prompt is stamped in @tags. Whether they survive to the server or are swapped back for
                // fixed picture numbers depends on what the server's MiniMaxH3-Contex-Loop can resolve.
                var aliases = CastPromptStamp.AllAliases(panels1.Count, panels2.Count);
                var numbered = CastPromptStamp.Detag(item.Prompt, panels1.Count, panels2.Count);
                var prompt = numbered;

                // A prompt stamped before this tab used tags has no aliases to activate, and under the tagged
                // node's strict policy "no aliases" means "no references at all" — so it stays on the numbered
                // path it was written for. Re-queue such an item to move it over.
                var tagged = CastPromptStamp.IsTagged(item.Prompt) && await SupportsTaggedReferencesAsync(token);
                if (tagged)
                {
                    json = ConvertToTaggedReferences(json, aliases);
                    prompt = item.Prompt;
                    AddLog($"References wired as tags: {string.Join(", ", aliases.Select(CastPromptStamp.Token))} — " +
                           "each picture reaches H3 only in the clips whose prompt names it.");
                }
                else
                {
                    AddLog($"References wired: {uploadedRefs.Count} image(s) as <Picture 1>–" +
                           $"<Picture {uploadedRefs.Count}>. ComfyUI's MiniMaxH3-Contex-Loop has no " +
                           $"{TaggedReferenceClass}; update that pack for prompt-driven references.");
                }

                // ── The face-refine pass, or one per character ─────────────────────────────────
                // Each pass tracks one subject, so a two-hander needs two: each conditioned on that
                // character's own panels, prompted with a cast of one, and tracked by their own face
                // close-up. An item queued before that existed keeps the single whole-cast pass, whose
                // prompt numbers every panel this clip sends.
                var cast = new CastPromptStamp.CastInfo(
                    string.IsNullOrEmpty(item.Sex1) ? null : item.Sex1,
                    string.IsNullOrEmpty(item.Sex2) ? null : item.Sex2,
                    item.SheetsShowWardrobe);
                var wardrobe = CastPromptStamp.ExtractWardrobe(item.Prompt);

                var perCharacterRefine = item.FaceRefine && item.PerCharacterRefine;
                var refineCharacter2 = perCharacterRefine && panels2.Count > 0;
                var refinePrompt1 = perCharacterRefine
                    ? CastPromptStamp.SoloRefinePrompt(item.Prompt, 1, panels1.Count, wardrobe, cast)
                    : numbered;
                var refinePrompt2 = refineCharacter2
                    ? CastPromptStamp.SoloRefinePrompt(item.Prompt, 2, panels2.Count, wardrobe, cast)
                    : string.Empty;

                if (item.FaceRefine)
                {
                    json = WireRefinePasses(json, refLoaders, panels1.Count, panels2.Count,
                                            perCharacterRefine, refineCharacter2);
                    SetInput(ref json, NodeRefinePrompt, "value", refinePrompt1);
                    if (refineCharacter2) SetInput(ref json, NodeRefinePrompt2, "value", refinePrompt2);
                    if (item.FaceRefine && !perCharacterRefine && panels2.Count > 0)
                        AddLog("Only one face is refined in this clip: it was queued before the per-character " +
                               "passes existed, so its refine prompt describes the whole cast. Re-queue the " +
                               "job to give each character their own pass.");
                }

                SetInput(ref json, NodePrompt, "value", prompt);
                SetInput(ref json, NodeResolution, "aspect_ratio", aspect);
                SetInput(ref json, NodeResolution, "megapixels", item.Megapixels);
                SetInput(ref json, NodeDuration, "value", len);
                SetInput(ref json, NodeSeed, "noise_seed", runSeed);
                SetInput(ref json, NodeVideoCombine, "frame_rate", OutputFrameRate);
                SetInput(ref json, NodeVideoCombine, "filename_prefix", $"{OutputSubfolder}/{runToken}");

                if (item.FaceRefine)
                {
                    SetInput(ref json, NodeRefineDenoise, "denoise", item.RefineDenoise);
                    SetInput(ref json, NodeFaceStitch, "blend", item.RefineBlend);
                    SetInput(ref json, NodeFaceTrack, "canvas_mode",
                             item.RefineNoDownscale ? CanvasModeNoDownscale : CanvasModeCapped);
                    // Its own noise, derived from the run seed so a re-run of the same item is reproducible.
                    SetInput(ref json, NodeRefineSeed, "noise_seed", (runSeed % (long.MaxValue - 1)) + 1);
                    AddLog(item.RefineNoDownscale
                        ? "Face refine canvas: uncapped — sized from the largest crop in the clip, so no " +
                          "frame is downscaled on the way in. Costs area squared on a close-up."
                        : "Face refine canvas: capped at 768. A crop bigger than that is downscaled on the " +
                          "way in, and that detail does not come back at the stitch — turn the cap off if " +
                          "close-up faces are losing texture.");
                    AddLog($"Face refine: character 1's crops re-generated at denoise {item.RefineDenoise:0.00} " +
                           $"against their own {panels1.Count} panel(s)" +
                           (perCharacterRefine ? ", tracked by their face close-up" : string.Empty) +
                           ", stage-1 audio locked, stitched back into the frames.");

                    if (refineCharacter2)
                    {
                        SetInput(ref json, NodeRefineDenoise2, "denoise", item.RefineDenoise);
                        SetInput(ref json, NodeFaceStitch2, "blend", item.RefineBlend);
                        SetInput(ref json, NodeFaceTrack2, "canvas_mode",
                                 item.RefineNoDownscale ? CanvasModeNoDownscale : CanvasModeCapped);
                        // A different seed: the two passes redraw different faces and would otherwise share
                        // the same noise on crops of the same size.
                        SetInput(ref json, NodeRefineSeed2, "noise_seed", (runSeed % (long.MaxValue - 2)) + 2);
                        AddLog($"Face refine: character 2 gets a second pass over character 1's stitched " +
                               $"frames, tracked by their own face close-up against their {panels2.Count} " +
                               "panel(s).");
                    }
                }
                else
                {
                    AddLog("Face refine off: the base H3 frames go straight to the video.");
                }


                // After WireRefinePasses, so the character-2 clone (211/291) already exists to be settled
                // alongside character 1's. Skipped entirely when the refine branch is not running.
                if (item.FaceRefine)
                {
                    json = WireFaceMask(json, item.UseSamFaceMask, refineCharacter2, out var maskWired);
                    if (item.UseSamFaceMask && !maskWired)
                        AddLog($"The SAM face mask was requested but the workflow file no longer carries " +
                               $"nodes {NodeSamLoader}/{NodeFaceMask} — compositing through the detected " +
                               "face box instead, which is what puts a moving rectangle over the face.");
                    else
                        AddLog(item.UseSamFaceMask
                            ? $"Face mask: SAM, feather {FeatherWithMask}px — the composite follows the jaw "
                              + "and hairline instead of the detected face box, so there is no rectangle to "
                              + "slide around as the box is re-detected each frame."
                            : $"Face mask: the detected face box, feather {FeatherWithBox}px. The box moves "
                              + "and resizes every frame, and the refined pixels inside it differ slightly "
                              + "from those outside — that boundary is what reads as a layer on the face.");
                }

                json = WireOutputChain(json, item.FaceRefine, refineCharacter2, item.RtxUpscale);

                var steps = ReadInt(json, NodeScheduler, "steps");
                json = PruneToOutputs(json, new[] { NodeVideoCombine }, out var prunedCount);
                if (prunedCount > 0)
                    AddLog($"Graph pruned to the video output: {prunedCount} disconnected node(s) removed.");

                var frameCount = FramesForSeconds(len);
                var finish = item.RtxUpscale
                    ? $"RTX ×{RtxScale:0.#} → ≈{upW}×{upH}"
                    : "no upscale";

                var latentUpscale = item.UseLatentUpscale && upscaleWired;
                var sampling = latentUpscale
                    ? $"4 draft steps at {draftW}×{draftH} → ×{LatentUpscaleFactor:0.#} → 3 finish steps"
                    : $"{steps} steps";

                ProcessingProgress = 10;
                ProcessingStatus = "Generating video...";
                AddLog($"Generating (seed {runSeed}, {len:0.#}s / {frameCount} frames @ {OutputFrameRate}fps, " +
                       $"{aspect} ≈{canvasW}×{canvasH}, {item.Megapixels:0.0} MP, {sampling}, {finish})...");
                AddLog(latentUpscale
                    ? $"Latent upscale on: the base pass settles the composition at {draftW}×{draftH}, the " +
                      $"MiniMax H3 3D upscaler doubles it, and three fixed-sigma steps finish at " +
                      $"{canvasW}×{canvasH}. The {uploadedRefs.Count} cast panel(s) are encoded at the " +
                      $"finished {canvasW}×{canvasH}, not at the draft. Node 57's own {steps}-step shifted " +
                      "schedule is not used in this mode."
                    : $"Latent upscale off: one {steps}-step pass at {canvasW}×{canvasH}.");

                // Said out loud before the wait rather than after the crash: every image node in this graph
                // holds the whole clip, so this is the number that decides whether the server survives.
                var peakGb = item.RtxUpscale
                    ? FrameStackGb(frameCount, upW, upH)
                    : FrameStackGb(frameCount, canvasW, canvasH);
                AddLog($"Peak frame stack ≈{peakGb:0.#} GB ({frameCount} frames held at once).");
                if (peakGb >= HeavyFrameStackGb)
                    AddLog("WARNING: that is large enough to take ComfyUI down mid-render — if this job dies " +
                           "with the prompt \"neither queued nor in the run history\", shorten the clip, drop " +
                           "to 0.7 MP, or turn RTX off here and upscale afterwards in ✨ Enhance Video.");

                var local = await SubmitAndRetrieveAsync(json, runToken, NodeVideoCombine, 10, 95, token);
                if (local == null || !File.Exists(local))
                    throw new Exception("No output video was generated.");

                var outputDir = Path.Combine(
                    _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), "H3Cast");
                Directory.CreateDirectory(outputDir);
                // One story's clips share a stem and differ only by index, so a finished chain sits together
                // in the folder in playback order — and the join writes its result beside them.
                var finalName = item.IsStoryClip
                    ? $"H3Cast_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                    : $"H3Cast_{ts}.mp4";
                var finalPath = Path.Combine(outputDir, finalName);
                File.Copy(local, finalPath, true);
                await LocalCopyService.CopyVideoAsync(finalPath);

                var fi = new FileInfo(finalPath);
                var refine = item.FaceRefine ? $"face refine {item.RefineDenoise:0.00}" : "no face refine";
                var size = item.RtxUpscale ? $"RTX ×{RtxScale:0.#} ≈{upW}×{upH}" : $"≈{canvasW}×{canvasH}";
                item.OutputVideoPath = finalPath;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    ResultVideoPath = finalPath;
                    ResultVideoInfo = $"H3 Cast • {(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                      $"{(item.HasCharacter2 ? "2 sheets" : "1 sheet")} • {refine} • " +
                                      $"turbo {steps}-step • {size} • {aspect} • " +
                                      $"{len:0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                    HasResult = true;
                    OnCanExecuteChanged();
                });
                ProcessingProgress = 100;
                ProcessingStatus = "Complete!";
                AddLog($"=== Complete: {finalPath} ===");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The queue loop decides whether this is a retry or a failure; it just needs the reason.
                AddLog($"ERROR: {ex.Message}");
                ProcessingStatus = $"Error: {ex.Message}";
                throw;
            }
            finally
            {
                lease?.Dispose();
                IsProcessing = false;
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// Wrapper around <see cref="WorkflowNodeUpdater.UpdateNodeInput"/> that fails loudly on a node id or
        /// input that is no longer in the graph. The updater silently no-ops instead, which on these
        /// workflows would mean shipping the baked-in demo prompt and reference images to the GPU.
        /// </summary>
        private static void SetInput(ref string json, string nodeId, string input, object value)
        {
            if (WorkflowNodeUpdater.GetNodeInput(json, nodeId, input) == null)
                throw new Exception($"Workflow node '{nodeId}' has no input '{input}' — the workflow file no longer matches this tab.");
            WorkflowNodeUpdater.UpdateNodeInput(ref json, nodeId, input, value);
        }

        /// <summary>
        /// Asserts the node classes the patches below assume, and makes sure both
        /// <c>MiniMaxH3ReferenceToVideo</c> nodes read the prompt, canvas and frame count from the input
        /// primitives rather than from widget values baked in by the export. Idempotent — the shipped file
        /// is already wired this way, and this is what keeps it that way after a re-export.
        ///
        /// <para>The refine pass's reference node (101) deliberately keeps its own width/height: they come
        /// from the face-crop canvas, not from the video canvas.</para>
        /// </summary>
        private static string EnsureInputPrimitives(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodePrompt, "PrimitiveStringMultiline");
            RequireClass(root, NodeResolution, "ResolutionSelector");
            RequireClass(root, NodeFrames, "ComfyMathExpression");
            RequireClass(root, NodeDuration, "PrimitiveFloat");
            RequireClass(root, NodeCharacter1, "LoadImage");
            RequireClass(root, NodeRtxUpscale, "RTXVideoSuperResolution");
            RequireClass(root, NodeVideoCombine, "VHS_VideoCombine");

            json = root.ToJsonString();
            SetInput(ref json, NodeReference, "prompt", new JsonArray(NodePrompt, 0));
            SetInput(ref json, NodeReference, "width", new JsonArray(NodeResolution, 0));
            SetInput(ref json, NodeReference, "height", new JsonArray(NodeResolution, 1));
            SetInput(ref json, NodeReference, "length", new JsonArray(NodeFrames, 1));

            // The refine pass shares the prompt and the frame count; only its canvas differs.
            if (JsonNode.Parse(json)?[NodeRefineReference] is JsonObject)
            {
                SetInput(ref json, NodeRefineReference, "prompt", new JsonArray(NodePrompt, 0));
                SetInput(ref json, NodeRefineReference, "length", new JsonArray(NodeFrames, 1));
            }
            return json;
        }

        /// <summary>
        /// Resolves the panel files a queued character actually renders from, splitting the sheet again when
        /// the frozen paths are gone.
        ///
        /// <para>Whatever this returns has to have the <i>same number</i> of entries as the item's prompt was
        /// numbered for, because <c>&lt;Picture n&gt;</c> was baked into that prompt at queue time and cannot
        /// be renegotiated now. So the panel count is forced, never re-detected:</para>
        /// <list type="bullet">
        /// <item>The panel cache is disposable — it lives outside the user's folders and can be swept at any
        /// time — so missing files are regenerated at the recorded count.</item>
        /// <item>An item queued before this tab split sheets at all carries no panel paths, and its prompt
        /// gives each character exactly one picture number. It therefore runs the old way, with the sheet
        /// whole. Re-queue it to get the split.</item>
        /// </list>
        /// </summary>
        protected IReadOnlyList<string> ResolvePanels(
            IReadOnlyList<string> frozen, string sheetPath, int character)
        {
            var kept = frozen.Where(p => !string.IsNullOrEmpty(p) && File.Exists(p)).ToList();
            if (kept.Count > 0 && kept.Count == frozen.Count) return kept;

            var legacy = frozen.Count == 0;
            var requested = legacy ? CharacterSheetSplitter.WholeSheet : frozen.Count;
            var panels = CharacterSheetSplitter.Split(sheetPath, requested);
            if (panels.Count == 0)
                throw new FileNotFoundException($"Character {character}'s sheet is gone: {sheetPath}");

            AddLog(legacy
                ? $"Character {character}: queued before sheets were split, and its prompt numbers this " +
                  "character as one picture — sending the sheet whole. Re-queue the item to split it."
                : $"Character {character}: cached panels missing, re-split ({panels.Note}).");

            if (!legacy && panels.Count != frozen.Count)
                AddLog($"WARNING: character {character} re-split into {panels.Count} panel(s) but the prompt " +
                       $"was numbered for {frozen.Count}. Re-queue this item to renumber it.");
            return panels.Paths;
        }

        /// <summary>
        /// Wires the cast's panels into <c>ref_images.ref_image_0…N</c> on both reference nodes — the base
        /// pass and the face-refine pass condition on the same cast, and a face the refiner has no reference
        /// for is a face it will redraw wrong.
        ///
        /// <para>The panels go in <b>unresized</b>. The graph shipped with an <c>ImageResizeKJv2</c> between
        /// the LoadImage and the reference node that scaled every reference to the exact video canvas, which
        /// handed H3 a canvas-shaped, canvas-sized picture of a character sheet — the shape of an output frame,
        /// which is a strong invitation to render one. It is also unnecessary:
        /// <c>MiniMaxH3ReferenceToVideo</c> sizes references itself (<c>ref_image_size</c> "match" scales each
        /// one down to the generation's pixel area keeping aspect, "max" to a 2048px short edge). That resize
        /// node is left unreferenced here and <see cref="PruneToOutputs"/> deletes it.</para>
        /// </summary>
        /// <param name="wired">The injected <c>LoadImage</c> node ids, in panel order — what the refine
        /// passes pick their own conditioning out of.</param>
        private static string WireReferenceImages(
            string json, IReadOnlyList<string> uploadedNames, out IReadOnlyList<string> wired)
        {
            if (uploadedNames.Count == 0)
                throw new Exception("No reference images to wire — the cast has no panels.");
            if (uploadedNames.Count > MaxReferenceImages)
                throw new Exception($"{uploadedNames.Count} reference images, but MiniMaxH3ReferenceToVideo " +
                                    $"takes at most {MaxReferenceImages}. Split the sheets into fewer panels.");

            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeCharacter1, "LoadImage");

            // The export ships one LoadImage; the rest are injected beside it, ids well clear of the graph's.
            var loaders = new List<string>();
            for (var i = 0; i < uploadedNames.Count; i++)
            {
                var id = i == 0
                    ? NodeCharacter1
                    : (ReferenceNodeIdBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploadedNames[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Ref Image {i + 1}" }
                };
                loaders.Add(id);
            }

            foreach (var id in new[] { NodeReference, NodeRefineReference })
                if (root[id] is JsonObject) AttachReferences(root, id, loaders);

            wired = loaders;
            return root.ToJsonString();
        }

        /// <summary>Rewrites a reference node's autogrow <c>ref_image_N</c> inputs to exactly these loaders.
        /// Cleared rather than overwritten: a run with fewer panels than the file was authored for must not
        /// inherit a stale slot pointing at a node that is about to be pruned.</summary>
        protected static void AttachReferences(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' has no inputs — the workflow file no longer matches this tab.");

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            for (var i = 0; i < loaders.Count; i++)
                inputs[RefImagePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new JsonArray(loaders[i], 0);
        }

        /// <summary>
        /// Points the face-refine pass — or, for a two-hander, <b>each of them</b> — at one character's
        /// panels, their own prompt primitive, and their own face close-up as the tracker's identity.
        ///
        /// <para><c>H3FaceTrackCrop</c> holds a single subject: with no <c>identity_reference</c> it picks
        /// whoever is largest in the first frame and follows them, so in a two-character clip the other
        /// character's face was never refined at all — and the pass that did run was shown both cast members'
        /// photographs, which gave it nothing to say about which of the two faces it had. Character 2's chain
        /// is cloned into the 200s and reads the frames character 1's pass stitched, so the two edits compose
        /// rather than one discarding the other.</para>
        ///
        /// <para>The face close-up is the <b>last</b> panel: that is the order
        /// <c>prompts/prompt2json/h3-charsheet-2511.md</c> builds a sheet in (full-body front, full-body back,
        /// face close-up) and the order the splitter cuts it in. A sheet sent whole is one picture, and that
        /// picture is the identity.</para>
        /// </summary>
        /// <param name="perCharacter">False for an item queued before the per-character passes existed: its
        /// one prompt is numbered for the whole cast, so that pass keeps every panel and no identity
        /// reference.</param>
        private static string WireRefinePasses(
            string json, IReadOnlyList<string> loaders, int panels1, int panels2,
            bool perCharacter, bool refineCharacter2)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");
            if (root[NodeRefineReference] is not JsonObject) return json;

            var loaders1 = loaders.Take(panels1).ToList();
            var loaders2 = loaders.Skip(panels1).Take(panels2).ToList();
            if (loaders1.Count == 0)
                throw new Exception("The face-refine pass has no panel to condition on — it would redraw " +
                                    "every face from the prompt text alone.");

            EnsureRefinePrompt(root, NodeRefineReference, NodeRefinePrompt, "Refine prompt (character 1)");
            if (perCharacter)
            {
                AttachReferences(root, NodeRefineReference, loaders1);
                SetIdentityReference(root, NodeFaceTrack, loaders1);
            }

            json = root.ToJsonString();
            if (!refineCharacter2 || loaders2.Count == 0) return json;

            json = AddSecondRefinePass(json);
            root = JsonNode.Parse(json)!.AsObject();
            EnsureRefinePrompt(root, NodeRefineReference2, NodeRefinePrompt2, "Refine prompt (character 2)");
            AttachReferences(root, NodeRefineReference2, loaders2);
            SetIdentityReference(root, NodeFaceTrack2, loaders2);
            return root.ToJsonString();

            // The pass describes a cast of one, so it cannot share node 48 with the clip — on the tagged path
            // it could not anyway, since it stays on the core reference node and reads picture numbers.
            static void EnsureRefinePrompt(JsonObject root, string referenceNode, string promptNode, string title)
            {
                if (root[referenceNode]?["inputs"] is not JsonObject inputs) return;
                root[promptNode] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["value"] = string.Empty },
                    ["class_type"] = "PrimitiveStringMultiline",
                    ["_meta"] = new JsonObject { ["title"] = title }
                };
                inputs["prompt"] = new JsonArray(promptNode, 0);
            }

            // With an identity reference the subject is chosen by face identity rather than by size, which is
            // the only way two people in one frame can be told apart across a clip.
            static void SetIdentityReference(JsonObject root, string trackNode, IReadOnlyList<string> loaders)
            {
                if (root[trackNode]?["inputs"] is not JsonObject inputs) return;
                inputs["identity_reference"] = new JsonArray(loaders[^1], 0);
            }
        }

        /// <summary>
        /// Clones the refine chain (<c>100</c>–<c>111</c>) into a second pass in the <c>200</c> block, reading
        /// the frames the first pass already stitched.
        ///
        /// <para>Injected here rather than shipped in the workflow file because it only exists for a clip that
        /// casts two characters — and because a hand-authored copy of a dozen nodes is a dozen more links to
        /// keep in step with the original every time the chain changes. Every link inside the clone is
        /// remapped to the clone; every link out of it is left alone, except the two that read the base
        /// render (<c>H3FaceTrackCrop.images</c> and <c>H3FaceStitch.base_images</c>), which move onto the
        /// first pass's output.</para>
        /// </summary>
        private static string AddSecondRefinePass(string json)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            // The refine pass's SLA patch lives outside the 100-111 window — there is no free id inside it —
            // so its clone pair is seeded by hand. Without this, character 2's guider and scheduler would be
            // patched off character 1's audio lock. Harmless when the node is absent: the loop skips a
            // source that is not in the graph.
            var map = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [NodeSlaRefine] = NodeSlaRefine2,
                [NodeFaceMask] = NodeFaceMask2,
            };
            foreach (var kv in root.ToList())
            {
                if (!int.TryParse(kv.Key, out var id) || id < 100 || id > 111) continue;
                map[kv.Key] = (id + RefinePass2IdOffset).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            foreach (var (source, clone) in map)
            {
                if (root[source] is not JsonObject node) continue;
                var copy = JsonNode.Parse(node.ToJsonString())!.AsObject();

                if (copy["inputs"] is JsonObject inputs)
                    foreach (var input in inputs.ToList())
                    {
                        if (input.Value is not JsonArray link || link.Count < 2) continue;
                        var from = link[0]?.GetValue<string>();
                        if (from == null) continue;
                        var slot = link[1]!.GetValue<int>();
                        var target = map.TryGetValue(from, out var mapped) ? mapped
                                   : from == NodeBaseFrames ? NodeFaceStitch
                                   : from;
                        inputs[input.Key] = new JsonArray(target, slot);
                    }

                var title = copy["_meta"]?["title"]?.GetValue<string>();
                copy["_meta"] = new JsonObject { ["title"] = $"{title ?? clone} (character 2)" };
                root[clone] = copy;
            }

            return root.ToJsonString();
        }

        /// <summary>
        /// Whether the connected ComfyUI's MiniMaxH3-Contex-Loop is new enough to resolve <c>@tags</c>
        /// (v0.4.0 and later). Only a "yes" is remembered: a "no" is re-asked, so updating the pack and
        /// restarting ComfyUI takes effect without restarting FlipPix.
        /// </summary>
        private async Task<bool> SupportsTaggedReferencesAsync(CancellationToken token)
        {
            if (_taggedReferencesSupported) return true;
            _taggedReferencesSupported = await _comfyUIService.HttpClient.HasNodeClassesAsync(
                new[] { TaggedReferenceClass, TaggedPictureClass }, token);
            return _taggedReferencesSupported;
        }

        private bool _taggedReferencesSupported;

        /// <summary>
        /// Rewires the base pass from fixed <c>ref_image_N</c> slots onto the tagged reference chain: each
        /// <c>LoadImage</c> gains a <c>MiniMaxH3TaggedPictureReference</c> registering it under one alias, the
        /// chain feeds <c>MiniMaxH3TaggedReferenceToVideo</c>, and that node decides per prompt which pictures
        /// are actually encoded and what <c>&lt;Picture N&gt;</c> each becomes.
        ///
        /// <para><b>Why this is worth a graph rewrite.</b> Splitting the sheets is what made the numbering
        /// fragile: a character stops being one picture, so <c>&lt;Picture 2&gt;</c> means character 2 in what
        /// the model writes and character 1's back view in what the server receives, and every re-stamp has to
        /// undo a numbering it cannot remember. An alias survives the split, the re-stamp, and a clip that
        /// leaves someone out — the node computes the numbers, from the prompt it is actually sending.</para>
        ///
        /// <para><b>The refine pass is deliberately left on the core node.</b> It carries
        /// <c>ref_audios.ref_audio_0</c> — stage one's decoded audio, which is what keeps the regenerated
        /// mouths on the take's own soundtrack — and the tagged node has no <c>ref_audios</c> input at all;
        /// audio would have to go round through a tagged audio reference and be activated by a tag in the
        /// prompt. So it keeps its numbered references and gets its own copy of the prompt with the aliases
        /// swapped back for those same numbers. Both passes therefore condition on the same cast, described
        /// with the same words, which is all that pass needs.</para>
        /// </summary>
        private static string ConvertToTaggedReferences(string json, IReadOnlyList<string> aliases)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeReference, "MiniMaxH3ReferenceToVideo");
            var inputs = root[NodeReference]?["inputs"]?.AsObject()
                         ?? throw new Exception($"Workflow node '{NodeReference}' has no inputs.");

            // The loaders in slot order — WireReferenceImages has already put them there, so the aliases line
            // up with the same panels the numbered path would have sent.
            var loaders = new List<string>();
            for (var i = 0; ; i++)
            {
                if (inputs[RefImagePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)]
                        is not JsonArray link || link.Count < 1) break;
                loaders.Add(link[0]!.GetValue<string>());
            }
            if (loaders.Count != aliases.Count)
                throw new Exception($"{loaders.Count} reference image(s) wired but {aliases.Count} alias(es) " +
                                    "to name them — the cast and the prompt disagree.");

            string? previous = null;
            for (var i = 0; i < aliases.Count; i++)
            {
                var id = (TaggedNodeIdBase + i).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var taggedInputs = new JsonObject
                {
                    ["image"] = new JsonArray(loaders[i], 0),
                    ["tag"] = aliases[i],
                };
                if (previous != null) taggedInputs["previous"] = new JsonArray(previous, 0);

                root[id] = new JsonObject
                {
                    ["inputs"] = taggedInputs,
                    ["class_type"] = TaggedPictureClass,
                    ["_meta"] = new JsonObject { ["title"] = $"@{aliases[i]}" }
                };
                previous = id;
            }

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            root[NodeReference]!["class_type"] = TaggedReferenceClass;
            inputs["references"] = new JsonArray(previous!, 0);
            // This graph submits one clip per prompt; the scene indices exist for the Contex Loop's recursion,
            // which this tab does not use.
            inputs["clip_index"] = 1;
            inputs["clip_count"] = 1;
            // strict, because every alias in the prompt was written by CastPromptStamp's reference line from this same list:
            // an unresolved one is a wiring bug, and a loud failure beats a silent "@char2_face" reaching H3.
            inputs["reference_policy"] = "strict";

            // The refine passes keep numbered references and get their own prompt primitives — see
            // WireRefinePasses, which runs for the numbered path too.
            return root.ToJsonString();
        }

        /// <summary>
        /// Wires the tail of the graph — which frames reach the file — for the two optional passes.
        ///
        /// <para>The frames come from the face-stitch node when the refine pass runs and from the base
        /// decode when it does not; they then go through the RTX upscale, or straight to the video sink when
        /// that is off. Whatever is left unreferenced becomes unreachable and <see cref="PruneToOutputs"/>
        /// deletes it, which is the only safe way to drop a branch: several of these nodes would otherwise
        /// still execute on their own.</para>
        /// </summary>
        private static string WireOutputChain(
            string json, bool faceRefine, bool refineCharacter2, bool rtxUpscale)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            RequireClass(root, NodeBaseFrames, "VAEDecode");
            RequireClass(root, NodeRtxUpscale, "RTXVideoSuperResolution");
            RequireClass(root, NodeVideoCombine, "VHS_VideoCombine");
            if (faceRefine) RequireClass(root, NodeFaceStitch, "H3FaceStitch");
            if (refineCharacter2) RequireClass(root, NodeFaceStitch2, "H3FaceStitch");

            json = root.ToJsonString();

            // Character 2's pass stitches on top of character 1's, so the last stitch in the chain is the
            // one the file is made from.
            var frames = faceRefine
                ? (refineCharacter2 ? NodeFaceStitch2 : NodeFaceStitch)
                : NodeBaseFrames;
            SetInput(ref json, NodeRtxUpscale, "images", new JsonArray(frames, 0));
            SetInput(ref json, NodeVideoCombine, "images",
                new JsonArray(rtxUpscale ? NodeRtxUpscale : frames, 0));
            return json;
        }

        /// <summary>
        /// Settles the draft -> 2x -> finish scheme on the base pass (nodes 70-80).
        ///
        /// <para><b>Why this is wired the other way round from the MiniMax I2V tab.</b> There,
        /// ResolutionSelector is handed a quarter of the megapixel target and names the <i>draft</i>; the
        /// reference node hangs off it, and since <c>ref_image_size</c> is <c>match</c> every panel is
        /// encoded at the draft canvas, a quarter of the area the user asked for. This tab exists to hold a
        /// face across a clip, so that is the wrong trade here.</para>
        ///
        /// <para>So the selector keeps naming the <b>finished</b> canvas and node 23 keeps every panel at
        /// it, exactly as with the upscale off. The draft canvas exists only as node 72 - a bare
        /// <c>MiniMaxH3ReferenceToVideo</c> with no reference images, whose only output of interest is an
        /// empty AV latent at half the width and half the height. Both samplers share node 23's
        /// conditioning, which is sound because the I2V base pass already reuses one conditioning across a
        /// draft and a 2x finish.</para>
        ///
        /// <para>Node 73 reads the <b>raw UNet</b> rather than the patched model: <c>MiniMaxH3SigmaShift</c>
        /// sits on the wire the tab's own scheduler reads, and a shifted ramp does not have its halfway
        /// point at sigma 0.5, which is where the draft has to stop for the finish sigmas to pick it up.</para>
        ///
        /// <para>Off, the base pass goes back to one 6-step sampling at the finished canvas: the sampler
        /// takes node 23's own latent and node 57's schedule again, the decoders read the sampler directly,
        /// and the whole 70-80 block falls out in the prune. Returns false when the workflow file predates
        /// the block, so the caller can say so.</para>
        /// </summary>
        private static string WireLatentUpscale(string json, bool useUpscale, out bool wired)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            wired = root[NodeDraftLatent] is JsonObject && root[NodeLatentUpscaler] is JsonObject &&
                    root[NodeLatentSwitch] is JsonObject && root[NodeDraftSigmas] is JsonObject;
            if (!wired) return json;

            RequireClass(root, NodeDraftLatent, "MiniMaxH3ReferenceToVideo");
            RequireClass(root, NodeLatentUpscaler, "MinimaxH3LatentUpscaler3D");
            RequireClass(root, NodeLatentSwitch, "ComfySwitchNode");
            RequireClass(root, NodeDraftSigmas, "SplitSigmas");

            json = root.ToJsonString();
            if (useUpscale)
            {
                // The factor has to be the same number in both places that derive the finished canvas: the
                // upscaler, and the two expressions that halve the selector's output for the draft.
                SetInput(ref json, NodeLatentUpscaler, "mode.scale", LatentUpscaleFactor);
                SetInput(ref json, NodeDraftWidth, "values.b", LatentUpscaleFactor);
                SetInput(ref json, NodeDraftHeight, "values.b", LatentUpscaleFactor);
                SetInput(ref json, NodeResolution, "multiple", UpscaledResolutionMultiple);
                return json;
            }

            SetInput(ref json, NodeSampler, "latent_image", new JsonArray(NodeReference, 1));
            SetInput(ref json, NodeSampler, "sigmas", new JsonArray(NodeScheduler, 0));
            SetInput(ref json, NodeBaseFrames, "samples", new JsonArray(NodeSampler, 0));
            SetInput(ref json, NodeBaseAudio, "samples", new JsonArray(NodeSampler, 0));
            SetInput(ref json, NodeResolution, "multiple", ResolutionMultiple);
            return json;
        }

        /// <summary>
        /// Settles the two attention patches: <c>H3SLAAttention</c> on each branch (node 66 after the sigma
        /// shift, node 67 after the audio lock, each last on its own MODEL wire), and the workflow's own
        /// <c>SolAttnPatch</c> at node 53.
        ///
        /// <para>Either one that is off is <b>cut out rather than disabled</b>: its consumers are pointed
        /// back at whatever fed it, which leaves it unreachable for <see cref="PruneToOutputs"/> to delete.
        /// That is what lets a server without the SLA pack render the job at all - a disabled node is still
        /// a node the server has to know how to load - and it is also how Sol-Attn, which this workflow
        /// shipped with hard-wired and no switch, gets turned off.</para>
        ///
        /// <para>Must run before <see cref="AddSecondRefinePass"/>: that clones the model wire verbatim, so
        /// whatever it finds is what character 2's pass inherits.</para>
        /// </summary>
        private static string WireAttention(
            string json, bool useSla, double sparsity, bool useSolAttn, out bool slaWired)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            slaWired = false;
            foreach (var id in new[] { NodeSlaBase, NodeSlaRefine })
            {
                if (root[id] is not JsonObject node) continue;
                RequireClass(root, id, "H3SLAAttention");
                slaWired = true;
                if (!useSla) { Unwire(root, id); continue; }

                var inputs = node["inputs"]!.AsObject();
                inputs["enabled"] = true;
                inputs["sparsity_ratio"] = sparsity;
                inputs["block_size"] = SlaBlockSize;
            }

            if (!useSolAttn && root[NodeSolAttn] is JsonObject) Unwire(root, NodeSolAttn);

            return root.ToJsonString();

            // Hands every consumer the model this patch was reading, and lets the prune take it.
            static void Unwire(JsonObject root, string id)
            {
                if (root[id]?["inputs"]?["model"] is not JsonArray source || source.Count < 2) return;
                var fromNode = source[0]!.GetValue<string>();
                var fromSlot = source[1]!.GetValue<int>();
                foreach (var other in root.ToList())
                {
                    if (other.Value?["inputs"] is not JsonObject otherInputs) continue;
                    foreach (var input in otherInputs.ToList())
                        if (input.Value is JsonArray link && link.Count >= 2 &&
                            link[0]?.GetValue<string>() == id)
                            otherInputs[input.Key] = new JsonArray(fromNode, fromSlot);
                }
            }
        }

        /// <summary>
        /// Points the stitch at a SAM mask that follows the face, or back at the detected face box.
        ///
        /// <para>Off is an unwire, not a flag: the <c>masks</c> input is removed from each stitch, which
        /// leaves <c>H3FaceMaskSAM</c> and the loader unreachable for <see cref="PruneToOutputs"/> to
        /// delete — so a server without an installed SAM model still renders the job. The feather moves
        /// with it, because the right blend width for a mask and for a rectangle are different numbers.</para>
        /// </summary>
        private static string WireFaceMask(string json, bool useMask, bool refineCharacter2, out bool wired)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            wired = root[NodeFaceMask] is JsonObject && root[NodeSamLoader] is JsonObject;
            if (!wired)
            {
                // A file without the mask nodes still has to lose any dangling link to them.
                foreach (var id in new[] { NodeFaceStitch, NodeFaceStitch2 })
                    (root[id]?["inputs"] as JsonObject)?.Remove("masks");
                return root.ToJsonString();
            }

            RequireClass(root, NodeFaceMask, "H3FaceMaskSAM");
            RequireClass(root, NodeSamLoader, "SAMLoader");

            if (!useMask)
                foreach (var id in new[] { NodeFaceStitch, NodeFaceStitch2 })
                    (root[id]?["inputs"] as JsonObject)?.Remove("masks");

            json = root.ToJsonString();
            SetInput(ref json, NodeFaceStitch, "feather", useMask ? FeatherWithMask : FeatherWithBox);
            if (refineCharacter2 && JsonNode.Parse(json)?[NodeFaceStitch2] is JsonObject)
                SetInput(ref json, NodeFaceStitch2, "feather", useMask ? FeatherWithMask : FeatherWithBox);
            return json;
        }

        /// <summary>Reads an integer widget out of the graph — used for the steps the workflow ships with,
        /// which the tab reports but never overrides.</summary>
        private static int ReadInt(string json, string nodeId, string input)
        {
            var node = JsonNode.Parse(json)?[nodeId]?["inputs"]?[input];
            return node is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        }

        /// <summary>Fails loudly when a node the patches rewire is missing or is no longer the class they
        /// assume — both would otherwise produce a graph that only fails on the server, or worse, silently
        /// renders the wrong thing.</summary>
        protected static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            if (root[nodeId] is not JsonObject node)
                throw new Exception($"Workflow node '{nodeId}' is not in the graph — the workflow file no longer matches this tab.");
            var actual = node["class_type"]?.GetValue<string>();
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual ?? "(none)"}, expected {expected} — the workflow file no longer matches this tab.");
        }

        /// <summary>
        /// Cuts the graph down to the output nodes we want plus everything they depend on, and deletes every
        /// other node outright. Pruning by reachability is the only reliable way to drop a branch: anything
        /// ending in an OUTPUT_NODE runs whether or not something downstream consumes it, so unhooking a
        /// sink is not enough on its own.
        /// </summary>
        protected static string PruneToOutputs(string json, IEnumerable<string> keepOutputs, out int removed)
        {
            var root = JsonNode.Parse(json)?.AsObject()
                       ?? throw new Exception("Workflow JSON could not be parsed.");

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            var stack = new Stack<string>(keepOutputs);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!reachable.Add(id)) continue;
                if (root[id]?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs)
                {
                    // A link is ["<source node id>", <output index>]; widget values are anything else.
                    if (input.Value is JsonArray link && link.Count == 2 && LinkSource(link[0]) is { } src)
                        stack.Push(src);
                }
            }

            removed = 0;
            foreach (var id in root.Select(kv => kv.Key).ToList())
            {
                if (reachable.Contains(id)) continue;
                root.Remove(id);
                removed++;
            }

            return root.ToJsonString();

            // Node ids are strings here (these graphs were exported with subgraphs flattened, so they look
            // like "115:3"), but plain integer ids show up in other exports of the same nodes.
            static string? LinkSource(JsonNode? node)
            {
                if (node is not JsonValue value) return null;
                if (value.TryGetValue<string>(out var s)) return s;
                if (value.TryGetValue<long>(out var i)) return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return null;
            }
        }

        /// <summary>Submits the workflow, waits for completion, and resolves the video sink's output to a
        /// local file — first via /history node outputs, then a disk scan for the run token.</summary>
        protected async Task<string?> SubmitAndRetrieveAsync(
            string json, string runToken, string outputNode, double from, double to, CancellationToken token)
        {
            var existing = GetExistingVideoFiles("*.mp4", OutputSubfolder);
            var promptId = await SubmitAsync(json, from, to, token);

            ProcessingStatus = "Waiting for output...";
            var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, token);
            if (byNode.TryGetValue(outputNode, out var outs) && outs.Count > 0)
            {
                var pick = outs.FirstOrDefault(f => f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) ?? outs[0];
                var local = await ResolveOutputToLocalAsync(pick);
                if (local != null) return local;
            }

            // Fallback: wait for a new mp4 carrying this run's token in the output subfolder.
            var found = await WaitForNewVideoAsync(existing, "*.mp4",
                TimeSpan.FromMinutes(60), TimeSpan.FromSeconds(4), OutputSubfolder);
            if (found != null && Path.GetFileName(found).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0)
                return found;
            return found ?? FindTokenVideoOnDisk(runToken);
        }

        /// <summary>
        /// Submits a sheet job. Unlike <see cref="SubmitAsync"/> it reports through <see cref="SheetPhase"/>
        /// and never touches the progress bar or the status line: a render may well be running underneath,
        /// and those belong to it.
        /// </summary>
        private async Task<string> SubmitSheetAsync(string json, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var phase = SheetPhase;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        SheetPhase = $"{phase} {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        protected async Task<string> SubmitAsync(string json, double progressFrom, double progressTo, CancellationToken token)
        {
            var workflow = JsonSerializer.Deserialize<JsonElement>(json);
            var span = progressTo - progressFrom;
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max != null && msg.Data.Max > 0)
                {
                    var pct = (double)msg.Data.Value / msg.Data.Max;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        ProcessingProgress = progressFrom + pct * span;
                        ProcessingStatus = $"Generating: {msg.Data.Value}/{msg.Data.Max}";
                    });
                }
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Workflow submitted, ID: {promptId}");
            return promptId;
        }

        protected async Task<string?> ResolveOutputToLocalAsync(string videoFile)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    var baseUrl = GetComfyUIBaseUrl();
                    var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                    var outputFolder = settings.ResolveOutputFolder(isRemote);
                    if (!string.IsNullOrEmpty(outputFolder))
                    {
                        var localPath = Path.Combine(outputFolder, videoFile.Replace('/', Path.DirectorySeparatorChar));
                        if (File.Exists(localPath))
                        {
                            await WaitForFileStableAsync(localPath);
                            return localPath;
                        }
                    }
                }

                var parts = videoFile.Split('/');
                var filename = parts.Last();
                var subfolder = parts.Length > 1 ? string.Join("/", parts.Take(parts.Length - 1)) : string.Empty;
                var bytes = await _comfyUIService.HttpClient.DownloadOutputVideoAsync(filename, subfolder);
                if (bytes is { Length: > 0 })
                {
                    var tempPath = Path.Combine(Path.GetTempPath(), $"h3cast_{Guid.NewGuid():N}_{filename}");
                    await File.WriteAllBytesAsync(tempPath, bytes);
                    return tempPath;
                }
            }
            catch (Exception ex)
            {
                AddLog($"Resolve output failed: {ex.Message}");
            }
            return null;
        }

        protected string? FindTokenVideoOnDisk(string runToken)
        {
            try
            {
                var settings = _settingsService.Settings;
                if (settings == null) return null;
                var baseUrl = GetComfyUIBaseUrl();
                var isRemote = IsComfyUIRemote(new Uri(baseUrl).Host);
                var outputFolder = settings.ResolveOutputFolder(isRemote);
                if (string.IsNullOrEmpty(outputFolder)) return null;

                var candidates = new List<string>();
                foreach (var folder in new[] { outputFolder, Path.Combine(outputFolder, OutputSubfolder) })
                {
                    if (Directory.Exists(folder))
                        candidates.AddRange(Directory.GetFiles(folder, "*.mp4", SearchOption.AllDirectories)
                            .Where(f => Path.GetFileName(f).IndexOf(runToken, StringComparison.OrdinalIgnoreCase) >= 0));
                }
                return candidates.OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            }
            catch (Exception ex)
            {
                AddLog($"Disk scan failed: {ex.Message}");
                return null;
            }
        }

        #endregion

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(CanAnalyze));
            OnPropertyChanged(nameof(CanGenerate));
            OnPropertyChanged(nameof(CanBuildSheets));
            OnPropertyChanged(nameof(CanFeelLucky));
            OnPropertyChanged(nameof(LuckyButtonText));
            OnPropertyChanged(nameof(BuildSheetsButtonText));
            OnPropertyChanged(nameof(AllSheetsReady));
            OnPropertyChanged(nameof(CastSummary));
            BuildSheetsCommand.NotifyCanExecuteChanged();
            FeelLuckyCommand.NotifyCanExecuteChanged();
            AnalyzeCommand.NotifyCanExecuteChanged();
            DeriveWardrobeCommand.NotifyCanExecuteChanged();
            GenerateCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
            RemoveQueueItemCommand.NotifyCanExecuteChanged();
            ClearQueueCommand.NotifyCanExecuteChanged();
            StartQueueCommand.NotifyCanExecuteChanged();
            StopQueueCommand.NotifyCanExecuteChanged();
            ReprocessAllFailedCommand.NotifyCanExecuteChanged();
            PlayVideoCommand.NotifyCanExecuteChanged();
            OpenResultFolderCommand.NotifyCanExecuteChanged();
            GenerateCastZimageLoraCommand.NotifyCanExecuteChanged();
            GenerateCastFamegridLoraCommand.NotifyCanExecuteChanged();
            GenerateCastKrea2LoraCommand.NotifyCanExecuteChanged();
            GenerateCastQwenCommand.NotifyCanExecuteChanged();
            GenerateCastKrea2SpicyCommand.NotifyCanExecuteChanged();
            ClearDerivedCastCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// One member of the cast: the photo the user picked, the character sheet built from it, and the panels
    /// that sheet is cut into before it reaches H3. All three are kept apart on purpose — the photo is what the
    /// sheet builder reads, the sheet is what the user sees and judges, the panels are what MiniMax H3 is
    /// actually handed, and loading a new photo invalidates the sheet that came from the old one.
    /// </summary>
    public class CharacterSlot : System.ComponentModel.INotifyPropertyChanged
    {
        public delegate BitmapImage? PreviewLoader(string path, out string info);

        private readonly PreviewLoader _loadPreview;
        private readonly Action _onChanged;

        private string _sourcePath = string.Empty;
        private BitmapImage? _sourcePreview;
        private string _sourceInfo = string.Empty;
        private string _sheetPath = string.Empty;
        private BitmapImage? _sheetPreview;
        private bool _useSourceAsSheet;
        private int _panelSplit = CharacterSheetSplitter.Auto;
        private SheetPanels _panels = SheetPanels.Empty;
        private string _kind;
        private string _role = string.Empty;
        private string _sheetWardrobe = string.Empty;
        private string _expectedWardrobe = string.Empty;
        private bool _isGeneratingPhoto;
        private string _photoPhase = string.Empty;

        public CharacterSlot(int index, PreviewLoader loadPreview, Action onChanged)
        {
            Index = index;
            _loadPreview = loadPreview;
            _onChanged = onChanged;
            // A two-hander is a man and a woman more often than it is anything else, and it is one click to
            // change — whereas an unset kind is one the wardrobe pass and the sheet builder have to guess.
            // Alternating extends that to an ensemble without moving slot 1 or slot 2.
            _kind = index % 2 == 0 ? Female : Male;
        }

        // ── What this character is ────────────────────────────────────────────────────────────
        // The first four are people and behave exactly as the old Male/Female pair did. The last
        // three are not, and every prompt that would otherwise call them "the same adult, a man",
        // dress them from a costume supervisor's garment list, or hand their face to a human face
        // tracker branches on IsPerson / HasFace instead.
        public const string Male = "Male";
        public const string Female = "Female";
        public const string Boy = "Boy";
        public const string Girl = "Girl";
        public const string Creature = "Creature";
        public const string Thing = "Character (not a person)";
        public const string Crowd = "Group of people";
        public const string Group = "Group (not people)";

        /// <summary>1 or 2 — the character's position in the cast.</summary>
        public int Index { get; }

        /// <summary>
        /// What this character <b>is</b>. It is not cosmetic: it goes into the wardrobe request (a costume
        /// supervisor asked for "an outfit" with no-one to dress writes a different one), into the sheet
        /// builder's instruction, into the reference line H3 reads — and it decides whether this character
        /// gets a face-refine pass at all, since that pass tracks <i>human</i> faces.
        ///
        /// <para>The two-hander tabs only ever set this through <see cref="Sex"/>, so for them it stays the
        /// Male/Female pair it has always been. A children's story is the case the rest exists for: a cloud,
        /// a mountain and a herd of goats are characters with wardrobes and continuity, and not one of them
        /// is "a man" or "a woman".</para>
        /// </summary>
        public string Kind
        {
            get => _kind;
            set
            {
                var v = KindOptions.FirstOrDefault(
                    k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase)) ?? Male;
                if (_kind == v) return;
                _kind = v;
                Raise(nameof(Kind), nameof(Sex), nameof(Noun), nameof(Descriptor), nameof(Description),
                      nameof(IsPerson), nameof(IsGroup), nameof(HasFace), nameof(Pronoun),
                      nameof(IsCast), nameof(Label), nameof(CanGeneratePhoto));
            }
        }

        public IReadOnlyList<string> KindOptions { get; } =
            new[] { Male, Female, Boy, Girl, Creature, Thing, Crowd, Group };

        /// <summary>
        /// The Male/Female facade the 🪪👥 H3 Cast and 🪪👥⚡ H3 Cast Hybrid cards still bind to. Reading it
        /// collapses the four person kinds onto the two those tabs know about; writing it sets
        /// <see cref="Kind"/>. Neither of those tabs offers a non-person kind, so the collapse is only ever
        /// visible on a slot the Ensemble tab set.
        /// </summary>
        public string Sex
        {
            get => _kind == Female || _kind == Girl ? Female : Male;
            set => Kind = string.Equals(value, Female, StringComparison.OrdinalIgnoreCase) ? Female : Male;
        }

        public IReadOnlyList<string> SexOptions { get; } = new[] { Male, Female };

        /// <summary>
        /// Whether this character is a human being — <b>including</b> a crowd of them. A village of
        /// wandering travellers is people; a herd of goats is not, and a cloud is not.
        ///
        /// <para>Kept separate from <see cref="IsGroup"/> because they are two different facts and only one
        /// of them was being asked. Folding a crowd in with the clouds told the prompt not to give the
        /// travellers human faces, and had their character sheet built from the "THE SUBJECT IS NOT A
        /// PERSON" brief.</para>
        /// </summary>
        public bool IsPerson => _kind is Male or Female or Boy or Girl or Crowd;

        /// <summary>Several of them acting as one character — a herd, a village, a flock.</summary>
        public bool IsGroup => _kind is Crowd or Group;

        /// <summary>
        /// Whether this character can have a face-refine pass. Two separate reasons to say no.
        /// <c>H3FaceTrackCrop</c> tracks and re-generates <i>human</i> faces, so pointing it at a mountain
        /// either finds nothing or finds somebody else's face and redraws that — worse than not refining at
        /// all. And it holds <i>one</i> subject through a clip, so a crowd is out too even though a crowd is
        /// made of people: it would pick whoever is largest and redraw only them.
        /// </summary>
        public bool HasFace => IsPerson && !IsGroup;

        /// <summary>"he" / "she", or empty when the story decides. A talking cloud may be "he", "she", "it"
        /// or "they" and nothing here can know which — so for the non-person kinds the prompts are told to
        /// take the pronoun from the story rather than being handed a wrong one.</summary>
        public string Pronoun => _kind switch
        {
            Male or Boy => "he",
            Female or Girl => "she",
            Crowd => "they",
            _ => string.Empty,
        };

        /// <summary>The bare word the prompts use — "man", "woman", "creature", "group".</summary>
        public string Noun => _kind switch
        {
            Female => "woman",
            Boy => "boy",
            Girl => "girl",
            Creature => "creature",
            Thing => "character",
            Crowd => "group of people",
            Group => "group",
            _ => "man",
        };

        /// <summary>
        /// What to call this character when a tag is not enough — "a man", or, for a non-person, whatever
        /// <see cref="Role"/> says they are ("Nimbus, a fluffy little cloud").
        ///
        /// <para>The Part field carries the whole description for a non-person character, because nothing
        /// here can guess it: "a creature" tells the costume supervisor and the sheet builder nothing, and
        /// the cast brief has to name what the thing actually is.</para>
        /// </summary>
        /// <summary>
        /// What to call this character when a tag is not enough — "a man", or, for anything that is not one
        /// named person, whatever <see cref="Role"/> says they are ("Nimbus, a fluffy little cloud", "a
        /// village of wandering travellers").
        ///
        /// <para>The Part field carries the whole description for a non-person and for a group, because
        /// nothing here can guess it: "a creature" and "a group of people" tell the costume designer and the
        /// sheet builder nothing, and the cast brief has to name what they actually are.</para>
        /// </summary>
        public string Descriptor =>
            IsPerson && !IsGroup ? $"a {Noun}"
            : HasRole ? _role
            : _kind == Crowd ? "a group of people"
            : _kind == Group ? "a group of characters"
            : $"a {Noun} that is not a person";

        /// <summary>
        /// Who this character is <i>in the story</i> — "the detective", "her younger brother", "the barman",
        /// "Nimbus, a fluffy little cloud". Unused by the two-hander tabs, where "the man" and "the woman"
        /// are unambiguous.
        ///
        /// <para>It exists for an ensemble. Asked to cast five anonymous subjects into a story, a language
        /// model assigns them by order of appearance and then loses track — subject 4 becomes whoever the
        /// current sentence needs. A role is the one thing that ties a slot to a character in the prose, and
        /// it is also what makes the wardrobe pass dress a chauffeur differently from a guest.</para>
        ///
        /// <para><b>For a non-person kind it is not optional</b>, it is the description: nothing else in the
        /// app knows that this slot is a cloud rather than a mountain. See <see cref="Descriptor"/>.</para>
        /// </summary>
        public string Role
        {
            get => _role;
            set
            {
                var v = (value ?? string.Empty).Trim();
                if (_role == v) return;
                _role = v;
                Raise(nameof(Role), nameof(HasRole), nameof(Descriptor), nameof(Description), nameof(Label),
                      nameof(IsCast), nameof(CanGeneratePhoto));
            }
        }

        public bool HasRole => _role.Length > 0;

        /// <summary>
        /// Whether the user has told the tab anything about this slot: a photo, a part, or a kind that is
        /// not a person. It is <b>not</b> the same as having a photo.
        ///
        /// <para>The wardrobe pass runs off the story long before anybody browses for pictures — that is the
        /// whole point of deriving it early, so the character sheets can be built already dressed. Keying it
        /// on the photo meant a story typed into an empty tab had no cast at all, and the two-hander
        /// fallback then invented a man and a woman: the outfits came back for two people who are not in the
        /// film, in a story whose cast is a cloud and a mountain.</para>
        /// </summary>
        public bool IsCast => HasSource || HasRole || !IsPerson || IsGroup;

        /// <summary>
        /// The fullest one-line description of this character — "man, the detective" for a person,
        /// "Nimbus, a fluffy little cloud" for anything else. What the prompts and the wardrobe pass use
        /// when a <c>&lt;Subject n&gt;</c> tag is not enough on its own.
        /// </summary>
        public string Description => IsPerson && !IsGroup
            ? HasRole ? $"{Noun}, {_role}" : Noun
            : Descriptor;

        /// <summary>The card's own heading, so the kind — and the part, when there is one — are legible
        /// without opening the dropdown.</summary>
        public string Label => HasRole
            ? $"Character {Index} · {_kind} · {_role}"
            : $"Character {Index} · {_kind}";

        /// <summary>
        /// The outfit the current sheet was <i>built</i> wearing, empty when the sheet predates the wardrobe or
        /// is the user's own image. Compared against <see cref="ExpectedWardrobe"/> to notice a sheet that no
        /// longer matches the locked wardrobe — a mismatch there is a costume change H3 has to arbitrate, and
        /// it always arbitrates it differently in each clip.
        /// </summary>
        public string SheetWardrobe => _sheetWardrobe;

        /// <summary>The outfit this character is supposed to be wearing — pushed in by the tab whenever the
        /// wardrobe box changes.</summary>
        public string ExpectedWardrobe
        {
            get => _expectedWardrobe;
            set
            {
                var v = value ?? string.Empty;
                if (_expectedWardrobe == v) return;
                _expectedWardrobe = v;
                Raise(nameof(ExpectedWardrobe), nameof(SheetMatchesWardrobe), nameof(SheetStatus));
            }
        }

        /// <summary>
        /// True when the sheet on screen shows the wardrobe that is currently locked. False also covers "there
        /// is no wardrobe" and "this image was loaded as a sheet", both of which mean the same thing to the
        /// prompt: what the reference is wearing is not known to match.
        /// </summary>
        public bool SheetMatchesWardrobe =>
            HasSheet && _sheetWardrobe.Length > 0 && _expectedWardrobe.Length > 0 &&
            string.Equals(_sheetWardrobe.Trim(), _expectedWardrobe.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>The photo the user picked. Never uploaded unless it doubles as the sheet.</summary>
        public string SourcePath
        {
            get => _sourcePath;
            set
            {
                if (_sourcePath == value) return;
                _sourcePath = value;
                _sourcePreview = _loadPreview(value, out _sourceInfo);
                // A new photo invalidates whatever sheet the old one produced.
                _sheetPath = string.Empty;
                _sheetPreview = null;
                _sheetWardrobe = string.Empty;
                _panels = SheetPanels.Empty;
                Raise(nameof(SourcePath), nameof(SourcePreview), nameof(SourceInfo), nameof(HasSource),
                      nameof(SheetPath), nameof(SheetPreview), nameof(HasSheet), nameof(SheetStatus),
                      nameof(SheetWardrobe), nameof(SheetMatchesWardrobe),
                      nameof(PanelCount), nameof(PanelStatus), nameof(CanGeneratePhoto));
            }
        }

        public BitmapImage? SourcePreview => _sourcePreview;
        public string SourceInfo => _sourceInfo;
        public bool HasSource => !string.IsNullOrEmpty(_sourcePath) && File.Exists(_sourcePath);

        /// <summary>The multi-view sheet — built by Qwen, or the photo itself when
        /// <see cref="UseSourceAsSheet"/> is on. What H3 receives is <see cref="PanelPaths"/>, not this.</summary>
        public string SheetPath => _sheetPath;
        public BitmapImage? SheetPreview => _sheetPreview;
        public bool HasSheet => !string.IsNullOrEmpty(_sheetPath) && File.Exists(_sheetPath);

        /// <summary>
        /// The loaded image already <i>is</i> a character sheet — skip the Qwen pass and send it as it is.
        /// </summary>
        public bool UseSourceAsSheet
        {
            get => _useSourceAsSheet;
            set
            {
                if (_useSourceAsSheet == value) return;
                _useSourceAsSheet = value;
                if (value && HasSource) SetSheet(_sourcePath);
                else if (!value && string.Equals(_sheetPath, _sourcePath, StringComparison.OrdinalIgnoreCase))
                {
                    _sheetPath = string.Empty;
                    _sheetPreview = null;
                    _sheetWardrobe = string.Empty;
                    _panels = SheetPanels.Empty;
                }
                Raise(nameof(UseSourceAsSheet), nameof(SheetPath), nameof(SheetPreview),
                      nameof(HasSheet), nameof(SheetStatus), nameof(SheetWardrobe),
                      nameof(SheetMatchesWardrobe), nameof(PanelCount), nameof(PanelStatus));
            }
        }

        /// <summary>
        /// How the sheet is cut up before it is handed to H3. <c>Auto</c> looks for the gaps between the
        /// figures; a number forces an even split; "Whole sheet" is the old behaviour, one collage reference.
        /// </summary>
        public IReadOnlyList<PanelSplitOption> PanelSplitOptions { get; } = new[]
        {
            new PanelSplitOption(CharacterSheetSplitter.Auto, "Auto — find the panels"),
            new PanelSplitOption(CharacterSheetSplitter.WholeSheet, "Whole sheet (1 reference)"),
            new PanelSplitOption(2, "Split evenly in 2"),
            new PanelSplitOption(3, "Split evenly in 3"),
            new PanelSplitOption(4, "Split evenly in 4"),
        };

        public int SelectedPanelSplit
        {
            get => _panelSplit;
            set
            {
                if (_panelSplit == value) return;
                _panelSplit = value;
                RebuildPanels();
                Raise(nameof(SelectedPanelSplit), nameof(PanelCount), nameof(PanelStatus));
            }
        }

        /// <summary>
        /// The images actually wired to <c>ref_images.ref_image_N</c>, left to right — one per view. Empty
        /// until there is a sheet.
        /// </summary>
        public IReadOnlyList<string> PanelPaths => _panels.Paths;

        /// <summary>How many reference slots this character occupies. Decides the <c>&lt;Picture n&gt;</c>
        /// numbering, so it has to be known before the prompt is written, not at submit time.</summary>
        public int PanelCount => _panels.Count;

        public string SheetStatus =>
            !HasSource ? "No image" :
            !HasSheet ? "Sheet not built yet" :
            UseSourceAsSheet ? "Using the loaded image as the sheet — its clothing is whatever the image shows" :
            _expectedWardrobe.Length == 0 ? "Sheet ready" :
            SheetMatchesWardrobe ? "Sheet ready — dressed in the locked wardrobe" :
            _sheetWardrobe.Length == 0 ? "Sheet was built before the wardrobe was locked — rebuild it"
                                       : "Wardrobe changed since this sheet was built — rebuild it";

        public string PanelStatus =>
            !HasSheet ? string.Empty :
            _panels.WasSplit ? $"→ H3 gets {_panels.Count} separate references ({_panels.Note})"
                             : $"→ H3 gets 1 reference ({_panels.Note})";

        /// <summary>
        /// Takes a freshly built (or loaded) sheet. <paramref name="wardrobe"/> is the outfit it was generated
        /// wearing — empty for an image the user supplied, whose clothing nothing here can vouch for.
        /// </summary>
        public void SetSheet(string path, string? wardrobe = null)
        {
            _sheetPath = path;
            _sheetWardrobe = (wardrobe ?? string.Empty).Trim();
            _sheetPreview = _loadPreview(path, out _);
            RebuildPanels();
            Raise(nameof(SheetPath), nameof(SheetPreview), nameof(HasSheet), nameof(SheetStatus),
                  nameof(SheetWardrobe), nameof(SheetMatchesWardrobe),
                  nameof(PanelCount), nameof(PanelStatus));
        }

        /// <summary>
        /// Cuts the current sheet up again. Done here, when the sheet lands, rather than at submit time,
        /// because the panel count is what the prompt's picture numbering is built from — the tab has to know
        /// it while the prompt is still being written.
        /// </summary>
        private void RebuildPanels() =>
            _panels = HasSheet ? CharacterSheetSplitter.Split(_sheetPath, _panelSplit) : SheetPanels.Empty;

        public void Clear()
        {
            _useSourceAsSheet = false;
            _sheetPath = string.Empty;
            _sheetPreview = null;
            _sheetWardrobe = string.Empty;
            // The part belongs to the character in the slot, not to the slot: clearing the photo is how an
            // ensemble tab says "this character is not in the film", and leaving their part behind would
            // put them back into the next wardrobe pass. The kind goes back to its default for the same
            // reason — a slot that held a cloud must not silently dress the next photo as one.
            _role = string.Empty;
            _kind = Index % 2 == 0 ? Female : Male;
            _panels = SheetPanels.Empty;
            SourcePath = string.Empty;
            Raise(nameof(UseSourceAsSheet), nameof(SheetPath), nameof(SheetPreview),
                  nameof(HasSheet), nameof(SheetStatus), nameof(SheetWardrobe),
                  nameof(SheetMatchesWardrobe), nameof(PanelCount), nameof(PanelStatus),
                  nameof(Role), nameof(HasRole), nameof(Label), nameof(Descriptor),
                  nameof(Description), nameof(Kind), nameof(Sex), nameof(Noun),
                  nameof(IsPerson), nameof(IsGroup), nameof(HasFace), nameof(Pronoun),
                  nameof(IsCast), nameof(CanGeneratePhoto));
        }

        /// <summary>Whether this card's ✨ Generate button may run: the slot has to say who the
        /// character is (a Part, a non-person Kind, or a photo — i.e. <see cref="IsCast"/>), and
        /// no photo for it can already be in flight.</summary>
        public bool CanGeneratePhoto => IsCast && !IsGeneratingPhoto;

        /// <summary>Set by the ensemble tab while it renders this character's photo with an Image
        /// Generator workflow. Blocks a second run for the same card while the first is in flight.</summary>
        public bool IsGeneratingPhoto
        {
            get => _isGeneratingPhoto;
            set
            {
                if (_isGeneratingPhoto == value) return;
                _isGeneratingPhoto = value;
                Raise(nameof(IsGeneratingPhoto), nameof(CanGeneratePhoto), nameof(PhotoPhase));
            }
        }

        /// <summary>What this card's photo generation is doing right now — shown under the ✨ Generate
        /// button. Raised directly rather than through <see cref="Raise"/> on purpose: it ticks with
        /// the sampler's progress, and must not retrigger the tab-level change handlers every step.</summary>
        public string PhotoPhase
        {
            get => _photoPhase;
            set
            {
                var v = value ?? string.Empty;
                if (_photoPhase == v) return;
                _photoPhase = v;
                PropertyChanged?.Invoke(this, new(nameof(PhotoPhase)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        private void Raise(params string[] names)
        {
            foreach (var name in names)
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            _onChanged();
        }
    }

    /// <summary>One entry of a character's "how do I cut this sheet up" dropdown.</summary>
    public record PanelSplitOption(int Value, string Label);
}
