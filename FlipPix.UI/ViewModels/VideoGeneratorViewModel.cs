using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels.Video;
using Microsoft.Extensions.DependencyInjection;

namespace FlipPix.UI.ViewModels
{
    /// <summary>
    /// Composer ViewModel for the Video Generator window: one sub-ViewModel per tab.
    ///
    /// MainVM and InfiniteTalkVM predate the per-tab DataContext pattern, so this class
    /// also forwards their properties and commands under the flat names their XAML binds
    /// to. Newer tabs bind through their own VM property instead (DataContext={Binding
    /// Scail2VM} and friends) and need no forwarding here.
    /// </summary>
    public partial class VideoGeneratorViewModel : ObservableObject, IDisposable
    {
        private bool _disposed = false;
        private readonly ComfyUIService _comfyUIService;
        private readonly LMStudioService _lmStudioService;
        private readonly IAppLogger _logger;
        private readonly FlipPix.Core.Services.SettingsService _settingsService;
        private readonly IServiceProvider? _serviceProvider;
        private readonly WorkflowQueueCoordinator _workflowCoordinator;
        private readonly IFileDialogService _fileDialogService;

        public event EventHandler? PlayRequested;

        /// <summary>
        /// Main video generation ViewModel - handles core i2v with first/last frame,
        /// video settings, image analysis, prompt queue, and story video queue.
        /// </summary>
        public VideoGeneratorMainViewModel MainVM { get; }

        /// <summary>
        /// InfiniteTalk video generation ViewModel - handles Wan2.1 InfiniteTalk
        /// video generation with audio-driven 81-frame chunk processing.
        /// </summary>
        public InfiniteTalkViewModel InfiniteTalkVM { get; }

        /// <summary>
        /// Scail 2 - unified char-swap (Klein) → SCAIL II motion-transfer flow on one tab.
        /// </summary>
        public Scail2ViewModel Scail2VM { get; }
        /// <summary>
        /// VR 180 ViewModel - converts a flat video into a 360° equirectangular VR panorama
        /// by outpainting each frame with the LTX-2.3-22B equirect IC-LoRA.
        /// </summary>
        public Vr180ViewModel Vr180VM { get; }

        /// <summary>
        /// Video Sound ViewModel - upload a clip, analyze its first frame into a [VISUAL]/[SPEECH]/
        /// [SOUNDS] directing prompt, then re-generate it with synchronized speech + sound effects
        /// through the LTX-2.3 audio-video workflow (VideoSound.json).
        /// </summary>
        public VideoSoundViewModel VideoSoundVM { get; }

        /// <summary>
        /// 10Eros ConvRot ViewModel - single face-reference image + prompt, generate 4 LTX 2.3 FaceID
        /// seed previews (reroll for more), then re-render the chosen seed(s) at full resolution.
        /// </summary>
        public ErosConvRotViewModel ErosConvRotVM { get; }

        /// <summary>
        /// FaceID Character Sheet ViewModel - single-shot LTX 2.3 FaceID + Union-Control video from a
        /// character image (with image Analyze), an audio file, and a reference video (pose/depth/edge control).
        /// </summary>
        public FaceIdCharSheetViewModel FaceIdCharSheetVM { get; }

        /// <summary>
        /// MiniMax I2V ViewModel - MiniMax H3 in Ref2VA mode: up to four reference pictures and a draft
        /// idea become the six-field Ref2VA prompt, and one submission renders video with synchronized
        /// audio - optionally continued past H3's ~15s ceiling by up to three further passes that each
        /// pick up out of the tail of the one before.
        /// </summary>
        public MiniMaxI2VViewModel MiniMaxI2VVM { get; }

        /// <summary>
        /// MiniMax FFLF ViewModel - H3 in FL2VA mode, driven as a keyframe chain: an opening frame plus
        /// up to four stills the take has to pass through, one clip between each pair, rendered as the
        /// base pass plus continuation passes inside a single submission.
        /// </summary>
        public MiniMaxFflfViewModel MiniMaxFflfVM { get; }

        /// <summary>
        /// MiniMax H3 T2V ViewModel - the long-form variant: one image is analyzed into a dense ~15-second
        /// multi-shot H3 prompt, then generated either with the image conditioned as the first frame or as
        /// pure text-to-video with the image used only as inspiration for the prompt.
        /// </summary>
        public MiniMaxH3TextToVideoViewModel MiniMaxH3T2VVM { get; }

        /// <summary>
        /// MiniMax Character ViewModel - reference-to-video: one or two character images stay on model as
        /// H3 reference frames while a third "scene" image (never uploaded) is analyzed into the multi-shot
        /// prompt they act out, with optional Wan 2.2 / LTX 2.3 refinement passes.
        /// </summary>
        public MiniMaxCharacterViewModel MiniMaxCharacterVM { get; }

        /// <summary>
        /// H3 Duo ViewModel — the H3 Cast machinery (story → wardrobe → character sheets → chain of clip
        /// prompts) on the MiniMax I2V turbo render pipeline: each clip renders as a quarter-canvas draft,
        /// a 2× pass through the MiniMax H3 3D latent upscaler, and three fixed-sigma finish steps. Faster
        /// and sharper than the Cast tab's own graph, with no face-refine branch — identity is held by the
        /// full-fidelity references instead.
        /// </summary>
        public H3DuoViewModel H3DuoVM { get; }

        /// <summary>
        /// H3 Experimental ViewModel — a working copy of the H3 Duo tab kept deliberately separate so
        /// prompt (and graph) experiments can be tried without disturbing the Duo tab: same story →
        /// wardrobe → sheets → chained-clips flow, on its own copy of the turbo graph, with its own
        /// queue file and output folders.
        /// </summary>
        public H3ExperimentalViewModel H3ExperimentalVM { get; }

        /// <summary>
        /// H3 Eros ViewModel — the H3 Experimental story flow rendered through the EROS-Hybrid
        /// seed-hunter graph: every clip is sampled three times at a small draft canvas, the user picks
        /// one of the three, and only that latent is upscaled to the finished megapixels and joined into
        /// the story.
        /// </summary>
        public H3ErosViewModel H3ErosVM { get; }

        /// <summary>
        /// H3 4-Step ViewModel — the H3 Eros story flow on the author's 4-step SLA reference stack. It
        /// hunts three drafts per clip and stops there: the take that is picked IS the clip, because on a
        /// four-step checkpoint an upscale per clip costs more than the whole hunt. Every draft is written
        /// with a .seed.json recipe for <see cref="SeedUpscaleVM"/> to render big later.
        /// </summary>
        public H34StepViewModel H34StepVM { get; }

        /// <summary>
        /// H3 VR ViewModel — the H3 Eros hunt with the VR180 SBS LoRA on top, so a cast's reference
        /// photographs come back as a stereoscopic side-by-side pair a headset plays in 3D. The stereo is
        /// composed by the model, not derived from a depth map afterwards as in <see cref="Vr180VM"/>.
        /// </summary>
        public H3VrViewModel H3VrVM { get; }

        /// <summary>
        /// H3 Batch ViewModel — a folder of story .txt files, each one put through the whole 🍀 pipeline
        /// in turn: its own cast, sheets, clips, takes, upscale and join. The loop is the only thing this
        /// adds; every step inside it is the one the buttons already run.
        /// </summary>
        public H3BatchViewModel H3BatchVM { get; }

        /// <summary>
        /// H3 Express ViewModel — H3 Batch without the seed hunt: a folder of stories, each one's clips
        /// rendered straight through one at a time and joined. Singularity and researched prompts by default.
        /// </summary>
        public H3ExpressViewModel H3ExpressVM { get; }

        /// <summary>
        /// Seed Upscale ViewModel — scans a folder of drafts and their recipes, and re-renders only the
        /// ticked ones: the recorded seed is sampled back through the same stack to reproduce its latent,
        /// and that latent is what MinimaxH3LatentUpscaler3D lifts to the finished canvas.
        /// </summary>
        public SeedUpscaleViewModel SeedUpscaleVM { get; }

        /// <summary>
        /// H3 Chain ViewModel - MiniMax H3 run as an autoregressive chain: two reference images and a
        /// soundtrack become one continuous take of arbitrary length, rendered as N segments inside a
        /// single ComfyUI submission where each segment continues out of the last frame of the one
        /// before it, and assembled and muxed against the song by the workflow itself.
        /// </summary>
        public H3ChainViewModel H3ChainVM { get; }

        /// <summary>
        /// H3 Multi ViewModel — the same ensemble machinery (five cast slots, wardrobe, location,
        /// storyboard, chained clips) rendered through the MiniMax I2V turbo pipeline instead of the
        /// hybrid graph: each clip is a 4-step draft at a quarter of the canvas, a 2× pass through the
        /// MiniMax H3 3D latent upscaler, then 3 fixed-sigma finish steps. Quicker and sharper; no
        /// face-refine branch and no FILM interpolation — identity is held by encoding the reference
        /// panels at a 2048px short edge by default (max-fidelity references).
        /// </summary>
        public H3MultiViewModel H3MultiVM { get; }

        // Bound to the main TabControl so code can switch tabs programmatically.
        // 0 = Scail 2 tab.
        private int _selectedTabIndex = 0;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (_selectedTabIndex != value)
                {
                    _selectedTabIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public VideoGeneratorViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider = null,
            IFileDialogService? fileDialogService = null)
        {
            _comfyUIService = comfyUIService ?? throw new ArgumentNullException(nameof(comfyUIService));
            _lmStudioService = lmStudioService ?? throw new ArgumentNullException(nameof(lmStudioService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
            _serviceProvider = serviceProvider;
            _workflowCoordinator = serviceProvider?.GetRequiredService<WorkflowQueueCoordinator>()
                ?? throw new InvalidOperationException("WorkflowQueueCoordinator is required");
            _fileDialogService = fileDialogService ?? serviceProvider?.GetRequiredService<IFileDialogService>()
                ?? throw new InvalidOperationException("IFileDialogService is required");

            // Initialize sub-ViewModels
            MainVM = new VideoGeneratorMainViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            InfiniteTalkVM = new InfiniteTalkViewModel(
                comfyUIService,
                logger,
                lmStudioService,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            Scail2VM = new Scail2ViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            Vr180VM = new Vr180ViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            VideoSoundVM = new VideoSoundViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            ErosConvRotVM = new ErosConvRotViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            FaceIdCharSheetVM = new FaceIdCharSheetViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            MiniMaxI2VVM = new MiniMaxI2VViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            MiniMaxFflfVM = new MiniMaxFflfViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            MiniMaxH3T2VVM = new MiniMaxH3TextToVideoViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            MiniMaxCharacterVM = new MiniMaxCharacterViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3DuoVM = new H3DuoViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3ExperimentalVM = new H3ExperimentalViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3ErosVM = new H3ErosViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H34StepVM = new H34StepViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3VrVM = new H3VrViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3BatchVM = new H3BatchViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3ExpressVM = new H3ExpressViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            SeedUpscaleVM = new SeedUpscaleViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3ChainVM = new H3ChainViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            H3MultiVM = new H3MultiViewModel(
                comfyUIService,
                lmStudioService,
                logger,
                settingsService,
                serviceProvider,
                _workflowCoordinator,
                _fileDialogService);

            // Forward PlayRequested events from sub-VMs
            MainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            InfiniteTalkVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            Scail2VM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            Vr180VM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            VideoSoundVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            ErosConvRotVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            FaceIdCharSheetVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxI2VVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxFflfVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxH3T2VVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            MiniMaxCharacterVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3DuoVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3ExperimentalVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3ErosVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H34StepVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3VrVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3BatchVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3ExpressVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            SeedUpscaleVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3ChainVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);
            H3MultiVM.PlayRequested += (s, e) => PlayRequested?.Invoke(this, e);

            // Forward PropertyChanged events from all sub-VMs for backward compatibility
            MainVM.PropertyChanged += ForwardPropertyChanged;
            InfiniteTalkVM.PropertyChanged += ForwardPropertyChanged;
            Scail2VM.PropertyChanged += ForwardPropertyChanged;
            Vr180VM.PropertyChanged += ForwardPropertyChanged;
            VideoSoundVM.PropertyChanged += ForwardPropertyChanged;
            ErosConvRotVM.PropertyChanged += ForwardPropertyChanged;
            FaceIdCharSheetVM.PropertyChanged += ForwardPropertyChanged;
            MiniMaxI2VVM.PropertyChanged += ForwardPropertyChanged;
            MiniMaxFflfVM.PropertyChanged += ForwardPropertyChanged;
            MiniMaxH3T2VVM.PropertyChanged += ForwardPropertyChanged;
            MiniMaxCharacterVM.PropertyChanged += ForwardPropertyChanged;
            H3DuoVM.PropertyChanged += ForwardPropertyChanged;
            H3ExperimentalVM.PropertyChanged += ForwardPropertyChanged;
            H3ErosVM.PropertyChanged += ForwardPropertyChanged;
            H34StepVM.PropertyChanged += ForwardPropertyChanged;
            H3VrVM.PropertyChanged += ForwardPropertyChanged;
            H3BatchVM.PropertyChanged += ForwardPropertyChanged;
            H3ExpressVM.PropertyChanged += ForwardPropertyChanged;
            SeedUpscaleVM.PropertyChanged += ForwardPropertyChanged;
            H3ChainVM.PropertyChanged += ForwardPropertyChanged;
            H3MultiVM.PropertyChanged += ForwardPropertyChanged;

            NavigateToImageGeneratorCommand = new RelayCommand(NavigateToImageGenerator);

            _logger.LogInfo("VideoGeneratorViewModel initialized with sub-ViewModels");
        }

        /// <summary>
        /// Opens the Image Generator window from the Video Generator's navigation bar.
        /// </summary>
        public ICommand NavigateToImageGeneratorCommand { get; }

        private void NavigateToImageGenerator()
        {
            if (_serviceProvider == null) return;

            try
            {
                if (_serviceProvider.GetService(typeof(ImageGeneratorWindow)) is ImageGeneratorWindow imageGeneratorWindow)
                {
                    imageGeneratorWindow.WindowState = System.Windows.WindowState.Normal;

                    // Ensure the window opens on screen with title bar visible
                    var screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
                    var screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
                    var windowWidth = imageGeneratorWindow.Width;
                    var windowHeight = imageGeneratorWindow.Height;

                    imageGeneratorWindow.Left = Math.Max(100, (screenWidth - windowWidth) / 2 - 200);
                    imageGeneratorWindow.Top = Math.Max(100, (screenHeight - windowHeight) / 2 - 100);

                    if (imageGeneratorWindow.Top < 50) imageGeneratorWindow.Top = 50;
                    if (imageGeneratorWindow.Left < 50) imageGeneratorWindow.Left = 50;
                    if (imageGeneratorWindow.Top + windowHeight > screenHeight - 50)
                        imageGeneratorWindow.Top = screenHeight - windowHeight - 50;
                    if (imageGeneratorWindow.Left + windowWidth > screenWidth - 50)
                        imageGeneratorWindow.Left = screenWidth - windowWidth - 50;

                    imageGeneratorWindow.Show();
                    imageGeneratorWindow.Activate();
                }
                else
                {
                    _logger.LogError("Failed to resolve Image Generator window");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error navigating to Image Generator: {ex.Message}");
            }
        }

        private void ForwardPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == null) return;

            // Re-fire with the original property name (handles any direct-name bindings).
            OnPropertyChanged(e.PropertyName);

            // Re-fire with empty string to refresh ALL bindings on this DataContext.
            // This is required because the parent VM exposes aliased pass-through properties
            // (e.g. InfiniteTalkPrompt → InfiniteTalkVM.Prompt). When the sub-VM fires
            // PropertyChanged("Prompt"), the XAML binding on InfiniteTalkPrompt would
            // otherwise never see the notification.
            OnPropertyChanged(string.Empty);
        }

        #region MainVM Backward Compatibility Properties

        // First Frame Image properties
        public string FirstFrameImagePath { get => MainVM.FirstFrameImagePath; set => MainVM.FirstFrameImagePath = value; }
        public BitmapImage? FirstFrameImagePreview { get => MainVM.FirstFrameImagePreview; set => MainVM.FirstFrameImagePreview = value; }
        public string FirstFrameImageInfo { get => MainVM.FirstFrameImageInfo; set => MainVM.FirstFrameImageInfo = value; }
        public bool HasFirstFrameImage => MainVM.HasFirstFrameImage;

        // Last Frame Image properties
        public string LastFrameImagePath { get => MainVM.LastFrameImagePath; set => MainVM.LastFrameImagePath = value; }
        public BitmapImage? LastFrameImagePreview { get => MainVM.LastFrameImagePreview; set => MainVM.LastFrameImagePreview = value; }
        public string LastFrameImageInfo { get => MainVM.LastFrameImageInfo; set => MainVM.LastFrameImageInfo = value; }
        public bool HasLastFrameImage => MainVM.HasLastFrameImage;

        // Image properties
        public string ImageFilePath { get => MainVM.ImageFilePath; set => MainVM.ImageFilePath = value; }
        public BitmapImage? ImagePreviewSource { get => MainVM.ImagePreviewSource; set => MainVM.ImagePreviewSource = value; }
        public string ImageInfo { get => MainVM.ImageInfo; set => MainVM.ImageInfo = value; }
        public bool HasImage => MainVM.HasImage;

        // Prompt properties
        public string VideoPrompt { get => MainVM.VideoPrompt; set => MainVM.VideoPrompt = value; }
        public string NegativePrompt { get => MainVM.NegativePrompt; set => MainVM.NegativePrompt = value; }

        // Video Settings
        public int VideoLength { get => MainVM.VideoLength; set => MainVM.VideoLength = value; }
        public string VideoLengthSeconds => MainVM.VideoLengthSeconds;
        public int Fps { get => MainVM.Fps; set => MainVM.Fps = value; }
        public int Steps { get => MainVM.Steps; set => MainVM.Steps = value; }
        public double Cfg { get => MainVM.Cfg; set => MainVM.Cfg = value; }
        public long Seed { get => MainVM.Seed; set => MainVM.Seed = value; }
        public int Width { get => MainVM.Width; set => MainVM.Width = value; }
        public int Height { get => MainVM.Height; set => MainVM.Height = value; }

        // Processing state
        public bool IsProcessing { get => MainVM.IsProcessing; set => MainVM.IsProcessing = value; }
        public string ProcessingStatus { get => MainVM.ProcessingStatus; set => MainVM.ProcessingStatus = value; }
        public double ProcessingProgress { get => MainVM.ProcessingProgress; set => MainVM.ProcessingProgress = value; }
        public string ProgressPercentage => MainVM.ProgressPercentage;
        public string LogOutput => MainVM.LogOutput;
        public string StatusBarMessage { get => MainVM.StatusBarMessage; set => MainVM.StatusBarMessage = value; }
        public string VideoInfo { get => MainVM.VideoInfo; set => MainVM.VideoInfo = value; }

        // Result state
        public bool HasResultVideo { get => MainVM.HasResult; set => MainVM.HasResult = value; }
        public string ResultVideoPath { get => MainVM.ResultVideoPath; set => MainVM.ResultVideoPath = value; }

        // Image analysis properties
        public bool IsAnalyzing { get => MainVM.IsAnalyzing; set => MainVM.IsAnalyzing = value; }
        public string AnalysisStatus { get => MainVM.AnalysisStatus; set => MainVM.AnalysisStatus = value; }
        public double AnalysisProgress { get => MainVM.AnalysisProgress; set => MainVM.AnalysisProgress = value; }
        public string ImageAnalysis { get => MainVM.ImageAnalysis; set => MainVM.ImageAnalysis = value; }
        public bool HasAnalysis => MainVM.HasAnalysis;

        // Queue properties
        public string NewQueuePrompt { get => MainVM.NewQueuePrompt; set => MainVM.NewQueuePrompt = value; }
        public ObservableCollection<QueueItem> PromptQueue => MainVM.PromptQueue;
        public bool IsProcessingQueue { get => MainVM.IsProcessingQueue; set => MainVM.IsProcessingQueue = value; }
        public bool IsQueuePaused { get => MainVM.IsQueuePaused; set => MainVM.IsQueuePaused = value; }
        public bool CanProcessQueue => MainVM.CanProcessQueue;
        public bool CanAddToQueue => MainVM.CanAddToQueue;
        public bool HasFailedItems => MainVM.HasFailedItems;
        public string QueueStatus { get => MainVM.QueueStatus; set => MainVM.QueueStatus = value; }
        public bool CanGenerateVideo => MainVM.CanGenerateVideo;

        // Story Video Generator Properties
        public string StoryPromptJsonPath { get => MainVM.StoryPromptJsonPath; set => MainVM.StoryPromptJsonPath = value; }
        public string StoryImagesFolderPath { get => MainVM.StoryImagesFolderPath; set => MainVM.StoryImagesFolderPath = value; }
        public ObservableCollection<StoryVideoQueueItem> StoryVideoQueue => MainVM.StoryVideoQueue;
        public bool IsProcessingStoryQueue { get => MainVM.IsProcessingStoryQueue; set => MainVM.IsProcessingStoryQueue = value; }
        public bool IsStoryQueuePaused { get => MainVM.IsStoryQueuePaused; set => MainVM.IsStoryQueuePaused = value; }
        public StoryVideoQueueItem? CurrentStoryQueueItem { get => MainVM.CurrentStoryQueueItem; set => MainVM.CurrentStoryQueueItem = value; }
        public int StoryQueueProgress { get => MainVM.StoryQueueProgress; set => MainVM.StoryQueueProgress = value; }
        public int StoryQueueTotal { get => MainVM.StoryQueueTotal; set => MainVM.StoryQueueTotal = value; }
        public string StoryQueueProgressText => MainVM.StoryQueueProgressText;
        public bool CanLoadStoryQueue => MainVM.CanLoadStoryQueue;
        public bool CanProcessStoryQueue => MainVM.CanProcessStoryQueue;
        public string StoryQueueStatus { get => MainVM.StoryQueueStatus; set => MainVM.StoryQueueStatus = value; }

        // Workflow selection
        public string SelectedWorkflow { get => MainVM.SelectedWorkflow; set => MainVM.SelectedWorkflow = value; }
        public bool UseLTXWorkflow { get => MainVM.UseLTXWorkflow; set => MainVM.UseLTXWorkflow = value; }
        public string WorkflowDisplay => MainVM.WorkflowDisplay;
        public string WorkflowIndicator => MainVM.WorkflowIndicator;
        public int SelectedWorkflowIndex { get => MainVM.SelectedWorkflowIndex; set => MainVM.SelectedWorkflowIndex = value; }

        // Story Video Generator Workflow (Tab 2)
        public int SelectedStoryWorkflowIndex
        {
            get => MainVM.SelectedStoryWorkflowIndex;
            set => MainVM.SelectedStoryWorkflowIndex = value;
        }

        // Single Video Generator Workflow (Tab 1) - separate from Story Video (Tab 2)
        public VideoGeneratorMainViewModel.SingleVideoWorkflow SelectedSingleWorkflow
        {
            get => MainVM.SelectedSingleWorkflow;
            set => MainVM.SelectedSingleWorkflow = value;
        }
        public string SingleWorkflowDisplay => MainVM.SingleWorkflowDisplay;
        public bool UseLTX2V => MainVM.UseLTX2V;
        public bool UseWan22 => MainVM.UseWan22;

        // UI state
        public string ComfyUIServer { get => MainVM.ComfyUIServer; set => MainVM.ComfyUIServer = value; }
        public string ComfyUIPort { get => MainVM.ComfyUIPort; set => MainVM.ComfyUIPort = value; }

        // MainVM Commands
        public ICommand SelectImageCommand => MainVM.SelectImageCommand;
        public ICommand SelectFirstFrameImageCommand => MainVM.SelectFirstFrameImageCommand;
        public ICommand SelectLastFrameImageCommand => MainVM.SelectLastFrameImageCommand;
        public ICommand GenerateVideoCommand => MainVM.GenerateVideoCommand;
        public ICommand PlayVideoCommand => MainVM.PlayVideoCommand;
        public ICommand OpenResultFolderCommand => MainVM.OpenResultFolderCommand;
        public ICommand SendToEditCameraCommand => MainVM.SendToEditCameraCommand;
        public ICommand AnalyzeImageCommand => MainVM.AnalyzeImageCommand;
        public ICommand AnalyzeFirstFrameImageCommand => MainVM.AnalyzeFirstFrameImageCommand;
        public ICommand SendAnalysisToQueueCommand => MainVM.SendAnalysisToQueueCommand;
        public ICommand OpenLMStudioSettingsCommand => MainVM.OpenLMStudioSettingsCommand;
        public ICommand CopyAnalysisCommand => MainVM.CopyAnalysisCommand;
        public ICommand AddToQueueCommand => MainVM.AddToQueueCommand;
        public ICommand RemoveFromQueueCommand => MainVM.RemoveFromQueueCommand;
        public ICommand ProcessQueueCommand => MainVM.ProcessQueueCommand;
        public ICommand ClearQueueCommand => MainVM.ClearQueueCommand;
        public ICommand StopQueueCommand => MainVM.StopQueueCommand;
        public ICommand ReprocessItemCommand => MainVM.ReprocessItemCommand;
        public ICommand ReprocessAllFailedCommand => MainVM.ReprocessAllFailedCommand;
        public ICommand PauseQueueCommand => MainVM.PauseQueueCommand;
        public ICommand ResumeQueueCommand => MainVM.ResumeQueueCommand;
        public ICommand SelectStoryPromptJsonCommand => MainVM.SelectStoryPromptJsonCommand;
        public ICommand SelectStoryImagesFolderCommand => MainVM.SelectStoryImagesFolderCommand;
        public ICommand LoadStoryQueueCommand => MainVM.LoadStoryQueueCommand;
        public ICommand ProcessStoryQueueCommand => MainVM.ProcessStoryQueueCommand;
        public ICommand ClearStoryQueueCommand => MainVM.ClearStoryQueueCommand;
        public ICommand StopStoryQueueCommand => MainVM.StopStoryQueueCommand;
        public ICommand ReprocessAllStoryFailedCommand => MainVM.ReprocessAllStoryFailedCommand;
        public bool HasStoryFailedItems => MainVM.HasStoryFailedItems;
        public ICommand PauseStoryQueueCommand => MainVM.PauseStoryQueueCommand;
        public ICommand ResumeStoryQueueCommand => MainVM.ResumeStoryQueueCommand;
        public ICommand ToggleWorkflowCommand => MainVM.ToggleWorkflowCommand;
        public ICommand ToggleSingleWorkflowCommand => MainVM.ToggleSingleWorkflowCommand;

        #endregion

        #region InfiniteTalkVM Backward Compatibility Properties

        public string InfiniteTalkImagePath { get => InfiniteTalkVM.ImagePath; set => InfiniteTalkVM.ImagePath = value; }
        public BitmapImage? InfiniteTalkImagePreview { get => InfiniteTalkVM.ImagePreview; set => InfiniteTalkVM.ImagePreview = value; }
        public string InfiniteTalkImageInfo { get => InfiniteTalkVM.ImageInfo; set => InfiniteTalkVM.ImageInfo = value; }
        public string InfiniteTalkAudioPath { get => InfiniteTalkVM.AudioPath; set => InfiniteTalkVM.AudioPath = value; }
        public string InfiniteTalkAudioInfo { get => InfiniteTalkVM.AudioInfo; set => InfiniteTalkVM.AudioInfo = value; }
        public string InfiniteTalkPrompt { get => InfiniteTalkVM.Prompt; set => InfiniteTalkVM.Prompt = value; }
        public int InfiniteTalkWidth { get => InfiniteTalkVM.Width; set => InfiniteTalkVM.Width = value; }
        public int InfiniteTalkHeight { get => InfiniteTalkVM.Height; set => InfiniteTalkVM.Height = value; }
        public bool IsProcessingInfiniteTalk { get => InfiniteTalkVM.IsProcessing; set => InfiniteTalkVM.IsProcessing = value; }
        public string InfiniteTalkProcessingStatus => InfiniteTalkVM.ProcessingStatus;
        public double InfiniteTalkProcessingProgress => InfiniteTalkVM.ProcessingProgress;
        public string InfiniteTalkLogOutput => InfiniteTalkVM.LogOutput;
        public bool HasInfiniteTalkResult => InfiniteTalkVM.HasResult;
        public string InfiniteTalkResultPath => InfiniteTalkVM.ResultVideoPath;
        public string InfiniteTalkVideoInfo => InfiniteTalkVM.ResultVideoInfo;
        public double InfiniteTalkAudioDuration => InfiniteTalkVM.AudioDuration;
        public int InfiniteTalkTotalFrames => InfiniteTalkVM.TotalFrames;
        public int InfiniteTalkTotalChunks => InfiniteTalkVM.TotalChunks;
        public bool CanGenerateInfiniteTalkVideo => InfiniteTalkVM.CanGenerateVideo;
        public string InfiniteTalkEstimatedDuration => InfiniteTalkVM.EstimatedDuration;
        public string InfiniteTalkProgressPercentage => InfiniteTalkVM.ProgressPercentage;

        // InfiniteTalkVM Commands
        public ICommand SelectInfiniteTalkImageCommand => InfiniteTalkVM.SelectImageCommand;
        public ICommand SelectInfiniteTalkAudioCommand => InfiniteTalkVM.SelectAudioCommand;
        public ICommand GenerateInfiniteTalkVideoCommand => InfiniteTalkVM.GenerateVideoCommand;
        public ICommand PlayInfiniteTalkVideoCommand => InfiniteTalkVM.PlayVideoCommand;
        public ICommand OpenInfiniteTalkResultFolderCommand => InfiniteTalkVM.OpenResultFolderCommand;
        public ICommand SendInfiniteTalkToEditCameraCommand => InfiniteTalkVM.SendToEditCameraCommand;

        // LMStudio AI analysis properties
        public bool InfiniteTalkIsAnalyzing   => InfiniteTalkVM.IsAnalyzing;
        public bool CanInfiniteTalkAnalyzeImage    => InfiniteTalkVM.CanAnalyzeImage;
        public bool CanInfiniteTalkEnhancePrompt   => InfiniteTalkVM.CanEnhancePrompt;
        public bool ShowInfiniteTalkVideoPrompt => InfiniteTalkVM.ShowVideoPrompt;
        public string InfiniteTalkAnalysisResult => InfiniteTalkVM.AnalysisResult;
        public bool HasInfiniteTalkAnalysis => InfiniteTalkVM.HasAnalysis;

        // LMStudio AI analysis commands
        public ICommand AnalyzeInfiniteTalkImageCommand  => InfiniteTalkVM.AnalyzeImageCommand;
        public ICommand EnhanceInfiniteTalkPromptCommand => InfiniteTalkVM.EnhancePromptCommand;

        #endregion

        #region Public Methods

        /// <summary>
        /// Sets the image path from external sources (e.g., edit camera).
        /// </summary>
        public void SetImagePath(string imagePath)
        {
            // Load the image as MiniMax I2V's <Picture 1> and bring that tab to the front,
            // so the user lands on it with the image already loaded.
            MiniMaxI2VVM.PrimaryReferencePath = imagePath;
            SelectedTabIndex = 2; // MiniMax I2V tab
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            if (!_disposed)
            {
                // Unsubscribe from events
                MainVM.PropertyChanged -= ForwardPropertyChanged;
                InfiniteTalkVM.PropertyChanged -= ForwardPropertyChanged;
                Vr180VM.PropertyChanged -= ForwardPropertyChanged;
                VideoSoundVM.PropertyChanged -= ForwardPropertyChanged;
                ErosConvRotVM.PropertyChanged -= ForwardPropertyChanged;
                FaceIdCharSheetVM.PropertyChanged -= ForwardPropertyChanged;
                MiniMaxI2VVM.PropertyChanged -= ForwardPropertyChanged;
                MiniMaxFflfVM.PropertyChanged -= ForwardPropertyChanged;
                MiniMaxH3T2VVM.PropertyChanged -= ForwardPropertyChanged;
                MiniMaxCharacterVM.PropertyChanged -= ForwardPropertyChanged;
                H3DuoVM.PropertyChanged -= ForwardPropertyChanged;
                H3ExperimentalVM.PropertyChanged -= ForwardPropertyChanged;
                H3ErosVM.PropertyChanged -= ForwardPropertyChanged;
                H34StepVM.PropertyChanged -= ForwardPropertyChanged;
                H3VrVM.PropertyChanged -= ForwardPropertyChanged;
                H3BatchVM.PropertyChanged -= ForwardPropertyChanged;
                H3ExpressVM.PropertyChanged -= ForwardPropertyChanged;
                SeedUpscaleVM.PropertyChanged -= ForwardPropertyChanged;
                H3ChainVM.PropertyChanged -= ForwardPropertyChanged;
                H3MultiVM.PropertyChanged -= ForwardPropertyChanged;

                // Dispose all sub-ViewModels
                (MainVM as IDisposable)?.Dispose();
                (InfiniteTalkVM as IDisposable)?.Dispose();
                (Vr180VM as IDisposable)?.Dispose();
                (VideoSoundVM as IDisposable)?.Dispose();
                (ErosConvRotVM as IDisposable)?.Dispose();
                (FaceIdCharSheetVM as IDisposable)?.Dispose();
                (MiniMaxI2VVM as IDisposable)?.Dispose();
                (MiniMaxFflfVM as IDisposable)?.Dispose();
                (MiniMaxH3T2VVM as IDisposable)?.Dispose();
                (MiniMaxCharacterVM as IDisposable)?.Dispose();
                (H3DuoVM as IDisposable)?.Dispose();
                (H3ExperimentalVM as IDisposable)?.Dispose();
                (H3ErosVM as IDisposable)?.Dispose();
                (H34StepVM as IDisposable)?.Dispose();
                (H3VrVM as IDisposable)?.Dispose();
                (H3BatchVM as IDisposable)?.Dispose();
                (H3ExpressVM as IDisposable)?.Dispose();
                (SeedUpscaleVM as IDisposable)?.Dispose();
                (H3ChainVM as IDisposable)?.Dispose();
                (H3MultiVM as IDisposable)?.Dispose();

                _disposed = true;
            }
        }

        #endregion

    }
}
