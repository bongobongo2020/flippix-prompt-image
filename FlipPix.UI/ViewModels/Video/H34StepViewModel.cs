using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using Application = System.Windows.Application;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 4-Step" tab — the 🌹🎯 H3 Eros story flow rendered through the author's
    /// <b>4-step SLA reference graph</b> (<c>05_ref2va_4step_sla.json</c>): the fused ref-delta turbo8
    /// checkpoint, <c>H3SLAAttention</c> at 0.9/64, <c>MiniMaxH3SigmaShift</c> 12/3, and
    /// <c>res_multistep</c>/<c>simple</c> at <b>four</b> steps.
    ///
    /// <para><b>What is different from H3 Eros, and why.</b> Eros hunts three drafts per clip and then
    /// upscales every picked latent — a second sampling pass, at the finished canvas, on every clip in
    /// the story. On a twelve-step hybrid checkpoint that is a reasonable trade. On a four-step one it is
    /// not: the hunt itself is so cheap that the upscale becomes the entire cost of the run, and it is
    /// paid on every clip whether or not that clip is one you would ever export at full size.</para>
    ///
    /// <para>So this tab <b>stops after the pick</b>. ▶ Generate hunts three drafts for every clip in the
    /// queue back to back, you choose one per clip on the board, and the chosen draft <i>is</i> the clip:
    /// it is copied into the output folder under this tab's name and joined into the story exactly as a
    /// finished Eros clip would be. There is no second sampling pass anywhere in the loop, and
    /// <c>h3-4step.json</c> does not even contain one — the file is the three hunt branches and their
    /// preview sinks, and nothing downstream.</para>
    ///
    /// <para><b>Upscaling is a separate, later act.</b> Every draft is written with a
    /// <see cref="SeedRecipe"/> sidecar beside it — the seed, the prompt as it was actually written into
    /// the graph, the references, the checkpoint, the step count and the draft canvas. The 🔎⬆️ Seed
    /// Upscale tab (<see cref="SeedUpscaleViewModel"/>) scans a folder of those, shows what it finds, and
    /// re-samples only the ones you tick — reproducing the draft's latent and lifting <i>that</i> through
    /// <c>MinimaxH3LatentUpscaler3D</c>. Which is why the sidecar has to be exact: an mp4 carries no
    /// latent, so the only route back to one is to sample the same seed through the same stack, and every
    /// field that differs is a different video.</para>
    ///
    /// <para>Everything before the render — the story, the beat sheet, the per-clip writer, the cast
    /// sheets, the wardrobe lock, the queue, the board, the FFmpeg join — is inherited from
    /// <see cref="H3ErosViewModel"/> untouched.</para>
    /// </summary>
    public class H34StepViewModel : H3ErosViewModel
    {
        public H34StepViewModel(
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
            // The author measured this graph at 0.9. Eros's dial defaults to the more conservative 0.85,
            // which is the right default for a twelve-step render; here it would be a different stack from
            // the one the reference numbers came from.
            SlaSparsity = 0.90;

            AddLog("H3 4-Step initialized — three drafts per clip on the 4-step SLA stack, and the take " +
                   "you pick is the clip. Upscale the keepers later in 🔎⬆️ Seed Upscale.");
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The hunt half of the 4-step SLA stack, built by <c>tools/build_h3_4step.py</c>. It has
        /// no upscaler, no second pass and no RIFE in it — see the class remarks.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-4step.json";

        protected override string OutputSubfolder => "h3_4step";

        protected override string OutputFileStem => "H34Step";

        protected override string OutputFolderName => "H34Step";

        protected override string TabDisplayName => "H3 4-Step";

        protected override string ChainLibraryFolder => "h34step";

        protected override string RunTokenPrefix => "h34step";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h34step_queue.json");

        /// <summary>What <c>h3-4step.json</c> names in its UNETLoader.</summary>
        protected override string ShippedModel =>
            "h3-minimax/minimax_h3_fused_refdelta_r1024_turbo8_mystic07_int8_convrot.safetensors";

        /// <summary>Four, because the checkpoint is distilled to four. Twelve — Eros's number, for the
        /// hybrid it renders — is eight steps of nothing on this one.</summary>
        protected override int FirstPassSteps => 4;

        protected override string? RecallDiffusionModel(ComfyUISettings? settings) =>
            settings?.H34StepDiffusionModel;

        protected override void StoreDiffusionModel(ComfyUISettings settings, string name) =>
            settings.H34StepDiffusionModel = name;

        // ── The one-line summaries, which say something different here ──────────────────────────────

        /// <summary>Eros's line ends with an upscale per clip. There is none here, so the honest sentence
        /// stops at the pick — and says where the upscale went.</summary>
        public override string HuntSummary
        {
            get
            {
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var pick = AutoPickSample
                    ? $"take {AutoPickSlot} picked automatically"
                    : "you pick one per clip";
                return $"Hunt sweep: {SampleCount} drafts per clip at ≈{dw}×{dh} " +
                       $"({PreviewMegapixels:0.##} MP), {FirstPassSteps} steps, the whole queue without " +
                       $"stopping, then {pick} — and the take you pick is the clip. " +
                       $"No upscale pass: send the keepers to 🔎⬆️ Seed Upscale.";
            }
        }

        /// <summary>
        /// One canvas, not two. Eros reports the finished size because its second pass holds a frame stack
        /// at that size; nothing here ever renders above the draft canvas, so quoting a finished size would
        /// be warning about memory this tab never allocates.
        /// </summary>
        public override string LoadSummary
        {
            get
            {
                var frames = FramesForSeconds(ClampLength(LengthSeconds));
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var draftGb = FrameStackGb(frames, dw, dh);
                var text = $"{frames} frames: ≈{draftGb:0.#} GB per draft at {dw}×{dh}, " +
                           $"one draft at a time.";
                return draftGb >= HeavyFrameStackGb
                    ? text + " ⚠ That is the size that takes ComfyUI down mid-render — shorten " +
                             "the clip or drop the draft quality."
                    : text;
            }
        }

        public override bool HasLoadWarning =>
            FrameStackGb(FramesForSeconds(ClampLength(LengthSeconds)),
                         H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32).Width,
                         H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32).Height)
            >= HeavyFrameStackGb;

        // ── The graph ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Written through <see cref="H3SeedGraph"/> rather than by hand, because this is the wiring the
        /// Seed Upscale tab has to reproduce exactly to get back the latent behind a draft. One method,
        /// called by both tabs, is the only version of that which stays true — see the class remarks
        /// on <see cref="H3SeedGraph"/>.
        /// </summary>
        protected override void ApplyCommonInputs(
            JsonObject root, H3CastQueueItem item, IReadOnlyList<string> uploaded,
            string prompt, double lengthSeconds)
        {
            var recipe = RecipeFor(item, prompt, lengthSeconds);
            var slaWired = H3SeedGraph.Apply(root, recipe, uploaded);

            if (item.UseSla && !slaWired)
                AddLog("  SLA is on but this workflow file has no H3SLAAttention node — rendering dense.");
            else if (slaWired)
                AddLog($"  SLA block-sparse attention at sparsity {recipe.SlaSparsity:0.00}, block 64. " +
                       "A short clip at a small canvas falls back to dense on its own.");
        }

        /// <summary>Everything about this clip that decides its latent, in the form both tabs read.
        /// The <paramref name="prompt"/> is the detagged text — the string that actually goes into the
        /// graph — because re-detagging it later would resolve the picture tags against whatever the
        /// panel count is then, which is how a two-hander comes back with one face.</summary>
        private SeedRecipe RecipeFor(H3CastQueueItem item, string prompt, double lengthSeconds)
        {
            var (dw, dh) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
            return new SeedRecipe
            {
                SourceTab = TabDisplayName,
                Prompt = prompt,
                MaxFidelityReferences = item.MaxFidelityReferences,
                LengthSeconds = lengthSeconds,
                AspectRatio = item.AspectRatio,
                DraftMegapixels = item.PreviewMegapixels,
                DraftWidth = dw,
                DraftHeight = dh,
                DiffusionModel = item.DiffusionModel,
                Steps = FirstPassSteps,
                UseSla = item.UseSla,
                SlaSparsity = item.SlaSparsity,
                TargetMegapixels = item.Megapixels,
                UpscaleSteps = item.UpscaleSteps,
                UseRife = item.UseRife,
                Title = item.DisplayText,
                StoryId = item.StoryId,
                ClipIndex = item.ClipIndex,
                ClipCount = item.ClipCount,
            };
        }

        // ── Drafts, and the recipe that outlives them ───────────────────────────────────────────────

        /// <summary>
        /// The stock draft bookkeeping, plus the sidecar. Written here rather than at the end of the hunt
        /// because this is the one place that knows both the draft's file and the seed it was sampled on,
        /// and a sidecar written from anywhere else would be guessing at the pairing.
        ///
        /// <para>A sidecar that cannot be written is logged and shrugged off: it costs a later upscale,
        /// not this run's clip.</para>
        /// </summary>
        protected override void SetDraft(ErosHuntClip row, int slot, string localPath, long seed)
        {
            base.SetDraft(row, slot, localPath, seed);

            try
            {
                var item = row.Item;
                var recipe = RecipeFor(item, DetaggedPrompt(item), ClampLength(item.LengthSeconds));
                recipe.Seed = seed;
                recipe.Slot = slot;
                recipe.DraftVideoPath = localPath;
                recipe.ReferenceImages = ReferencePanelsFor(item).ToList();
                recipe.Save(SeedRecipe.SidecarPathFor(localPath));
            }
            catch (Exception ex)
            {
                AddLog($"  Take {slot}: the seed recipe could not be written ({ex.Message}) — this draft " +
                       "can still be picked, but 🔎⬆️ Seed Upscale will not be able to reproduce it.");
            }
        }

        /// <summary>The local panel files behind this clip's references, in the order they are wired.
        /// The same policy as the upload: one reference per view, never the assembled sheet, and
        /// character 2's only when the clip's own text names them.</summary>
        private IReadOnlyList<string> ReferencePanelsFor(H3CastQueueItem item)
        {
            var panels1 = ResolvePanels(item.Character1PanelPaths, item.Character1SheetPath, 1);
            var panels2 = CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2)
                ? ResolvePanels(item.Character2PanelPaths, item.Character2SheetPath, 2)
                : (IReadOnlyList<string>)Array.Empty<string>();
            return panels1.Concat(panels2).ToList();
        }

        // ── The finish, which is a copy ─────────────────────────────────────────────────────────────

        /// <summary>
        /// No second render: the picked draft <i>is</i> the clip. It is copied into the output folder
        /// under this tab's naming — the same names an Eros finish would produce — so the inherited story
        /// join, the results pane and the Send-to-Edit path all work unchanged.
        /// </summary>
        protected override async Task FinishAsync(ErosHuntClip row, double progressFrom, double progressTo,
            CancellationToken token)
        {
            var item = row.Item;
            var chosen = item.ChosenSampleSlot;
            if (chosen is < 1 or > SampleCount)
                throw new Exception("This clip has no usable pick — choose a take on the board first.");

            var source = item.HuntSamplePaths.ElementAtOrDefault(chosen - 1);
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                throw new Exception($"Take {chosen}'s file is gone — 🎲 re-roll it, or pick another take.");

            IsProcessing = true;
            HasResult = false;
            ProcessingStatus = $"Saving {row.Title} take {chosen}...";
            ProcessingProgress = progressFrom;

            var ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var outputDir = Path.Combine(
                _settingsService.Settings?.OutputFolderPath ?? Path.GetTempPath(), OutputFolderName);
            Directory.CreateDirectory(outputDir);
            var finalName = item.IsStoryClip
                ? $"{OutputFileStem}_{(string.IsNullOrEmpty(item.StoryId) ? ts : item.StoryId)}_clip{item.ClipIndex:00}.mp4"
                : $"{OutputFileStem}_{ts}.mp4";
            var finalPath = Path.Combine(outputDir, finalName);

            File.Copy(source, finalPath, true);
            // The recipe travels with the clip, so a keeper can be found and upscaled from the output
            // folder as readily as from ComfyUI's, which is the folder people actually keep.
            var sidecar = SeedRecipe.SidecarPathFor(source);
            if (File.Exists(sidecar)) File.Copy(sidecar, SeedRecipe.SidecarPathFor(finalPath), true);

            await LocalCopyService.CopyVideoAsync(finalPath);
            token.ThrowIfCancellationRequested();

            var fi = new FileInfo(finalPath);
            var (dw, dh) = H3Canvas.Resolve(item.AspectRatio, item.PreviewMegapixels, 32);
            item.OutputVideoPath = finalPath;
            ProcessingProgress = progressTo;

            Application.Current.Dispatcher.Invoke(() =>
            {
                row.OutputPath = finalPath;
                ResultVideoPath = finalPath;
                ShowInPlayer(finalPath, $"{row.Title} · take {chosen}");
                ResultVideoInfo = $"{TabDisplayName} • " +
                                  $"{(item.IsStoryClip ? $"clip {item.ClipIndex}/{item.ClipCount} • " : string.Empty)}" +
                                  $"take {chosen} • {dw}×{dh} • {item.AspectRatio} • " +
                                  $"{ClampLength(item.LengthSeconds):0.#}s • {fi.Length / 1024 / 1024.0:F1}MB";
                HasResult = true;
                OnCanExecuteChanged();
            });
            AddLog($"=== {row.Title} complete: {finalPath} (take {chosen}, seed {item.ChosenSeed}) ===");
        }
    }
}
