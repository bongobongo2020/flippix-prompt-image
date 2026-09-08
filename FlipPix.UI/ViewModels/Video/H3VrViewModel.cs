using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.Core.Models;
using FlipPix.UI.Models;
using FlipPix.UI.Services;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 VR" tab — the 🌹🎯 H3 Eros story flow rendered through
    /// <b>rehan-fal/minimax-h3-vr180-sbs-lora</b>, so the reference photographs you already use for a
    /// cast come back as a <b>stereoscopic VR180 side-by-side</b> clip a headset plays in 3D.
    ///
    /// <para><b>What the LoRA actually does.</b> It does not add a depth pass or a reprojection step —
    /// unlike the 🥽 VR 180 tab, which takes a finished flat clip and builds stereo from an estimated
    /// depth map. This one makes the model <i>compose</i> the stereo pair directly: one wide frame whose
    /// left half is the left-eye view and whose right half is the right-eye view of the same scene, a
    /// few pixels of horizontal parallax apart. There is no post-processing anywhere in this tab. What
    /// ComfyUI writes is already the SBS pair.</para>
    ///
    /// <para><b>Everything the model card pins down is pinned here</b>, because a stereo pair is a
    /// composition the model has to be steered into and each of these is a way to lose it:
    /// the <c>vr180sbs</c> trigger and its stereo sentence lead every prompt
    /// (<see cref="VrPreamble"/>); the canvas defaults to <b>21:9 at 1.3 MP = 1792×768</b>, the
    /// LoRA's native 768p ultrawide; the finish is 24 fps and clips run to 15s, which is what it was
    /// trained and evaluated on. The card is explicit that 2K/4K upscales "were not evaluated for stereo
    /// consistency", so <see cref="MegapixelOptions"/> stops at native and says so above it.</para>
    ///
    /// <para><b>The one honest caveat about the hunt.</b> Eros drafts small and upscales the picked
    /// latent, and this LoRA was trained at 768p. A draft canvas well below that can form a weaker split
    /// than the finish will — so <see cref="PreviewMegapixels"/> defaults to 0.3 (864×352, each eye
    /// 432×352) rather than Eros's 0.15, and the board's summary line names the per-eye size so a draft
    /// that has not split is recognisable as such before it is picked.</para>
    ///
    /// <para><b>The file is named for the headset.</b> Every clip and the joined story end in
    /// <c>_LR_180.mp4</c> — the naming the model card's own samples use, and what DeoVR, Skybox and
    /// Pigasus read the layout from. No spatial-media metadata box is written; the players that matter
    /// take the filename.</para>
    ///
    /// <para>Everything before the render — the story, the beat sheet, the per-clip writer, the cast
    /// sheets and @char tags, the wardrobe lock, the queue, the hunt board, the latent-upscale finish and
    /// the FFmpeg join — is inherited from <see cref="H3ErosViewModel"/> untouched, on the same
    /// <c>h3-eros.json</c> graph. The LoRA is injected into that graph at submit time rather than baked
    /// into a copy of the file, so its strength is a dial and the two tabs cannot drift apart.</para>
    /// </summary>
    public class H3VrViewModel : H3ErosViewModel
    {
        /// <summary>
        /// The LoRA, as ComfyUI names it under <c>models/loras</c>. Downloaded from
        /// <c>huggingface.co/rehan-fal/minimax-h3-vr180-sbs-lora</c> (<c>h3-vr180-sbs-lora-v2</c>,
        /// rank 32, 104 target modules — the self-attention <c>qkv_proj</c> and <c>out_proj</c> of all
        /// 52 blocks, and nothing else). Those key names match every checkpoint in
        /// <c>diffusion_models/h3-minimax</c>, so it applies on top of whichever one the dropdown names.
        /// </summary>
        public const string VrLoraName = "H3/h3-vr180-sbs-lora-v2.safetensors";

        /// <summary>The node id this tab adds to the graph — not in <c>h3-eros.json</c>; see
        /// <see cref="ApplyCommonInputs"/>.</summary>
        private const string NodeVrLora = "h3vr_lora";

        /// <summary>Node 21, the rgthree Power Lora Loader the Eros graph ships with no LoRAs in it. Its
        /// MODEL output is what the four guiders and the scheduler read, so this is where a LoRA has to be
        /// spliced in to reach both sweeps.</summary>
        private const string NodePowerLora = "21";

        /// <summary>
        /// Whether this view model renders through the VR180 pipeline at all. Always true on the H3 VR tab
        /// itself; overridden to a checkbox on 🗂️ H3 Batch, which can run the same folder of stories flat
        /// (the ordinary Eros flow) or as VR. Every VR-specific member below — the prompt preamble, the
        /// LoRA splice, the stereo scene rule, the 21:9 canvas, the per-eye wording — sits behind this one
        /// flag, so "off" is not "the LoRA at strength 0" but the Eros path exactly, with nothing of this
        /// class left in the graph or the prompt.
        ///
        /// <para>Read from the base constructor, where a derived override is not yet trustworthy: field
        /// initialisers of the derived class have not run, so an override there sees its default value.
        /// That is deliberate here — H3 Batch applies the VR canvas defaults itself, from its persisted
        /// checkbox, after the base constructor has finished.</para>
        /// </summary>
        protected virtual bool VrPipelineActive => true;

        /// <summary>
        /// The trigger and the stereo sentence, verbatim from the model card, in the position it asks for:
        /// the very start of the prompt.
        ///
        /// <para>Written in code and prepended at submit time rather than stamped into the queue item's
        /// text, for the same reason the reference preamble and the wardrobe lock are: it is identical in
        /// every clip, it is what makes the output stereo at all, and a box that can be edited is a box
        /// in which it can be deleted. It therefore never reaches the board's description editor, and
        /// editing that box cannot stale a clip out of being VR.</para>
        /// </summary>
        public const string VrPreamble =
            "vr180sbs Stereoscopic VR180 video shown side by side: the left half is the left-eye view " +
            "and the right half is the right-eye view of the same 180-degree scene, nearly identical " +
            "with a slight horizontal offset. ";

        /// <summary>
        /// Added to <see cref="VrPreamble"/> when the cast is one character and <see cref="SoloPov"/> is on.
        ///
        /// <para><b>Why a solo cast needs saying twice.</b> Attaching one reference photograph does not tell
        /// H3 that there is one person in the video — it tells it whose face to use for the person it was
        /// already going to put there, and it will happily put a second, unreferenced body beside them.
        /// The wording below therefore does two jobs at once: it forbids anyone else in frame, and it gives
        /// the model somewhere to put the missing second party — the camera. A prohibition on its own tends
        /// to be answered with a solo shot of someone acting at nobody; naming the viewer as the person they
        /// are acting toward is what makes the same instruction produce a scene.</para>
        ///
        /// <para>First-person is also the shape the LoRA is best at: its card says the training data skews
        /// toward first-person content, which is the same bias that was putting a partner in frame.</para>
        /// </summary>
        public const string SoloPovPreamble =
            "This is a first-person point-of-view shot: the camera is the viewer's own eyes, held at head " +
            "height, and the viewer's own body is never seen. There is exactly ONE person in this video — " +
            "the person in the attached reference photographs. That person appears once in the left half of " +
            "the frame and once in the right half, because those two halves are the left-eye and right-eye " +
            "views of them; that is one person, not two. Nobody else appears at any point: no " +
            "partner, no second figure, no bystander, no crowd, no passer-by, no face in a mirror or a " +
            "reflection, no silhouette, and no one entering or leaving the frame. Every look, gesture, " +
            "movement and spoken line is directed at the camera, because the camera is where the viewer is. " +
            "Where the scene implies somebody with them, that somebody IS the viewer and is never drawn. ";

        /// <summary>The LoRA's native canvas: 21:9 at 1.3 MP resolves to 1792×768, i.e. 768p ultrawide,
        /// two 896×768 eyes. Protected because 🗂️ H3 Batch switches to it when its VR checkbox goes on.</summary>
        protected const double NativeMegapixels = 1.3;

        private double _vrLoraStrength = 1.0;
        private bool _soloPov = true;

        public H3VrViewModel(
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
            // The card's numbers, as defaults — on a tab that can only be VR. The aspect in particular is
            // not a preference: the LoRA packs two eyes into one frame, so a 16:9 or portrait canvas gives
            // each eye half of a shape it was never trained to fill, and the split stops forming. A derived
            // class with the pipeline off (H3 Batch unticked) skips this and keeps the Eros canvas.
            if (VrPipelineActive)
            {
                SelectedAspectRatio = "21:9 (Ultrawide)";
                Megapixels = NativeMegapixels;
                PreviewMegapixels = 0.3;

                AddLog("H3 VR initialized — the H3 Eros hunt with the VR180 SBS LoRA on top. Three drafts " +
                       "per clip at 21:9, you pick one, and the finish is a side-by-side stereo pair at " +
                       "1792×768. Files land as ..._LR_180.mp4; a headset player reads the layout from that.");
            }
        }

        // ── Identity ────────────────────────────────────────────────────────────────────────────────

        /// <summary>The same graph H3 Eros renders. The LoRA is spliced in at submit time, so there is no
        /// second copy of this file to keep in step with the first.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-eros.json";

        protected override string OutputSubfolder => "h3_vr";

        protected override string OutputFileStem => "H3VR";

        /// <summary>What DeoVR, Skybox and Pigasus read the stereo layout from, and the naming the model
        /// card's own sample clips use. It is a suffix rather than part of the stem because it has to be
        /// the last thing before the extension for those players to see it.</summary>
        protected override string OutputFileSuffix => "_LR_180";

        protected override string OutputFolderName => "H3VR";

        protected override string TabDisplayName => "H3 VR";

        protected override string ChainLibraryFolder => "h3vr";

        protected override string RunTokenPrefix => "h3vr";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3vr_queue.json");

        protected override string? RecallDiffusionModel(ComfyUISettings? settings) =>
            settings?.H3VrDiffusionModel;

        protected override void StoreDiffusionModel(ComfyUISettings settings, string name) =>
            settings.H3VrDiffusionModel = name;

        // ── The tab's own dial ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// How hard the VR180 LoRA is applied. The model card's own example runs it at 1.0 and that is the
        /// default; below about 0.6 the two halves stop being distinct views and the clip is a flat video
        /// in a wide frame, which is worse than useless in a headset.
        ///
        /// <para>It is a dial at all because the checkpoint underneath is <i>not</i> what the LoRA was
        /// trained on: it was trained on base <c>minimax-h3-fl2va</c>, and this graph samples one of the
        /// int8 convrot H3 checkpoints, most of which are themselves LoRA merges. The key names line up
        /// exactly — all 104 of them — but the weights they land on have moved, so the strength that
        /// reproduces the card's samples is not necessarily the strength that works best here.</para>
        ///
        /// <para>Frozen onto every queue item at Add to Queue, like the model and the step count: the
        /// finish re-samples the picked draft's branch, and a finish at a different LoRA strength is a
        /// different latent — which is to say, not the take that was picked.</para>
        /// </summary>
        public double VrLoraStrength
        {
            get => _vrLoraStrength;
            set
            {
                var v = Math.Clamp(Math.Round(value, 2), 0.0, 1.5);
                if (Math.Abs(_vrLoraStrength - v) < 0.0001) return;
                _vrLoraStrength = v;
                OnPropertyChanged();
                OnPropertyChanged(nameof(VrLoraSummary));
                OnPropertyChanged(nameof(HasVrLoraWarning));
            }
        }

        /// <summary>Turns the summary line red. Below 0.6 the clip is not stereo, which is a failed run
        /// rather than a softer effect — the same treatment <c>HasLoadWarning</c> gets, for the same
        /// reason: it is not a setting anyone would choose on purpose.</summary>
        public bool HasVrLoraWarning => VrLoraStrength < 0.6;

        // ── Solo casts are first-person ─────────────────────────────────────────────────────────────

        /// <summary>
        /// With one character loaded, shoot the video first-person and let nobody else into it.
        ///
        /// <para><b>The bug this fixes.</b> A solo cast was coming back with a second person in it. Nothing
        /// asked for one: the story writer, handed a scene with a single named person, wrote a partner for
        /// them to act against; the clip writer then wrote that partner into the prose; and H3, which has no
        /// reference photograph for them, invented a face — a different one in every clip. Three layers each
        /// assumed the layer below would say "one person means one person", and none of them did.</para>
        ///
        /// <para><b>It is fixed in all three, because each one alone is not enough.</b> A beat that already
        /// contains two people cannot be written back into one at clip level, and a prompt that says the
        /// right thing cannot rescue prose that already describes a partner. So
        /// <see cref="SoloCastDirective"/> goes to the beat sheet <i>and</i> the per-clip writer,
        /// <see cref="ValidateCastClip"/> rejects a clip that writes a second character anyway, and
        /// <see cref="SoloPovPreamble"/> states it once more in the render prompt itself, which is the only
        /// one of the three H3 actually reads.</para>
        ///
        /// <para>On by default, and it does nothing when a second character is loaded. Turn it off for a
        /// third-person solo clip — an observed subject rather than one addressing the viewer; the
        /// no-second-person rule goes with it, so this is also the switch for "let the story write whoever
        /// it wants".</para>
        /// </summary>
        public bool SoloPov
        {
            get => _soloPov;
            set
            {
                if (_soloPov == value) return;
                _soloPov = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SoloPovSummary));
            }
        }

        /// <summary>Whether the cast on screen is one character — the condition <see cref="SoloPov"/> acts
        /// under. Character 2 is loaded or it is not; there is no third state.</summary>
        public bool IsSoloCast => !HasCharacter2;

        /// <summary>True when this run really will be written and rendered first-person.</summary>
        public bool SoloPovActive => IsSoloCast && SoloPov;

        /// <summary>The line under the checkbox: what it is doing right now, not what it would do.</summary>
        public string SoloPovSummary =>
            !IsSoloCast
                ? "Two characters are loaded — this does nothing. Clear character 2 to shoot first-person."
                : SoloPov
                    ? "One character: the story, every clip and the render prompt all say so, and the camera " +
                      "is the person they are acting toward. Nobody else is drawn."
                    : "One character, third person. The story is free to write other people into the scene — " +
                      "and H3 has no reference photograph for them, so it will invent their faces.";

        /// <summary>
        /// What the story writer and the per-clip writer are told about a solo cast, on top of "there is one
        /// character". See <see cref="SoloPov"/> for why saying it here as well as in the render prompt is
        /// not belt-and-braces but the actual fix.
        /// </summary>
        protected override string SoloCastDirective => VrPipelineActive && SoloPovActive
            ? "\n\nPOINT OF VIEW — this video is shot first-person. The camera is the viewer's own eyes at " +
              "head height; it is never seen and it has no body. CHARACTER 1 is the ONLY person in the " +
              "video. Never write a second person of any kind — no partner, opponent, friend, stranger, " +
              "bystander, crowd or passer-by — and never write <Picture 2>: there is no second reference " +
              "photograph, so anyone you invent will be rendered with a face that changes from clip to " +
              "clip. Wherever the story implies somebody with CHARACTER 1, that somebody is the camera: " +
              "write CHARACTER 1 looking at, moving toward, reaching for, touching or speaking to the " +
              "camera instead of to another person. Camera moves are part of the action here — say where " +
              "the viewer's head is, what it turns to, and how close CHARACTER 1 comes to it."
            : string.Empty;

        /// <summary>
        /// The stock checks, plus the one this tab needs: a first-person clip that names a second character
        /// is rejected and asked for again from its beat.
        ///
        /// <para>Only the tag is checked, not the prose. A regex over "the man"/"someone" would reject far
        /// more good clips than bad ones, and the tag is the part that actually decides the render — a body
        /// carrying <c>&lt;Picture 2&gt;</c> is a body whose second person the submit path would try to cast.
        /// Prose that invents an untagged extra is caught by <see cref="SoloPovPreamble"/> at render time
        /// instead, which is the layer that can still do something about it.</para>
        /// </summary>
        protected override string? ValidateCastClip(string body)
        {
            var stock = base.ValidateCastClip(body);
            if (stock != null || !VrPipelineActive || !SoloPovActive) return stock;

            return body.Contains("<Picture 2>", StringComparison.Ordinal)
                ? "it named <Picture 2>, but this is a first-person clip with one character and there is no " +
                  "second reference photograph — whoever you wrote there would be rendered with an invented " +
                  "face. Write CHARACTER 1 acting toward the camera instead, and use <Picture 1> only."
                : null;
        }

        /// <summary>The line under the strength slider: what is loaded, and the one thing that goes wrong.</summary>
        public string VrLoraSummary =>
            VrLoraStrength <= 0.0
                ? "LoRA off — this renders an ordinary flat clip in a 21:9 frame, with no stereo in it at all."
                : VrLoraStrength < 0.6
                    ? $"h3-vr180-sbs-lora-v2 at {VrLoraStrength:0.00} ⚠ below ~0.6 the halves stop being " +
                      "two views and the result is a flat video in a wide frame."
                    : $"h3-vr180-sbs-lora-v2 at {VrLoraStrength:0.00} — the model card's own runs are at 1.00.";

        /// <summary>
        /// The VR finish list, held in a field because <see cref="MegapixelOptions"/> is now computed —
        /// the pipeline can be switched off underneath it (H3 Batch's checkbox), and off means the plain
        /// Eros list, not these.
        /// </summary>
        private static readonly MegapixelOption[] VrFinishOptions =
        {
            new MegapixelOption(0.5, "0.5 MP — fast (1120×480, 560px eyes)"),
            new MegapixelOption(0.8, "0.8 MP — balanced (1408×608, 704px eyes)"),
            new MegapixelOption(1.0, "1.0 MP — high (1568×672, 784px eyes)"),
            new MegapixelOption(NativeMegapixels, "1.3 MP — 768p native (1792×768, 896px eyes)"),
            new MegapixelOption(2.0, "2.0 MP — 2K ⚠ stereo not evaluated above native (2208×960)"),
        };

        /// <summary>
        /// The finished canvas, at 21:9 — the megapixel target handed to the 3D latent upscaler. Sizes are
        /// what <c>keep_proportion</c> + 32px alignment really produces at that aspect, and the per-eye
        /// figure is half the width, which is the number that decides whether the clip is worth a headset.
        ///
        /// <para>The list stops at native and labels why. The model card evaluated stereo consistency at
        /// 768p only and says outright that 2K/4K upscales were not checked — and the failure mode of an
        /// upscaler that does not know it is holding a stereo pair is that it resolves each half
        /// independently and the parallax stops agreeing between them.</para>
        ///
        /// <para>With the pipeline off, the base list — the plain Eros one — is served instead, so a flat
        /// batch shows flat sizes and is not offered "per eye" figures for frames that have none.</para>
        /// </summary>
        public override IReadOnlyList<MegapixelOption> MegapixelOptions =>
            VrPipelineActive ? VrFinishOptions : base.MegapixelOptions;

        private static readonly MegapixelOption[] VrPreviewOptions =
        {
            new MegapixelOption(0.15, "0.15 MP — quickest (608×256, 304px eyes)"),
            new MegapixelOption(0.2, "0.2 MP — quick (704×288, 352px eyes)"),
            new MegapixelOption(0.3, "0.3 MP — default, the split is readable (864×352, 432px eyes)"),
            new MegapixelOption(0.4, "0.4 MP — clearer (992×416, 496px eyes)"),
        };

        /// <summary>
        /// The draft canvas, at 21:9 and quoted per eye — because the question a draft has to answer here
        /// is not "is this composition the one" but "did the frame split into two views at all", and the
        /// eye width is what decides whether that is visible.
        ///
        /// <para>Eros's default of 0.15 MP is deliberately not this tab's. The LoRA was trained at 768p;
        /// at 304px an eye the halves are close enough to each other, and coarse enough in themselves,
        /// that a draft can read as flat when the finish will be stereo and vice versa. 0.3 is the cheapest
        /// canvas on which that call can actually be made.</para>
        ///
        /// <para>With the pipeline off, the base list is served instead — same reason as
        /// <see cref="MegapixelOptions"/>.</para>
        /// </summary>
        public override IReadOnlyList<MegapixelOption> PreviewMegapixelOptions =>
            VrPipelineActive ? VrPreviewOptions : base.PreviewMegapixelOptions;

        // ── The summaries, which have to talk about eyes ────────────────────────────────────────────

        /// <summary>Eros's line in per-frame terms. Here the number that matters is per <i>eye</i>: the
        /// frame is a pair, so half its width is the resolution anything is actually seen at. With the
        /// pipeline off it is Eros's own line again — per-frame, because the frame is one view again.</summary>
        public override string HuntSummary
        {
            get
            {
                if (!VrPipelineActive) return base.HuntSummary;
                var (dw, dh) = H3Canvas.Resolve(ResolvedAspectRatio, PreviewMegapixels, 32);
                var (fw, fh) = H3Canvas.Resolve(ResolvedAspectRatio, Megapixels, 32);
                var pick = AutoPickSample
                    ? $"take {AutoPickSlot} picked automatically"
                    : "you pick one per clip";
                return $"Hunt sweep: {SampleCount} drafts per clip at {dw}×{dh} " +
                       $"({dw / 2}×{dh} per eye), {FirstPassSteps} steps, the whole queue without " +
                       $"stopping — then {pick}, and only that latent is upscaled to {fw}×{fh} " +
                       $"({fw / 2}×{fh} per eye).";
            }
        }

        // ── The graph ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The stock Eros inputs, with the VR trigger in front of the prompt and the LoRA spliced into the
        /// model chain. Both are applied here, in the one method both sweeps call, precisely because hunt
        /// and finish have to agree: a finish that differs from its hunt by a prompt prefix or a LoRA is a
        /// different latent, and the clip that comes out is not the take that was picked.
        /// </summary>
        protected override void ApplyCommonInputs(
            JsonObject root, H3CastQueueItem item, IReadOnlyList<string> uploaded,
            string prompt, double lengthSeconds)
        {
            // Pipeline off: the Eros graph, the Eros prompt, untouched. Nothing below this line runs —
            // not the preamble, not the stereo rule, not the LoRA — so a flat batch clip is submitted
            // exactly as H3 Eros would submit it.
            if (!VrPipelineActive)
            {
                base.ApplyCommonInputs(root, item, uploaded, prompt, lengthSeconds);
                return;
            }

            // The item's own flag, not the checkbox's — it is the hunt that has to match the finish, and the
            // cast can have changed on screen since. A clip that was hunted first-person is finished
            // first-person.
            var solo = item.SoloPov && !CastPromptStamp.IncludesCharacter2(item.Prompt, item.HasCharacter2);
            var preamble = solo ? VrPreamble + SoloPovPreamble : VrPreamble;
            if (solo)
                AddLog("  Solo cast: first-person, and the prompt forbids a second person in frame.");

            // The cast preamble's scene rule forbids a split frame and forbids one person appearing twice
            // in it. On every other tab that is exactly right; here it is a flat contradiction of the LoRA,
            // and the reading that satisfies both is a different person in each half. Swap it for the
            // stereo one before anything else reads the prompt.
            var stereo = CastPromptStamp.MakeStereo(prompt);
            if (!ReferenceEquals(stereo, prompt) && stereo.Length != prompt.Length)
                AddLog("  Scene rule swapped for the stereo one: the frame is two eyes of one scene, not " +
                       "two people side by side.");
            else if (prompt.Contains(CastPromptStamp.StereoSceneRule, StringComparison.Ordinal))
                AddLog("  Scene rule is already the stereo one.");
            else
                AddLog("  WARNING: this prompt carries no cast scene rule, so the stereo one could not " +
                       "replace it — an unstamped or hand-edited prompt may render the two eyes as two " +
                       "different people.");

            base.ApplyCommonInputs(root, item, uploaded, preamble + stereo, lengthSeconds);

            var strength = item.VrLoraStrength;
            if (strength <= 0.0)
            {
                AddLog("  VR180 LoRA strength is 0 — sampling the bare checkpoint. Nothing about this " +
                       "clip will be stereo.");
                return;
            }

            RequireClass(root, NodePowerLora, "Power Lora Loader (rgthree)");

            // Order matters both ways round. Retarget first, while the new node does not exist yet — it
            // rewrites *every* reader of node 21's MODEL output, and a LoRA node added before the call
            // would have its own input rewritten to point at itself. Then wire the LoRA to node 21, which
            // is now read by nothing else.
            Retarget(root, NodePowerLora, 0, NodeVrLora);
            root[NodeVrLora] = new JsonObject
            {
                ["inputs"] = new JsonObject
                {
                    ["lora_name"] = VrLoraName,
                    ["strength_model"] = strength,
                    ["model"] = new JsonArray(NodePowerLora, 0)
                },
                ["class_type"] = "LoraLoaderModelOnly",
                ["_meta"] = new JsonObject { ["title"] = "VR180 SBS LoRA" }
            };

            AddLog($"  VR180 SBS LoRA at {strength:0.00} on top of " +
                   $"{(string.IsNullOrWhiteSpace(item.DiffusionModel) ? "the shipped checkpoint" : LabelFor(item.DiffusionModel))}, " +
                   $"prompt led by the vr180sbs trigger.");
        }

        // ── Queue ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>The stock Eros queue-add, plus this tab's own dial frozen onto every item — see
        /// <see cref="VrLoraStrength"/> for why the finish cannot be allowed to read it live. With the
        /// pipeline off, it is the stock Eros queue-add and nothing else.</summary>
        protected override void AddToQueue()
        {
            if (!VrPipelineActive)
            {
                base.AddToQueue();
                return;
            }

            var before = Queue.Count;
            base.AddToQueue();
            for (var i = before; i < Queue.Count; i++)
            {
                Queue[i].VrLoraStrength = VrLoraStrength;
                Queue[i].SoloPov = SoloPovActive;
                // Written into the item, not left to submit time, so the text on the board's description row
                // and the text that is rendered are the same text.
                Queue[i].Prompt = CastPromptStamp.MakeStereo(Queue[i].Prompt);
            }
            if (Queue.Count > before) SaveQueueToFile();
        }

        /// <summary>
        /// Migrates a clip queued before the stereo scene rule existed. Its prompt still carries the
        /// ordinary one, which on this tab is a contradiction of the LoRA — see
        /// <see cref="CastPromptStamp.StereoSceneRule"/>.
        ///
        /// <para>The rewrite makes an <b>already-hunted</b> clip stale, and that is the point: its drafts
        /// were sampled from the contradictory text, so they are the ones with two different people in
        /// them. A stale clip cannot be picked and is refused by the finish until it is re-rolled — which
        /// is exactly the right thing to be forced into here.</para>
        /// </summary>
        protected override void OnQueueItemLoaded(H3CastQueueItem item)
        {
            // A stereo migration has no business rewriting a queue that was built flat.
            if (!VrPipelineActive) return;

            var fixedPrompt = CastPromptStamp.MakeStereo(item.Prompt);
            if (fixedPrompt.Length == item.Prompt.Length) return;

            item.Prompt = fixedPrompt;

            // Said once per run, not once per clip: a restored twelve-clip story would otherwise bury the
            // rest of the load in it.
            if (_reportedMigration) return;
            _reportedMigration = true;
            AddLog("Queue migrated: clips written before the stereo scene rule have had it swapped in. " +
                   "Any of them already hunted is now stale — its drafts were sampled from the old text, " +
                   "which is the text that put a different person in each eye. 🎲 Re-roll those clips.");
        }

        private bool _reportedMigration;

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            OnPropertyChanged(nameof(VrLoraSummary));
            OnPropertyChanged(nameof(HasVrLoraWarning));
            // The cast can change without the queue changing, and every one of these reads it.
            OnPropertyChanged(nameof(IsSoloCast));
            OnPropertyChanged(nameof(SoloPovActive));
            OnPropertyChanged(nameof(SoloPovSummary));
        }
    }
}
