using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// H3 Cast, the story's cast and the cast photos: the two-hander twin of
    /// <c>H3EnsembleViewModel.Cast.cs</c>, sharing its machinery through <see cref="CastPhotoWorkflows"/>.
    ///
    /// <para><b>Reading the cast out of the story.</b> A story uploaded or pasted into the story box
    /// names its two leads, and a debounced llama-server pass now fills the character cards with
    /// them — the kind that shows in the Sex dropdown, and the Part the card heading shows. It never
    /// overwrites a card the user has made their own: a photo, a hand-written Part, or a hand-set sex
    /// mark a slot as spoken for, and a card this pass filled earlier is only rewritten while it
    /// still says exactly what the pass left there. This tab casts <b>people only</b> — its sheet
    /// builder, wardrobe pass and Sex dropdown have no branch for a cloud or a herd, so the ask is
    /// restricted to man / woman / boy / girl. A story whose cast is not two people belongs on the
    /// 🎬🎭 H3 Ensemble tab.</para>
    ///
    /// <para><b>Generating the photo.</b> Each card's ✨ Generate button renders that character's
    /// portrait with one of the Image Generator tab's base graphs — Z-Image, Z-Famegrid, Krea2 realism,
    /// or Qwen 2.5.1.2 — from the character's Part plus the locked wardrobe, and drops the result into
    /// the card's photo slot as if it had been browsed for. Build Character Sheet then turns it into the
    /// reference H3 receives, exactly as with a browsed photo.</para>
    /// </summary>
    public partial class H3CastViewModel
    {
        // The two-hander is exactly two slots by design — the graph, the prompt and the Sex
        // dropdown all say so.
        private const int CastSlots = 2;

        private bool _isDerivingCast;
        private CancellationTokenSource? _castCts;

        /// <summary>slot index → <c>"kind|role"</c> as the automatic pass last wrote it. A field that
        /// still matches its half of the stamp is one the user has not touched since, so a later pass may
        /// rewrite it; anything else is the user's own work and stands. Read through
        /// <see cref="AutoStampFor"/>.</summary>
        private readonly Dictionary<int, string> _autoCastStamp = new();

        #region Cast detection — the story names them, the tab casts them

        /// <summary>
        /// Debounced, like the wardrobe: fires 2.5 s after the story stops changing (typing, pasting
        /// and 📄 Load .txt all land here through the <see cref="StoryText"/> setter).
        /// </summary>
        private void ScheduleCastDerive()
        {
            _castCts?.Cancel();
            _castCts = null;

            if (!HasStoryText) return;

            var cts = new CancellationTokenSource();
            _castCts = cts;
            _ = AutoDeriveCastAsync(cts);
        }

        private async Task AutoDeriveCastAsync(CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2.5), token);

                // Another model pass is in flight — come back when it lands rather than queuing a
                // second llama-server turn behind it. The user typing again cancels and restarts this.
                if (_isDerivingCast || _isDerivingWardrobe || IsAnalyzing)
                {
                    ScheduleCastDerive();
                    return;
                }

                await DeriveCastNowAsync(token, quiet: true);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"Automatic cast pass failed: {ex.Message}");
            }
            finally
            {
                if (ReferenceEquals(_castCts, cts)) _castCts = null;
                cts.Dispose();
            }
        }

        /// <summary>
        /// The cast pass itself, without the 2.5 s debounce in front of it: read the story's two leads and
        /// write their sex and Part into whichever cards are still free.
        ///
        /// <para>Split out of <see cref="AutoDeriveCastAsync"/> so 🍀 I'm Feeling Lucky can <b>await</b> it.
        /// A run that starts by generating photographs cannot wait out a debounce and hope — a card with no
        /// Part on it yet produces a portrait of nobody in particular, and the whole chain is then built on
        /// that.</para>
        ///
        /// <para>Silent, and a no-op, when both cards already say something: a photo, a hand-written Part or
        /// a hand-set sex all mark a slot as spoken for, and this must never overwrite them.</para>
        /// </summary>
        protected async Task DeriveCastNowAsync(CancellationToken token, bool quiet)
        {
            if (!SlotIsFree(_character1) && !SlotIsFree(_character2)) return;

            _isDerivingCast = true;
            IsAnalyzing = true;
            try
            {
                var model = await ResolveLlmModelAsync(token, quiet);
                if (model == null) return;

                AddLog($"Reading the cast out of the story — sending to {_lmStudioService.DescribeTarget(model)}…");
                var reply = await CastPhotoWorkflows.AskCastAsync(
                    _lmStudioService, model, StoryText, CastSlots, personKindsOnly: true, token);
                token.ThrowIfCancellationRequested();

                var detected = CastPhotoWorkflows.ParseCastLines(reply, CastSlots);
                if (detected.Count == 0)
                {
                    AddLog("The cast could not be read out of the story — set the sex and Part on " +
                           "the cards by hand.");
                    return;
                }
                ApplyDetectedCast(detected);
            }
            finally
            {
                _isDerivingCast = false;
                // See the wardrobe pass: the shared busy flag is only ours to clear while nothing else
                // is holding it.
                if (!IsWritingPrompt) IsAnalyzing = false;
            }
        }

        /// <summary>The kind and Part this pass last wrote into a slot, or nulls if it never wrote one.
        /// Split apart because the two are answerable separately: the user moving the sex dropdown on a
        /// card does not make its Part theirs, and vice versa.</summary>
        private (string? Kind, string? Role) AutoStampFor(int index)
        {
            if (!_autoCastStamp.TryGetValue(index, out var stamp)) return (null, null);
            var bar = stamp.IndexOf('|');
            return bar < 0 ? (null, null) : (stamp[..bar], stamp[(bar + 1)..]);
        }

        /// <summary>
        /// Whether the automatic cast pass may write this card's Part — either a card with no Part on it
        /// yet, or one this pass itself filled and the user has not typed over since.
        ///
        /// <para><b>A photo does not stop it.</b> The picture says who this character <i>is</i>; the Part
        /// says who they are <i>in this story</i>, and pasting a new story is exactly the moment those two
        /// stop agreeing. Blocking on the photo meant a tab whose cast had been picked first — which is how
        /// H3 Eros is used — kept the previous story's parts under the cards while the wardrobe, which
        /// never asked about photos, re-dressed everyone for the new one. The sex is a separate question,
        /// and there the photo does decide: see <see cref="SlotKindIsFree"/>.</para>
        /// </summary>
        private bool SlotIsFree(CharacterSlot c)
        {
            var (_, role) = AutoStampFor(c.Index);
            if (role != null) return role == c.Role;
            return !c.HasRole && (c.Kind == CharacterSlot.Male || c.Kind == CharacterSlot.Female);
        }

        /// <summary>
        /// Whether the pass may also set this card's <i>sex</i>. A browsed or generated photo pins it — the
        /// picture decides who is on screen, and flipping a photographed woman to "Male" because the new
        /// story's first lead is a man would leave the card describing somebody who is not in it. Without a
        /// photo it is the same rule as the Part: ours to move until the user moves it.
        /// </summary>
        private bool SlotKindIsFree(CharacterSlot c)
        {
            if (c.HasSource) return false;
            var (kind, _) = AutoStampFor(c.Index);
            return kind == null || kind == c.Kind;
        }

        /// <summary>
        /// Writes the detected characters into the free cards, in cast order, and retires the stale
        /// auto-filled ones a one-lead story no longer lists. Everything goes through the normal
        /// property setters, so the wardrobe pass and the summaries react exactly as if the user had
        /// set the cards by hand.
        /// </summary>
        private void ApplyDetectedCast(IReadOnlyList<(string Kind, string Role)> detected)
        {
            var taken = detected.Take(CastSlots).ToList();
            var free = new[] { _character1, _character2 }.Where(SlotIsFree).ToList();
            var retired = 0;

            for (var i = 0; i < free.Count; i++)
            {
                if (i < taken.Count)
                {
                    // The sex only moves on a card whose photo has not already answered it.
                    if (SlotKindIsFree(free[i])) free[i].Kind = taken[i].Kind;
                    free[i].Role = taken[i].Role;
                    // Stamped from the live card, not from what was detected: the kind may have been
                    // left alone above, and the Role setter trims. A stamp that does not match what the
                    // card now reads would look like the user's own edit on the next story.
                    _autoCastStamp[free[i].Index] = $"{free[i].Kind}|{free[i].Role}";
                }
                else if (_autoCastStamp.ContainsKey(free[i].Index))
                {
                    // Only auto-filled cards are retired — a pristine card stays pristine, and the
                    // user's own cards were never in this list.
                    free[i].Role = string.Empty;
                    if (SlotKindIsFree(free[i]))
                        free[i].Kind = free[i].Index % 2 == 0 ? CharacterSlot.Female : CharacterSlot.Male;
                    _autoCastStamp.Remove(free[i].Index);
                    retired++;
                }
            }

            var who = string.Join("; ", new[] { _character1, _character2 }
                .Where(c => c.HasRole)
                .Select(c => $"Character {c.Index} — {c.Role}"));
            AddLog((retired > 0
                ? $"Cast from the story: {who} — retired {retired} character(s) the story no longer lists."
                : $"Cast from the story: {who}") +
                " Check each card's sex and Part, then ✨ Generate or browse a photo.");
        }

        /// <summary>
        /// Whether there is anything for 🧹 to throw away: a Part on either card, or a wardrobe. The kinds
        /// are deliberately not counted — every card always has one, so counting them would leave the button
        /// permanently enabled and pressing it on a pristine tab would do nothing visible.
        /// </summary>
        public bool HasDerivedCast => HasCastWardrobe || _character1.HasRole || _character2.HasRole;

        /// <summary>
        /// The 🧹 button: throws away everything the story wrote about the cast — both cards' Part, the sex
        /// of any card no photo has answered for, the record of what the automatic pass last filled in, and
        /// the wardrobe with them — and hands the whole question back to whatever story is in the box now.
        ///
        /// <para><b>Why a button and not an automatic reset.</b> The automatic pass is deliberately
        /// conservative: a card the user typed over is theirs and is never overwritten, and a card it filled
        /// itself is only rewritten while it still reads exactly as it left it (see
        /// <see cref="SlotIsFree"/>). That is right while one story is being worked on, and wrong the moment a
        /// different story is pasted in — the new film then inherits the previous one's parts, the wardrobe is
        /// re-derived around characters who are not in it, and every clip is written for the wrong cast.
        /// Nothing here can tell a pasted rewrite from a pasted replacement, so the answer is one press rather
        /// than a guess.</para>
        ///
        /// <para><b>What it leaves alone.</b> The photos, the built sheets and the prompt box: those were
        /// chosen or written, not derived from the story text. A sheet built in the old wardrobe reports
        /// itself stale as soon as the new one lands, exactly as it does after 🎽 Derive.</para>
        /// </summary>
        private void ClearDerivedCast()
        {
            foreach (var c in new[] { _character1, _character2 })
            {
                c.Role = string.Empty;
                // The photo is what pins the sex — see SlotKindIsFree. A card with a picture on it keeps the
                // kind the picture shows; an empty one goes back to the default the pass is free to move.
                if (!c.HasSource) c.Kind = c.Index % 2 == 0 ? CharacterSlot.Female : CharacterSlot.Male;
            }

            // Nothing on the cards is ours any more, so nothing is stamped: the next pass sees two free
            // slots rather than two it filled and must leave standing.
            _autoCastStamp.Clear();

            // The outfits describe these characters, so they go with them — and clearing the box is also what
            // puts the wardrobe back under the story's control when the user had unlocked it (see
            // ClearWardrobe). It re-arms the wardrobe debounce; ScheduleCastDerive below re-arms the cast's.
            ClearWardrobe();

            AddLog("Cast cleared: both Parts, the sexes no photo had answered for, and the wardrobe. The " +
                   "story in the box re-casts itself a couple of seconds after it stops changing — press " +
                   "🎽 Derive to do the wardrobe now, and Analyze to rewrite the clips. Photos, sheets and " +
                   "the prompt box are untouched.");
            ScheduleCastDerive();
        }

        #endregion

        #region Cast photos — Z-Image / Krea2 / Qwen 2.5.1.2

        /// <summary>Whose card the ✨ menu was last opened on. The menu's LoRA entries carry only the
        /// LoRA as their parameter, so the slot rides along on the tab instead — set by the window
        /// every time a card's menu opens, before any entry can be clicked.</summary>
        internal CharacterSlot? CastPhotoMenuSlot { get; set; }

        /// <summary>The ✨ menu's Z-Image entries: "(as authored …)" first, then every zimage LoRA
        /// on disk. Rebuilt each time a menu opens — see <see cref="RefreshCastPhotoLoras"/>.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastZimageLoraMenu { get; } = new();

        /// <summary>The ✨ menu's Z-Famegrid entries — the zib identity LoRAs of the zimage tree
        /// (the whole zimage folder when there is no zib), feeding that workflow's own
        /// character-LoRA slot. Same shape as the Z-Image list.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastFamegridLoraMenu { get; } = new();

        /// <summary>The ✨ menu's Krea2 entries, same shape as the Z-Image list.</summary>
        public System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> CastKrea2LoraMenu { get; } = new();

        /// <summary>Rescans the LoRA folders the Image Generator tab reads and rebuilds the ✨ menu's
        /// LoRA lists. Called on every menu open — a disk scan is cheap next to a GPU render.</summary>
        internal void RefreshCastPhotoLoras()
        {
            Refill(CastZimageLoraMenu, CastPhotoWorkflows.ListZimageLoras(_settingsService.Settings, AddLog), "zimage");
            Refill(CastFamegridLoraMenu, CastPhotoWorkflows.ListFamegridLoras(_settingsService.Settings, AddLog), "famegrid");
            Refill(CastKrea2LoraMenu, CastPhotoWorkflows.ListKrea2Loras(_settingsService.Settings, AddLog), "krea2");

            void Refill(
                System.Collections.ObjectModel.ObservableCollection<CastPhotoWorkflows.CastLora> menu,
                IReadOnlyList<CastPhotoWorkflows.CastLora> loras,
                string engine)
            {
                menu.Clear();
                menu.Add(CastPhotoWorkflows.CastLora.AsAuthored);
                foreach (var lora in loras)
                    menu.Add(lora);
                if (menu.Count == 1)
                    AddLog($"No {engine} LoRAs found — the ✨ menu offers only the workflow's own. " +
                           "Check the LoRA folders in Settings.");
            }
        }

        private bool CanGenerateCastPhoto(CharacterSlot? slot) =>
            slot is { IsCast: true } && !slot.IsGeneratingPhoto && !IsGeneratingCastPhoto;

        /// <summary>One at a time: the lease serializes the GPU anyway, and a second card's photo
        /// landing while the first is in flight is the one race worth never having.</summary>
        private bool IsGeneratingCastPhoto =>
            _character1.IsGeneratingPhoto || _character2.IsGeneratingPhoto;

        /// <summary>
        /// Renders one character's portrait with an Image Generator base workflow and drops it into
        /// their card's photo slot. Mirrors <see cref="BuildSheetsAsync"/>'s lifecycle — wardrobe
        /// first, then the GPU lease, then ComfyUI — because it is the same kind of job: a small
        /// generation whose output becomes part of the cast's references.
        /// </summary>
        private async Task GenerateCastPhotoAsync(CharacterSlot? slot, string engine, CastPhotoWorkflows.CastLora? lora = null)
        {
            if (slot == null || !CanGenerateCastPhoto(slot)) return;

            var label = CastPhotoWorkflows.LabelFor(engine) +
                        (lora is { IsDefault: false } ? $" + LoRA {lora.Name}" : string.Empty);
            slot.IsGeneratingPhoto = true;
            slot.PhotoPhase = "Preparing…";

            WorkflowQueueCoordinator.WorkflowLease? lease = null;
            try
            {
                AddLog($"=== Character {slot.Index}: generating the photo with {label} ===");

                // The portrait should wear what the sheet will be photographed in — deciding the
                // wardrobe first is what keeps photo, sheet and prompt agreeing on the outfit.
                slot.PhotoPhase = "Deciding the wardrobe…";
                if (!await EnsureWardrobeAsync(CancellationToken.None))
                    AddLog($"WARNING: no wardrobe could be written — Character {slot.Index}'s photo is " +
                           "generated from their description alone.");

                slot.PhotoPhase = "Waiting for the GPU…";
                lease = await _workflowCoordinator.AcquireAsync("H3Cast", CancellationToken.None);

                slot.PhotoPhase = "Checking ComfyUI…";
                var comfyOk = await _comfyUIService.DetectAndRestartIfCrashedAsync(s => AddLog($"[Auto-Restart] {s}"));
                if (!comfyOk) throw new Exception("ComfyUI is not running.");
                if (!_comfyUIService.IsConnected)
                {
                    slot.PhotoPhase = "Connecting to ComfyUI…";
                    await _comfyUIService.ConnectAsync();
                }

                var prompt = BuildCastPhotoPrompt(slot);
                var ts = DateTime.Now.ToString("yyyyMMddHHmmss");
                var runToken = $"cast_{slot.Index}_{ts}";
                var seed = System.Random.Shared.NextInt64(0, 1_000_000_000_000_000L);

                var (json, saveNode) = await CastPhotoWorkflows.BuildAsync(
                    engine, $"{OutputSubfolder}/{runToken}", seed, prompt, AddLog, lora);
                AddLog($"Character {slot.Index} ({slot.Description}): {label} portrait — {prompt}");

                var promptId = await SubmitCastPhotoAsync(json, slot, CancellationToken.None);

                slot.PhotoPhase = "Retrieving the photo…";
                string? local = null;
                var byNode = await _comfyUIService.HttpClient.GetOutputsByNodeAsync(promptId, CancellationToken.None);
                if (byNode.TryGetValue(saveNode, out var outs) && outs.Count > 0)
                    local = await ResolveImageToLocalAsync(outs[0]);
                local ??= FindTokenImageOnDisk(runToken);
                if (local == null || !File.Exists(local))
                    throw new Exception($"Character {slot.Index}'s photo was not produced.");

                // Exactly what browsing for it does — the sheet build, the references and the
                // refine pass all take it from there. A portrait is not yet a reference; the
                // sheet waits for 🪪 Build Character Sheet, so the user can re-generate or
                // swap photos first and only pay for the sheet once they are satisfied.
                Application.Current.Dispatcher.Invoke(() =>
                {
                    slot.SourcePath = local;
                    slot.UseSourceAsSheet = false;   // a generated portrait is a photo, never a ready-made sheet
                });
                AddLog($"Character {slot.Index}: photo generated — {Path.GetFileName(local)}.");
                slot.PhotoPhase = "Photo ready — build the sheet with 🪪";

                // A photo generated a moment ago has never had a sheet built from it, so this all but always
                // misses. It is here anyway because ✨ Generate can be pressed on a card whose photo was
                // regenerated from the same seed, and because the two ways a photo reaches a card should not
                // behave differently.
                await AdoptSheetFromLibraryAsync(slot);
                if (slot.HasSheet) slot.PhotoPhase = "Photo ready — its sheet came back from the library";
            }
            catch (Exception ex)
            {
                AddLog($"ERROR (cast photo): {ex.Message}");
                slot.PhotoPhase = $"Error: {ex.Message}";
                MessageBox.Show($"Generating Character {slot.Index}'s photo failed:\n{ex.Message}",
                    "H3 Cast", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                lease?.Dispose();
                slot.IsGeneratingPhoto = false;
                OnCanExecuteChanged();
            }
        }

        private async Task<string> SubmitCastPhotoAsync(string json, CharacterSlot slot, CancellationToken token)
        {
            var workflow = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
            var progress = new Progress<ProgressMessage>(msg =>
            {
                if (msg.Data?.Value != null && msg.Data?.Max > 0)
                    Application.Current.Dispatcher.Invoke(() =>
                        slot.PhotoPhase = $"Generating… {msg.Data.Value}/{msg.Data.Max}");
            });

            var promptId = await _comfyUIService.ExecuteWorkflowAsync(workflow, progress, token);
            AddLog($"Cast photo workflow submitted, ID: {promptId}");
            return promptId;
        }

        /// <summary>
        /// The portrait brief: who the character is, what they wear (the locked wardrobe, when there
        /// is one), and a clean neutral studio shot of exactly them — the sheet builder re-renders it
        /// into panels anyway, so what it needs is a readable single subject, not a performance.
        /// </summary>
        private string BuildCastPhotoPrompt(CharacterSlot slot)
        {
            var outfit = CastPromptStamp.OutfitFor(CastWardrobe, slot.Index);
            var sb = new StringBuilder();

            sb.Append($"A full-length character portrait photograph of {slot.Description}, standing " +
                      "relaxed and facing the camera, head to feet fully in frame, looking at the lens.");

            if (outfit.Length > 0)
                sb.Append(" Wearing exactly this: ").Append(outfit.TrimEnd('.', ' ')).Append('.');

            sb.Append(" Neutral expression, natural pose, no other people, no props, no text. " +
                      "Plain light grey seamless studio background, soft even lighting, sharp focus — a " +
                      "clean reference photograph of this character for a film's cast list.");
            return sb.ToString();
        }

        #endregion
    }
}
