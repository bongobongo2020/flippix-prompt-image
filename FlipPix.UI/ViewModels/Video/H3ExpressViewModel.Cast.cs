using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// ⚡ H3 Express's CAST card: the run's own people. A photo in a slot replaces the portrait that slot's
    /// character would otherwise be given, in every story of the run; an empty slot is still cast from the
    /// story.
    ///
    /// <para><b>How it gets onto a story.</b> The batch clears both cast cards between stories. Straight after
    /// that (<see cref="OnStoryReset"/>) the photos and their sex are put back, before 🍀 runs — so the cast
    /// pass fills in each card's Part but, because a photo pins the sex, leaves who they are alone; the
    /// portrait step skips a card that has a photo; and the sheets are built from these photos.</para>
    ///
    /// <para><b>What they wear — the two modes.</b></para>
    /// <list type="bullet">
    /// <item><b>The story's saved wardrobe</b> (default). Nothing changes but the faces: the story's wardrobe is
    /// locked as always, the sheet builder re-dresses the photographed people into it, and saved clips are used
    /// word for word — they describe those garments already.</item>
    /// <item><b>Their own clothes.</b> What each person wears in their photo is read once by the vision model
    /// (and editable on the card), and those lines replace the story's in the wardrobe lock — so the sheets
    /// show them in their own clothes and H3 is told to copy the clothes from the references. A saved story's
    /// clips still describe the old clothes in their prose, so they are re-dressed: one short call per clip
    /// that may change clothing words only (<see cref="ClipRedress"/>), checked, and filed as a separate
    /// version so the same cast on the same story is instant next time. A story written fresh is simply written
    /// in these clothes.</item>
    /// </list>
    /// </summary>
    public partial class H3ExpressViewModel
    {
        public ExpressCastMember CastMember1 { get; } = new(1);
        public ExpressCastMember CastMember2 { get; } = new(2);

        private IEnumerable<ExpressCastMember> CastMembers => new[] { CastMember1, CastMember2 };

        private bool _castOwnClothes;
        private bool _loadingCast;

        /// <summary>The re-dressed version the clips on the board came from, or empty when they are the story's
        /// own — an edit saved from the clip editor goes back to whichever it is.</summary>
        private string _boardVariantKey = string.Empty;

        private void InitCast()
        {
            var s = _settingsService.Settings;
            _loadingCast = true;
            try
            {
                LoadMember(CastMember1, s?.H3ExpressCast1Photo, s?.H3ExpressCast1Sex, s?.H3ExpressCast1Outfit, s?.H3ExpressCast1OutfitSource);
                LoadMember(CastMember2, s?.H3ExpressCast2Photo, s?.H3ExpressCast2Sex, s?.H3ExpressCast2Outfit, s?.H3ExpressCast2OutfitSource);
                _castOwnClothes = s?.H3ExpressCastOwnClothes ?? false;
            }
            finally
            {
                _loadingCast = false;
            }

            foreach (var member in CastMembers)
            {
                var m = member;
                m.BrowseCommand = new RelayCommand(async () => await BrowseCastPhotoAsync(m), () => CanEditCast);
                m.ClearCommand = new RelayCommand(() => ClearCastPhoto(m), () => CanEditCast && m.PhotoPath.Length > 0);
                m.ReadOutfitCommand = new RelayCommand(
                    async () => await ReadOutfitAsync(m, CancellationToken.None, manual: true),
                    () => CanEditCast && m.HasPhoto && !m.IsReadingOutfit);
                m.PropertyChanged += (_, e) => OnCastMemberChanged(m, e.PropertyName);
            }
        }

        private static void LoadMember(ExpressCastMember m, string? photo, string? sex, string? outfit, string? source)
        {
            m.Sex = string.Equals(sex, ExpressCastMember.Female, StringComparison.OrdinalIgnoreCase)
                ? ExpressCastMember.Female
                : string.Equals(sex, ExpressCastMember.Male, StringComparison.OrdinalIgnoreCase)
                    ? ExpressCastMember.Male
                    : m.Sex;
            m.Outfit = outfit ?? string.Empty;
            m.OutfitSource = source ?? string.Empty;
            m.PhotoPath = photo ?? string.Empty;
        }

        // ── What the card shows ─────────────────────────────────────────────────────────────────────

        /// <summary>Whether any story of the run is cast with the user's own people.</summary>
        public bool HasCastOverride => CastMembers.Any(m => m.HasPhoto);

        /// <summary>The cast is read at the start of each story, so it is locked for the run like the stack.</summary>
        public bool CanEditCast => CanChangeWorkflow;

        /// <summary>On: the cast wear their own clothes. Off: each story's saved wardrobe.</summary>
        public bool CastOwnClothes
        {
            get => _castOwnClothes;
            set
            {
                if (_castOwnClothes == value) return;
                _castOwnClothes = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CastStoryWardrobe));
                OnPropertyChanged(nameof(ExpressCastSummary));

                var settings = _settingsService.Settings;
                if (settings != null)
                {
                    settings.H3ExpressCastOwnClothes = value;
                    _settingsService.SaveSettings(settings);
                }

                AddLog(value
                    ? "Cast: their own clothes — outfits are read from the photos, and saved clips are re-dressed to match."
                    : "Cast: the story's saved wardrobe — the photographed cast is dressed in each story's own outfits.");

                // Read what nobody has read yet, so the card shows the outfits before a run needs them.
                if (value)
                    foreach (var m in CastMembers.Where(m => m.HasPhoto && !m.HasOutfitForPhoto))
                        _ = ReadOutfitAsync(m, CancellationToken.None, manual: false);
            }
        }

        /// <summary>The other radio button.</summary>
        public bool CastStoryWardrobe
        {
            get => !_castOwnClothes;
            set { if (value) CastOwnClothes = false; }
        }

        public string ExpressCastSummary
        {
            get
            {
                var cast = CastMembers.Where(m => m.HasPhoto).ToList();
                if (cast.Count == 0)
                    return "Empty: every story casts itself and its characters are photographed from the story. " +
                           "Add a photo to put your own person in that part, in every story of the run.";

                var who = cast.Count == 2
                    ? $"Your {cast[0].Noun} and {cast[1].Noun} play characters 1 and 2"
                    : $"Your {cast[0].Noun} plays character {cast[0].Index}";
                var rest = cast.Count == 1
                    ? $"; character {(cast[0].Index == 1 ? 2 : 1)} is still cast from each story"
                    : string.Empty;

                return CastOwnClothes
                    ? $"{who} in every story, in their own clothes{rest}. A saved story's clips are re-dressed to " +
                      "match — one short LLM call per clip, kept for next time — and a new story is written in them."
                    : $"{who} in every story{rest}, dressed in each story's own wardrobe. Saved clips are used as " +
                      "they are; the sheets re-dress your people.";
            }
        }

        private void OnCastMemberChanged(ExpressCastMember m, string? property)
        {
            switch (property)
            {
                case nameof(ExpressCastMember.HasPhoto):
                    OnPropertyChanged(nameof(HasCastOverride));
                    OnPropertyChanged(nameof(ExpressCastSummary));
                    NotifyCastCommands();
                    return;
                case nameof(ExpressCastMember.Sex):
                    OnPropertyChanged(nameof(ExpressCastSummary));
                    break;
                case nameof(ExpressCastMember.Outfit):
                    // Typed by hand counts as this photo's outfit.
                    if (!_loadingCast && m.Outfit.Trim().Length > 0 && m.OutfitSource != m.PhotoPath)
                        m.OutfitSource = m.PhotoPath;
                    break;
                case nameof(ExpressCastMember.IsReadingOutfit):
                    NotifyCastCommands();
                    return;
                case nameof(ExpressCastMember.PhotoPath):
                case nameof(ExpressCastMember.OutfitSource):
                    NotifyCastCommands();
                    break;
                default:
                    return;
            }

            if (_loadingCast) return;
            var settings = _settingsService.Settings;
            if (settings == null) return;
            if (m.Index == 1)
            {
                settings.H3ExpressCast1Photo = m.PhotoPath;
                settings.H3ExpressCast1Sex = m.Sex;
                settings.H3ExpressCast1Outfit = m.Outfit;
                settings.H3ExpressCast1OutfitSource = m.OutfitSource;
            }
            else
            {
                settings.H3ExpressCast2Photo = m.PhotoPath;
                settings.H3ExpressCast2Sex = m.Sex;
                settings.H3ExpressCast2Outfit = m.Outfit;
                settings.H3ExpressCast2OutfitSource = m.OutfitSource;
            }
            _settingsService.SaveSettings(settings);
        }

        private void NotifyCastCommands()
        {
            foreach (var m in CastMembers)
            {
                m.BrowseCommand?.NotifyCanExecuteChanged();
                m.ClearCommand?.NotifyCanExecuteChanged();
                m.ReadOutfitCommand?.NotifyCanExecuteChanged();
            }
        }

        private async Task BrowseCastPhotoAsync(ExpressCastMember m)
        {
            var initialDir = _settingsService.Settings?.VideoGeneratorImageFolder;
            if (string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir))
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            var path = await _fileDialogService.OpenFileDialogAsync(
                $"Select a photo for character {m.Index}",
                "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.webp|All Files|*.*",
                initialDir,
                persistKey: $"h3express.cast{m.Index}");
            if (path == null) return;

            m.PhotoPath = path;
            AddLog($"Cast: character {m.Index} is {Path.GetFileName(path)}, a {m.Noun}, in every story of the run.");
            if (CastOwnClothes) _ = ReadOutfitAsync(m, CancellationToken.None, manual: false);
        }

        private void ClearCastPhoto(ExpressCastMember m)
        {
            m.PhotoPath = string.Empty;
            AddLog($"Cast: character {m.Index} is cast from each story again.");
        }

        // ── Reading an outfit off a photo ───────────────────────────────────────────────────────────

        /// <summary>
        /// Asks the vision model what the person in the photo is wearing and puts it on the card. True when the
        /// card ends up with an outfit for its current photo. Never throws for a model or photo problem; the
        /// card's box can always be typed into instead.
        /// </summary>
        private async Task<bool> ReadOutfitAsync(ExpressCastMember m, CancellationToken token, bool manual)
        {
            if (!m.HasPhoto || m.IsReadingOutfit) return m.HasOutfitForPhoto;
            var photo = m.PhotoPath;

            m.IsReadingOutfit = true;
            try
            {
                var model = await ResolveLlmModelAsync(token, quiet: !manual);
                if (model == null) return m.HasOutfitForPhoto;

                AddLog($"Cast: reading what character {m.Index} wears in {m.PhotoName}…");
                var reply = await _lmStudioService.AnalyzeImageWithSystemPromptAsync(
                    model, photo, ClipRedress.OutfitRequest(m.Index, m.Noun), ClipRedress.OutfitSystemPrompt,
                    maxTokens: 600, cancellationToken: token);

                var roles = CastMembers.Select(c => new CastPromptStamp.CastRole(c.Index, c.Noun)).ToList();
                var line = CastPromptStamp.NormalizeWardrobe(CleanOutput(reply), roles, new[] { roles[m.Index - 1] });
                var outfit = CastPromptStamp.OutfitFor(line, m.Index).Trim().TrimEnd('.', ' ');
                if (outfit.Length > 400) outfit = outfit[..400].TrimEnd();

                if (!string.Equals(m.PhotoPath, photo, StringComparison.OrdinalIgnoreCase)) return false;
                if (outfit.Length == 0)
                {
                    AddLog($"Cast: no outfit could be read from {m.PhotoName} — type it into character {m.Index}'s box.");
                    return false;
                }

                m.OutfitSource = photo;
                m.Outfit = outfit;
                AddLog($"Cast: character {m.Index} wears {outfit}.");
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                AddLog($"Cast: character {m.Index}'s outfit could not be read ({ex.Message}) — type it into the box " +
                       "on the card instead.");
                return false;
            }
            finally
            {
                m.IsReadingOutfit = false;
            }
        }

        /// <summary>The wardrobe lines for the photographed cast in their own clothes, reading any outfit that
        /// has not been read for its current photo. Empty for a slot whose outfit could not be had.</summary>
        private async Task<string> OwnClothesWardrobeAsync(CancellationToken token)
        {
            foreach (var m in CastMembers.Where(m => m.HasPhoto && !m.HasOutfitForPhoto).ToList())
            {
                await ReadOutfitAsync(m, token, manual: false);
                token.ThrowIfCancellationRequested();
            }

            foreach (var m in CastMembers.Where(m => m.HasPhoto && !m.HasOutfitForPhoto))
                AddLog($"WARNING: character {m.Index} has no outfit of their own, so they wear the story's instead.");

            return string.Join("\n", CastMembers.Select(m => m.WardrobeLineFor(SlotOf(m))).Where(l => l.Length > 0));
        }

        // ── Onto the story ──────────────────────────────────────────────────────────────────────────

        /// <summary>The batch has just cleared both cards: put the run's own people back on them.</summary>
        protected override void OnStoryReset()
        {
            var placed = new List<string>();
            foreach (var (m, slot) in new[] { (CastMember1, Character1), (CastMember2, Character2) })
            {
                if (!m.HasPhoto) continue;
                // The kind first: a card with a photo is one the cast pass may not change the sex of.
                slot.Kind = m.Sex == ExpressCastMember.Female ? CharacterSlot.Female : CharacterSlot.Male;
                slot.SourcePath = m.PhotoPath;
                placed.Add($"character {m.Index} = {m.PhotoName} ({m.Noun})");
            }
            if (placed.Count > 0)
                AddLog($"Cast: your photos are on the cards ({string.Join(", ", placed)}) — they are matched to this " +
                       "story's characters by sex once its cast has been read.");
        }

        /// <summary>This story's slot for each photographed member, by member number. Filled per story by
        /// <see cref="AlignCastToStory"/>; a member missing from it plays their own slot.</summary>
        private readonly Dictionary<int, int> _memberSlot = new();

        private int SlotOf(ExpressCastMember m) => _memberSlot.TryGetValue(m.Index, out var slot) ? slot : m.Index;

        private static string KindFor(string sex) =>
            sex == CastAssignment.Female ? CharacterSlot.Female : CharacterSlot.Male;

        /// <summary>
        /// Lines the cards up with the story's own character numbering, before anyone is dressed: each card gets
        /// the sex that story's character has, the Parts follow their characters, and the run's photos go to the
        /// characters of their own sex (<see cref="CastAssignment"/>).
        ///
        /// <para>The numbering that counts is the saved prompts' when there are any — the clips and the wardrobe
        /// were written against it — and otherwise the cast pass's reading of the story, which is what the clip
        /// writer's beat sheet will number the characters by too.</para>
        /// </summary>
        private void AlignCastToStory(SavedStoryPrompts? saved)
        {
            _memberSlot.Clear();

            var detected = LastDetectedCast;
            var fromStory = new string?[]
            {
                detected is { Count: > 0 } ? CastAssignment.SexOf(detected[0].Kind) : null,
                detected is { Count: > 1 } ? CastAssignment.SexOf(detected[1].Kind) : null,
            };
            var fromSaved = saved == null
                ? new string?[2]
                : CastAssignment.FirstKnown(CastAssignment.FromNouns(saved.CastNouns), CastAssignment.FromWardrobe(saved.Wardrobe));
            var need = saved != null && fromSaved.Any(s => s != null) ? fromSaved : fromStory;
            if (need.All(s => s == null))
            {
                if (HasCastOverride)
                    AddLog("WARNING: this story's characters' sexes could not be read, so your photos stay in their own " +
                           "slots — check that character 1 is the right person.");
                return;
            }

            var cards = new[] { Character1, Character2 };

            // The Parts are in the cast pass's order. When the saved numbering runs the other way, they swap, so
            // the woman's part is on the woman's card.
            if (saved != null && need[0] != null && need[1] != null && need[0] != need[1] &&
                fromStory[0] == need[1] && fromStory[1] == need[0])
            {
                (cards[0].Role, cards[1].Role) = (cards[1].Role, cards[0].Role);
                AddLog("Cast: this story's saved prompts number its characters the other way round from this reading " +
                       "of it — the Parts are swapped to match the clips.");
            }

            var photos = CastMembers.Where(m => m.HasPhoto).ToList();
            var map = CastAssignment.Decide(photos.Select(m => (m.Index, m.Sex)).ToList(), need);
            foreach (var (member, slot) in map) _memberSlot[member] = slot;

            var crossed = map.Any(p => p.Key != p.Value);
            if (crossed)
                foreach (var card in cards) card.SourcePath = string.Empty;

            for (var slot = 1; slot <= 2; slot++)
            {
                var card = cards[slot - 1];
                var member = photos.FirstOrDefault(m => SlotOf(m) == slot);
                if (member != null)
                {
                    card.Kind = KindFor(member.Sex);
                    if (crossed) card.SourcePath = member.PhotoPath;
                }
                else if (need[slot - 1] is { } sex && CastAssignment.SexOf(card.Kind) != sex)
                {
                    // A character the story still casts takes the sex its clips were written for. Only when it is
                    // wrong: a "Girl" the cast pass read is already the right sex and must not become a "Female".
                    card.Kind = KindFor(sex);
                }
            }

            if (crossed)
                AddLog("Cast: this story's " + string.Join(" and ",
                           Enumerable.Range(1, 2).Where(s => need[s - 1] != null)
                                     .Select(s => $"character {s} is a {(need[s - 1] == CastAssignment.Female ? "woman" : "man")}")) +
                       " — " + string.Join(", ", photos.Select(m => $"your {m.Noun} ({m.PhotoName}) plays character {SlotOf(m)}")) + ".");

            foreach (var m in photos)
            {
                var slot = SlotOf(m);
                if (need[slot - 1] is { } wanted && wanted != CastAssignment.SexOf(m.Sex))
                    AddLog($"WARNING: your {m.Noun} ({m.PhotoName}) plays character {slot}, whom this story " +
                           $"{(saved != null ? "'s saved prompts were written for as" : "casts as")} a " +
                           $"{(wanted == CastAssignment.Female ? "woman" : "man")} — there is no photo of that sex to put there. " +
                           "The clips will call them by the other pronoun.");
            }
        }

        /// <summary>
        /// Who changes clothes in this saved story: each photographed character in their own clothes whose
        /// outfit differs from the one the clips were written in, and who appears in the clips at all.
        /// </summary>
        private List<ClipRedress.Change> RedressChanges(SavedStoryPrompts saved)
        {
            var changes = new List<ClipRedress.Change>();
            if (!HasCastOverride || !CastOwnClothes) return changes;

            foreach (var m in CastMembers.Where(m => m.HasPhoto && m.HasOutfitForPhoto))
            {
                var slot = SlotOf(m);
                var tag = $"<Picture {slot}>";
                if (!saved.Clips.Any(c => c.Contains(tag, StringComparison.Ordinal))) continue;

                var to = CastPromptStamp.OutfitFor(CastWardrobe, slot).Trim().TrimEnd('.', ' ');
                if (to.Length == 0) continue;
                var from = CastPromptStamp.OutfitFor(saved.Wardrobe, slot).Trim().TrimEnd('.', ' ');
                if (string.Equals(Collapse(from), Collapse(to), StringComparison.OrdinalIgnoreCase)) continue;

                changes.Add(new ClipRedress.Change(slot, m.Noun, from, to));
            }
            return changes;

            static string Collapse(string s) =>
                string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>The outcome of re-dressing a story: the clips to render, whether every clip that needed it
        /// was re-dressed, and the version key they are filed under (empty when they were not filed).</summary>
        private sealed record Redressed(List<string> Clips, bool Complete, string Key);

        /// <summary>
        /// A saved story's clips in the cast's own clothes: the version filed by an earlier run when there is one,
        /// otherwise one checked rewrite per clip that carries a changing character. A clip whose rewrite fails
        /// its checks twice keeps its original wording and says so. Null when stopped.
        /// </summary>
        private async Task<Redressed?> RedressClipsAsync(SavedStoryPrompts saved, IReadOnlyList<ClipRedress.Change> changes)
        {
            var key = ClipRedress.VariantKey(saved.Clips, changes);
            var cached = await StoryPrompts.FindVariantAsync(saved.StoryHash, key);
            if (cached != null && cached.Clips.Count == saved.Clips.Count)
            {
                AddLog($"📚 These clips were already re-dressed for this cast's clothes on an earlier run — using that version.");
                return new Redressed(cached.Clips, true, key);
            }

            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_analyzeCts.Token, LuckyToken);
            var token = linked.Token;

            IsAnalyzing = true;
            IsWritingPrompt = true;
            var status = ProcessingStatus;
            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null)
                {
                    AddLog("WARNING: no LLM to re-dress the clips with — they keep their original clothing words, while " +
                           "the wardrobe lock and the sheets show your cast's own clothes.");
                    return new Redressed(saved.Clips.ToList(), false, string.Empty);
                }

                AddLog($"=== Re-dressing {saved.Clips.Count} saved clip(s) for your cast's own clothes: " +
                       string.Join("; ", changes.Select(c => $"<Picture {c.Character}> now wears {c.To}")) + " ===");

                var clips = new List<string>(saved.Clips.Count);
                var kept = new List<int>();
                for (var i = 0; i < saved.Clips.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var clip = saved.Clips[i];
                    var relevant = changes.Where(c => clip.Contains($"<Picture {c.Character}>", StringComparison.Ordinal)).ToList();
                    if (relevant.Count == 0)
                    {
                        clips.Add(clip);
                        continue;
                    }

                    var phase = $"Re-dressing clip {i + 1} of {saved.Clips.Count} for your cast…";
                    ProcessingStatus = phase;
                    if (IsFeelingLucky) LuckyPhase = "5/6 · " + phase;

                    string? done = null;
                    var reason = string.Empty;
                    for (var attempt = 1; attempt <= 2 && done == null; attempt++)
                    {
                        var raw = await _lmStudioService.SendTextChatAsync(
                            model, ClipRedress.SystemPrompt, ClipRedress.BuildRequest(clip, relevant, reason),
                            maxTokens: 3000, cancellationToken: token, sampling: new LlmSampling(Temperature: 0.3));
                        var body = NormalizeClipBody(raw ?? string.Empty);
                        var complaint = ClipRedress.Validate(clip, body);
                        if (complaint == null) done = body;
                        else reason = complaint;
                    }

                    if (done == null)
                    {
                        kept.Add(i + 1);
                        clips.Add(clip);
                        AddLog($"WARNING: clip {i + 1} could not be re-dressed cleanly ({reason}) — it keeps its original wording.");
                    }
                    else
                    {
                        clips.Add(done);
                        AddLog($"Clip {i + 1}/{saved.Clips.Count} re-dressed.");
                    }
                }

                if (kept.Count > 0)
                {
                    AddLog($"WARNING: clip(s) {string.Join(", ", kept)} still describe the story's old clothes. The " +
                           "wardrobe lock and the sheets outrank the prose, so they should render close — but this " +
                           "version is not filed, and the next run tries those clips again.");
                    return new Redressed(clips, false, string.Empty);
                }

                var variant = saved.Clone();
                variant.Clips = clips;
                variant.Wardrobe = CastWardrobe.Trim();
                variant.CastNouns = CastNouns();
                variant.Origin = "Re-dressed for your cast's own clothes";
                variant.EditedByHand = false;
                variant.ModifiedAt = DateTime.Now;
                try
                {
                    await StoryPrompts.SaveVariantAsync(variant, key);
                    AddLog("📚 Re-dressed version filed — the same cast in the same clothes on this story skips this step next time.");
                }
                catch (Exception ex)
                {
                    AddLog($"📚 The re-dressed version could not be filed ({ex.Message}); it is used for this run only.");
                }
                return new Redressed(clips, true, key);
            }
            catch (OperationCanceledException)
            {
                AddLog("Re-dressing stopped.");
                return null;
            }
            finally
            {
                IsWritingPrompt = false;
                IsAnalyzing = false;
                ProcessingStatus = status;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
            }
        }
    }
}
