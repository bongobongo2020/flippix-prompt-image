using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "Seed Upscale" tab — points at a folder of drafts, shows what it finds, and re-renders only the ones
    /// you tick, at whatever size you ask for.
    ///
    /// <para><b>The problem it solves.</b> 🌀4️⃣ H3 4-Step deliberately stops at the pick: on a four-step
    /// checkpoint the hunt is so cheap that upscaling every clip is the whole cost of a run, and most of
    /// those clips are not ones anyone would export at full size. So the upscale is moved out of the render
    /// loop entirely and becomes something you do later, to the handful of takes that turned out to be
    /// keepers, in one batch.</para>
    ///
    /// <para><b>Why it is not just "upscale this mp4".</b> There is no latent left in a finished video, and
    /// a pixel-space enlargement of a 576×320 draft is not the same picture rendered bigger. What this tab
    /// does instead is <i>reproduce</i> the draft: it samples the recorded seed back through the same stack
    /// — same checkpoint, steps, sampler, scheduler, prompt, references and draft canvas — which is
    /// deterministic and therefore lands on the same latent, and <b>that</b> goes through
    /// <c>MinimaxH3LatentUpscaler3D</c> and a short fixed-sigma second pass. Everything needed to do that is
    /// in the <see cref="SeedRecipe"/> sidecar written beside each draft, which is why the scan looks for
    /// <c>*.seed.json</c> rather than for videos: a draft with no recipe cannot be reproduced, only
    /// re-rolled, and showing it would be offering something this tab cannot deliver.</para>
    ///
    /// <para><b>Derived from <see cref="H3CastViewModel"/> for its plumbing, not its UI.</b> The submit,
    /// poll, output-resolution, upload, prune and FFmpeg helpers this needs are all protected members
    /// there, single-sourced, and copying them — which is what the older standalone tabs did — is how they
    /// drift. None of the cast, story or queue surface is bound by this tab's page; the queue file path is
    /// overridden so it cannot touch H3 Cast's.</para>
    /// </summary>
    public class SeedUpscaleViewModel : H3CastViewModel, ISeedUpscaleHost
    {
        private readonly ObservableCollection<SeedUpscaleItem> _all = new();
        private readonly IFileDialogService _dialogs;

        private string _scanFolder = string.Empty;
        private bool _includeSubfolders = true;
        private bool _hideAlreadyUpscaled;
        private bool _folderExists;
        private string _filterText = string.Empty;
        private string _scanStatus = string.Empty;
        private bool _isScanning;
        private bool _isUpscaling;
        private double _targetMegapixels = 1.0;
        private int _upscaleSteps = 4;
        private bool _useRife = true;
        private string? _activePreviewUri;
        private string _activePreviewLabel = string.Empty;
        private bool _isPreviewMuted = true;
        private double _tileSize = 132;
        private CancellationTokenSource? _runCts;

        public SeedUpscaleViewModel(
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
            _dialogs = fileDialogService;

            BrowseFolderCommand = new RelayCommand(() => _ = BrowseFolderAsync(),
                                                   () => !IsScanning && !IsUpscaling);
            RescanCommand = new RelayCommand(() => _ = ScanAsync(), () => !IsScanning && !IsUpscaling);
            SelectAllCommand = new RelayCommand(() => SetAllSelected(true), () => Items.Count > 0);
            SelectNoneCommand = new RelayCommand(() => SetAllSelected(false), () => HasSelection);
            InvertSelectionCommand = new RelayCommand(InvertSelection, () => Items.Count > 0);
            UpscaleSelectedCommand = new RelayCommand(() => _ = RunAsync(), () => HasSelection && !IsUpscaling);
            StopUpscaleCommand = new RelayCommand(StopRun, () => IsUpscaling);
            ToggleMuteCommand = new RelayCommand(() => IsPreviewMuted = !IsPreviewMuted);
            ZoomTilesInCommand = new RelayCommand(() => TileSize += TileSizeStep, () => TileSize < MaxTileSize);
            ZoomTilesOutCommand = new RelayCommand(() => TileSize -= TileSizeStep, () => TileSize > MinTileSize);
            OpenScanFolderCommand = new RelayCommand(OpenScanFolder, () => HasScanFolder);

            _scanFolder = settingsService.Settings?.SeedUpscaleFolder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(_scanFolder)) _scanFolder = DefaultScanFolder();

            AddLog("Seed Upscale initialized — point it at a folder of H3 4-Step drafts, tick the keepers, " +
                   "and their seeds are re-sampled and latent-upscaled to the size you choose.");

            // Off the constructor's thread: the scan walks a folder and reads a file per draft, and this
            // tab is on the Video Generator's startup path.
            _ = ScanAsync();
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The reproduce-and-upscale half of the 4-step SLA stack, built by
        /// <c>tools/build_h3_4step.py</c>. One sampler branch, then the upscaler and the second pass.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-seed-upscale.json";

        protected override string OutputSubfolder => "h3_seed_upscale";

        protected override string OutputFileStem => "H3SeedUpscale";

        /// <summary>The subfolder of the output folder this tab writes into. Not an override: the two
        /// naming hooks below are declared on <c>H3DuoViewModel</c>, further down the H3 family than this
        /// tab sits, and nothing in <see cref="H3CastViewModel"/> reads them.</summary>
        private const string OutputFolderName = "SeedUpscale";

        private const string TabDisplayName = "Seed Upscale";

        /// <summary>Its own file, so the inherited queue machinery — which this tab does not use — can
        /// never load or overwrite H3 Cast's.</summary>
        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "seedupscale_queue.json");

        // ── The folder ──────────────────────────────────────────────────────────────────────────────

        /// <summary>Where H3 4-Step copies its picked clips, and therefore where their recipes end up.</summary>
        private string DefaultScanFolder()
        {
            var root = _settingsService.Settings?.OutputFolderPath;
            return string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, "H34Step");
        }

        public string ScanFolder
        {
            get => _scanFolder;
            set
            {
                if (_scanFolder == value) return;
                _scanFolder = value ?? string.Empty;
                OnPropertyChanged();
                // HasScanFolder is settled by the scan, off the UI thread — see ScanAsync.

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.SeedUpscaleFolder = _scanFolder;
                    _settingsService.SaveSettings(settings);
                }
                _ = ScanAsync();
            }
        }

        /// <summary>Whether the last scan found the folder. Cached rather than probed: WPF asks a
        /// command's CanExecute constantly, and a Directory.Exists against a dead share on every one of
        /// those is the UI-thread stall this codebase keeps rediscovering.</summary>
        public bool HasScanFolder => _folderExists;

        /// <summary>On by default: a story's clips land in per-run subfolders as often as not, and a scan
        /// that stopped at the top level would show an empty board on a folder full of drafts.</summary>
        public bool IncludeSubfolders
        {
            get => _includeSubfolders;
            set { if (_includeSubfolders == value) return; _includeSubfolders = value; OnPropertyChanged(); _ = ScanAsync(); }
        }

        /// <summary>Hides drafts whose upscale is already sitting in the output folder — what makes a second
        /// pass over a big folder show only what is left to do.</summary>
        public bool HideAlreadyUpscaled
        {
            get => _hideAlreadyUpscaled;
            set { if (_hideAlreadyUpscaled == value) return; _hideAlreadyUpscaled = value; OnPropertyChanged(); ApplyFilter(); }
        }

        public string FilterText
        {
            get => _filterText;
            set { if (_filterText == value) return; _filterText = value ?? string.Empty; OnPropertyChanged(); ApplyFilter(); }
        }

        public RelayCommand BrowseFolderCommand { get; }
        public RelayCommand RescanCommand { get; }
        public RelayCommand OpenScanFolderCommand { get; }

        private async Task BrowseFolderAsync()
        {
            var picked = await _dialogs.OpenFolderDialogAsync(
                "Folder of H3 4-Step drafts to scan",
                HasScanFolder ? ScanFolder : null,
                persistKey: "seedupscale.scan");
            if (!string.IsNullOrWhiteSpace(picked)) ScanFolder = picked;
        }

        private void OpenScanFolder()
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ScanFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AddLog($"Could not open the folder: {ex.Message}"); }
        }

        // ── The board ───────────────────────────────────────────────────────────────────────────────

        /// <summary>What the board shows: <see cref="_all"/> after the search box and the "hide done"
        /// tick. Rebuilt rather than filtered in the view, so the counts under the buttons mean the rows
        /// that are actually on screen.</summary>
        public ObservableCollection<SeedUpscaleItem> Items { get; } = new();

        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (_isScanning == value) return;
                _isScanning = value;
                OnPropertyChanged();
                RefreshCommands();
            }
        }

        public bool IsUpscaling
        {
            get => _isUpscaling;
            private set
            {
                if (_isUpscaling == value) return;
                _isUpscaling = value;
                OnPropertyChanged();
                RefreshCommands();
            }
        }

        public string ScanStatus
        {
            get => _scanStatus;
            private set { if (_scanStatus == value) return; _scanStatus = value; OnPropertyChanged(); }
        }

        public int SelectedCount => Items.Count(i => i.IsSelected);

        public bool HasSelection => SelectedCount > 0;

        public bool HasItems => Items.Count > 0;

        public string SelectionSummary
        {
            get
            {
                if (_all.Count == 0) return "No recipes found in this folder.";
                var shown = Items.Count == _all.Count ? $"{_all.Count} draft(s)" : $"{Items.Count} of {_all.Count} draft(s)";
                return SelectedCount == 0 ? $"{shown} — tick the takes worth rendering big."
                                          : $"{shown}, {SelectedCount} ticked.";
            }
        }

        /// <summary>
        /// Scans for sidecars, not for videos: a draft without one cannot be reproduced, and listing it
        /// would offer an upscale this tab cannot perform. A sidecar whose video has been deleted is
        /// dropped for the same reason — there is nothing to preview and nothing to compare against.
        ///
        /// <para><b>Every filesystem call is inside the <see cref="Task.Run"/>, the folder's own existence
        /// check included.</b> The default folder lives on <c>Z:\</c>, this runs from the constructor on the
        /// Video Generator's startup path, and a single <c>Directory.Exists</c> against a share that is down
        /// hangs the whole window for the SMB timeout. Only the results come back to the UI thread.</para>
        /// </summary>
        public async Task ScanAsync()
        {
            if (IsScanning || IsUpscaling) return;
            var folder = ScanFolder;

            IsScanning = true;
            ScanStatus = "Scanning…";
            try
            {
                var (exists, found) = await Task.Run(() =>
                {
                    if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                        return (false, new List<SeedUpscaleItem>());
                    var items = ReadFolder(folder);
                    // The "has this already been rendered" probe is one Directory.Exists plus one
                    // File.Exists per draft. Off-thread with the rest of the scan, not in the loop that
                    // publishes to the board.
                    var dir = OutputDirectory();
                    var haveDir = Directory.Exists(dir);
                    foreach (var item in items)
                    {
                        if (!haveDir) continue;
                        var candidate = Path.Combine(dir, UpscaleFileName(item));
                        if (File.Exists(candidate)) item.UpscaledPath = candidate;
                    }
                    return (true, items);
                });

                _folderExists = exists;
                OnPropertyChanged(nameof(HasScanFolder));
                OpenScanFolderCommand.NotifyCanExecuteChanged();

                if (!exists)
                {
                    _all.Clear();
                    Items.Clear();
                    ScanStatus = string.IsNullOrWhiteSpace(folder)
                        ? "Choose a folder of H3 4-Step drafts."
                        : $"That folder does not exist: {folder}";
                    RefreshCounts();
                    return;
                }

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // Ticks and thumbnails already earned are kept across a rescan — a rescan is usually
                    // "did that finish yet", not "start again".
                    var previous = _all.ToDictionary(i => i.Key, i => i);
                    _all.Clear();
                    foreach (var item in found)
                    {
                        if (previous.TryGetValue(item.Key, out var old))
                        {
                            item.IsSelected = old.IsSelected;
                            item.Thumbnail = old.Thumbnail;
                            item.Status = old.Status;
                        }
                        item.PropertyChanged += ItemPropertyChanged;
                        _all.Add(item);
                    }
                    ApplyFilter();
                    ScanStatus = _all.Count == 0
                        ? "No .seed.json recipes here. H3 4-Step writes one beside every draft it hunts."
                        : $"{_all.Count} draft(s) with recipes.";
                });

                await FillThumbnailsAsync();
            }
            catch (Exception ex)
            {
                AddLog($"Seed scan failed: {ex.Message}");
                ScanStatus = $"Scan failed: {ex.Message}";
            }
            finally { IsScanning = false; }
        }

        private List<SeedUpscaleItem> ReadFolder(string folder)
        {
            var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var sidecars = Directory.EnumerateFiles(folder, "*" + SeedRecipe.Extension, option).ToList();

            var items = new List<SeedUpscaleItem>();
            var skipped = 0;
            foreach (var sidecar in sidecars)
            {
                var recipe = SeedRecipe.TryLoad(sidecar, out var problem);
                if (recipe == null)
                {
                    skipped++;
                    AddLog($"  Skipped {Path.GetFileName(sidecar)}: {problem}");
                    continue;
                }

                // Beside the sidecar first: a folder that has been moved or copied still reads correctly,
                // and the path baked into the recipe points at where the draft used to live.
                var beside = Path.Combine(Path.GetDirectoryName(sidecar) ?? folder,
                                          Path.GetFileName(sidecar)[..^SeedRecipe.Extension.Length] + ".mp4");
                var video = File.Exists(beside) ? beside
                          : File.Exists(recipe.DraftVideoPath) ? recipe.DraftVideoPath
                          : null;
                if (video == null)
                {
                    skipped++;
                    continue;
                }

                items.Add(new SeedUpscaleItem(this, recipe, video, sidecar));
            }

            if (skipped > 0) AddLog($"  {skipped} recipe(s) skipped — unreadable, or their draft is gone.");
            return items.OrderByDescending(i => i.Recipe.CreatedUtc)
                        .ThenBy(i => i.Recipe.ClipIndex)
                        .ThenBy(i => i.Recipe.Slot)
                        .ToList();
        }

        private void ItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SeedUpscaleItem.IsSelected)) RefreshCounts();
        }

        private void ApplyFilter()
        {
            var needle = FilterText.Trim();
            IEnumerable<SeedUpscaleItem> query = _all;
            if (needle.Length > 0)
                query = query.Where(i => i.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase));
            if (HideAlreadyUpscaled)
                query = query.Where(i => !i.HasUpscale);

            Items.Clear();
            foreach (var item in query) Items.Add(item);
            RefreshCounts();
        }

        private void RefreshCounts()
        {
            OnPropertyChanged(nameof(SelectedCount));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(SelectionSummary));
            OnPropertyChanged(nameof(RunSummary));
            RefreshCommands();
        }

        private void RefreshCommands()
        {
            BrowseFolderCommand.NotifyCanExecuteChanged();
            RescanCommand.NotifyCanExecuteChanged();
            SelectAllCommand.NotifyCanExecuteChanged();
            SelectNoneCommand.NotifyCanExecuteChanged();
            InvertSelectionCommand.NotifyCanExecuteChanged();
            UpscaleSelectedCommand.NotifyCanExecuteChanged();
            StopUpscaleCommand.NotifyCanExecuteChanged();
        }

        /// <summary>Fills the tiles one at a time, off the UI thread — a folder can hold hundreds of drafts
        /// and each thumbnail is an FFmpeg process.</summary>
        private async Task FillThumbnailsAsync()
        {
            foreach (var item in _all.ToList())
            {
                if (item.Thumbnail != null) continue;
                var path = item.VideoPath;
                var bmp = await Task.Run(() => ExtractFirstFrame(path));
                if (bmp == null) continue;
                Application.Current.Dispatcher.Invoke(() => item.Thumbnail = bmp);
            }
        }

        // ── Selection ───────────────────────────────────────────────────────────────────────────────

        public RelayCommand SelectAllCommand { get; }
        public RelayCommand SelectNoneCommand { get; }
        public RelayCommand InvertSelectionCommand { get; }

        /// <summary>Acts on what is on screen, not on everything scanned — otherwise "Select all" while a
        /// search is typed in ticks rows nobody can see.</summary>
        private void SetAllSelected(bool selected)
        {
            foreach (var item in Items) item.IsSelected = selected;
            RefreshCounts();
        }

        private void InvertSelection()
        {
            foreach (var item in Items) item.IsSelected = !item.IsSelected;
            RefreshCounts();
        }

        // ── The upscale dials ───────────────────────────────────────────────────────────────────────

        /// <summary>The finished canvas — the upscaler takes a <i>target</i>, not a factor, so this is
        /// independent of whatever the drafts were hunted at.</summary>
        public double TargetMegapixels
        {
            get => _targetMegapixels;
            set
            {
                if (Math.Abs(_targetMegapixels - value) < 0.0001) return;
                _targetMegapixels = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RunSummary));
            }
        }

        public IReadOnlyList<MegapixelOption> TargetMegapixelOptions { get; } = new[]
        {
            new MegapixelOption(0.5, "0.5 MP — 960×544"),
            new MegapixelOption(0.7, "0.7 MP — 1152×640"),
            new MegapixelOption(1.0, "1.0 MP — 1344×768"),
            new MegapixelOption(1.5, "1.5 MP — 1664×928"),
            new MegapixelOption(2.0, "2.0 MP — 2K (1920×1088)"),
        };

        /// <summary>Fixed sigmas on the second pass: 3, 4 or 5.</summary>
        public int UpscaleSteps
        {
            get => _upscaleSteps;
            set
            {
                if (_upscaleSteps == value || !H3SeedGraph.SigmaSchedules.ContainsKey(value)) return;
                _upscaleSteps = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RunSummary));
            }
        }

        public IReadOnlyList<int> UpscaleStepOptions { get; } = new[] { 3, 4, 5 };

        public bool UseRife
        {
            get => _useRife;
            set
            {
                if (_useRife == value) return;
                _useRife = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RunSummary));
            }
        }

        public string RunSummary
        {
            get
            {
                var aspect = Items.FirstOrDefault(i => i.IsSelected)?.Recipe.AspectRatio
                             ?? _all.FirstOrDefault()?.Recipe.AspectRatio
                             ?? "16:9 (Widescreen)";
                var (fw, fh) = H3Canvas.Resolve(aspect, TargetMegapixels, 32);
                var fps = UseRife
                    ? $"RIFE {H3SeedGraph.DraftFrameRate}→{H3SeedGraph.DraftFrameRate * 2} fps"
                    : $"{H3SeedGraph.DraftFrameRate} fps";
                var n = SelectedCount;
                return n == 0
                    ? $"Each ticked draft: its seed re-sampled, the latent upscaled to ≈{fw}×{fh} " +
                      $"({TargetMegapixels:0.0} MP), {UpscaleSteps} fixed sigmas, {fps}."
                    : $"{n} draft(s): each seed re-sampled, then upscaled to ≈{fw}×{fh} " +
                      $"({TargetMegapixels:0.0} MP), {UpscaleSteps} fixed sigmas, {fps}.";
            }
        }

        // ── The shared player ───────────────────────────────────────────────────────────────────────

        public string? ActivePreviewUri
        {
            get => _activePreviewUri;
            private set
            {
                if (_activePreviewUri == value) return;
                _activePreviewUri = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasActivePreview));
            }
        }

        public bool HasActivePreview => !string.IsNullOrEmpty(ActivePreviewUri);

        public string ActivePreviewLabel
        {
            get => _activePreviewLabel;
            private set { if (_activePreviewLabel == value) return; _activePreviewLabel = value; OnPropertyChanged(); }
        }

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

        public string MuteGlyph => IsPreviewMuted ? "🔇" : "🔊";

        public string MuteTip => IsPreviewMuted
            ? "Muted — click to hear the take"
            : "Playing sound — click to mute";

        private void ShowInPlayer(string? uri, string label)
        {
            ActivePreviewUri = uri;
            ActivePreviewLabel = label;
        }

        /// <summary>The window's <c>MediaFailed</c> handler, routed into this tab's log.</summary>
        public void ReportPreviewFailed(string reason) =>
            AddLog($"Preview could not play: {reason}");

        public double TileSize
        {
            get => _tileSize;
            private set
            {
                var clamped = Math.Clamp(value, MinTileSize, MaxTileSize);
                if (Math.Abs(_tileSize - clamped) < 0.5) return;
                _tileSize = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TileWidth));
                ZoomTilesInCommand.NotifyCanExecuteChanged();
                ZoomTilesOutCommand.NotifyCanExecuteChanged();
            }
        }

        /// <summary>The tile's whole width — the thumbnail plus room for the two text lines beside it.</summary>
        public double TileWidth => TileSize * 2.6;

        private const double MinTileSize = 96;
        private const double MaxTileSize = 240;
        private const double TileSizeStep = 24;

        public RelayCommand ZoomTilesInCommand { get; }
        public RelayCommand ZoomTilesOutCommand { get; }

        // ── ISeedUpscaleHost ────────────────────────────────────────────────────────────────────────

        public void PlaySeed(SeedUpscaleItem? item)
        {
            if (item == null || !File.Exists(item.VideoPath)) return;
            ShowInPlayer(item.VideoPath, $"{item.Title} · draft · {item.Details}");
        }

        public void ToggleSeed(SeedUpscaleItem? item)
        {
            if (item == null) return;
            item.IsSelected = !item.IsSelected;
            RefreshCounts();
        }

        public void PlayUpscaled(SeedUpscaleItem? item)
        {
            if (item?.UpscaledPath == null || !File.Exists(item.UpscaledPath)) return;
            ShowInPlayer(item.UpscaledPath, $"{item.Title} · upscaled");
        }

        public void RevealSeed(SeedUpscaleItem? item)
        {
            if (item == null) return;
            var target = item.HasUpscale ? item.UpscaledPath! : item.VideoPath;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{target}\"",
                    UseShellExecute = true
                });
            }
            catch (Exception ex) { AddLog($"Could not reveal the file: {ex.Message}"); }
        }

        // ── The run ─────────────────────────────────────────────────────────────────────────────────

        public RelayCommand UpscaleSelectedCommand { get; }
        public RelayCommand StopUpscaleCommand { get; }

        private void StopRun()
        {
            _runCts?.Cancel();
            AddLog("Stopping after the clip in flight…");
        }

        /// <summary>Ticked drafts, in board order, one submission each.</summary>
        private async Task RunAsync()
        {
            if (IsUpscaling) return;
            var todo = Items.Where(i => i.IsSelected).ToList();
            if (todo.Count == 0) return;

            IsUpscaling = true;
            IsProcessing = true;
            _runCts?.Dispose();
            _runCts = new CancellationTokenSource();
            var token = _runCts.Token;

            AddLog($"=== Seed upscale: {todo.Count} draft(s) → {TargetMegapixels:0.0} MP, " +
                   $"{UpscaleSteps} fixed sigmas, {(UseRife ? "RIFE on" : "RIFE off")} ===");

            var done = 0;
            var failed = 0;
            try
            {
                for (var i = 0; i < todo.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var item = todo[i];
                    var from = 100.0 * i / todo.Count;
                    var to = 100.0 * (i + 1) / todo.Count;

                    item.IsBusy = true;
                    item.Status = "upscaling…";
                    ScanStatus = $"Upscaling {i + 1} of {todo.Count} — {item.Title}";
                    try
                    {
                        var output = await UpscaleOneAsync(item, from, to, token);
                        item.UpscaledPath = output;
                        item.Status = "upscaled";
                        item.IsSelected = false;
                        done++;
                    }
                    catch (OperationCanceledException)
                    {
                        item.Status = "stopped";
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        item.Status = $"failed: {ex.Message}";
                        AddLog($"{item.Title}: upscale FAILED — {ex.Message}");
                    }
                    finally
                    {
                        item.IsBusy = false;
                        RefreshCounts();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                AddLog("Seed upscale stopped.");
            }
            finally
            {
                IsUpscaling = false;
                IsProcessing = false;
                ProcessingProgress = 0;
                ProcessingStatus = "Idle";
                ScanStatus = $"{done} upscaled" + (failed > 0 ? $", {failed} failed." : ".");
                if (HideAlreadyUpscaled) ApplyFilter();
                RefreshCounts();
                OnCanExecuteChanged();
            }
        }

        /// <summary>
        /// One draft: its references re-uploaded, its recipe written back into the graph, its seed into the
        /// one sampler, and the finished canvas into the upscaler. Everything the sink cannot reach is
        /// pruned, which here removes the single-pass decode and — when RIFE is off — RIFE itself.
        /// </summary>
        private async Task<string> UpscaleOneAsync(
            SeedUpscaleItem item, double progressFrom, double progressTo, CancellationToken token)
        {
            var recipe = item.Recipe;

            var missing = recipe.ReferenceImages.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
                throw new Exception($"{missing.Count} reference image(s) are gone — " +
                                    $"the first is {Path.GetFileName(missing[0])}. Without them the seed " +
                                    "re-samples against different conditioning and is a different take.");
            if (recipe.ReferenceImages.Count == 0)
                throw new Exception("This recipe records no reference images.");

            ProcessingStatus = "Uploading references...";
            var uploaded = new List<string>();
            foreach (var path in recipe.ReferenceImages)
                uploaded.Add(await EnsureUploadedAsync(path));

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmssfff");
            var runToken = $"h3seedupscale_{ts}_s{recipe.Seed}";
            var (fw, fh) = H3Canvas.Resolve(recipe.AspectRatio, TargetMegapixels, 32);

            var json = await LoadFileAsync(WorkflowFileName, token);
            var root = H3SeedGraph.Parse(json);
            H3SeedGraph.Apply(root, recipe, uploaded);

            // The seed that reproduces the latent. Every other input was just written from the same recipe,
            // so this is the last thing standing between "the same picture, bigger" and "a different take".
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeSeedNoise, "noise_seed", recipe.Seed);

            // The second pass. Its own noise is fresh every run — re-running it is how the authored graph
            // offers variations of one composition.
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeUpscaler, "mode", "megapixels");
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeUpscaler, "mode.megapixels", TargetMegapixels);
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeUpscaleNoise, "noise_seed",
                                 System.Random.Shared.NextInt64(0, long.MaxValue));
            H3SeedGraph.Link(root, H3SeedGraph.NodeUpscaleSampler, "sigmas",
                             H3SeedGraph.SigmaSchedules[UpscaleSteps], 0);

            if (UseRife)
            {
                H3SeedGraph.SetInput(root, H3SeedGraph.NodeRife, "source_fps", (double)H3SeedGraph.DraftFrameRate);
                H3SeedGraph.SetInput(root, H3SeedGraph.NodeRife, "target_fps", (double)(H3SeedGraph.DraftFrameRate * 2));
                H3SeedGraph.Link(root, H3SeedGraph.NodeRife, "images", H3SeedGraph.NodeUpscaledVideo, 0);
                H3SeedGraph.Link(root, H3SeedGraph.NodeFinalSave, "images", H3SeedGraph.NodeRife, 0);
                H3SeedGraph.SetInput(root, H3SeedGraph.NodeFinalSave, "frame_rate", H3SeedGraph.DraftFrameRate * 2);
            }
            else
            {
                H3SeedGraph.Link(root, H3SeedGraph.NodeFinalSave, "images", H3SeedGraph.NodeUpscaledVideo, 0);
                H3SeedGraph.SetInput(root, H3SeedGraph.NodeFinalSave, "frame_rate", H3SeedGraph.DraftFrameRate);
            }
            H3SeedGraph.Link(root, H3SeedGraph.NodeFinalSave, "audio", H3SeedGraph.NodeUpscaledAudio, 0);
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeFinalSave, "save_output", true);
            H3SeedGraph.SetInput(root, H3SeedGraph.NodeFinalSave, "filename_prefix",
                                 $"{OutputSubfolder}/{runToken}");

            json = PruneToOutputs(root.ToJsonString(), new[] { H3SeedGraph.NodeFinalSave }, out var pruned);
            AddLog($"{item.Title}: reproducing seed {recipe.Seed} at {recipe.DraftWidth}×{recipe.DraftHeight} " +
                   $"({recipe.Steps} steps, {H3SeedGraph.SigmaSchedules.Count} schedules available), then " +
                   $"upscaling to ≈{fw}×{fh}. Graph: {pruned} node(s) removed.");

            ProcessingStatus = "Waiting for other workflows to finish...";
            var lease = await _workflowCoordinator.AcquireAsync(TabDisplayName, token);
            string? local;
            try
            {
                ProcessingStatus = $"Upscaling {item.Title} to {fw}×{fh}...";
                local = await SubmitAndRetrieveAsync(json, runToken, H3SeedGraph.NodeFinalSave,
                                                    progressFrom, progressTo, token);
            }
            finally { lease.Dispose(); }

            if (local == null || !File.Exists(local))
                throw new Exception("No output video was generated.");

            var outputDir = OutputDirectory();
            Directory.CreateDirectory(outputDir);
            var finalPath = Path.Combine(outputDir, UpscaleFileName(item));
            File.Copy(local, finalPath, true);
            await LocalCopyService.CopyVideoAsync(finalPath);

            // The recipe travels with the upscale too: it is still the record of which seed this is, and a
            // re-render at a different size later needs exactly the same fields.
            try { item.Recipe.Save(SeedRecipe.SidecarPathFor(finalPath)); }
            catch (Exception ex) { AddLog($"  (the recipe could not be copied beside the upscale: {ex.Message})"); }

            var fi = new FileInfo(finalPath);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ResultVideoPath = finalPath;
                ShowInPlayer(finalPath, $"{item.Title} · upscaled · ≈{fw}×{fh}");
                ResultVideoInfo = $"{TabDisplayName} • seed {recipe.Seed} • ≈{fw}×{fh} • " +
                                  $"{recipe.AspectRatio} • {recipe.LengthSeconds:0.#}s • " +
                                  $"{fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            AddLog($"=== {item.Title} upscaled: {finalPath} ===");
            return finalPath;
        }

        /// <summary>
        /// The tile image. Several simultaneous WPF <c>MediaElement</c>s render as solid black — a folder
        /// scan can turn up hundreds — so the tiles are still frames and the one being watched plays in the
        /// single shared player.
        /// </summary>
        private System.Windows.Media.Imaging.BitmapImage? ExtractFirstFrame(string videoPath)
        {
            try
            {
                var ffmpeg = FindFFmpeg();
                if (ffmpeg == null) return null;
                var outPath = Path.Combine(Path.GetTempPath(), $"seedupscale_thumb_{Guid.NewGuid():N}.png");
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

                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
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

        private string OutputDirectory() => Path.Combine(
            _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);

        /// <summary>Named from the draft and the target size, so a folder holds one file per draft per size
        /// and re-running a draft at the same size overwrites rather than accumulating.</summary>
        private string UpscaleFileName(SeedUpscaleItem item) =>
            $"{OutputFileStem}_{Path.GetFileNameWithoutExtension(item.VideoPath)}_" +
            $"{TargetMegapixels.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}MP.mp4";
    }
}
