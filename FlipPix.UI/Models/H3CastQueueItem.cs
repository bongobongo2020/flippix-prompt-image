using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// A serializable snapshot of one H3 Cast job: the 1–2 <b>character sheets</b> that go to MiniMax H3 as
    /// reference frames, plus the finished H3 prompt, rendered through
    /// <c>workflow/video/h3-minimax/h3facerefiner.json</c>.
    ///
    /// <para>What is frozen here is the <i>sheet</i>, not the source photo: the Qwen-Image-Edit-2511 pass
    /// that builds a sheet runs before the job is queued, so a queued item never depends on the sheet builder
    /// running again — and the source paths are kept only so the queue row can show a face.</para>
    /// </summary>
    public class H3CastQueueItem : BaseQueueItem
    {
        /// <summary>Character 1's sheet. Kept for the queue thumbnail and the row label; what is uploaded is
        /// <see cref="Character1PanelPaths"/>. Required.</summary>
        public string Character1SheetPath { get; set; } = string.Empty;

        /// <summary>Character 2's sheet. Empty = single-character run.</summary>
        public string Character2SheetPath { get; set; } = string.Empty;

        /// <summary>
        /// Character 1's sheet cut into single-view panels, left to right — the images actually uploaded and
        /// wired to <c>ref_images.ref_image_0…</c>, so <c>&lt;Picture 1&gt;</c> onwards resolve to them.
        ///
        /// <para>Frozen at queue time rather than recomputed at submit time because the prompt's picture
        /// numbering was written from this count. A queue file written before panels existed deserializes to
        /// an empty list, which the tab reads as "split the sheet now" — the old items still run.</para>
        /// </summary>
        public List<string> Character1PanelPaths { get; set; } = new();

        /// <summary>Character 2's panels, continuing the numbering after character 1's.</summary>
        public List<string> Character2PanelPaths { get; set; } = new();

        /// <summary>The photo character 1's sheet was built from. Never uploaded — kept for the queue thumbnail.</summary>
        public string Character1SourcePath { get; set; } = string.Empty;

        /// <summary>The photo character 2's sheet was built from. Never uploaded.</summary>
        public string Character2SourcePath { get; set; } = string.Empty;

        /// <summary>The scene image the prompt was analyzed from. Never uploaded.</summary>
        public string SceneImagePath { get; set; } = string.Empty;

        /// <summary>The full H3 prompt, sheet reference line included.</summary>
        public string Prompt { get; set; } = string.Empty;

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";
        public double Megapixels { get; set; } = 1.0;
        public double LengthSeconds { get; set; } = 10;

        /// <summary>-1 = pick a fresh random seed when the item runs.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>When false the face-refine second pass is pruned out and the base H3 frames are saved as-is.</summary>
        public bool FaceRefine { get; set; } = true;

        /// <summary>Denoise of the face-refine pass — node 106. How far the cropped faces are allowed to move.</summary>
        public double RefineDenoise { get; set; } = 0.45;

        /// <summary>
        /// Opacity of the refined face at the stitch (<c>H3FaceStitch.blend</c>). 1.0 replaces the face
        /// outright with a VAE round-tripped copy; below that mixes the original pixels back, which is the
        /// only control that attenuates the round-trip loss the denoise cannot reach.
        /// </summary>
        public double RefineBlend { get; set; } = 1.0;

        /// <summary>
        /// Sizes the refine canvas from the largest crop rather than clamping it to 768
        /// (<c>H3FaceTrackCrop.canvas_mode</c>). Only bites on close-ups, where the capped mode would
        /// downscale the crop on the way in. Off by default: uncapped, and the cost is quadratic.
        /// </summary>
        public bool RefineNoDownscale { get; set; }

        /// <summary>
        /// Composite the refined face through a SAM mask (<c>H3FaceStitch.masks</c>) rather than the
        /// detected face box. On by default: the box is re-detected every frame, so pasting through it puts
        /// a moving rectangle over the face. Off, the mask nodes are pruned and the box comes back.
        /// </summary>
        public bool UseSamFaceMask { get; set; } = true;

        /// <summary>
        /// One refine pass per character rather than one for the whole cast.
        ///
        /// <para><c>H3FaceTrackCrop</c> holds a single subject, so the old single pass refined whoever was
        /// largest and left the other character's face exactly as the base pass rendered it — while being
        /// shown both cast members' photographs, which gave it nothing to say about which face it had. Each
        /// character now gets a pass tracked by their own face close-up and conditioned on their own panels.</para>
        ///
        /// <para><b>Default false</b>: an item queued before this existed carries a prompt written for the
        /// whole cast, and running that prompt against one character's panels would number pictures the pass
        /// never receives. Re-queue such an item to get the second pass.</para>
        /// </summary>
        public bool PerCharacterRefine { get; set; }

        /// <summary>"man" / "woman" for character 1, frozen at queue time — the refine passes rebuild their
        /// own reference line and it names the cast the same way the clip's own prompt did.</summary>
        public string Sex1 { get; set; } = string.Empty;

        /// <summary>"man" / "woman" for character 2.</summary>
        public string Sex2 { get; set; } = string.Empty;

        /// <summary>Whether the sheets were built wearing the locked wardrobe — flips the reference line
        /// between disowning the references' clothing and pointing at it.</summary>
        public bool SheetsShowWardrobe { get; set; }

        /// <summary>
        /// The draft → 2× latent upscale → 3-step finish scheme on the base pass. On by default. The cast's
        /// panels are unaffected either way: they are encoded at the finished canvas.
        /// </summary>
        public bool UseLatentUpscale { get; set; } = true;

        /// <summary>
        /// Block-sparse attention on both H3 passes. On by default; when false the two H3SLAAttention nodes
        /// are unwired and pruned, so a server without the pack still renders the job.
        /// </summary>
        public bool UseSla { get; set; } = true;

        /// <summary>The fraction of key blocks SLA skips. Below ~0.60 the kernel is slower than dense.</summary>
        public double SlaSparsity { get; set; } = 0.85;

        /// <summary>
        /// Sol-Attn (node 53), which this workflow shipped with always on. <b>Off by default</b>: SLA
        /// supersedes it for H3, and an item queued before this field existed deserializes to off, which is
        /// the right way round now that SLA carries the speedup.
        /// </summary>
        public bool UseSparseAttention { get; set; }

        /// <summary>
        /// When false — the default — the RTX ×2 super-resolution node is pruned and the frames are muxed at
        /// the H3 canvas. It is the single largest allocation in the graph (a whole ×2 frame stack, held at
        /// once, at the very end of a run), so it is opt-in rather than opt-out. The default also decides
        /// what a queue item written before this field existed deserializes to, which is the safe way round.
        /// </summary>
        public bool RtxUpscale { get; set; }

        /// <summary>
        /// The reference pipeline's encoding size on the H3 Duo render: 'max' — a 2048px short edge —
        /// rather than 'match', which on that graph scales every reference to the <i>draft</i> canvas, a
        /// quarter of the chosen megapixels. On by default there because holding a face is the point; it
        /// costs reference tokens on every step. The H3 Cast graph has no such switch — its panels are
        /// encoded at the finished canvas either way — so it ignores this.
        /// </summary>
        public bool MaxFidelityReferences { get; set; }

        /// <summary>
        /// The audio-enhancement pass over the saved clip, on the H3 Duo render's I2V graph. On by
        /// default. The H3 Cast graph has no such switch and ignores this.
        /// </summary>
        public bool UseAudioEnhancement { get; set; } = true;

        /// <summary>
        /// 🌹 H3 Eros only — the megapixels the three seed previews are sampled at. Small on purpose:
        /// the hunt exists to compare compositions cheaply, and <see cref="Megapixels"/> is what the
        /// picked one is finished at.
        /// </summary>
        public double PreviewMegapixels { get; set; } = 0.2;

        /// <summary>
        /// 🌹 H3 Eros only — the diffusion model both sweeps load, as ComfyUI names it under
        /// <c>diffusion_models</c> (e.g. <c>h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors</c>).
        /// Frozen onto the item at queue time so that changing the dropdown mid-run cannot finish a clip
        /// with a different model than the one its drafts were hunted with — the finish re-samples the
        /// picked branch, and a different model is a different latent. Empty means the workflow file's own.
        /// </summary>
        public string DiffusionModel { get; set; } = string.Empty;

        /// <summary>
        /// 🌹 H3 Eros only — how many fixed sigmas the upscale pass runs (3, 4 or 5). The graph ships
        /// one ManualSigmas schedule per count and the render links the chosen one.
        /// </summary>
        public int UpscaleSteps { get; set; } = 4;

        /// <summary>
        /// 🥽 H3 VR only — the strength the VR180 SBS LoRA is applied at, frozen here at Add to Queue.
        /// The finish re-samples the picked draft's branch, so hunting and finishing at different LoRA
        /// strengths would produce a clip that is not the take that was picked. 1.0 is the model card's
        /// own figure; 0 disables the LoRA and renders a flat clip in a wide frame.
        /// </summary>
        public double VrLoraStrength { get; set; } = 1.0;

        /// <summary>
        /// 🥽 H3 VR only — render this clip first-person, because its cast is one character. Frozen at Add
        /// to Queue beside <see cref="VrLoraStrength"/>, and for the same reason: it changes the prompt, and
        /// the finish re-samples the picked draft from the prompt. Meaningless when
        /// <see cref="HasCharacter2"/> is true; the tab never sets it then.
        /// </summary>
        public bool SoloPov { get; set; }

        /// <summary>
        /// 🌹 H3 Eros only — RIFE frame interpolation on the finished clip, 24 → 48 fps. On by default,
        /// which is how the authored graph runs.
        /// </summary>
        public bool UseRife { get; set; } = true;

        /// <summary>
        /// 🌹 H3 Eros only — which of the three seed previews was picked, 1-3, or 0 while the hunt has
        /// not run or is waiting on the user. Written back onto the item so a re-run of a completed
        /// story clip finishes the same sample rather than hunting again.
        /// </summary>
        public int ChosenSampleSlot { get; set; }

        /// <summary>
        /// 🌹 H3 Eros only — the base noise seed the hunt that produced <see cref="ChosenSampleSlot"/>
        /// ran on, or -1 when no hunt has run. The three previews start here: slot <i>n</i> is this seed
        /// plus <i>n-1</i>, unless a single slot has since been re-rolled on its own, which is why
        /// <see cref="HuntSampleSeeds"/> — not this — is what the finish pass reads.
        /// </summary>
        public long HuntBaseSeed { get; set; } = -1;

        /// <summary>
        /// 🌹 H3 Eros only — the draft each preview slot produced, in slot order, as a local file path.
        /// Empty string = that slot is unfilled (never hunted, deleted, or failed).
        ///
        /// <para>Persisted with the queue so the whole hunt survives a restart: the tab hunts every clip
        /// in the story before anything is picked, and a board of thirty-six drafts that vanished when
        /// the app closed would have to be paid for twice.</para>
        /// </summary>
        public List<string> HuntSamplePaths { get; set; } = new();

        /// <summary>
        /// 🌹 H3 Eros only — the noise seed each preview slot was sampled on, in slot order (-1 = unfilled).
        /// Kept per slot rather than derived from <see cref="HuntBaseSeed"/> because a single draft can be
        /// re-rolled on its own; the finish pass writes the chosen slot's seed back into the graph, so a
        /// wrong number here finishes a take nobody saw.
        /// </summary>
        public List<long> HuntSampleSeeds { get; set; } = new();

        /// <summary>
        /// 🌹 H3 Eros only — the noise seed of the picked draft, or -1 when nothing is picked. Written
        /// alongside <see cref="ChosenSampleSlot"/> so the finish never has to re-derive it.
        /// </summary>
        public long ChosenSeed { get; set; } = -1;

        /// <summary>
        /// 🌹 H3 Eros only — where this clip is in the tab's three-stage pipeline:
        /// <c>""</c> not hunted yet · <c>"hunted"</c> its drafts are on the board waiting to be picked ·
        /// <c>"finished"</c> the picked draft has been upscaled and the clip file exists.
        ///
        /// <para>Separate from <see cref="BaseQueueItem.Status"/> on purpose: a hunted clip is still a
        /// Pending queue item — there is GPU work left to do on it — and the base class's drain loop,
        /// its story-join check and its retry handling all read that.</para>
        /// </summary>
        public string ErosStage { get; set; } = string.Empty;

        /// <summary>
        /// 🌹 H3 Eros only — the exact <see cref="Prompt"/> the drafts on the board were hunted with.
        ///
        /// <para>The board lets the description be edited in place, and the finish pass <i>re-samples</i> the
        /// picked branch from the prompt rather than reading a cached latent — so a prompt edited after the
        /// hunt would finish a video nobody ever saw. Comparing this against the current prompt is how the
        /// tab knows a clip's takes have gone stale, and it is persisted so that survives a restart.</para>
        /// </summary>
        public string HuntPromptStamp { get; set; } = string.Empty;

        /// <summary>
        /// Groups the clips of one story so they render in order, sort together on disk and can be joined
        /// when the last one lands. Empty for a standalone clip.
        /// </summary>
        public string StoryId { get; set; } = string.Empty;

        /// <summary>1-based position of this clip within its story. 1 for a standalone clip.</summary>
        public int ClipIndex { get; set; } = 1;

        /// <summary>How many clips the story was split into. 1 for a standalone clip.</summary>
        public int ClipCount { get; set; } = 1;

        /// <summary>True when this item is one beat of a longer chain rather than the whole video.</summary>
        [JsonIgnore]
        public bool IsStoryClip => ClipCount > 1;

        public string? OutputVideoPath { get; set; }

        [JsonIgnore]
        public bool HasCharacter2 => !string.IsNullOrEmpty(Character2SheetPath);

        [JsonIgnore]
        public string DisplayText
        {
            get
            {
                var cast = HasCharacter2
                    ? $"{Name(Character1SourcePath, Character1SheetPath)} + {Name(Character2SourcePath, Character2SheetPath)}"
                    : Name(Character1SourcePath, Character1SheetPath);
                var refine = FaceRefine ? $" · face refine {RefineDenoise:0.00}" : " · no refine";
                var rtx = RtxUpscale ? " · RTX ×2" : string.Empty;
                var clip = IsStoryClip ? $" · clip {ClipIndex}/{ClipCount}" : string.Empty;
                return $"{cast} → {AspectRatio} · {LengthSeconds:0.#}s{clip}{refine}{rtx}";
            }
        }

        private static string Name(string source, string fallback)
        {
            var path = string.IsNullOrEmpty(source) ? fallback : source;
            return string.IsNullOrEmpty(path) ? "(none)" : Path.GetFileNameWithoutExtension(path);
        }

        private BitmapImage? _thumbnail;
        private bool _thumbnailTried;

        /// <summary>
        /// Small preview for the queue row — the scene image if there is one, otherwise character 1's sheet.
        /// Decoded on first bind rather than on deserialize, so a restored queue never pays for thumbnails
        /// the user has not looked at yet.
        /// </summary>
        [JsonIgnore]
        public BitmapImage? QueueThumbnail
        {
            get
            {
                if (_thumbnailTried) return _thumbnail;
                _thumbnailTried = true;

                var source = !string.IsNullOrEmpty(SceneImagePath) && File.Exists(SceneImagePath)
                    ? SceneImagePath
                    : Character1SheetPath;
                if (string.IsNullOrEmpty(source) || !File.Exists(source)) return null;

                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(source, UriKind.Absolute);
                    bitmap.DecodePixelHeight = 40;
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _thumbnail = bitmap;
                }
                catch { _thumbnail = null; }

                return _thumbnail;
            }
        }
    }
}
