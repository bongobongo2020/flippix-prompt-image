<div align="center">

<img src="flippix.png" alt="FlipPix" width="420">

**A desktop studio for AI image and video generation.**

FlipPix turns ~30 hand-tuned ComfyUI graphs into single-purpose tabs — each one with its own
vision-LLM prompt writer, its own job queue, and no graph editing required.

### [⬇ Download FlipPix v1.2.0](https://github.com/bongobongo2020/flippix/releases/latest)

[![latest release](https://img.shields.io/github/v/release/bongobongo2020/flippix?label=release&color=8b7bff)](https://github.com/bongobongo2020/flippix/releases/latest)
[![downloads](https://img.shields.io/github/downloads/bongobongo2020/flippix/total?color=34d3bd)](https://github.com/bongobongo2020/flippix/releases)
[![platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-93a3bd)](#requirements)
[![arch linux](https://img.shields.io/badge/Arch%20Linux-x86__64%20(port)-1793d1)](#install-on-arch-linux)

**Windows** — grab `FlipPix-Setup.zip`, extract, and double-click `Install-FlipPix.bat`; self-contained, no .NET runtime needed.

**Arch Linux** — one command:

```bash
curl -fsSL https://raw.githubusercontent.com/bongobongo2020/flippix/HEAD/install-arch.sh | bash
```

[Quick start](QUICKSTART.md) · [Arch install](#install-on-arch-linux) · [ComfyUI setup](COMFYUI_SETUP.md) · [Setup scripts](scripts/README.md) · [Architecture](architecture.md) · [Release notes](https://github.com/bongobongo2020/flippix/releases/tag/v1.2.0)

</div>

---

## Demo

<div align="center">

[<img src="docs/demo-poster.jpg" alt="Watch the FlipPix demo video" width="100%">](https://github.com/bongobongo2020/flippix/releases/download/v1.2.0/FlipPix-demo.mp4)

**▶ [Watch the 40-second demo](https://github.com/bongobongo2020/flippix/releases/download/v1.2.0/FlipPix-demo.mp4)** *(14 MB MP4)*

</div>

One job start to finish on the **🎭👥 MiniMax Character** tab — dropping in a character
reference and a scene image, Analyze writing the H3 prompt, the queue running it, and the
finished clip playing back in-app.

---

## What's in the app

<img src="docs/feature-map.svg" alt="FlipPix feature map: the Image Generator, Video Generator and Enhance windows and their tabs" width="100%">

FlipPix opens on the **Image Generator** window. A nav bar at the top jumps to the **Video
Generator** and **Enhance Video** windows; three pills (Create / Edit / Advanced) switch which
set of image tabs is visible.

---

## Feature catalogue

### 🎨 Image Generator window

| Group | Tab | What it does |
|---|---|---|
| Create | **🎨 Image Generator** | Text-to-image across six stacks — Z-Image (styled), Z-Image Base (plain, no style preset), Qwen 2512 (INT8 ConvRot + Lightning), Klien (Flux.2), Anima, and Krea2 Turbo. LoRA support per stack, including multi-slot Power-LoRA stacks for Krea2. Aspect presets (1:1, 3:4, 9:16, 4:3, 16:9) plus steps/CFG/seed. Can also run from an image analysis instead of a typed prompt. |
| Create | **📖 Story Image Q** | Batch a whole story into keyframes in one pass — including the FFLF Continuous Shot templates, which write ten stills five seconds apart — then hand the whole folder straight to the Video Generator's MiniMax FFLF tab, which lays it out as a keyframe chain and walks it as a series of joining takes. |
| Create | **📷 Amateur** | Amateur / phone-camera realism generation with its own LoRA selection. |
| Create | **🔤 Ideogram** | High-level prompt plus a canvas of draggable bounding boxes that define composition. Renders at the chosen base resolution then does a 2× latent upscale + refine pass. |
| Edit | **✏️ Editor** | Paint a mask over any image with an adjustable brush and re-render just that region. |
| Edit | **🧑‍🤝‍🧑 Qwen Edit** | Character swap: two character references dropped into a base scene. Analyze sends all three images to the LLM, which writes one Qwen-Image-Edit-2511 instruction that swaps the people and leaves the scene alone. |
| Edit | **♻️ Restore** | Flux.2 Klein restoration pass — upscale to a megapixel budget, re-render with a guidance prompt, then realign (pixel-drift fix) and blend back over the original. |
| Edit | **🔍 Image Upscaler** | SeedVR2 tiled super-resolution. The frame is cut into overlapping tiles, each is upscaled by the diffusion transformer, and the tiles are blended back — so peak VRAM tracks the tile size, not the output size. Upscale one uploaded image, or point at a folder and walk every image in it (optionally recursing, mirroring the tree into the output folder, and skipping anything already done so an interrupted batch resumes). |
| Advanced | **🎥 Camera Angle** | Re-shoot a photo from a new angle — low, high, bird's-eye, rotation — while preserving identity, clothing and pose. |
| Advanced | **🎛️ Control** | Two modes: *Klein Flux.2 ControlNet* (subject reference + a pose image or video, with QwenVL writing the prompt) and *Krea2 two-reference edit* (scene image A + subject image B + an edit instruction). |

### 🎬 Video Generator window

| Tab | What it does |
|---|---|
| **📖 Story Video Generator** | The batch workhorse: a folder of stills and a prompt JSON become a queue of clips. Five selectable stacks — 🟢 Vantage Sulphur 2, 🟠 10Eros InstantAction, 🔵 LTX-22-B, Dasiwa Wan 2.2 (autoregressive chain where each clip's last frame seeds the next), and Wan 2.2 I2V. |
| **🎤 Infinite Talk** | Audio-driven talking video on Wan 2.1 InfiniteTalk, processed in 81-frame chunks so long audio works. |
| **🎭 Scail 2** | Unified character-swap → motion-transfer flow: a Klein pass swaps the character, then SCAIL II transfers motion from a reference video. Includes trim in/out scrubbing and a "keep original background" option to fight autoregressive drift. |
| **🥽 VR 180** | Flat clip → side-by-side 3D VR180, by outpainting each frame with the LTX-2.3 equirect IC-LoRA, deriving depth, and doing DIBR/SBS in FFmpeg. Adjustable stereo strength. |
| **🔊 Video Sound** | Upload a clip, analyze its first frame into a `[VISUAL]` / `[SPEECH]` / `[SOUNDS]` directing script, then regenerate it through the LTX-2.3 audio-video workflow with synced speech and effects. Optional voice reference. |
| **🎭 10Eros ConvRot** | Seed hunting for faces: one face reference + a prompt yields 4 cheap LTX-2.3 FaceID previews (reroll for more), then the chosen seed(s) re-render at full resolution. |
| **🪪 FaceID Char Sheet** | One-shot LTX-2.3 FaceID + Union-Control video from a character image (with Analyze), an audio track, and a reference video supplying pose/depth/edge control. |
| **🌀 MiniMax H3** | Image-to-video with synchronized audio: the uploaded image is the first frame, Analyze turns it into a full four-block H3 prompt, one video comes out. |
| **🌀🎯 MiniMax FFLF** | MiniMax H3 in FL2VA mode, driven as a keyframe chain: an opening frame plus up to four stills the take has to pass through. Each clip runs from one keyframe to the next — the pictures *are* its first and last frames — and clips after the first continue out of the tail of the one before them inside the same submission, blended over an overlap window. Analyze walks the chain pair by pair and writes one FL2VA prompt per clip, each given the previous clip's prompt so the whole thing reads as one take. Jobs are queued, and the graph is pruned to whichever sink the take needs. Point it at a folder of stills and it walks the whole thing as a series of takes — each opening on the keyframe the one before it closed on — then FFmpeg-joins them into a single video once the last take lands. |
| **🌀📝 MiniMax H3 T2V** | Long-form: one image is analyzed into a dense ~15-second, 9–14 shot H3 prompt. Toggle whether the image is conditioned as the first frame or used only as inspiration for a true text-to-video run. |
| **🎭👥 MiniMax Character** | Reference-to-video: one or two character images stay on-model as H3 reference frames while a third *scene* image (never uploaded — only the LLM sees it) is analyzed into the prompt they act out. Story mode writes a run of clips for a 5–120 s target and queues one job per clip. Scenes are saved to a persistent scene library. |
| **🪪👥 H3 Cast** | The same reference-to-video idea, run through *character sheets*. Each character arrives as one ordinary photo and Qwen-Image-Edit-2511 (int8 convrot) turns it into a three-panel sheet — full-body front, full-body back, face close-up — which is what H3 receives as `<Picture n>`. The prompt is written from a scene image, from a story you type, paste or load from a `.txt` — either is enough on its own — and the video runs through the face-refiner graph: a second H3 pass re-generates the tracked face crops against those sheets and stitches them back in. Ask for 5–120 s and the story is written as a chain of ≤15 s clips, queued one job per clip against the same sheets and joined when the last one lands. |
| **🪪🌀 H3 Duo** | Everything H3 Cast does — story → locked wardrobe → two characters' sheets (✨ generated photos optional) → chained clip prompts → queue → FFmpeg join — rendered through the 🌀 MiniMax I2V turbo pipeline instead: each clip is a 4-step draft at a quarter of the canvas, a 2× pass through the MiniMax H3 3D latent upscaler, then 3 fixed-sigma finish steps at full size. Quicker and sharper than the Cast tab's own graph; there is no face-refine pass here, so identity is held by encoding the sheet panels at a 2048px short edge by default (max-fidelity references). Own queue, own output folder (`H3Duo`). |
| **🪪👥⚡ H3 Cast Hybrid** | The H3 Cast pipeline on MiniMax H3's *hybrid* `fl2va+ref2va` checkpoint, which completes supplied keyframes **and** generates from the character sheets in one pass. Add stills to a timeline, each pinned to a second: at `0.00` the frame *is* your picture, at `3.00` it hard-cuts to the next — pose, wardrobe and background together — while the cast sheets ride along as identity references that must never become frames. The prompt is the six-section hybrid form from `prompts/h3-hybrid-prompting-guide.md`, and four of those six sections (`subject_definitions`, `retention_analysis`, the alignment paragraph, the global negatives) are written in code from the reference list itself, so they cannot drift across a chain — the llama-server writes only the summary, the shots, the soundscape and the score. 8-step turbo LoRA, and an optional finish of the same face-refine pass H3 Cast runs — **one pass per character**, each tracked by that character's own face close-up (`identity_reference`), conditioned on their own panels alone and prompted with their own keyframe-free copy of the clip, the second stitching over the first — plus FILM ×2 interpolation and RTX ×2. Because likeness collapses when a face is only a handful of pixels, the shot rules cap how wide the camera may go, and each character is sent the panels that carry their face (front + face by default) rather than the whole sheet. |
| **🎬🎭 H3 Ensemble** | The Hybrid pipeline widened from a two-hander to a cast of up to **five** — people, creatures, groups — plus a photograph of the location that is both what Analyze reads the setting off *and* a reference wired into the generator. The nine reference slots of `MiniMaxH3ReferenceToVideo` are divided between whoever each clip actually names (Auto budget), so a five-character story renders as a chain of two- and three-handers; the wardrobe is derived once from the story and locked into every clip, the sheets are cut back into panels, a 🎬 storyboard pass renders each clip's opening frame before the clips are committed, and the finished chain is FFmpeg-joined. |
| **🪪🎬 H3 Multi** | Everything H3 Ensemble does — five cast slots, wardrobe lock, the location as prompt input *and* reference, storyboard, chained clips, FFmpeg join — rendered through the 🌀 MiniMax I2V turbo pipeline instead of the hybrid graph: each clip is a 4-step draft at a quarter of the canvas, a 2× pass through the MiniMax H3 3D latent upscaler, then 3 fixed-sigma finish steps at full size. Quicker and sharper than the Ensemble tab's own graph; there is no face-refine pass and no FILM interpolation here, so identity is held by encoding the reference panels at a 2048px short edge by default (max-fidelity references). Own graph (`h3-multi.json`), own queue, own output folder (`H3Multi`). |

### ✨ Enhance Video window

| Tab | What it does |
|---|---|
| **🎞 Interpolate** | GIMM frame interpolation for smooth slow motion, queued like everything else. |
| **🔍 Upscale** | RTX Super Resolution or SeedVR2 7B INT8, with a selectable pre-resize multiplier that decides the output resolution. |
| **🎯 Targeted Wan Enhance** | Rebuild only the things you name inside a clip. SAM3 finds them from a text prompt ("woman", "face, hands") and tracks them across every frame; three WanVideo 2.2 T2V low-noise passes then sample through that mask at falling denoise and rising resolution — a draft that does the work, then two refinements — and each result is composited back into the untouched source through a feathered edge, so the background stays bit-for-bit the original and the source audio is remuxed onto the result. The resolution ladder is fitted to the source aspect, so a landscape clip is not squashed into the authored portrait canvas. Pass-one denoise is the dial that matters: ~0.35 keeps a face recognisable, ~0.5 cleans up real errors, ~0.75 makes someone new. |
| **🌀 Enhance HD** | Re-render a finished MiniMax H3 clip at HD/2K. Not an upscaler: the clip is resized to a base canvas, encoded to an H3 latent, lifted by `MinimaxH3LatentUpscaler3D` to the detail megapixels and re-sampled in one partial-denoise pass by the same REF2VA model that made it — conditioned by the same reference stills and prompt, so the model re-draws the frames at the larger size rather than interpolating pixels. Both canvases are computed from the source aspect, so nothing is stretched. Denoise is the dial: 0.45 is the authored value, past ~0.6 the pass invents motion instead of sharpening it, below ~0.3 it barely bites. H3 samples video and audio as one latent, so the clip has to carry the soundtrack it was generated with — a silent clip is rejected up front rather than dying in the loader. H3 has no context window here either: the whole clip is one sequence and the ceiling is a hard OOM, so a clip longer than one pass holds is cut into evenly spread segments of a length H3 accepts (5+17n), rendered in turn under one seed and rejoined with the overlaps trimmed. Measured on a 24 GB card: 124 frames at 2.1 MP in 271 s, so a 36-second clip is eight passes. The form shows the split before you queue it. |

---

## How a job flows

<img src="docs/pipeline.svg" alt="FlipPix pipeline: input, analyze, patch, queue, execute, collect" width="100%">

Every tab runs the same path — only the workflow JSON and the system prompt change:

1. **Input** — an image, a first/last frame pair, a video, audio, or a reference cast.
2. **Analyze** — a vision LLM (LM Studio, Ollama, or a llama-server) reads the input using that
   tab's system prompt from `prompts/prompt2json/`, and writes the generation prompt. You can
   always edit the result before generating.
3. **Patch** — the workflow JSON is loaded and the prompt, seed, resolution, duration, LoRAs and
   uploaded references are injected into the right node inputs. Unused branches are pruned.
4. **Queue** — `WorkflowQueueCoordinator` serialises execution so only one graph runs at a time.
   Per-tab queues pause, resume, cancel, and are persisted across restarts.
5. **Execute** — ComfyUI runs the graph; progress streams back over WebSocket. Validation
   errors (`node_errors`) and execution errors surface as real failures instead of silent hangs.
6. **Collect** — outputs are pulled from `/history` (images, videos, gifs and audio), previewed
   in-app, and optionally joined with FFmpeg.

Along the way, three safety nets are always on:

- **Missing models** — if a checkpoint/LoRA/VAE the graph needs isn't installed, FlipPix offers to
  download it from the model catalog, locate an existing copy on disk (and remember it), or register it.
- **Missing nodes** — an unknown `class_type` is resolved to its custom-node pack via the node
  catalog and ComfyUI-Manager mappings, then git-cloned, pip-installed, and the server rebooted.
- **VRAM tier** — on ~16 GB GPUs FlipPix loads the memory-optimised graphs from `workflow/16gb`
  instead of crashing. Auto-detected from the connected server, or forced in Settings.

---

## Requirements

| | |
|---|---|
| OS | Windows 10/11 x64, or Arch Linux x86_64 (Avalonia port — see [gaps](#install-on-arch-linux)) |
| Runtime | .NET 8 — bundled in the Windows build; `dotnet-runtime-8.0` from pacman on Arch |
| RAM | 16 GB minimum, 32 GB recommended for video |
| GPU | NVIDIA, 12 GB VRAM minimum; 16 GB triggers the memory-optimised tier; 24 GB+ recommended for the video tabs |
| Storage | ~60 GB for models and dependencies (much more if you install every stack) |
| Server | A reachable ComfyUI — local or remote — default `http://127.0.0.1:8188` |
| Optional | LM Studio / Ollama / llama-server for the Analyze buttons; FFmpeg for joining and enhancement |

---

## Install

**New here? Start with [QUICKSTART.md](QUICKSTART.md).**

### Windows

1. Download **[`FlipPix-Setup.zip` from the latest release](https://github.com/bongobongo2020/flippix/releases/latest)** and extract it.
2. Double-click **`Install-FlipPix.bat`** — a small wizard that installs the app and can also set
   up ComfyUI for you.
3. Or skip the wizard and run `publish\FlipPix.UI.exe` straight from the extracted folder.

The build is self-contained, so no .NET runtime is required. Upgrading? Install over the top —
your settings, prompts and queues in `%APPDATA%\FlipPix` are kept.

On first launch FlipPix asks whether your ComfyUI is **local** or **remote** and walks you
through pointing at it. Everything else (server URL, output folder, LoRA folders, LLM server
profiles, auto-restart) lives in **⚙️ Settings**.

### Install on Arch Linux

The Linux build is `FlipPix.UI.Linux`, an **Avalonia port** of the WPF app. It talks to the same
ComfyUI server, reads the same `workflow/` and `prompts/` trees, and stores its settings in the
same JSON shape — but a handful of things do not carry across; read [Linux gaps](#linux-gaps)
before you rely on it.

One command does everything — dependencies, build, package, desktop entry:

```bash
curl -fsSL https://raw.githubusercontent.com/bongobongo2020/flippix/HEAD/install-arch.sh | bash
```

Or from a checkout:

```bash
git clone https://github.com/bongobongo2020/flippix.git
cd flippix
./install-arch.sh
```

It installs the runtime and X11/Skia libraries with `pacman`, builds the app with `makepkg`, and
installs `flippix` as a normal pacman package — so `pacman -Rns flippix` removes it and the .NET
runtime keeps getting security updates through pacman. Launch it with `flippix`, or from your
application menu.

| Flag | What it does |
|---|---|
| *(none)* | Build and install the `flippix` pacman package (recommended) |
| `--local` | Install into `~/.local` instead — no root, no package, nothing touched system-wide |
| `--self-contained` | With `--local`, bundle the .NET runtime so `dotnet-runtime-8.0` isn't needed |
| `-y`, `--yes` | Pass `--noconfirm` to pacman and makepkg (unattended) |
| `--skip-deps` | Assume the dependencies are already installed |
| `--uninstall` | Remove whichever of the two installs is present |

The first build compiles the Avalonia app and downloads NuGet packages, so give it a few minutes.
The installed package is framework-dependent and about 57 MB.

**Build without installing** — to test a build, or produce a tarball for a machine with no SDK:

```bash
./packaging/build-linux.sh              # framework-dependent → publish-linux/
./packaging/build-linux.sh --tarball    # also writes flippix-linux-x64.tar.gz
./launch-linux.sh                       # run it in place
```

#### Where files go on Linux

FlipPix follows the XDG Base Directory spec:

| Kind | Location |
|---|---|
| Settings, prompt history, scene library | `~/.config/FlipPix` |
| Persisted queues | `~/.config/FlipPix/queue` |
| Logs | `~/.local/state/FlipPix/logs` |
| Generated stills · clips | `~/Pictures/flippix-images` · `~/Videos/flippix-vids` |
| Installed app (package) | `/usr/lib/flippix`, launcher `/usr/bin/flippix` |

`FLIPPIX_FFMPEG` / `FLIPPIX_FFPROBE` override ffmpeg discovery; `FLIPPIX_SOFTWARE_RENDER=1` skips
GLX/EGL if the window comes up black. Avalonia 11.2 has no Wayland backend, so a Wayland session
runs FlipPix through XWayland — transparent apart from fractional scaling, which the X11 backend
reads from `GDK_SCALE` / `QT_SCALE_FACTOR`.

#### Linux gaps

The port tracks the Windows app tab-for-tab (same nine video tabs, same ten pill-grouped
image tabs, same settings sections) and has caught up on the functional side — but a few
things still differ:

- **No video plays inside the app.** Avalonia has no MediaElement, so clip previews are ffmpeg
  poster frames; the play button hands the file to your desktop's player, and scrub sliders move
  the poster rather than a playhead.
- **The mask painter is FlipPix's own** — no InkCanvas, so the Editor tab rasterizes stroke
  points at source resolution. No pressure or stylus tips.
- **The ComfyUI Backup & Restore settings panel is Windows-only** — it drives the Windows
  installer bundle.
- Remote ComfyUI servers can't be *auto*-repaired by the missing-node installer (it clones into
  the local `custom_nodes`); it guides you to ComfyUI-Manager instead, exactly like the WPF app.
- The same four ViewModels that are built and never bound on Windows (`Vr180`, `VideoSound`,
  `FaceIdCharSheet`, `MiniMaxH3TextToVideo`) have no tab here either.

Full detail, plus a hardware verification checklist and notes on porting further tabs, is in
**[packaging/README.md](packaging/README.md)**.

#### Other distributions

There is no package for anything but Arch, but nothing in the app is Arch-specific. On any x86_64
distro with .NET 8 and the X11/fontconfig libraries, `./packaging/build-linux.sh --self-contained`
produces a tree that runs from `./launch-linux.sh`.

### Getting a ComfyUI that works

FlipPix needs a ComfyUI with a specific set of custom nodes. Two ways to get one:

#### Option A — Clone a ready-made ComfyUI ⭐ (lowest friction)

Restore a snapshot of a known-good install. The snapshot bundles the entire Python environment
*and* every custom node, so restoring is extract + run — no pip, no missing-node hunts.

1. **(Maintainer, once)** Double-click **`Backup-ComfyUI.bat`** on the live ComfyUI box → produces a
   `.tar.gz` (~15 GB) + `.sha256` in `%USERPROFILE%\FlipPix-ComfyUI-Backup`. Upload both to a
   Hugging Face model repo (the script prints the `hf upload` commands).
2. **(Each user)** On a Linux GPU box or WSL, one command downloads, verifies and restores it:
   ```bash
   bash restore-comfyui.sh --hf <user>/flippix-comfyui
   cd ~/flippix-comfyui/ && ./run_nvidia_gpu.sh    # ComfyUI on 0.0.0.0:8188
   ```
   Needs an NVIDIA GPU + recent driver (in WSL, a current Windows NVIDIA driver exposes it).

Models aren't bundled — they can be hundreds of GB. After restoring, point `ComfyUI/models` at
your weights or use the model manifest (`scripts/flippix-models.txt`). Details:
**[scripts/README.md → Backup / Restore](scripts/README.md#backup--restore-a-working-comfyui-clone-an-existing-install)**.

#### Option B — Fresh install on Windows

Double-click **`Install-ComfyUI.bat`** (or tick "Also install ComfyUI" in the FlipPix wizard) to
provision a self-contained ComfyUI — bundled Python + torch/CUDA — auto-install all FlipPix custom
nodes, and optionally download models. `Install-ComfyUI-Minimal.bat` installs a trimmed node set;
`Install-ComfyUI-WSL.bat` does the WSL variant.

- **📖 [Complete ComfyUI setup guide](COMFYUI_SETUP.md)** — manual, step by step
- **🚀 [Automated setup scripts](scripts/README.md)** — install, backup, restore, model manifests
- Node list: `scripts/flippix-custom-nodes.txt` · Models: `scripts/flippix-models.txt`

Key node packs the workflows depend on include ComfyUI-Manager, the kijai stack (KJNodes,
WanVideoWrapper, Florence2, SAM2, GIMM-VFI, MMAudio), Lightricks' LTXVideo nodes, rgthree-comfy,
Comfyroll, VideoHelperSuite, controlnet_aux and ComfyUI-GGUF — 29 packs in total, listed with the
nodes each one provides in `scripts/flippix-custom-nodes.txt`. Anything still missing is picked up
by the in-app missing-node resolver on demand.

---

## Project structure

```
flippix/
├── FlipPix.Core/                 # Models, interfaces, settings + image analysis services
├── FlipPix.ComfyUI/              # ComfyUI integration
│   ├── Http/                     #   uploads, prompt submission, /history, health checks
│   ├── WebSocket/                #   live progress + execution errors
│   └── Services/                 #   orchestration, local process manager (auto-start/restart)
├── FlipPix.UI/                   # WPF app (MVVM)
│   ├── ImageGeneratorWindow      #   startup window — Create / Edit / Advanced tabs
│   ├── VideoGeneratorWindow      #   11 video pipelines
│   ├── VideoEnhanceWindow        #   interpolate + upscale
│   ├── ViewModels/               #   one ViewModel per tab (ViewModels/Video/* for video)
│   ├── Services/                 #   LLM clients, LoRA manager, model/node installers, queues
│   ├── Controls/ · Themes/       #   SectionHeader, log panel, shared design tokens
│   └── Models/                   #   queue items, prompt data, LLM API types
├── FlipPix.UI.Linux/             # Avalonia port (Linux) — same ViewModels, Views/ per tab
├── packaging/                    # Linux build + Arch packaging
│   ├── build-linux.sh            #   dotnet publish → publish-linux/, optional tarball
│   └── arch/                     #   PKGBUILD, launcher, .desktop entry
├── install-arch.sh               # One-command Arch install (pacman package or ~/.local)
├── workflow/                     # ComfyUI graphs (API format), grouped by domain
│   ├── image/{zimage,qwen,qwen-edit,klein,krea,anima,ideogram}/
│   ├── video/{ltx,wan,h3-minimax,story}/
│   └── 16gb/                     #   memory-optimised variants for smaller GPUs
├── prompts/prompt2json/          # One system prompt per tab (h3minimax.md, vr180.md, …)
├── scripts/                      # Installers, backup/restore, model + node manifests
├── tools/                        # Workflow conversion + audit helpers (incl. the tab porter)
├── docs/                         # README diagrams + demo poster (regenerate: python docs/make-diagrams.py)
└── publish/                      # Built self-contained app
```

### Where things live at runtime

| Contents | Windows | Linux |
|---|---|---|
| Server URLs, folders, LLM server profiles, VRAM tier | `%APPDATA%\FlipPix\settings.json` | `~/.config/FlipPix/settings.json` |
| Serilog file logs | `%APPDATA%\FlipPix\logs\` | `~/.local/state/FlipPix/logs/` |
| Saved prompts and the scene library (`scenes/index.json` + thumbnails) | `%APPDATA%\FlipPix\prompts\` | `~/.config/FlipPix/prompts/` |
| Generated images and videos | ComfyUI output folder (set in Settings) | ditto, default `~/Pictures/flippix-images` · `~/Videos/flippix-vids` |

---

## Building from source

**Windows (WPF):**

```bash
./publish.bat        # self-contained win-x64 build into publish/

# or manually
dotnet publish FlipPix.UI/FlipPix.UI.csproj -c Release -r win-x64 --self-contained true
```

**Linux (Avalonia):**

```bash
sudo pacman -S --needed dotnet-sdk-8.0 ffmpeg
./packaging/build-linux.sh                    # framework-dependent → publish-linux/
./packaging/build-linux.sh --self-contained   # bundles the runtime instead
./launch-linux.sh                             # run it without installing
```

**Tests:**

```bash
dotnet test tests/FlipPix.Tests    # or dotnet test FlipPix.sln
```

They cover the deterministic passes that decide what gets rendered — the story continuity plan
(`StoryContinuity`: where each clip is, at what hour, in what light) and the beat sheet's parsing and
fitting — because those run without a GPU, a ComfyUI or an LLM behind them, and a chain whose place and
time drift is a finished film that is wrong. Everything downstream of them still needs the app.

Stack: .NET 8 · WPF (Windows) / Avalonia 11.2 (Linux) · MVVM (CommunityToolkit.Mvvm 8.2.2) ·
Microsoft.Extensions.DependencyInjection · Serilog · FFMpegCore · System.Text.Json · YamlDotNet ·
xUnit (tests).

A note for contributors: XAML resource keys and `Style TargetType` mismatches compile cleanly and
only blow up when the window loads, so run the app (or the verification scripts) after touching
styles — don't trust a green build. This holds on both toolkits; the Avalonia side is checked by
instantiating every view on a headless platform, which catches a missing resource key but not a
binding that silently resolves to nothing.

Porting another WPF tab to Avalonia? `tools/port_tab_to_avalonia.py` does the mechanical part and
prints what it could not translate — see [packaging/README.md](packaging/README.md#porting-more-tabs).

---

## Troubleshooting

**FlipPix can't reach ComfyUI** — confirm it's serving on the URL in Settings, check the firewall,
and make sure nothing else holds port 8188. For a remote box, ComfyUI must listen on `0.0.0.0`.

**First run on Linux: pick "Remote Server", click Connect, and nothing happens** — the app exited
before the main window appeared. Closing the setup dialog left it as the last open window, so
Avalonia's default `ShutdownMode = OnLastWindowClose` began shutdown and won the race against the
fire-and-forget task that creates the main window. Fixed by switching to explicit shutdown while
the setup windows are up (restored to normal once the main window shows) and by making that task
log and surface failures instead of discarding them. The server URL and output folder were saved
before the exit, so an older install that still shows this only needs a relaunch - it will skip
setup and go straight to the main window.

**"Missing node" or "missing model" dialog** — accept the offer to install; it clones the node pack
or downloads/locates the weight and remembers the location. If you decline, the job can't run.

**Workflow finishes in ComfyUI but the app keeps waiting** — usually a new output type. FlipPix
reads `images`, `videos`, `gifs` and `audio` from `/history`; a node emitting anything else needs
support added.

**Out of memory** — force 16 GB mode in Settings (loads `workflow/16gb` graphs), lower the
resolution or duration, and close other GPU applications.

**Slow startup or a slow window open** — usually a network drive. FlipPix defers queue and gallery
loading off the UI thread and caches FFmpeg probing; if a mapped drive is dead, unmap it.

**Analyze does nothing** — check the LLM server profile in Settings; the status line under the
button names the exact server and model it's talking to.

### Linux only

**The window is black, or GL errors in the log** — run `FLIPPIX_SOFTWARE_RENDER=1 flippix` to skip
GLX/EGL. Logs are in `~/.local/state/FlipPix/logs`.

**Video previews are blank panels** — ffmpeg isn't found or can't read the container. Install it
(`sudo pacman -S ffmpeg`), or point at a specific binary with `FLIPPIX_FFMPEG=/path/to/ffmpeg`.

**`flippix: no .NET runtime found`** — `sudo pacman -S dotnet-runtime-8.0`, or set `DOTNET_ROOT` if
your runtime lives somewhere other than `/usr/share/dotnet`.

**Output lands in `~/Pictures` literally rather than your localized folder name** — install
`xdg-user-dirs`.

**makepkg fails during the build** — usually a NuGet restore that couldn't reach the network. Rerun
`./install-arch.sh`; it resumes rather than starting clean.

---

## Contributing

Useful directions: new workflow stacks (add the API JSON + a tab ViewModel + a system prompt),
model/node catalog coverage, UI polish against the shared design tokens, and performance work on
the startup path.

## License

Provided as-is for personal and educational use.
