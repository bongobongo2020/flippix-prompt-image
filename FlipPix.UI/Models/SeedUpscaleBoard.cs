using System;
using System.IO;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>What a tile on the Seed Upscale board can ask its tab to do.</summary>
    public interface ISeedUpscaleHost
    {
        /// <summary>Plays this draft in the shared player. Does not change the tick.</summary>
        void PlaySeed(SeedUpscaleItem? item);

        /// <summary>Ticks or unticks it for the next upscale run.</summary>
        void ToggleSeed(SeedUpscaleItem? item);

        /// <summary>Plays the upscaled clip this tile produced.</summary>
        void PlayUpscaled(SeedUpscaleItem? item);

        /// <summary>Opens the folder the draft (or its upscale) sits in.</summary>
        void RevealSeed(SeedUpscaleItem? item);
    }

    /// <summary>
    /// One draft found by the folder scan: the mp4, the <see cref="SeedRecipe"/> beside it, and whether it
    /// is ticked for the next upscale.
    ///
    /// <para><b>The commands live here, not on the tab.</b> A <c>DataTemplate</c> has its own XAML
    /// namescope, so <c>{Binding DataContext.XCommand, ElementName=SomeRoot}</c> inside one silently
    /// resolves to nothing and produces a dead button. H3 Eros shipped that bug once already; the fix is
    /// commands on the item, bound as plain <c>{Binding PlayCommand}</c>.</para>
    /// </summary>
    public partial class SeedUpscaleItem : ObservableObject
    {
        private readonly ISeedUpscaleHost _host;

        public SeedUpscaleItem(ISeedUpscaleHost host, SeedRecipe recipe, string videoPath, string sidecarPath)
        {
            _host = host;
            Recipe = recipe;
            VideoPath = videoPath;
            SidecarPath = sidecarPath;

            PlayCommand = new RelayCommand(() => _host.PlaySeed(this));
            ToggleCommand = new RelayCommand(() => _host.ToggleSeed(this));
            PlayUpscaledCommand = new RelayCommand(() => _host.PlayUpscaled(this), () => HasUpscale);
            RevealCommand = new RelayCommand(() => _host.RevealSeed(this));
        }

        public SeedRecipe Recipe { get; }

        /// <summary>The draft on disk. Preferred over <see cref="SeedRecipe.DraftVideoPath"/>, which is
        /// where it was when the recipe was written — a folder that has since been moved or copied still
        /// scans correctly this way.</summary>
        public string VideoPath { get; }

        public string SidecarPath { get; }

        /// <summary>What the scan sorts and de-duplicates on.</summary>
        public string Key => VideoPath;

        // ── What the tile shows ─────────────────────────────────────────────────────────────────────

        /// <summary>The clip this draft is of. Falls back to the filename for a draft whose recipe was
        /// written outside a story.</summary>
        public string Title =>
            !string.IsNullOrWhiteSpace(Recipe.Title) ? Recipe.Title
            : Path.GetFileNameWithoutExtension(VideoPath);

        public string ClipLabel =>
            Recipe.ClipCount > 1 ? $"clip {Recipe.ClipIndex}/{Recipe.ClipCount}" : string.Empty;

        public string SeedLabel => $"seed {Recipe.Seed}";

        public string CanvasLabel =>
            Recipe.DraftWidth > 0 ? $"{Recipe.DraftWidth}×{Recipe.DraftHeight}" : $"{Recipe.DraftMegapixels:0.##} MP";

        public string ModelLabel
        {
            get
            {
                var name = Recipe.DiffusionModel;
                if (string.IsNullOrWhiteSpace(name)) return "workflow default";
                var slash = name.LastIndexOfAny(new[] { '/', '\\' });
                if (slash >= 0) name = name[(slash + 1)..];
                var dot = name.LastIndexOf('.');
                return dot > 0 ? name[..dot] : name;
            }
        }

        public string WhenLabel => Recipe.CreatedUtc.ToLocalTime().ToString("d MMM HH:mm");

        /// <summary>The second line under a tile: everything that is not the title, in one string, so the
        /// template does not need four bindings and three separators.</summary>
        public string Details
        {
            get
            {
                var bits = new System.Collections.Generic.List<string>();
                if (ClipLabel.Length > 0) bits.Add(ClipLabel);
                bits.Add($"take {Recipe.Slot}");
                bits.Add(SeedLabel);
                bits.Add(CanvasLabel);
                bits.Add($"{Recipe.LengthSeconds:0.#}s");
                return string.Join(" · ", bits);
            }
        }

        /// <summary>Everything the search box matches against.</summary>
        public string SearchText =>
            $"{Title} {Recipe.StoryId} {ModelLabel} {Recipe.SourceTab} {Recipe.Seed} " +
            $"{Path.GetFileName(VideoPath)} {Recipe.Prompt}";

        // ── State ───────────────────────────────────────────────────────────────────────────────────

        [ObservableProperty] private BitmapImage? _thumbnail;

        [ObservableProperty] private bool _isSelected;

        [ObservableProperty] private bool _isBusy;

        [ObservableProperty] private string _status = string.Empty;

        /// <summary>The upscaled clip, once this tile has produced one. Kept so a second run over the same
        /// folder can say what has already been done rather than doing it again.</summary>
        [ObservableProperty] private string? _upscaledPath;

        public bool HasUpscale => !string.IsNullOrEmpty(UpscaledPath) && File.Exists(UpscaledPath);

        partial void OnUpscaledPathChanged(string? value)
        {
            OnPropertyChanged(nameof(HasUpscale));
            PlayUpscaledCommand.NotifyCanExecuteChanged();
        }

        public RelayCommand PlayCommand { get; }
        public RelayCommand ToggleCommand { get; }
        public RelayCommand PlayUpscaledCommand { get; }
        public RelayCommand RevealCommand { get; }
    }
}
