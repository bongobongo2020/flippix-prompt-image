using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using FlipPix.UI.Services;
using FlipPix.UI.ViewModels;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI
{
    public partial class VideoGeneratorWindow : Window
    {
        private readonly VideoGeneratorViewModel _viewModel;
        private readonly WindowPositionService _windowPositionService;

        // This window is a reused singleton: closing it hides it (so reopening is instant) rather
        // than destroying it. Only a real application shutdown sets this true to allow a true close.
        private bool _allowClose;

        private DispatcherTimer? _scrubTimerScail2;

        // Scail 2 playhead tracking (trim-track playhead ↔ media element)
        private DispatcherTimer? _scail2PosTimer;
        private bool _scail2IsPlaying;

        public VideoGeneratorWindow(VideoGeneratorViewModel viewModel, WindowPositionService windowPositionService)
        {
            InitializeComponent();
            DataContext = viewModel;
            _viewModel = viewModel;
            _windowPositionService = windowPositionService ?? throw new ArgumentNullException(nameof(windowPositionService));
            Loaded += OnLoaded;

            // App shuts down when the main window closes (ShutdownMode.OnMainWindowClose). Let this
            // reused window actually close at that point instead of cancelling into Hide() forever.
            if (System.Windows.Application.Current?.MainWindow is System.Windows.Window main && !ReferenceEquals(main, this))
            {
                main.Closed += (_, _) =>
                {
                    _allowClose = true;
                    Close();
                };
            }

            _viewModel.PlayRequested += OnPlayRequested;
            _viewModel.Scail2VM.SeekRequested += OnScail2SeekRequested;
            _viewModel.Scail2VM.PropertyChanged += Scail2VM_PropertyChanged;
            // Drive the seed-preview player Sources from code-behind: a string {Binding} to
            // MediaElement.Source silently fails to load the Z:\ output paths (black frame, no
            // MediaOpened/MediaFailed). Set an absolute Uri explicitly instead.
            _viewModel.ErosConvRotVM.PropertyChanged += ErosConvRotVM_PropertyChanged;
            _viewModel.H3ErosVM.PropertyChanged += H3ErosVM_PropertyChanged;
            _viewModel.H34StepVM.PropertyChanged += H34StepVM_PropertyChanged;
            _viewModel.H3VrVM.PropertyChanged += H3VrVM_PropertyChanged;
            _viewModel.H3BatchVM.PropertyChanged += H3BatchVM_PropertyChanged;
            _viewModel.SeedUpscaleVM.PropertyChanged += SeedUpscaleVM_PropertyChanged;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _windowPositionService.EnsureWindowVisible(this);
            // Pick up a video that was already loaded before this window's handlers wired up.
            ApplyScail2RefSource();
            ApplyErosConvRotSource();
            ApplyH3ErosSource();
            ApplyH34StepSource();
            ApplyH3VrSource();
            ApplyH3BatchSource();
            ApplySeedUpscaleSource();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                this.DragMove();
        }

        private void OnPlayRequested(object? sender, System.EventArgs e)
        {
            if (Scail2VideoPlayer != null && Scail2VideoPlayer.Source != null)
            {
                Scail2VideoPlayer.Position = System.TimeSpan.Zero;
                Scail2VideoPlayer.Play();
            }

            if (MiniMaxI2VVideoPlayer != null && MiniMaxI2VVideoPlayer.Source != null)
            {
                MiniMaxI2VVideoPlayer.Position = System.TimeSpan.Zero;
                MiniMaxI2VVideoPlayer.Play();
            }

            if (MiniMaxCharacterVideoPlayer != null && MiniMaxCharacterVideoPlayer.Source != null)
            {
                MiniMaxCharacterVideoPlayer.Position = System.TimeSpan.Zero;
                MiniMaxCharacterVideoPlayer.Play();
            }

            if (H3ChainVideoPlayer != null && H3ChainVideoPlayer.Source != null)
            {
                H3ChainVideoPlayer.Position = System.TimeSpan.Zero;
                H3ChainVideoPlayer.Play();
            }

            if (H3DuoVideoPlayer != null && H3DuoVideoPlayer.Source != null)
            {
                H3DuoVideoPlayer.Position = System.TimeSpan.Zero;
                H3DuoVideoPlayer.Play();
            }

            if (H3ExperimentalVideoPlayer != null && H3ExperimentalVideoPlayer.Source != null)
            {
                H3ExperimentalVideoPlayer.Position = System.TimeSpan.Zero;
                H3ExperimentalVideoPlayer.Play();
            }

            if (H3ErosVideoPlayer != null && H3ErosVideoPlayer.Source != null)
            {
                H3ErosVideoPlayer.Position = System.TimeSpan.Zero;
                H3ErosVideoPlayer.Play();
            }

            if (H3VrVideoPlayer != null && H3VrVideoPlayer.Source != null)
            {
                H3VrVideoPlayer.Position = System.TimeSpan.Zero;
                H3VrVideoPlayer.Play();
            }

            if (H3BatchVideoPlayer != null && H3BatchVideoPlayer.Source != null)
            {
                H3BatchVideoPlayer.Position = System.TimeSpan.Zero;
                H3BatchVideoPlayer.Play();
            }

            if (H3MultiVideoPlayer != null && H3MultiVideoPlayer.Source != null)
            {
                H3MultiVideoPlayer.Position = System.TimeSpan.Zero;
                H3MultiVideoPlayer.Play();
            }
        }

        // Never seek a scrub preview to the exact end of the clip. Landing on the final
        // frame fires MediaElement.MediaEnded, which rewinds/resets the player (the
        // "shrink and expand" flicker) and leaves it in an ended state where
        // ScrubbingEnabled stops rendering new frames — so the Out marker shows no preview
        // and the In marker breaks too. Hold a couple of frames back from the end.
        private static double ClampPreviewSeek(System.Windows.Controls.MediaElement p, double seconds, double fps)
        {
            var t = Math.Max(0, seconds);
            if (p.NaturalDuration.HasTimeSpan)
            {
                double dur = p.NaturalDuration.TimeSpan.TotalSeconds;
                double guard = Math.Max(0.05, 2.0 / (fps > 0 ? fps : 24.0));
                if (dur > guard) t = Math.Min(t, dur - guard);
            }
            return t;
        }

        // ── SCAIL II draggable in/out trim markers ───────────────────────────
        // The green/red thumbs write straight to the view model's TrimInSeconds /
        // TrimOutSeconds (which clamp to [0, out] / [in, duration]), so a dropped
        // marker stays put. The purple region between them shows the kept clip,
        // and TrimmedFrames (in/out length in frames) is what the workflow loads.
        private const double ScailTrimThumbWidth = 14.0;

        // WAN processes the clip in 81-frame chunks; the timeline marks each boundary.
        private const int ScailChunkFrames = 81;

        private void ErosConvRotVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.ErosConvRotViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplyErosConvRotSource();
            else Dispatcher.Invoke(ApplyErosConvRotSource);
        }

        private void ApplyErosConvRotSource()
        {
            var p = ErosConvRotPlayer;
            if (p == null) return;
            var path = _viewModel.ErosConvRotVM.ActivePreviewUri;
            if (string.IsNullOrEmpty(path))
            {
                p.Stop();
                p.Source = null;
                return;
            }

            Uri target;
            try { target = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute); }
            catch { target = new Uri(path, UriKind.RelativeOrAbsolute); }

            if (string.Equals(p.Source?.OriginalString, target.OriginalString, StringComparison.OrdinalIgnoreCase))
            {
                p.Position = System.TimeSpan.Zero;
                p.Play();
                return;
            }
            p.Stop();
            p.Source = target; // MediaOpened handler starts playback.
        }

        private void ErosConvRotPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            _viewModel.ErosConvRotVM.ReportPreviewOpened(ErosConvRotPlayer.Source?.OriginalString ?? "");
            ErosConvRotPlayer.Play();
        }

        private void ErosConvRotPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            ErosConvRotPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            ErosConvRotPlayer.Play();
        }

        private void ErosConvRotPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _viewModel.ErosConvRotVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");
        }

        // ────────────────────────────────────────────────────────────────────
        // H3 Eros — one shared player for every take on the hunt board and for the
        // finished clips. It follows H3ErosVM.ActivePreviewUri, which changes on every
        // tile click, so it has to start playing on its own each time.
        //
        // The Source is built here as an ABSOLUTE Uri rather than bound as a string:
        // WPF's string→Uri conversion silently fails to open the Z:\output paths these
        // drafts live on — no MediaOpened, no MediaFailed, just a black frame. The
        // element is also never collapsed, because a collapsed MediaElement will not
        // open media at all. Both mistakes have already cost this app a working preview
        // once each; see project_fflfseedhunt_preview_player.
        // ────────────────────────────────────────────────────────────────────

        private void H3ErosVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.H3ErosViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplyH3ErosSource();
            else Dispatcher.Invoke(ApplyH3ErosSource);
        }

        private void ApplyH3ErosSource()
        {
            var p = H3ErosVideoPlayer;
            if (p == null) return;
            var path = _viewModel.H3ErosVM.ActivePreviewUri;
            if (string.IsNullOrEmpty(path))
            {
                p.Stop();
                p.Source = null;
                return;
            }

            Uri target;
            try { target = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute); }
            catch { target = new Uri(path, UriKind.RelativeOrAbsolute); }

            // Clicking the same tile twice replays it rather than doing nothing.
            if (string.Equals(p.Source?.OriginalString, target.OriginalString, StringComparison.OrdinalIgnoreCase))
            {
                p.Position = System.TimeSpan.Zero;
                p.Play();
                return;
            }
            p.Stop();
            p.Source = target; // MediaOpened starts playback.
        }

        private void H3ErosPlayer_MediaOpened(object sender, RoutedEventArgs e) => H3ErosVideoPlayer.Play();

        private void H3ErosPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            H3ErosVideoPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            H3ErosVideoPlayer.Play();
        }

        private void H3ErosPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
            _viewModel.H3ErosVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

        // ────────────────────────────────────────────────────────────────────
        // H3 4-Step and Seed Upscale — the same shared-player pattern as H3 Eros
        // above, and for the same two reasons: an ABSOLUTE Uri set from code (a
        // string {Binding} silently fails to open Z:\output paths), on an element
        // that is never collapsed (a collapsed MediaElement will not open media).
        // ────────────────────────────────────────────────────────────────────

        private void H34StepVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.H3ErosViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplyH34StepSource();
            else Dispatcher.Invoke(ApplyH34StepSource);
        }

        private void ApplyH34StepSource() =>
            ApplySharedPlayerSource(H34StepVideoPlayer, _viewModel.H34StepVM.ActivePreviewUri);

        private void H34StepPlayer_MediaOpened(object sender, RoutedEventArgs e) => H34StepVideoPlayer.Play();

        private void H34StepPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            H34StepVideoPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            H34StepVideoPlayer.Play();
        }

        private void H34StepPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
            _viewModel.H34StepVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

        private void H3VrVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.H3ErosViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplyH3VrSource();
            else Dispatcher.Invoke(ApplyH3VrSource);
        }

        private void ApplyH3VrSource() =>
            ApplySharedPlayerSource(H3VrVideoPlayer, _viewModel.H3VrVM.ActivePreviewUri);

        private void H3VrPlayer_MediaOpened(object sender, RoutedEventArgs e) => H3VrVideoPlayer.Play();

        private void H3VrPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            H3VrVideoPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            H3VrVideoPlayer.Play();
        }

        private void H3VrPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
            _viewModel.H3VrVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

        private void H3BatchVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.H3ErosViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplyH3BatchSource();
            else Dispatcher.Invoke(ApplyH3BatchSource);
        }

        private void ApplyH3BatchSource() =>
            ApplySharedPlayerSource(H3BatchVideoPlayer, _viewModel.H3BatchVM.ActivePreviewUri);

        private void H3BatchPlayer_MediaOpened(object sender, RoutedEventArgs e) => H3BatchVideoPlayer.Play();

        private void H3BatchPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            H3BatchVideoPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            H3BatchVideoPlayer.Play();
        }

        private void H3BatchPlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
            _viewModel.H3BatchVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

        private void SeedUpscaleVM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(ViewModels.Video.SeedUpscaleViewModel.ActivePreviewUri)) return;
            if (Dispatcher.CheckAccess()) ApplySeedUpscaleSource();
            else Dispatcher.Invoke(ApplySeedUpscaleSource);
        }

        private void ApplySeedUpscaleSource() =>
            ApplySharedPlayerSource(SeedUpscaleVideoPlayer, _viewModel.SeedUpscaleVM.ActivePreviewUri);

        private void SeedUpscalePlayer_MediaOpened(object sender, RoutedEventArgs e) => SeedUpscaleVideoPlayer.Play();

        private void SeedUpscalePlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            SeedUpscaleVideoPlayer.Position = System.TimeSpan.FromMilliseconds(1);
            SeedUpscaleVideoPlayer.Play();
        }

        private void SeedUpscalePlayer_MediaFailed(object sender, ExceptionRoutedEventArgs e) =>
            _viewModel.SeedUpscaleVM.ReportPreviewFailed(e.ErrorException?.Message ?? "unknown media error");

        /// <summary>The body of ApplyH3ErosSource, shared by the two boards added after it. Clicking the
        /// same tile twice replays it rather than doing nothing.</summary>
        private static void ApplySharedPlayerSource(System.Windows.Controls.MediaElement? player, string? path)
        {
            if (player == null) return;
            if (string.IsNullOrEmpty(path))
            {
                player.Stop();
                player.Source = null;
                return;
            }

            Uri target;
            try { target = new Uri(System.IO.Path.GetFullPath(path), UriKind.Absolute); }
            catch { target = new Uri(path, UriKind.RelativeOrAbsolute); }

            if (string.Equals(player.Source?.OriginalString, target.OriginalString, StringComparison.OrdinalIgnoreCase))
            {
                player.Position = System.TimeSpan.Zero;
                player.Play();
                return;
            }
            player.Stop();
            player.Source = target; // MediaOpened starts playback.
        }

        // ──────────────────────────────────────────────────────────────────────
        // Scail 2 — same reference-player + trim-marker machinery as WAN SCAIL II,
        // bound to _viewModel.Scail2VM. The trim track doubles as: (a) the scrub used
        // to pick the Klein char-swap frame, and (b) the In/Out range for SCAIL II.
        // ──────────────────────────────────────────────────────────────────────

        private void ApplyScail2RefSource()
        {
            var p = Scail2RefVideoPlayer;
            if (p == null) return;
            var path = _viewModel.Scail2VM.VideoFileUri;
            var target = string.IsNullOrEmpty(path) ? null : new Uri(path, UriKind.RelativeOrAbsolute);
            if (string.Equals(p.Source?.OriginalString, target?.OriginalString, StringComparison.OrdinalIgnoreCase))
                return;
            p.Source = target;
        }

        private void Scail2RefPlayer_MediaOpened(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer.Play();
            Scail2RefVideoPlayer.Pause();

            if (_scail2PosTimer == null)
            {
                _scail2PosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _scail2PosTimer.Tick += (_, _) =>
                {
                    var p = Scail2RefVideoPlayer;
                    if (p?.Source == null) return;
                    if (!_scail2IsPlaying) return;
                    if (p.NaturalDuration.HasTimeSpan)
                        _viewModel.Scail2VM.PlaybackPositionSeconds = p.Position.TotalSeconds;
                };
            }
            _scail2PosTimer.Start();
        }

        private void Scail2Play_Click(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer?.Play();
            _scail2IsPlaying = Scail2RefVideoPlayer?.Source != null;
        }

        private void Scail2Pause_Click(object sender, RoutedEventArgs e)
        {
            Scail2RefVideoPlayer?.Pause();
            _scail2IsPlaying = false;
            // Pausing settles on a deliberate frame → that's the Klein base frame.
            _viewModel.Scail2VM.NotifyScrubbed();
        }

        private void Scail2RefPlayer_MediaEnded(object sender, RoutedEventArgs e)
            => _scail2IsPlaying = false;

        /// <summary>
        /// Opens the picture behind a cast thumbnail at full size, in whatever the user has set as their
        /// image viewer — the card's frames are 92px tall, which shows which photo is loaded but not whether
        /// the face in it is the right one.
        ///
        /// <para>Tag carries the path (SourcePath on the photo, SheetPath on the built sheet); an empty
        /// frame, or a file that has since been moved or deleted, does nothing rather than raising a shell
        /// error the user cannot act on.</para>
        /// </summary>
        private void CastThumbnail_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            if (!System.IO.File.Exists(path)) return;

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true,   // the shell, not us, decides which viewer opens it
                });
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Could not open {System.IO.Path.GetFileName(path)}: {ex.Message}",
                    "FlipPix", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// Opens a cast card's ✨ Generate menu on a left click (WPF only opens a Button's ContextMenu
        /// on right-click by itself). The menu's bindings go through PlacementTarget — see the card
        /// templates in VideoGeneratorWindow.xaml.
        /// </summary>
        private void CastGenerateButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button { ContextMenu: { } menu } button)
            {
                menu.PlacementTarget = button;
                menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                menu.IsOpen = true;
            }
        }

        /// <summary>
        /// Runs for every cast-photo menu — left- or right-clicked open. The menu's LoRA entries carry
        /// only the LoRA as their parameter, so the card they belong to is remembered on the tab's
        /// ViewModel here, and the LoRA lists are rescanned while the menu opens. Tag carries the
        /// tab's ViewModel (H3Cast or H3Ensemble) across the ContextMenu boundary.
        /// </summary>
        private void CastPhotoMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.ContextMenu { PlacementTarget: System.Windows.Controls.Button { } button })
            {
                switch (button.Tag)
                {
                    case H3CastViewModel cast when button.DataContext is CharacterSlot castSlot:
                        cast.CastPhotoMenuSlot = castSlot;
                        cast.RefreshCastPhotoLoras();
                        break;
                    case H3EnsembleViewModel ensemble when button.DataContext is CharacterSlot ensembleSlot:
                        ensemble.CastPhotoMenuSlot = ensembleSlot;
                        ensemble.RefreshCastPhotoLoras();
                        break;
                }
            }
        }

        private void SeekScail2RefTo(double seconds)
        {
            var p = Scail2RefVideoPlayer;
            if (p?.Source == null) return;
            p.Pause();
            _scail2IsPlaying = false;
            var t = ClampPreviewSeek(p, seconds, _viewModel.Scail2VM.Fps);
            p.Position = TimeSpan.FromSeconds(t);
            _viewModel.Scail2VM.PlaybackPositionSeconds = t;
            // Moving an in/out marker seeks the preview to that frame — count it as a deliberate scrub.
            _viewModel.Scail2VM.NotifyScrubbed();
        }

        private double Scail2TrimTrackWidth =>
            Scail2TrimTrack != null && Scail2TrimTrack.ActualWidth > 1 ? Scail2TrimTrack.ActualWidth : 0;

        private double Scail2TrimSecToX(double seconds, double duration)
        {
            var w = Scail2TrimTrackWidth;
            if (duration <= 0 || w <= 0) return 0;
            return Math.Max(0, Math.Min(w, seconds / duration * w));
        }

        private void Scail2VM_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ViewModels.Video.WanScailViewModel.VideoFileUri):
                    if (Dispatcher.CheckAccess()) ApplyScail2RefSource();
                    else Dispatcher.Invoke(ApplyScail2RefSource);
                    break;
                case nameof(ViewModels.Video.WanScailViewModel.TrimInSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.TrimOutSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.VideoDurationSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.PlaybackPositionSeconds):
                case nameof(ViewModels.Video.WanScailViewModel.Fps):
                case nameof(ViewModels.Video.WanScailViewModel.TotalFrames):
                    if (Dispatcher.CheckAccess()) UpdateScail2TrimMarkers();
                    else Dispatcher.Invoke(UpdateScail2TrimMarkers);
                    break;
            }
        }

        private void Scail2TrimTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateScail2TrimMarkers();

        private readonly System.Collections.Generic.List<UIElement> _scail2Ticks = new();
        private string _scail2TickSig = "";

        private void RebuildScail2Ticks()
        {
            if (Scail2TrimTrack == null) return;
            var vm = _viewModel.Scail2VM;
            double fps = vm.Fps > 0 ? vm.Fps : 24.0;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            int total = vm.TotalFrames;

            string sig = $"{fps:F3}|{dur:F3}|{w:F1}|{total}";
            if (sig == _scail2TickSig) return;
            _scail2TickSig = sig;

            foreach (var t in _scail2Ticks) Scail2TrimTrack.Children.Remove(t);
            _scail2Ticks.Clear();

            if (dur <= 0 || w <= 0 || fps <= 0) return;
            int totalFrames = total > 0 ? total : (int)Math.Round(dur * fps);

            for (int frame = ScailChunkFrames; frame < totalFrames; frame += ScailChunkFrames)
            {
                double x = Scail2TrimSecToX(frame / fps, dur);
                var tick = new System.Windows.Controls.Border
                {
                    Width = 1.5,
                    Height = 18,
                    Background = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x4B, 0x55, 0x63)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    IsHitTestVisible = false,
                    Margin = new Thickness(x - 0.75, 0, 0, 0)
                };
                _scail2Ticks.Add(tick);
                Scail2TrimTrack.Children.Insert(1, tick);
            }
        }

        private void UpdateScail2TrimMarkers()
        {
            if (Scail2InThumb == null) return; // not yet templated
            RebuildScail2Ticks();
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;

            double outSec = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            double inX = Scail2TrimSecToX(vm.TrimInSeconds, dur);
            double outX = Scail2TrimSecToX(outSec, dur);
            double playX = Scail2TrimSecToX(vm.PlaybackPositionSeconds, dur);

            Scail2InThumb.Margin = new Thickness(inX - ScailTrimThumbWidth / 2, 0, 0, 0);
            Scail2OutThumb.Margin = new Thickness(outX - ScailTrimThumbWidth / 2, 0, 0, 0);
            Scail2TrimRegion.Margin = new Thickness(inX, 0, 0, 0);
            Scail2TrimRegion.Width = Math.Max(0, outX - inX);
            Scail2TrimPlayhead.Margin = new Thickness(Math.Max(0, playX - 1), 0, 0, 0);
        }

        private void Scail2InThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            vm.TrimInSeconds += e.HorizontalChange / w * dur;
            UpdateScail2TrimMarkers();
            SeekScail2RefTo(vm.TrimInSeconds);
        }

        private void Scail2OutThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            var vm = _viewModel.Scail2VM;
            double dur = vm.VideoDurationSeconds;
            double w = Scail2TrimTrackWidth;
            if (dur <= 0 || w <= 0) return;
            double cur = vm.TrimOutSeconds > 0 ? vm.TrimOutSeconds : dur;
            vm.TrimOutSeconds = cur + e.HorizontalChange / w * dur;
            UpdateScail2TrimMarkers();
            SeekScail2RefTo(vm.TrimOutSeconds);
        }

        // Generation is explicit: the user presses "Generate video" once the In/Out range is set.
        private void Scail2Process_Click(object sender, RoutedEventArgs e)
            => _ = _viewModel.Scail2VM.OnTrimFinalizedAsync();

        private void OnScail2SeekRequested(object? sender, System.TimeSpan startPos)
        {
            var player = Scail2RefVideoPlayer;
            if (player?.Source == null) return;

            _scrubTimerScail2?.Stop();

            var vm = _viewModel.Scail2VM;
            var fps = vm.Fps > 0 ? vm.Fps : 24.0;
            var chunk = vm.ChunkItems.FirstOrDefault(c => c.IsSelected);
            var endPos = chunk != null
                ? TimeSpan.FromSeconds(chunk.EndFrame / fps)
                : startPos + TimeSpan.FromSeconds(4);
            var midPos = TimeSpan.FromTicks((startPos.Ticks + endPos.Ticks) / 2);

            player.Position = startPos;

            var step = 0;
            _scrubTimerScail2 = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _scrubTimerScail2.Tick += (s, e) =>
            {
                step++;
                if (step == 1) player.Position = midPos;
                else { player.Position = endPos; _scrubTimerScail2!.Stop(); }
            };
            _scrubTimerScail2.Start();
        }

        // Stops every media element so the window releases its hold on the underlying video files
        // (important when only hiding, so the files aren't left locked while the window lingers).
        private void StopAllPlayers()
        {
            _scrubTimerScail2?.Stop();

            ErosConvRotPlayer?.Stop();
            Scail2RefVideoPlayer?.Stop();
            Scail2VideoPlayer?.Stop();
            MiniMaxI2VVideoPlayer?.Stop();
            MiniMaxFflfVideoPlayer?.Stop();
            MiniMaxCharacterVideoPlayer?.Stop();
            H3ChainVideoPlayer?.Stop();
            H3DuoVideoPlayer?.Stop();
            H3ExperimentalVideoPlayer?.Stop();
            H3ErosVideoPlayer?.Stop();
            H34StepVideoPlayer?.Stop();
            H3VrVideoPlayer?.Stop();
            H3BatchVideoPlayer?.Stop();
            SeedUpscaleVideoPlayer?.Stop();
            H3MultiVideoPlayer?.Stop();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Reused singleton: a user-initiated close just hides the window so the next open is
            // instant. Stop playback first to release video file handles. Only a true app shutdown
            // (_allowClose) falls through to a real close.
            if (!_allowClose)
            {
                e.Cancel = true;
                StopAllPlayers();
                Hide();
                return;
            }

            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            StopAllPlayers();

            _viewModel.Scail2VM.SeekRequested -= OnScail2SeekRequested;
            _viewModel.Scail2VM.PropertyChanged -= Scail2VM_PropertyChanged;
            _viewModel.PlayRequested -= OnPlayRequested;

            if (_viewModel is IDisposable disposable)
                disposable.Dispose();

            DataContext = null;
            base.OnClosed(e);
        }
    }
}
