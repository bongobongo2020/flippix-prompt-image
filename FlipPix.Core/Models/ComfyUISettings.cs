using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FlipPix.Core.Models;

public class ComfyUISettings
{
    public string BaseUrl { get; set; } = "http://localhost:8188";
    public int ConnectionTimeout { get; set; } = 10000; // 10 seconds
    public int UploadTimeoutMilliseconds { get; set; } = 600000; // 10 minutes for large file uploads
    public int MaxRetries { get; set; } = 3;
    public int RetryDelayMilliseconds { get; set; } = 2000;
    public string ComfyUIFolderPath { get; set; } = string.Empty;
    public string OutputFolderPath { get; set; } = string.Empty;
    public string RemoteOutputFolderPath { get; set; } = string.Empty; // Network path to remote ComfyUI output folder
    public string RemoteLoraFolderPath { get; set; } = string.Empty; // Network path to remote ComfyUI LoRA folder
    public string KreaLoraFolderPath { get; set; } = string.Empty; // Network path to the Krea2 LoRA folder (e.g. loras\krea2)
    // Trigger word to prepend to the prompt for each Krea2 LoRA, keyed by the LoRA file name
    // (lower-cased, no extension). Seeded from the file name the first time a LoRA is picked;
    // a correction typed into the Krea2 LoRA row is remembered here for every later session.
    public Dictionary<string, string> KreaLoraTriggerWords { get; set; } = new();

    public string WslModelsFolderPath { get; set; } = string.Empty; // Windows models folder to expose to a WSL ComfyUI (e.g. E:\aimodels\comfyui\models)

    // Network/UNC path to a REMOTE ComfyUI's "models" folder. Lets the missing-model resolver
    // install (download/copy) weights for a remote server FlipPix can't reach via local disk.
    // Empty until the user points at it the first time a remote model is missing.
    public string RemoteModelsFolderPath { get; set; } = string.Empty;

    // Folders the user has pointed at when locating an already-downloaded model (e.g. another
    // ComfyUI's models dir, or a downloads folder). The missing-model resolver scans these first,
    // silently, on every future miss so the "where is it?" prompt only happens once. Most-recent first.
    public List<string> UserModelSourceFolders { get; set; } = new();

    // VRAM tier: controls whether the app loads full-size workflows or the memory-optimized
    // ones under workflow/16gb. "auto" decides from the GPU VRAM reported by ComfyUI's
    // /system_stats; "full" / "16gb" force a tier regardless of detection.
    public string VramTier { get; set; } = "auto"; // auto | full | 16gb
    // Last GPU VRAM (GB) detected from /system_stats. 0 = unknown / not yet detected.
    public double DetectedVramGb { get; set; } = 0;

    public List<SavedCameraPrompt> SavedCameraPrompts { get; set; } = new();

    /// <summary>
    /// What the 🌀 MiniMax I2V tab puts in the draft-idea box when &lt;Picture 1&gt; turns out to be a
    /// stereoscopic pair packed into one frame. <c>{LAYOUT}</c> is replaced with "side-by-side" or
    /// "over-under" to match what was detected.
    ///
    /// <para>Editable because the wording is source-specific — "wide-angle fisheye" is right for VR180
    /// footage and wrong for a stereo photograph, and the tab has no way to tell which it is looking at.
    /// Blank turns the auto-insert off.</para>
    /// </summary>
    public string MiniMaxI2VStereoPrompt { get; set; } = MiniMaxI2VStereoPromptDefault;

    /// <summary>The shipped wording for <see cref="MiniMaxI2VStereoPrompt"/>, and what Reset restores.</summary>
    public const string MiniMaxI2VStereoPromptDefault =
        "The video maintains the identity and appearance of <Subject 2> from <Picture 2>. "
        + "The visual style retains the wide-angle fisheye lens distortion and stereoscopic 3D "
        + "{LAYOUT} format of <Picture 1>, with <Subject 2> as the central figure.";
    public LMStudioSettings LMStudioSettings { get; set; } = new LMStudioSettings();

    /// <summary>
    /// Where ComfyUI's outputs land <i>as seen from this machine</i>, for a local or remote server.
    ///
    /// <para>There are two configured paths and only one of them is right for a given server, so every
    /// caller used to write <c>isRemote ? RemoteOutputFolderPath : OutputFolderPath</c> by hand. That
    /// picks a path even when it is stale or unreachable — a remote setup whose
    /// <see cref="RemoteOutputFolderPath"/> points at a dead network drive would keep scanning it and
    /// ignore the perfectly good <see cref="OutputFolderPath"/> the user had just corrected, because
    /// the main Settings window only ever edited the latter.</para>
    ///
    /// <para>So: prefer the path that matches the server, but fall back to the other one when the
    /// preferred path is unset or not reachable. When neither is reachable the preferred path is
    /// returned unchanged, so logs and error messages name the folder the user actually configured.</para>
    /// </summary>
    public string ResolveOutputFolder(bool isRemote)
    {
        var preferred = (isRemote ? RemoteOutputFolderPath : OutputFolderPath) ?? string.Empty;
        var fallback = (isRemote ? OutputFolderPath : RemoteOutputFolderPath) ?? string.Empty;

        if (IsReachableFolder(preferred)) return preferred;
        if (IsReachableFolder(fallback)) return fallback;

        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;
    }

    // Probing a disconnected network drive can block for seconds, and the callers below sit in
    // polling loops, so remember the answer briefly. A folder that appears (or disappears) is
    // picked up on the next expiry rather than instantly, which is fine for an output directory.
    private static readonly ConcurrentDictionary<string, (DateTime Probed, bool Exists)> _folderProbes =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan FolderProbeTtl = TimeSpan.FromSeconds(30);

    /// <summary>True when <paramref name="path"/> is set and currently resolves to a real directory.</summary>
    public static bool IsReachableFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        var now = DateTime.UtcNow;
        if (_folderProbes.TryGetValue(path, out var cached) && now - cached.Probed < FolderProbeTtl)
            return cached.Exists;

        bool exists;
        try { exists = Directory.Exists(path); }
        catch { exists = false; }   // unreachable share, denied, malformed path

        _folderProbes[path] = (now, exists);
        return exists;
    }

    /// <summary>
    /// Last-used browse folder per dialog context, keyed by an arbitrary caller key
    /// (e.g. "wanscail.image", "wanscail.video"). Lets each browse button reopen where it
    /// was last used, persisted across app restarts. The reserved key "__global__" holds the
    /// most-recent folder across all callers and is used as a fallback for keyless dialogs.
    /// </summary>
    public Dictionary<string, string> LastBrowseFolders { get; set; } = new();

    // ComfyUI crash detection and restart settings
    public bool AutoRestartComfyUI { get; set; } = true;
    public string ComfyUIRestartScriptPath { get; set; } = string.Empty;
    public int ComfyUIRestartDelaySeconds { get; set; } = 10; // Wait time before attempting restart
    public int ComfyUIStartupTimeoutSeconds { get; set; } = 300; // Max wait time for ComfyUI to start (5 minutes for large model loading)

    // Story Generator Q last used folder locations
    public string StoryGeneratorPromptJsonFolder { get; set; } = string.Empty;
    public string StoryGeneratorInputImageFolder { get; set; } = string.Empty;

    // Story Generator Q — Z mode LoRA settings (persisted across sessions)
    public bool StoryImageQZLoraEnabled { get; set; } = false;
    public string StoryImageQZSelectedLora { get; set; } = string.Empty;
    public double StoryImageQZLoraStrengthModel { get; set; } = 1.0;

    // Story Image Generator (Z) last used folder locations
    public string StoryImageGeneratorPromptJsonFolder { get; set; } = string.Empty;

    // Enhance Video last used folder location
    public string EnhanceVideoFolder { get; set; } = string.Empty;

    // Video Generator last used folder locations
    public string VideoGeneratorImageFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryPromptJsonFolder { get; set; } = string.Empty;
    public string VideoGeneratorStoryImagesFolder { get; set; } = string.Empty;

    // Story Video Generator last used folder locations
    public string StoryVideoPromptsFolder { get; set; } = string.Empty;

    // Video Generator workflow settings
    public string SelectedVideoWorkflow { get; set; } = "ltx2_i2v"; // Default to LTXV
    public int Ltx23FrameCount { get; set; } = 240;

    // Scail 2 tab: internal per-chunk window (frames) for the SCAIL2 hi-res loop (workflow node 45
    // "VIDEO BATCH SIZE"). Lower = less peak VRAM per pass (avoids OOM on long clips). Default 40 is
    // safe on a 24GB card; persisted so the choice survives restarts.
    public int Scail2VideoBatchSize { get; set; } = 40;

    // Scail 2 tab: output resolution for the final SCAIL II video. "auto" (the default) sizes the
    // generation canvas from the driving video's own aspect ratio at ≈960×544 worth of pixels, so the
    // character image is never stretched onto a canvas of a different shape. A concrete "WxH" (e.g.
    // "1280x720") forces that exact canvas instead, and "0x0" keeps the workflow's authored 640×960 one.
    // Persisted so the choice survives restarts.
    public string Scail2Resolution { get; set; } = "auto";

    // Scail 2 tab: keep the driving video's original background (SCAIL2 "replacement" mode, node 39) and
    // regenerate only the swapped character, instead of regenerating the whole frame ("animation" mode).
    // Keeping the original background composites the real scene every frame, so a static background (e.g.
    // a waterfall) cannot colour-drift or soften across the autoregressive chunks. Persisted per user.
    public bool Scail2KeepOriginalBackground { get; set; } = false;

    // Scail 2 tab: run the segmentation-control workflow's post pass — RIFE frame interpolation (2×) then
    // the RTX Video Super Resolution upscale (2×) — instead of saving the raw sampler output. Doubles both
    // the frame rate and the resolution at the cost of a longer run, and needs the RIFE + nvidia-vfx nodes
    // installed. Off by default so the tab works on a plain SCAIL install. Persisted per user.
    public bool Scail2Interpolate { get; set; } = false;

    // H3 Eros tab: which diffusion_models/h3-minimax checkpoint the seed hunt and the finish are sampled
    // with, as ComfyUI names it (e.g. "h3-minimax/minimax_h3_ref2va_pruned_int8_convrot.safetensors").
    // Empty means "whatever the workflow file ships", which is what an install that has never touched the
    // dropdown gets. Persisted per user so a comparison run survives a restart.
    public string H3ErosDiffusionModel { get; set; } = string.Empty;

    // H3 4-Step tab: the same dropdown, its own slot. Kept separate from H3ErosDiffusionModel because the
    // two tabs render different graphs — Eros's twelve-step hybrid and the 4-step turbo checkpoint are not
    // interchangeable, and sharing one field would have each tab silently reset the other's choice.
    public string H34StepDiffusionModel { get; set; } = string.Empty;

    // H3 VR tab: the same dropdown again, its own slot. The VR180 SBS LoRA sits on top of whichever
    // checkpoint is chosen here, so the choice that reads best in a headset is not necessarily the one
    // either of the other two tabs settled on.
    public string H3VrDiffusionModel { get; set; } = string.Empty;

    // Where already-built character sheets are kept, so loading a cast photo the app has seen before brings
    // its sheet back instead of paying Qwen-Image-Edit for it again. Empty means the default,
    // Pictures/flippix-images/faces-ai/cast/sheets — the folder they were already being filed into by hand.
    public string CastSheetLibraryFolder { get; set; } = string.Empty;

    // H3 Batch tab: the folder of story .txt files it walks, and its own diffusion-model slot. The folder
    // is remembered because a batch folder is a place you come back to, often with two more files in it.
    public string H3BatchFolder { get; set; } = string.Empty;
    public string H3BatchDiffusionModel { get; set; } = string.Empty;

    // H3 Batch tab: whether the batch renders every story through the H3 VR workflow (the VR180 SBS LoRA
    // on top of the same Eros hunt) rather than as ordinary flat films. Persisted because a folder of
    // stories is usually all one kind of film — once it is a VR folder it stays one.
    public bool H3BatchRenderAsVr { get; set; }

    // H3 Batch tab: whether the batch renders every story through the Singularity stack
    // (h3-singularity.json — the Singularity ref2va checkpoint, the comfy-kitchen attention backend,
    // chunked feed-forward, fp16 accumulation, euler/simple at 10 steps) instead of the Eros one.
    // Persisted for the same reason the VR flag is: a folder of stories is rendered as one set, and
    // half of it on a different checkpoint is a folder of films that do not match each other.
    public bool H3BatchUseSingularity { get; set; }

    // H3 Express tab: H3 Batch without the seed hunt. Its own folder and model slot, so the two tabs can be
    // pointed at different folders, and its own Singularity flag — which defaults ON here, because the
    // quickest stack is the one a tab named for speed should start on.
    public string H3ExpressFolder { get; set; } = string.Empty;
    public string H3ExpressDiffusionModel { get; set; } = string.Empty;
    public bool H3ExpressUseSingularity { get; set; } = true;

    // Seed Upscale tab: the folder its scan starts in. Empty means "the H3 4-Step output folder", which is
    // where the drafts it upscales are written.
    public string SeedUpscaleFolder { get; set; } = string.Empty;

    // Painter (WAN 2.2 LightX2V) workflow model names — adjust to match your ComfyUI server
    public string PainterHighNoiseModel { get; set; } = @"wan\wan2.2_i2v_high_noise_14B_Q8_0.gguf";
    public string PainterLowNoiseModel { get; set; } = @"wan\wan2.2_i2v_low_noise_14B_Q8_0.gguf";

    // Prompt2Json save directory for image analysis
    public string Prompt2JsonSaveDirectory { get; set; } = string.Empty;

    // Default prompts
    public string DefaultImagePrompt { get; set; } = string.Empty;
    public string DefaultVideoPrompt { get; set; } = string.Empty;
    public string DefaultNegativePrompt { get; set; } = string.Empty;

    // LM Studio helper properties for UI binding (parsed from LMStudioSettings.BaseUrl)
    public string LMStudioServer
    {
        get => LMStudioSettings.ParseBaseUrl(LMStudioSettings?.BaseUrl).Host;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            var (_, port) = LMStudioSettings.ParseBaseUrl(LMStudioSettings.BaseUrl);
            LMStudioSettings.BaseUrl = LMStudioSettings.BuildBaseUrl(value, port);
        }
    }

    public string LMStudioPort
    {
        get => LMStudioSettings.ParseBaseUrl(LMStudioSettings?.BaseUrl).Port;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            var (host, _) = LMStudioSettings.ParseBaseUrl(LMStudioSettings.BaseUrl);
            LMStudioSettings.BaseUrl = LMStudioSettings.BuildBaseUrl(host, value);
        }
    }

    /// <summary>Saved LM Studio server URLs (most-recent first), surfaced for UI binding.</summary>
    public List<string> LMStudioServerHistory => LMStudioSettings?.ServerHistory ?? new List<string>();

    /// <summary>Friendly name of the default LLM server, for UI binding.</summary>
    public string LMStudioServerName
    {
        get => LMStudioSettings?.DefaultProfile?.Name ?? string.Empty;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            LMStudioSettings.EnsureProfiles();
            var profile = LMStudioSettings.DefaultProfile;
            if (profile != null) profile.Name = (value ?? string.Empty).Trim();
        }
    }

    /// <summary>Friendly name of the default LLM model, for UI binding.</summary>
    public string LMStudioModelName
    {
        get => LMStudioSettings?.DefaultProfile?.ModelName ?? string.Empty;
        set
        {
            LMStudioSettings ??= new LMStudioSettings();
            LMStudioSettings.EnsureProfiles();
            var profile = LMStudioSettings.DefaultProfile;
            if (profile != null) profile.ModelName = (value ?? string.Empty).Trim();
        }
    }

    /// <summary>Where image analysis is currently sent, e.g. "Alien Box (http://alien:8080) · Qwen2.5-VL 7B".</summary>
    public string LlmTargetDescription => LMStudioSettings?.DescribeTarget() ?? string.Empty;

    // Selected model for binding to the ComboBox
    public string SelectedModel
    {
        get => LMStudioSettings?.SelectedModel ?? string.Empty;
        set
        {
            if (LMStudioSettings != null)
            {
                LMStudioSettings.SelectedModel = value;
            }
        }
    }
}

public class SavedCameraPrompt
{
    public string Name { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string Icon { get; set; } = "💾";
}