#!/usr/bin/env python3
r"""
Builds the "H3 Singularity" graph from the author's Singularity render.

Source: workflow/video/h3-minimax/minimax-singularity.json  (ComfyUI UI graph, 57 nodes, 2 passes)
Output: workflow/video/h3-minimax/h3-singularity.json       (what 🗂️ H3 Batch submits with the box ticked)

WHAT THE AUTHORED GRAPH IS, AND WHAT IT IS NOT
----------------------------------------------
It is a **single-take first/last-frame render**: one MiniMaxH3ImageToVideo, one sampling pass at
10 steps, a short 4-step denoise-0.2 refine of the same latent at the same canvas, FILM x2, one mp4.
Its two image loaders are authored bypassed, so as it stands it is a text-to-video graph with a
fifteen-second prompt in it and no way to say who is in the shot.

That is the one thing 🗂️ H3 Batch cannot use. Every story it renders is cast: two characters are
photographed, split into panels, and wired into the render one panel per `ref_image_N` slot so the
faces hold across a twelve-clip film. `MiniMaxH3ImageToVideo` has no reference inputs at all — it
takes a first frame and a last frame — so the authored graph would render a batch of stories about
nobody in particular, with a new invented face every clip.

So what this script keeps from the authored file is the **sampling stack**, every widget value the
author measured that render at:

    UNETLoader             h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8
    ModelAttentionBackend  comfy kitchen attention
    MiniMaxChunkFeedForward chunks 2, seq_threshold 4096
    ModelPatchTorchSettings fp16 accumulation on
    MiniMaxH3SigmaShift    shift_video 12, shift_audio 3      (last on the MODEL wire, as authored)
    BasicScheduler         simple, 10 steps, denoise 1
    KSamplerSelect         euler
    CLIP / VAEs            qwen3vl_32b_minimax_h3-w4a8_convrot, video fp16, audio fp32
    canvas / length        16:9 at 0.6 MP, 15 s

and what it puts around that stack is the H3 Eros topology the tab already drives:

  * `MiniMaxH3ReferenceToVideo` in place of `MiniMaxH3ImageToVideo` — same clip/vae/prompt/canvas/
    length inputs, plus the `audio_vae` it needs and the autogrow `ref_images` slots the tab fills
    with one LoadImage per cast panel at submit time. The checkpoint is a **ref2va** one (it says so
    in its own filename), which is the conditioning it was built for; the authored graph was simply
    driving it through the other node.
  * THREE sampler branches off that one conditioning + latent, each with its own RandomNoise and its
    own preview sink, so a clip is a seed hunt like every other clip this tab renders.
  * the finish chain: LTXVSeparateAVLatent -> MinimaxH3LatentUpscaler3D -> LTXVConcatAVLatent -> a
    short fixed-sigma second pass -> RIFE -> the final sink.
  * an empty `Power Lora Loader (rgthree)` seat on the MODEL wire, because 🗂️ H3 Batch is also the
    VR tab: with 🥽 ticked, H3VrViewModel splices the VR180 SBS LoRA into exactly that node. Without
    the seat the two checkboxes could not be ticked at once.

NODE IDS ARE DELIBERATELY h3-eros.json's
----------------------------------------
Every id the tab drives is the id H3ErosViewModel already drives — `22:11` prompt, `22:23` seconds,
`22:8` steps, `22:9` the draft canvas, `5` the reference node, the three (sampler, sink, noise)
triples, `242`/`243`/`244`, `135:26`/`135:27`, `222`/`221`/`220`, `189`/`190`, `259`/`258`, `165`,
`34`, `171:4` the UNet, `21` the LoRA seat. That is what makes the tab's Singularity checkbox a
one-line change of `WorkflowFileName` rather than a second render path: the same ApplyCommonInputs,
the same hunt, the same finish, the same VR splice, driving a different stack. The colonned ids are
meaningless here (this graph has no subgraphs) and are kept purely for that parity. Two model
patches have no counterpart in h3-eros.json and get readable ids of their own:

    "torch"  ModelPatchTorchSettings — fp16 accumulation
    "shift"  MiniMaxH3SigmaShift     — last on the MODEL wire, feeding the guiders and the scheduler
    "clean"  easy cleanGpuUsed       — see below

DELIBERATE DIFFERENCES FROM THE AUTHORED GRAPH
----------------------------------------------
  * **The refine pass became the upscale pass.** The author's second pass re-samples the same latent
    at the same canvas at denoise 0.2. Here the picked latent is lifted to the finished megapixels by
    MinimaxH3LatentUpscaler3D first and the second pass re-samples *that*, on the fixed schedules the
    tab's ⬆ steps dial picks between (`222`/`221`/`220`). A 0.2 denoise at the draft canvas would
    finish every film at the size the drafts were hunted at, which on this tab is 0.15 MP.
  * **RIFE, not FILM.** The author interpolates with FrameInterpolate + film_net; the tab's fps
    checkbox drives `RIFEInterpolation`'s source_fps/target_fps, which FrameInterpolate does not
    have. The interpolator is not what makes this stack the Singularity stack, and a checkbox that
    silently does nothing is worse than a different net. (The authored graph also muxes its
    interpolated frames at 24 fps, i.e. at half speed; node `34` here is written at 48.)
  * **`easy cleanGpuUsed` kept, `easy clearCacheAll` dropped.** The clean sits on the LATENT wire
    between the second pass and the final decode — where the tab's own relinking cannot delete it,
    and where freeing the sampler's VRAM before decoding fifteen seconds of video is worth having.
    The cache clear is dropped on purpose: ComfyUI's execution cache is what lets a finish that
    follows its own hunt skip re-sampling the picked branch, and this tab runs hundreds of
    submissions back to back overnight.
  * **`ApplyVDNH3Advanced` dropped.** Authored bypassed, and the server has no VDN stage directory
    under models/vdn for it to load, so its `vdn_checkpoint` combo is empty.
  * **The image loaders dropped.** `VHS_LoadImagePath` + `ImageResizeKJv2`, both authored bypassed,
    both pointing at paths on the author's own machine (`F:\ComfyUI\output\...`).
  * **The prompt is a placeholder.** The authored graph ships a fifteen-second shot list in `22:11`;
    the tab overwrites that node on every submission, and a story's own text is the only thing that
    should ever be rendered from it.

Run:  python tools/build_h3_singularity.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "minimax-singularity.json")
DST = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-singularity.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_singularity"

# The frame-count expression: seconds * 24, floored at 5, rounded up to the 17-frame VAE stride.
# Read off the authored graph rather than written here, so a re-export that changes it changes this.
FRAMES_EXPR_FALLBACK = "max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17"

# The three hunt branches, exactly as H3ErosViewModel.SampleBranches names them:
#   slot -> (noise, guider, sampler, video decode, audio decode, sink)
BRANCHES = [
    ("125:17", "125:14", "125:12", "176", "177", "18"),
    ("133:128", "133:130", "133:129", "187", "188", "134"),
    ("143:138", "143:140", "143:139", "180", "181", "144"),
]

# Fixed schedules for the upscale pass, by step count. Same values h3-eros.json ships; the tab links
# one of them into the second-pass sampler from its UpscaleSteps dial.
SIGMA_SCHEDULES = {
    "222": ("3 step Sigmas", "0.9035, 0.6316, 0.3158, 0.0000"),
    "221": ("4 step Sigmas", "0.9035, 0.8000, 0.6316, 0.3158, 0.0000"),
    "220": ("5 step Sigmas", "0.9231, 0.8780, 0.8000, 0.6316, 0.3158, 0.0000"),
}

# Inputs a node carries as browser-side widgets and never declares in /object_info. rgthree's Power
# Lora Loader keeps its header and its "add" button there; h3-eros.json ships both and the server
# ignores them, so the seat is written the same way here rather than differently for no reason.
WIDGET_ONLY_INPUTS = {
    "Power Lora Loader (rgthree)": {"PowerLoraLoaderHeaderWidget", "âž• Add Lora"},
}

PROMPT_PLACEHOLDER = (
    "The story's own text is written into this node at submit time — 🗂️ H3 Batch overwrites it on "
    "every clip. Whatever is left here is never rendered."
)


# ── reading the authored graph ──────────────────────────────────────────────────────────────────

def load_source():
    """The authored widget values, by node type. Everything the Singularity stack is."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)

    by_type = {}
    for n in ui["nodes"]:
        by_type.setdefault(n["type"], []).append(n)

    def nodes_of(node_type):
        found = by_type.get(node_type)
        if not found:
            raise SystemExit(f"{os.path.basename(SRC)}: no {node_type} node — the authored graph changed.")
        return found

    def widgets(node_type, index=0):
        return nodes_of(node_type)[index].get("widgets_values") or []

    # Two VAELoaders; read them by name rather than by order, so a re-export that swaps them does not
    # swap them here.
    vaes = [(n.get("widgets_values") or [""])[0] for n in nodes_of("VAELoader")]
    video_vae = next(v for v in vaes if "audio" not in v.lower())
    audio_vae = next(v for v in vaes if "audio" in v.lower())

    # Two BasicSchedulers: the first pass is the one at denoise 1. The refine pass's steps/denoise are
    # not carried over — see the module docstring — but reading it here keeps the assumption checkable.
    schedulers = sorted(
        ((n.get("widgets_values") or ["simple", 10, 1.0]) for n in nodes_of("BasicScheduler")),
        key=lambda w: -float(w[2]),
    )
    first_pass = schedulers[0]
    if float(first_pass[2]) != 1.0:
        raise SystemExit(f"{os.path.basename(SRC)}: no full-denoise BasicScheduler — which pass is first?")

    # Two ResolutionSelector-adjacent numbers: the megapixels primitive feeds the selector, so the
    # selector's own widget is stale. Take the primitive by its title, which the author labels.
    def primitive(marker, default):
        for n in nodes_of("PrimitiveFloat"):
            if marker in (n.get("title") or "").lower() or marker in str(n.get("properties", {})).lower():
                return float((n.get("widgets_values") or [default])[0])
        return default

    megapixels = primitive("resolution", 0.6)
    seconds = primitive("duration", 15.0)

    frames_expr = FRAMES_EXPR_FALLBACK
    for n in nodes_of("ComfyMathExpression"):
        expr = (n.get("widgets_values") or [""])[0]
        if "17" in expr:
            frames_expr = expr
            break

    shift = widgets("MiniMaxH3SigmaShift")
    chunk = widgets("MiniMaxChunkFeedForward")
    return {
        "unet": widgets("UNETLoader")[0],
        "weight_dtype": widgets("UNETLoader")[1],
        "clip": widgets("CLIPLoader")[0],
        "clip_type": widgets("CLIPLoader")[1],
        "clip_device": widgets("CLIPLoader")[2],
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "attention": widgets("ModelAttentionBackend")[0],
        "chunks": int(chunk[0]),
        "seq_threshold": int(chunk[1]),
        "fp16_accumulation": bool(widgets("ModelPatchTorchSettings")[0]),
        "shift_video": float(shift[0]),
        "shift_audio": float(shift[1]),
        "sampler_name": widgets("KSamplerSelect")[0],
        "scheduler": first_pass[0],
        "steps": int(first_pass[1]),
        "denoise": float(first_pass[2]),
        "aspect": widgets("ResolutionSelector")[0],
        "megapixels": megapixels,
        "multiple": int(widgets("ResolutionSelector")[2]),
        "seconds": seconds,
        "frames_expr": frames_expr,
    }


# ── building the API graph ──────────────────────────────────────────────────────────────────────

def node(class_type, title, **inputs):
    return {"inputs": inputs, "class_type": class_type, "_meta": {"title": title}}


def sink(prefix, fps, crf, title, images, audio):
    return node(
        "VHS_VideoCombine", title,
        frame_rate=fps, loop_count=0, filename_prefix=prefix, format="video/h264-mp4",
        pix_fmt="yuv420p", crf=crf, save_metadata=False, trim_to_audio=False,
        pingpong=False, save_output=True, images=images, audio=audio,
    )


def build(src):
    g = {}

    # ── loaders ─────────────────────────────────────────────────────────────
    g["171:4"] = node("UNETLoader", "UNETLoader",
                      unet_name=src["unet"], weight_dtype=src["weight_dtype"])
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      clip_name=src["clip"], type=src["clip_type"], device=src["clip_device"])
    g["171:2"] = node("VAELoader", "VAELoader", vae_name=src["video_vae"])
    g["171:1"] = node("VAELoader", "VAELoader", vae_name=src["audio_vae"])

    # ── the MODEL wire ──────────────────────────────────────────────────────
    # The author's order — attention backend, feed-forward chunking, torch settings — with the sigma
    # shift last, which is where it belongs and where the authored graph has it. The one insertion is
    # the empty Power Lora Loader seat before the shift: it is a pass-through with no LoRAs in it, and
    # it is what H3VrViewModel splices the VR180 SBS LoRA into when 🥽 is ticked alongside this stack.
    g["196"] = node("ModelAttentionBackend", "ModelAttentionBackend",
                    model=["171:4", 0], attention=src["attention"])
    g["193"] = node("MiniMaxChunkFeedForward", "MiniMaxChunkFeedForward",
                    model=["196", 0], chunks=src["chunks"], seq_threshold=src["seq_threshold"])
    g["torch"] = node("ModelPatchTorchSettings", "ModelPatchTorchSettings",
                      model=["193", 0], enable_fp16_accumulation=src["fp16_accumulation"])
    g["21"] = node("Power Lora Loader (rgthree)", "Power Lora Loader (rgthree)",
                   model=["torch", 0],
                   **{"PowerLoraLoaderHeaderWidget": {"type": "PowerLoraLoaderHeaderWidget"},
                      "\u00e2\u017e\u2022 Add Lora": ""})
    g["shift"] = node("MiniMaxH3SigmaShift", "MiniMaxH3SigmaShift",
                      model=["21", 0], shift_video=src["shift_video"],
                      shift_audio=src["shift_audio"])
    model = ["shift", 0]

    # ── prompt, length, canvas, steps ───────────────────────────────────────
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", value=PROMPT_PLACEHOLDER)
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", value=src["seconds"])
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      expression=src["frames_expr"], **{"values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     aspect_ratio=src["aspect"], megapixels=src["megapixels"],
                     multiple=src["multiple"])
    g["22:8"] = node("INTConstant", "TOTAL STEPS", value=src["steps"])
    g["22:7"] = node("BasicScheduler", "BasicScheduler",
                     model=model, scheduler=src["scheduler"], steps=["22:8", 0],
                     denoise=src["denoise"])
    g["22:6"] = node("KSamplerSelect", "KSamplerSelect", sampler_name=src["sampler_name"])

    # ── the conditioning ────────────────────────────────────────────────────
    # MiniMaxH3ReferenceToVideo where the authored graph has MiniMaxH3ImageToVideo: same prompt,
    # canvas and length, plus the audio VAE and the autogrow ref_images slots the tab fills with one
    # LoadImage per cast panel at submit time. No reference loaders are shipped — the panels are
    # uploaded per clip and injected beside this node.
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  clip=["171:3", 0], vae=["171:2", 0], audio_vae=["171:1", 0],
                  prompt=["22:11", 0], width=["22:9", 0], height=["22:9", 1],
                  length=["22:24", 1], ref_image_size="match")

    # ── the hunt: three seeds off one conditioning ──────────────────────────
    for slot, (noise, guider, sampler, vdec, adec, out) in enumerate(BRANCHES, start=1):
        g[noise] = node("RandomNoise", "RandomNoise", noise_seed=slot - 1)
        g[guider] = node("BasicGuider", "BasicGuider", model=model, conditioning=["5", 0])
        g[sampler] = node("SamplerCustomAdvanced", f"Sampler #{slot}",
                          noise=[noise, 0], guider=[guider, 0], sampler=["22:6", 0],
                          sigmas=["22:7", 0], latent_image=["5", 1])
        g[vdec] = node("VAEDecode", "VAEDecode", samples=[sampler, 0], vae=["171:2", 0])
        g[adec] = node("VAEDecodeAudio", "VAEDecodeAudio", samples=[sampler, 0], vae=["171:1", 0])
        g[out] = sink(f"{OUT_SUBFOLDER}/preview_{slot}", 24, 19, f"Preview {slot}", [vdec, 0], [adec, 0])

    first = BRANCHES[0][2]

    # ── the finish: the picked latent, upscaled and re-sampled ─────────────
    # Slot 1 is denoised_output — the previews decode slot 0, and with a schedule ending at 0.0 the two
    # are the same tensor. The tab repoints `242`/`259`/`258` at whichever branch was picked.
    g["242"] = node("LTXVSeparateAVLatent", "LTXVSeparateAVLatent", av_latent=[first, 1])
    g["243"] = node("MinimaxH3LatentUpscaler3D", "MinimaxH3LatentUpscaler3D",
                    latent=["242", 0], model_name="minimax_h3_latent_upscaler_3d_bf16.safetensors",
                    mode="megapixels", align=32, keep_proportion=True,
                    device="cuda", precision="fp16", **{"mode.megapixels": 1.0})
    g["244"] = node("LTXVConcatAVLatent", "LTXVConcatAVLatent",
                    video_latent=["243", 0], audio_latent=["242", 1])

    for nid, (title, sigmas) in SIGMA_SCHEDULES.items():
        g[nid] = node("ManualSigmas", title, sigmas=sigmas)

    g["135:30"] = node("KSamplerSelect", "KSamplerSelect", sampler_name=src["sampler_name"])
    g["135:27"] = node("RandomNoise", "RandomNoise", noise_seed=0)
    g["135:29"] = node("BasicGuider", "BasicGuider", model=model, conditioning=["5", 0])
    g["135:26"] = node("SamplerCustomAdvanced", "Upscale Pass",
                       noise=["135:27", 0], guider=["135:29", 0], sampler=["135:30", 0],
                       sigmas=["221", 0], latent_image=["244", 0])

    # The author's VRAM hygiene, moved onto the LATENT wire. Everything downstream of the decodes is
    # relinked by the tab (RIFE on or off), so this is the one place a pass-through survives; and it is
    # the better place anyway — the sampler's weights go before fifteen seconds of video is decoded.
    g["clean"] = node("easy cleanGpuUsed", "Free VRAM before the decode", anything=["135:26", 0])

    g["189"] = node("VAEDecode", "Final Decode", samples=["clean", 0], vae=["171:2", 0])
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode", samples=["clean", 0], vae=["171:1", 0])

    # The "skip the upscale" decode of the picked latent, for a finish that wants the draft at its own
    # size. Nothing consumes it, so the prune deletes it unless the tab wires it to the sink.
    g["259"] = node("VAEDecode", "Single-pass Decode", samples=[first, 1], vae=["171:2", 0])
    g["258"] = node("VAEDecodeAudio", "Single-pass Audio Decode", samples=[first, 1], vae=["171:1", 0])

    g["165"] = node("RIFEInterpolation", "RIFEInterpolation",
                    images=["189", 0], source_fps=24.0, target_fps=48.0, scale=1,
                    model_name="flownet.pkl", batch_size=8, use_fp16=True)
    g["34"] = sink(f"{OUT_SUBFOLDER}/final", 48, 16, "Final Video", ["165", 0], ["190", 0])
    return g


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def fetch_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=120) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def conditional_inputs(*sections):
    """Inputs a node only declares once a format is chosen — VHS_VideoCombine's pix_fmt / crf and
    friends live inside its `format` spec's `formats` map, not in required/optional."""
    names = set()
    for section in sections:
        for spec_v in section.values():
            meta = spec_v[1] if len(spec_v) > 1 and isinstance(spec_v[1], dict) else {}
            for entries in (meta.get("formats") or {}).values():
                for entry in entries:
                    if isinstance(entry, list) and entry and isinstance(entry[0], str):
                        names.add(entry[0])
    return names


def check(graph, obj):
    """Every class installed, every link resolvable, every required input present."""
    problems = []
    for nid, n in graph.items():
        ct = n["class_type"]
        spec = obj.get(ct)
        if spec is None:
            problems.append(f"{nid}: class '{ct}' is not installed on the server")
            continue
        required = (spec.get("input") or {}).get("required") or {}
        optional = (spec.get("input") or {}).get("optional") or {}
        known = (set(required) | set(optional) | conditional_inputs(required, optional)
                 | WIDGET_ONLY_INPUTS.get(ct, set()))
        for key, value in n["inputs"].items():
            base = key.split(".")[0]
            if base not in known and key not in known:
                problems.append(f"{nid} ({ct}): unknown input '{key}'")
            if isinstance(value, list) and len(value) == 2 and isinstance(value[1], int):
                if value[0] not in graph:
                    problems.append(f"{nid} ({ct}): input '{key}' links to missing node '{value[0]}'")
        for key in required:
            if key in n["inputs"] or any(k.split(".")[0] == key for k in n["inputs"]):
                continue
            problems.append(f"{nid} ({ct}): required input '{key}' is missing")
        for key, value in n["inputs"].items():
            if not isinstance(value, str):
                continue
            spec_v = required.get(key) or optional.get(key)
            if not spec_v:
                continue
            options = None
            if isinstance(spec_v[0], list):
                options = spec_v[0]
            elif len(spec_v) > 1 and isinstance(spec_v[1], dict) and "options" in spec_v[1]:
                options = spec_v[1]["options"]
            if options and all(isinstance(o, str) for o in options) and value not in options:
                problems.append(f"{nid} ({ct}): '{key}' = '{value}' is not one of the server's options")
    return problems


def check_contract(graph):
    """Every id H3ErosViewModel drives has to be here, carrying the class it expects. This is the whole
    reason the tab's Singularity checkbox is a change of file name and nothing else."""
    expected = {
        "22:11": "PrimitiveStringMultiline", "22:23": "PrimitiveFloat", "22:8": "INTConstant",
        "22:9": "ResolutionSelector", "5": "MiniMaxH3ReferenceToVideo",
        "242": "LTXVSeparateAVLatent", "243": "MinimaxH3LatentUpscaler3D",
        "135:26": "SamplerCustomAdvanced", "135:27": "RandomNoise",
        "259": "VAEDecode", "258": "VAEDecodeAudio", "189": "VAEDecode", "190": "VAEDecodeAudio",
        "165": "RIFEInterpolation", "34": "VHS_VideoCombine", "171:4": "UNETLoader",
        "21": "Power Lora Loader (rgthree)",
        "222": "ManualSigmas", "221": "ManualSigmas", "220": "ManualSigmas",
    }
    for noise, _guider, sampler, _v, _a, out in BRANCHES:
        expected[noise] = "RandomNoise"
        expected[sampler] = "SamplerCustomAdvanced"
        expected[out] = "VHS_VideoCombine"

    problems = []
    for nid, ct in expected.items():
        if nid not in graph:
            problems.append(f"h3-eros.json contract: node '{nid}' ({ct}) is missing")
        elif graph[nid]["class_type"] != ct:
            problems.append(f"h3-eros.json contract: node '{nid}' is {graph[nid]['class_type']}, expected {ct}")
    return problems


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=OBJ_URL,
                    help="URL or path of a ComfyUI /object_info dump for validation")
    ap.add_argument("--check", action="store_true", help="validate only, do not write")
    args = ap.parse_args()

    src = load_source()
    graph = build(src)

    problems = check_contract(graph)
    try:
        obj = fetch_object_info(args.object_info)
    except Exception as exc:                                     # noqa: BLE001 — advisory only
        print(f"! could not read object_info ({exc}); skipping server validation")
        obj = None
    if obj:
        problems += check(graph, obj)

    for p in problems:
        print(f"! {p}")
    if problems:
        return 1
    print(f"validated {os.path.basename(DST)}: {len(graph)} nodes")

    if args.check:
        return 0

    with open(DST, "w", encoding="utf-8") as fh:
        json.dump(graph, fh, indent=2, ensure_ascii=False)
        fh.write("\n")
    print(f"wrote {DST} ({len(graph)} nodes)")
    print(f"  model    {src['unet']}")
    print(f"  clip     {src['clip']}")
    print(f"  sampling {src['sampler_name']} / {src['scheduler']} / {src['steps']} steps, "
          f"shift {src['shift_video']}/{src['shift_audio']}")
    print(f"  patches  {src['attention']}, chunk-ff {src['chunks']}/{src['seq_threshold']}, "
          f"fp16 accumulation {src['fp16_accumulation']}")
    print(f"  authored canvas {src['aspect']} at {src['megapixels']} MP, {src['seconds']}s "
          f"(the tab writes its own)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
