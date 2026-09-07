using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using FlipPix.UI.Models;
using FlipPix.UI.ViewModels.Video;

namespace FlipPix.UI.Services
{
    /// <summary>
    /// The one place that writes a <see cref="SeedRecipe"/> into a graph — shared by the 🌀4️⃣ H3 4-Step
    /// tab, which hunts the draft, and the 🔎⬆️ Seed Upscale tab, which reproduces its latent hours or
    /// days later.
    ///
    /// <para><b>Why this is a class and not two methods on two tabs.</b> Upscaling a picked seed is a
    /// re-sample, not a resume: the latent is gone the moment the draft is decoded, and what makes the
    /// second run land on the same tensor is that <i>every</i> input to the sampler is identical. Two
    /// copies of that wiring, in two tabs, drift — one gains a field, one rounds a number differently —
    /// and the failure is silent, because the upscale still produces a perfectly good clip. It is just
    /// not the clip that was chosen. One method, called by both, is the only version of this that stays
    /// true.</para>
    ///
    /// <para>Both graphs come from <c>tools/build_h3_4step.py</c> and share their node ids with
    /// <c>h3-eros.json</c>, so the ids below are the same ones <see cref="H3ErosViewModel"/> drives.</para>
    /// </summary>
    internal static class H3SeedGraph
    {
        // ── Node ids, shared by h3-4step.json and h3-seed-upscale.json ──────────────────────────────
        public const string NodePrompt = "22:11";      // PrimitiveStringMultiline
        public const string NodeSeconds = "22:23";     // PrimitiveFloat → the frame-count expression
        public const string NodeSteps = "22:8";        // INTConstant → BasicScheduler steps
        public const string NodeResolution = "22:9";   // ResolutionSelector — the *draft* canvas
        public const string NodeRef2V = "5";           // MiniMaxH3ReferenceToVideo
        public const string NodeUnet = "171:4";        // UNETLoader
        public const string NodeSla = "sla";           // H3SLAAttention — unwired when SLA is off
        public const string NodeShift = "shift";       // MiniMaxH3SigmaShift — last on the MODEL wire

        // ── h3-4step.json only: the three hunt branches ─────────────────────────────────────────────
        /// <summary>Draft slot (1-based) → the sampler that produced it, the sink that saved it, its noise.
        /// The same triples <see cref="H3ErosViewModel"/> drives, because the graphs share their ids.</summary>
        public static readonly (string Sampler, string Sink, string Noise)[] SampleBranches =
        {
            ("125:12", "18", "125:17"),
            ("133:129", "134", "133:128"),
            ("143:139", "144", "143:138"),
        };

        // ── h3-seed-upscale.json only: the finish chain ─────────────────────────────────────────────
        /// <summary>Where the reproduced seed goes. The upscale graph has one branch, whatever slot the
        /// draft came from on the board: a slot is a position, the seed is what reproduces.</summary>
        public const string NodeSeedNoise = "125:17";       // RandomNoise
        public const string NodeSeedSampler = "125:12";     // SamplerCustomAdvanced — reproduces the latent
        public const string NodeLatentSplit = "242";        // LTXVSeparateAVLatent
        public const string NodeUpscaler = "243";           // MinimaxH3LatentUpscaler3D — the finished canvas
        public const string NodeUpscaleSampler = "135:26";  // SamplerCustomAdvanced — the 2nd pass
        public const string NodeUpscaleNoise = "135:27";    // RandomNoise — the 2nd pass's own seed
        public const string NodeUpscaledVideo = "189";      // VAEDecode of the 2nd pass
        public const string NodeUpscaledAudio = "190";      // VAEDecodeAudio of the 2nd pass
        public const string NodeRife = "165";               // RIFEInterpolation — 24 → 48 fps
        public const string NodeFinalSave = "34";           // VHS_VideoCombine — the finished clip

        /// <summary>Frames per second the drafts are muxed at, and what RIFE doubles.</summary>
        public const int DraftFrameRate = 24;

        /// <summary><c>ManualSigmas</c> schedules for the second pass, by step count. Both files carry all
        /// three; the run links one.</summary>
        public static readonly Dictionary<int, string> SigmaSchedules = new()
        {
            [3] = "222",   // 0.9035, 0.6316, 0.3158, 0.0000
            [4] = "221",   // 0.9035, 0.8000, 0.6316, 0.3158, 0.0000
            [5] = "220",   // 0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000
        };

        // Added to the graph rather than found in it: the two INT sources that stand in for
        // ResolutionSelector's outputs when the chosen aspect is one its combo does not accept.
        public const string NodeCanvasWidth = "seed_canvas_w";
        public const string NodeCanvasHeight = "seed_canvas_h";

        /// <summary>Injected reference loaders are named from this, one per uploaded panel.</summary>
        private const string RefLoaderPrefix = "seed_ref_";

        /// <summary>MiniMaxH3ReferenceToVideo's autogrow slots.</summary>
        private const string RefImagePrefix = "ref_images.ref_image_";

        /// <summary>H3 packs audio at 80 rows/sec, so a 128-row block forces 1.6s of speech through one
        /// attention pattern and it comes back robotic. Every clip here has a soundtrack; 64 always.</summary>
        private const string SlaBlockSize = "64";

        /// <summary>
        /// Writes everything that decides the latent: the checkpoint, the references, the prompt, the
        /// length, the step count, the draft canvas and the attention patch. Not the seed — that belongs
        /// to a branch, and the two tabs wire different branches.
        /// </summary>
        /// <param name="uploaded">The reference filenames as ComfyUI knows them, in recipe order.</param>
        /// <returns>Whether the SLA patch was left in the graph, for the caller's log line.</returns>
        public static bool Apply(JsonObject root, SeedRecipe recipe, IReadOnlyList<string> uploaded)
        {
            RequireClass(root, NodeRef2V, "MiniMaxH3ReferenceToVideo");

            // ── The checkpoint ────────────────────────────────────────────────
            // Empty means "whatever the workflow file ships", which is what a recipe written before the
            // model dropdown existed says. Leaving the file's own value alone is the right reading.
            if (!string.IsNullOrWhiteSpace(recipe.DiffusionModel))
            {
                RequireClass(root, NodeUnet, "UNETLoader");
                SetInput(root, NodeUnet, "unet_name", recipe.DiffusionModel);
            }

            // ── References ────────────────────────────────────────────────────
            // The graph ships none: the author's were LoadImageCrop nodes pointing at files on their own
            // server. Every reference is injected here, in recipe order, because that order is what the
            // prompt's <Picture N> tags were resolved against.
            var loaders = new List<string>();
            for (var i = 0; i < uploaded.Count; i++)
            {
                var id = RefLoaderPrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                root[id] = new JsonObject
                {
                    ["inputs"] = new JsonObject { ["image"] = uploaded[i] },
                    ["class_type"] = "LoadImage",
                    ["_meta"] = new JsonObject { ["title"] = $"Picture {i + 1}" }
                };
                loaders.Add(id);
            }
            AttachReferences(root, NodeRef2V, loaders);
            SetInput(root, NodeRef2V, "ref_image_size", recipe.MaxFidelityReferences ? "max" : "match");

            // ── Prompt, length, steps ─────────────────────────────────────────
            SetInput(root, NodePrompt, "value", recipe.Prompt);
            SetInput(root, NodeSeconds, "value", recipe.LengthSeconds);
            SetInput(root, NodeSteps, "value", recipe.Steps);

            // ── The draft canvas ──────────────────────────────────────────────
            // The upscale re-samples at the canvas the draft was hunted at and lifts the latent from
            // there, so this is the draft's size in both tabs — never the finished one.
            if (H3Canvas.RequiresLiteralCanvas(recipe.AspectRatio))
            {
                var (cw, ch) = H3Canvas.Resolve(recipe.AspectRatio, recipe.DraftMegapixels, 32);
                root[NodeCanvasWidth] = IntNode(cw, "Draft width");
                root[NodeCanvasHeight] = IntNode(ch, "Draft height");
                Retarget(root, NodeResolution, 0, NodeCanvasWidth);
                Retarget(root, NodeResolution, 1, NodeCanvasHeight);
            }
            else
            {
                SetInput(root, NodeResolution, "aspect_ratio", recipe.AspectRatio);
                SetInput(root, NodeResolution, "megapixels", recipe.DraftMegapixels);
                SetInput(root, NodeResolution, "multiple", 32);
            }

            return WireAttention(root, recipe);
        }

        /// <summary>
        /// Settles the <c>H3SLAAttention</c> patch. Off is a <b>cut, not a flag</b>: its consumers are
        /// pointed back at the UNet, which leaves the node unreachable for the prune to delete. A merely
        /// disabled node is still a node the server must know how to load, and this pack has come and
        /// gone from 10.0.0.10 twice — unwiring is what lets a server without it render at all.
        /// </summary>
        private static bool WireAttention(JsonObject root, SeedRecipe recipe)
        {
            if (root[NodeSla] is not JsonObject node) return false;
            RequireClass(root, NodeSla, "H3SLAAttention");

            if (!recipe.UseSla)
            {
                Unwire(root, NodeSla, "model");
                return false;
            }

            var inputs = node["inputs"]!.AsObject();
            inputs["enabled"] = true;
            inputs["sparsity_ratio"] = recipe.SlaSparsity;
            inputs["block_size"] = SlaBlockSize;
            return true;
        }

        /// <summary>Hands every consumer whatever this node was reading, so the prune can take it.</summary>
        private static void Unwire(JsonObject root, string id, string passThroughInput)
        {
            if (root[id]?["inputs"]?[passThroughInput] is not JsonArray source || source.Count < 2) return;
            var fromNode = source[0]!.GetValue<string>();
            var fromSlot = source[1]!.GetValue<int>();

            foreach (var other in root.ToList())
            {
                if (other.Value?["inputs"] is not JsonObject otherInputs) continue;
                foreach (var input in otherInputs.ToList())
                    if (input.Value is JsonArray link && link.Count >= 2 &&
                        link[0]?.GetValue<string>() == id)
                        otherInputs[input.Key] = new JsonArray(fromNode, fromSlot);
            }
        }

        // ── Small graph helpers ─────────────────────────────────────────────────────────────────────

        public static JsonObject Parse(string json) =>
            JsonNode.Parse(json)?.AsObject()
            ?? throw new Exception("Workflow JSON could not be parsed.");

        public static void SetInput(JsonObject root, string nodeId, string input, object value)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                _ => JsonValue.Create(value.ToString())
            };
        }

        /// <summary>Points one node's input at another node's output.</summary>
        public static void Link(JsonObject root, string nodeId, string input, string sourceId, int slot)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            if (root[sourceId] == null)
                throw new Exception($"Workflow node '{sourceId}' is missing — the workflow file no longer matches this tab.");

            inputs[input] = new JsonArray(sourceId, slot);
        }

        public static void RequireClass(JsonObject root, string nodeId, string expected)
        {
            var actual = root[nodeId]?["class_type"]?.GetValue<string>();
            if (actual == null)
                throw new Exception($"Workflow node '{nodeId}' is missing — the workflow file no longer matches this tab.");
            if (actual != expected)
                throw new Exception($"Workflow node '{nodeId}' is a {actual}, expected {expected} — " +
                                    "the workflow file no longer matches this tab.");
        }

        private static JsonObject IntNode(int value, string title) => new()
        {
            ["inputs"] = new JsonObject { ["value"] = value },
            ["class_type"] = "PrimitiveInt",
            ["_meta"] = new JsonObject { ["title"] = title }
        };

        /// <summary>Repoints every link reading <paramref name="slot"/> of <paramref name="sourceId"/> at
        /// slot 0 of <paramref name="newId"/>.</summary>
        private static void Retarget(JsonObject root, string sourceId, int slot, string newId)
        {
            foreach (var node in root)
            {
                if (node.Value?["inputs"] is not JsonObject inputs) continue;

                foreach (var input in inputs.ToList())
                {
                    if (input.Value is not JsonArray link || link.Count != 2) continue;
                    if (link[0]?.ToString() != sourceId) continue;
                    if (link[1] is not JsonValue index || !index.TryGetValue<int>(out var i) || i != slot)
                        continue;

                    inputs[input.Key] = new JsonArray(newId, 0);
                }
            }
        }

        /// <summary>Rewrites the reference node's autogrow slots to exactly these loaders. Cleared rather
        /// than overwritten: a run with fewer panels than the file was authored for must not inherit a
        /// stale slot pointing at a node that is about to be pruned.</summary>
        private static void AttachReferences(JsonObject root, string nodeId, IReadOnlyList<string> loaders)
        {
            if (root[nodeId]?["inputs"] is not JsonObject inputs)
                throw new Exception($"Workflow node '{nodeId}' has no inputs — the workflow file no longer matches this tab.");

            foreach (var key in inputs.Select(kv => kv.Key)
                                      .Where(k => k.StartsWith(RefImagePrefix, StringComparison.Ordinal))
                                      .ToList())
                inputs.Remove(key);

            for (var i = 0; i < loaders.Count; i++)
                inputs[RefImagePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                    new JsonArray(loaders[i], 0);
        }
    }
}
