using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// The sidecar written beside every draft the 🌀4️⃣ H3 4-Step tab hunts — everything needed to
    /// <b>reproduce that draft's latent</b>, which is the one thing the mp4 itself does not carry.
    ///
    /// <para><b>Why a file and not a queue entry.</b> A latent upscale cannot start from a video: by the
    /// time a clip is an mp4 the latent it was decoded from is gone, and the only way back to it is to
    /// sample the same seed through the same stack again. Sampling is deterministic, so the same model,
    /// steps, sampler, scheduler, prompt, references and canvas give back the same tensor — but every one
    /// of those has to be recorded, exactly, at the moment the draft was made. That record is this file.
    /// It sits next to the draft as <c>&lt;name&gt;.seed.json</c> so the 🔎⬆️ Seed Upscale tab can find
    /// drafts by scanning a folder rather than by being told which run produced them.</para>
    ///
    /// <para><b>Every field here is part of the latent.</b> Changing one and re-sampling gives a different
    /// video — not a worse one, a <i>different</i> one — which is the whole failure this file exists to
    /// prevent. <see cref="TargetMegapixels"/>, <see cref="UpscaleSteps"/> and <see cref="UseRife"/> are
    /// the exceptions: they describe the second pass, which happens after the latent exists, and the Seed
    /// Upscale tab is free to override them.</para>
    /// </summary>
    public sealed class SeedRecipe
    {
        /// <summary>Bumped when a field that feeds the sampler is added or its meaning changes. A recipe
        /// from a newer build is refused rather than half-read, because a half-read recipe reproduces
        /// something that is not the draft.</summary>
        public const int CurrentVersion = 1;

        /// <summary>The suffix that makes a sidecar findable by a folder scan.</summary>
        public const string Extension = ".seed.json";

        public int Version { get; set; } = CurrentVersion;

        /// <summary>Which tab hunted this draft, for the Seed Upscale list's "from" column.</summary>
        public string SourceTab { get; set; } = string.Empty;

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

        /// <summary>The draft this recipe reproduces, as it was written. The scan prefers the file sitting
        /// beside the sidecar — a folder that has been moved or copied is still readable that way — and
        /// falls back to this path.</summary>
        public string DraftVideoPath { get; set; } = string.Empty;

        // ── The latent ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The branch's <c>RandomNoise.noise_seed</c>. With everything else below held, this is
        /// what separates one draft from its siblings.</summary>
        public long Seed { get; set; } = -1;

        /// <summary>Which of the hunt's three tiles this was. Cosmetic — the seed is what reproduces.</summary>
        public int Slot { get; set; }

        /// <summary>The prompt <b>as it was written into the graph</b>: picture tags already resolved
        /// against the references below. Storing the pre-detag text would re-resolve it against whatever
        /// the tab thinks the panel count is now, which is how a two-hander comes back with one face.</summary>
        public string Prompt { get; set; } = string.Empty;

        /// <summary>Local paths of the reference panels, in the order they were wired into
        /// <c>ref_images.ref_image_N</c>. The order is part of the conditioning: &lt;Picture 2&gt; means
        /// the second slot, not "the other character".</summary>
        public List<string> ReferenceImages { get; set; } = new();

        /// <summary><c>ref_image_size</c>: true is <c>max</c>, false is <c>match</c>.</summary>
        public bool MaxFidelityReferences { get; set; }

        public double LengthSeconds { get; set; }

        public string AspectRatio { get; set; } = "16:9 (Widescreen)";

        /// <summary>The megapixel target the drafts were hunted at — <em>not</em> the finished size.</summary>
        public double DraftMegapixels { get; set; }

        /// <summary>What that resolved to, for the list to show without recomputing. The graph is still
        /// driven from the aspect and the megapixels, so these two are display, not truth.</summary>
        public int DraftWidth { get; set; }

        public int DraftHeight { get; set; }

        public string DiffusionModel { get; set; } = string.Empty;

        /// <summary>Steps on the pass that made the draft. A different number is a different latent, so
        /// this travels with the recipe rather than being read off whatever the tab is set to now.</summary>
        public int Steps { get; set; }

        public bool UseSla { get; set; } = true;

        public double SlaSparsity { get; set; } = 0.90;

        // ── The second pass (the Seed Upscale tab may override all three) ───────────────────────────

        /// <summary>The finished canvas the source tab suggested. Only a default: the upscale happens
        /// after the latent exists, so changing it does not change which take you get.</summary>
        public double TargetMegapixels { get; set; } = 1.0;

        public int UpscaleSteps { get; set; } = 4;

        public bool UseRife { get; set; } = true;

        // ── Where it came from, for the list ────────────────────────────────────────────────────────

        public string Title { get; set; } = string.Empty;

        public string StoryId { get; set; } = string.Empty;

        public int ClipIndex { get; set; }

        public int ClipCount { get; set; }

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        /// <summary>The sidecar that belongs to a draft video.</summary>
        public static string SidecarPathFor(string videoPath) =>
            Path.Combine(Path.GetDirectoryName(videoPath) ?? string.Empty,
                         Path.GetFileNameWithoutExtension(videoPath) + Extension);

        public void Save(string sidecarPath)
        {
            var dir = Path.GetDirectoryName(sidecarPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(sidecarPath, JsonSerializer.Serialize(this, Json));
        }

        /// <summary>Reads a sidecar, or returns null with a reason. Never throws: the Seed Upscale tab
        /// walks whatever folder it is pointed at, and one unreadable file must not end the scan.</summary>
        public static SeedRecipe? TryLoad(string sidecarPath, out string problem)
        {
            problem = string.Empty;
            try
            {
                var recipe = JsonSerializer.Deserialize<SeedRecipe>(File.ReadAllText(sidecarPath));
                if (recipe == null)
                {
                    problem = "empty recipe";
                    return null;
                }
                if (recipe.Version > CurrentVersion)
                {
                    problem = $"written by a newer build (version {recipe.Version})";
                    return null;
                }
                if (recipe.Seed < 0)
                {
                    problem = "no seed recorded";
                    return null;
                }
                if (string.IsNullOrWhiteSpace(recipe.Prompt))
                {
                    problem = "no prompt recorded";
                    return null;
                }
                return recipe;
            }
            catch (Exception ex)
            {
                problem = ex.Message;
                return null;
            }
        }
    }
}
