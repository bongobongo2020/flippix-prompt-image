using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One person in ⚡ H3 Express's CAST card: a photograph the user supplies for character 1 or 2, the sex
    /// that photograph is of, and — for the "their own clothes" mode — the outfit read off it.
    ///
    /// <para>It is <b>not</b> a <c>CharacterSlot</c>. Those are cleared between stories on purpose (see
    /// <c>H3BatchViewModel.ResetForNextStory</c>); this is what the run puts back on them afterwards, so it
    /// has to outlive the reset.</para>
    ///
    /// <para>Nothing here touches the disk on the UI thread: the preview is decoded on the thread pool, and
    /// <see cref="HasPhoto"/> is the result of that load rather than a <c>File.Exists</c> a binding would
    /// call on every refresh — a remembered photo on a mapped drive would otherwise stall the tab.</para>
    /// </summary>
    public partial class ExpressCastMember : ObservableObject
    {
        public const string Male = "Male";
        public const string Female = "Female";

        public ExpressCastMember(int index)
        {
            Index = index;
            Sex = index % 2 == 0 ? Female : Male;
        }

        /// <summary>1 or 2.</summary>
        public int Index { get; }

        public string Heading => $"Character {Index}";

        public string Tag => $"<Picture {Index}>";

        public IReadOnlyList<string> SexOptions { get; } = new[] { Male, Female };

        /// <summary>"man" / "woman" — the word the wardrobe lines and the reference line use.</summary>
        public string Noun => Sex == Female ? "woman" : "man";

        private string _photoPath = string.Empty;

        /// <summary>The photograph. Setting it starts the preview load; an outfit read from a different photo
        /// stops counting as this one's.</summary>
        public string PhotoPath
        {
            get => _photoPath;
            set
            {
                var v = (value ?? string.Empty).Trim();
                if (_photoPath == v) return;
                _photoPath = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PhotoName));
                OnPropertyChanged(nameof(HasOutfitForPhoto));
                _ = LoadPreviewAsync(v);
            }
        }

        public string PhotoName => _photoPath.Length == 0 ? string.Empty : Path.GetFileName(_photoPath);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Noun))]
        [NotifyPropertyChangedFor(nameof(WardrobeLine))]
        private string _sex = Male;

        [ObservableProperty]
        private BitmapImage? _preview;

        /// <summary>True once the photo has been found and decoded.</summary>
        [ObservableProperty]
        private bool _hasPhoto;

        /// <summary>What the person in the photo is wearing, as one garment sentence. Editable.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOutfitForPhoto))]
        private string _outfit = string.Empty;

        /// <summary>The photo <see cref="Outfit"/> was read from. A new photo means a new outfit to read.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasOutfitForPhoto))]
        private string _outfitSource = string.Empty;

        public bool HasOutfitForPhoto =>
            Outfit.Trim().Length > 0 &&
            string.Equals(OutfitSource, PhotoPath, StringComparison.OrdinalIgnoreCase);

        [ObservableProperty]
        private bool _isReadingOutfit;

        // Wired by the tab, which owns the file dialog, the settings and the vision model.
        public IRelayCommand BrowseCommand { get; set; } = null!;
        public IRelayCommand ClearCommand { get; set; } = null!;
        public IRelayCommand ReadOutfitCommand { get; set; } = null!;

        /// <summary>The line this person contributes to the locked wardrobe, in the shape the wardrobe pass
        /// writes, or empty when there is no outfit for the current photo.</summary>
        public string WardrobeLine => WardrobeLineFor(Index);

        /// <summary>The same line for the character this person plays in a particular story — which is not
        /// always their own slot number (see <c>CastAssignment</c>).</summary>
        public string WardrobeLineFor(int character) =>
            HasPhoto && HasOutfitForPhoto
                ? $"Character {character} (<Picture {character}>, a {Noun}) wears: {Outfit.Trim().TrimEnd('.', ' ')}."
                : string.Empty;

        private int _loadGeneration;

        private async Task LoadPreviewAsync(string path)
        {
            var generation = ++_loadGeneration;
            if (path.Length == 0)
            {
                Preview = null;
                HasPhoto = false;
                return;
            }

            var bitmap = await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(path)) return null;
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.DecodePixelWidth = 240;
                    b.UriSource = new Uri(path, UriKind.Absolute);
                    b.EndInit();
                    b.Freeze();
                    return b;
                }
                catch
                {
                    return null;
                }
            });

            if (generation != _loadGeneration) return;
            Preview = bitmap;
            HasPhoto = bitmap != null;
        }
    }
}
