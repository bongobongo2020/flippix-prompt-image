#!/usr/bin/env python3
"""
Builds the two "H3 4-Step" graphs from the author's 4-step SLA reference render.

Source: workflow/video/h3-minimax/05_ref2va_4step_sla.json  (ComfyUI UI graph, 22 nodes, single pass)
Outputs:
    workflow/video/h3-minimax/h3-4step.json        the hunt   — 3 seeds, 3 preview sinks, nothing else
    workflow/video/h3-minimax/h3-seed-upscale.json the upscale — 1 seed reproduced, then the finish chain

Two files rather than one because the two halves are now two tabs. H3 4-Step only ever hunts: a
latent upscale on every clip is the slowest thing in the loop and the whole point of a 4-step
checkpoint is that hunting is cheap. Upscaling is a separate, later, deliberate act — the Seed
Upscale tab scans a folder of drafts and re-samples only the ones that were chosen.

WHY A BUILDER AND NOT A CONVERTER
---------------------------------
The authored file is a *benchmark*: one MiniMaxH3ReferenceToVideo, one SamplerCustomAdvanced, one
SaveVideo. Rendered as-is it can only do a single take at a single canvas, which is not what the
H3 Eros flow is. What this script keeps from it is the **sampling stack** — every widget value the
author measured that render at:

    UNETLoader     h3-minimax/minimax_h3_fused_refdelta_r1024_turbo8_mystic07_int8_convrot
    H3SLAAttention sparsity 0.9, block_size 64, min_seq_len 8192, protect_audio on
    SigmaShift     shift_video 12, shift_audio 3
    BasicScheduler simple, 4 steps, denoise 1
    KSamplerSelect res_multistep
    CLIP / VAEs    qwen3vl_32b_minimax_h3_nvfp4_awq, video int8_convrot, audio fp32

and what it adds around that stack is the H3 Eros topology, split across the two files:

  * HUNT (h3-4step.json) — the one conditioning + latent fanned out to THREE SamplerCustomAdvanced
    branches, each with its own RandomNoise and its own VHS_VideoCombine preview sink, so a clip is
    a seed hunt. Nothing downstream exists in this file at all.
  * UPSCALE (h3-seed-upscale.json) — the same stack with ONE branch, whose RandomNoise carries the
    seed recorded in a draft's sidecar. Re-sampling that seed at the canvas it was hunted at
    reproduces the draft's latent bit for bit (same model, steps, sampler, scheduler, prompt and
    references ⇒ same latent), and that latent — not the mp4, which has no latent left in it — is
    what goes through LTXVSeparateAVLatent -> MinimaxH3LatentUpscaler3D -> LTXVConcatAVLatent -> a
    second fixed-sigma SamplerCustomAdvanced -> RIFE -> the final sink.

    This is why every draft is written with a sidecar .json: an mp4 on disk cannot be latent-upscaled,
    only re-rendered. The sidecar is the recipe that makes the re-sample reproduce rather than reroll.

NODE IDS ARE DELIBERATELY h3-eros.json's
----------------------------------------
Every id the tab drives is the id H3ErosViewModel already drives — `22:11` prompt, `22:23` seconds,
`22:8` steps, `22:9` the draft canvas, `5` the reference node, the three (sampler, sink, noise)
triples, `242`/`243`/`244`, `135:26`/`135:27`, `222`/`221`/`220`, `189`/`190`, `259`/`258`, `165`,
`34`, `171:4` the UNet. That is what lets H34StepViewModel and SeedUpscaleViewModel be subclasses
that override paths and a step count rather than second copies of the render path. The colonned ids
are meaningless here (this graph has no subgraphs) and are kept purely for that parity.

Two nodes have no counterpart in h3-eros.json and get readable ids of their own:

    "sla"    H3SLAAttention      — the tab unwires it when SLA is off, so a server without the pack
                                   (it has come and gone from 10.0.0.10 twice) still renders
    "shift"  MiniMaxH3SigmaShift — last on the MODEL wire, feeding the guiders and the scheduler

DELIBERATE DIFFERENCES FROM h3-eros.json
----------------------------------------
  * No Power Lora Loader, no MiniMaxChunkFeedForward / MiniMaxLowVRAMAttention / ModelAttentionBackend.
    The authored graph carries none of them; H3SLAAttention is the only model patch, and it sits last
    on the wire, which is where it belongs (see the SLA notes: placement is what makes it work).
  * The second pass samples with res_multistep, not er_sde. The whole point of this model is that it
    is distilled for res_multistep/simple; switching samplers between the two passes would sample the
    upscaled latent with something the checkpoint was never trained against.
  * The scheduler is `simple` at 4 steps, not `beta` at 12. Twelve steps on a turbo8 checkpoint is
    twelve steps of nothing.

Run:  python tools/build_h3_4step.py [--object-info URL_OR_PATH] [--check]
"""
import argparse
import json
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "workflow", "video", "h3-minimax", "05_ref2va_4step_sla.json")
DST_HUNT = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-4step.json")
DST_UPSCALE = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-seed-upscale.json")
OBJ_URL = "http://10.0.0.10:8188/object_info"

OUT_SUBFOLDER = "h3_4step"
UPSCALE_SUBFOLDER = "h3_seed_upscale"

# The frame-count expression: seconds * 24, floored at 5, rounded up to the 17-frame VAE stride.
FRAMES_EXPR = "max(5, round(a * 24)) + (5 - (max(5, round(a * 24)) % 17)) % 17"

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


# ── reading the authored graph ──────────────────────────────────────────────────────────────────

def load_source():
    """The authored widget values, by node type. The file has one of each of the types we want."""
    with open(SRC, encoding="utf-8") as fh:
        ui = json.load(fh)
    by_type = {}
    for node in ui["nodes"]:
        by_type.setdefault(node["type"], []).append(node)

    def widgets(node_type, index=0):
        nodes = by_type.get(node_type)
        if not nodes:
            raise SystemExit(f"{os.path.basename(SRC)}: no {node_type} node — the authored graph changed.")
        return nodes[index].get("widgets_values") or []

    # Two VAELoaders; the one wired into MiniMaxH3ReferenceToVideo.vae is the video VAE. Read the
    # names rather than the order, so a re-export that swaps them does not swap them here.
    vaes = [w[0] for w in (n.get("widgets_values") or [] for n in by_type["VAELoader"])]
    video_vae = next(v for v in vaes if "audio" not in v.lower())
    audio_vae = next(v for v in vaes if "audio" in v.lower())

    sla = widgets("H3SLAAttention")
    scheduler = widgets("BasicScheduler")
    shift = widgets("MiniMaxH3SigmaShift")
    return {
        "unet": widgets("UNETLoader")[0],
        "weight_dtype": widgets("UNETLoader")[1],
        "clip": widgets("CLIPLoader")[0],
        "clip_type": widgets("CLIPLoader")[1],
        "clip_device": widgets("CLIPLoader")[2],
        "video_vae": video_vae,
        "audio_vae": audio_vae,
        "sla_sparsity": float(sla[0]),
        "sla_block": str(sla[1]),
        "sla_min_seq": int(sla[2]),
        "sla_dense_last": int(sla[3]),
        "sla_protect_audio": bool(sla[4]),
        "shift_video": float(shift[0]),
        "shift_audio": float(shift[1]),
        "sampler_name": widgets("KSamplerSelect")[0],
        "scheduler": scheduler[0],
        "steps": int(scheduler[1]),
        "denoise": float(scheduler[2]),
        "aspect": widgets("ResolutionSelector")[0],
        "megapixels": float(widgets("ResolutionSelector")[1]),
        "multiple": int(widgets("ResolutionSelector")[2]),
        "seconds": float(widgets("PrimitiveFloat")[0]),
        "prompt": widgets("PrimitiveStringMultiline")[0],
        "ref_image_size": widgets("MiniMaxH3ReferenceToVideo")[4],
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


def common_stack(src, g):
    """Everything both graphs share: loaders, the MODEL wire, the prompt/length/canvas primitives and
    the one MiniMaxH3ReferenceToVideo whose conditioning and latent every sampler reads."""
    # ── loaders ─────────────────────────────────────────────────────────────
    g["171:4"] = node("UNETLoader", "UNETLoader",
                      unet_name=src["unet"], weight_dtype=src["weight_dtype"])
    g["171:3"] = node("CLIPLoader", "CLIPLoader",
                      clip_name=src["clip"], type=src["clip_type"], device=src["clip_device"])
    g["171:2"] = node("VAELoader", "VAELoader", vae_name=src["video_vae"])
    g["171:1"] = node("VAELoader", "VAELoader", vae_name=src["audio_vae"])

    # ── the MODEL wire ──────────────────────────────────────────────────────
    # Order is the author's: UNet -> SLA -> sigma shift, with the shift last before the guiders and
    # the scheduler. SLA is given its own readable id because the tab unwires it when SLA is off.
    g["sla"] = node("H3SLAAttention", f"H3 SLA Attention {src['sla_sparsity']}/{src['sla_block']}",
                    model=["171:4", 0], sparsity_ratio=src["sla_sparsity"],
                    block_size=src["sla_block"], min_seq_len=src["sla_min_seq"],
                    dense_last_steps=src["sla_dense_last"],
                    protect_audio=src["sla_protect_audio"], enabled=True)
    g["shift"] = node("MiniMaxH3SigmaShift", "MiniMaxH3SigmaShift",
                      model=["sla", 0], shift_video=src["shift_video"],
                      shift_audio=src["shift_audio"])
    model = ["shift", 0]

    # ── prompt, length, canvas, steps ───────────────────────────────────────
    g["22:11"] = node("PrimitiveStringMultiline", "Prompt", value=src["prompt"])
    g["22:23"] = node("PrimitiveFloat", "Video Length (seconds)", value=src["seconds"])
    g["22:24"] = node("ComfyMathExpression", "ComfyMathExpression",
                      expression=FRAMES_EXPR, **{"values.a": ["22:23", 0]})
    g["22:9"] = node("ResolutionSelector", "Resolution Selector (Size)",
                     aspect_ratio=src["aspect"], megapixels=src["megapixels"],
                     multiple=src["multiple"])
    g["22:8"] = node("INTConstant", "TOTAL STEPS", value=src["steps"])
    g["22:7"] = node("BasicScheduler", "BasicScheduler",
                     model=model, scheduler=src["scheduler"], steps=["22:8", 0],
                     denoise=src["denoise"])
    g["22:6"] = node("KSamplerSelect", "KSamplerSelect", sampler_name=src["sampler_name"])

    # No reference loaders: both tabs inject their own LoadImage nodes into the autogrow
    # ref_images.ref_image_N slots at submit time, one per cast panel.
    g["5"] = node("MiniMaxH3ReferenceToVideo", "MiniMaxH3ReferenceToVideo",
                  clip=["171:3", 0], vae=["171:2", 0], audio_vae=["171:1", 0],
                  prompt=["22:11", 0], width=["22:9", 0], height=["22:9", 1],
                  length=["22:24", 1], ref_image_size=src["ref_image_size"])
    return model


def sample_branch(g, model, slot, sink_prefix=None):
    """One RandomNoise -> BasicGuider -> SamplerCustomAdvanced, optionally decoded to a preview sink.
    The ids are H3ErosViewModel.SampleBranches', so the tab drives them without a second lookup table."""
    noise, guider, sampler, vdec, adec, out = BRANCHES[slot - 1]
    g[noise] = node("RandomNoise", "RandomNoise", noise_seed=slot - 1)
    g[guider] = node("BasicGuider", "BasicGuider", model=model, conditioning=["5", 0])
    g[sampler] = node("SamplerCustomAdvanced", f"Sampler #{slot}",
                      noise=[noise, 0], guider=[guider, 0], sampler=["22:6", 0],
                      sigmas=["22:7", 0], latent_image=["5", 1])
    if sink_prefix is None:
        return sampler
    g[vdec] = node("VAEDecode", "VAEDecode", samples=[sampler, 0], vae=["171:2", 0])
    g[adec] = node("VAEDecodeAudio", "VAEDecodeAudio", samples=[sampler, 0], vae=["171:1", 0])
    g[out] = sink(sink_prefix, 24, 19, f"Preview {slot}", [vdec, 0], [adec, 0])
    return sampler


def build_hunt(src):
    """h3-4step.json — three seeds, three preview sinks, and deliberately nothing downstream."""
    g = {}
    model = common_stack(src, g)
    for slot in range(1, len(BRANCHES) + 1):
        sample_branch(g, model, slot, f"{OUT_SUBFOLDER}/preview_{slot}")
    return g


def build_upscale(src):
    """h3-seed-upscale.json — one seed reproduced at the canvas it was hunted at, then upscaled.

    Only branch 1 exists here. The Seed Upscale tab writes the sidecar's recorded seed into `125:17`
    whatever slot the draft came from, because a slot is only a position on a board: what makes the
    latent reproduce is the seed, and a seed sampled through this stack lands in the same place
    wherever it is wired."""
    g = {}
    model = common_stack(src, g)
    sampler = sample_branch(g, model, 1)

    # Slot 1 is denoised_output. The hunt's previews decode slot 0 (output); with a schedule ending at
    # 0.0 the two are the same tensor, and this is the split h3-eros.json ships.
    g["242"] = node("LTXVSeparateAVLatent", "LTXVSeparateAVLatent", av_latent=[sampler, 1])
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

    g["189"] = node("VAEDecode", "Final Decode", samples=["135:26", 0], vae=["171:2", 0])
    g["190"] = node("VAEDecodeAudio", "Final Audio Decode", samples=["135:26", 0], vae=["171:1", 0])

    # The "skip the upscale" decode of the reproduced latent, for a run that only wants the draft at
    # its own size. Nothing consumes it, so the prune deletes it unless the tab wires it to the sink.
    g["259"] = node("VAEDecode", "Single-pass Decode", samples=[sampler, 1], vae=["171:2", 0])
    g["258"] = node("VAEDecodeAudio", "Single-pass Audio Decode", samples=[sampler, 1], vae=["171:1", 0])

    g["165"] = node("RIFEInterpolation", "RIFEInterpolation",
                    images=["189", 0], source_fps=24.0, target_fps=48.0, scale=1,
                    model_name="flownet.pkl", batch_size=8, use_fp16=True)
    g["34"] = sink(f"{UPSCALE_SUBFOLDER}/final", 48, 16, "Final Video", ["165", 0], ["190", 0])
    return g


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def fetch_object_info(where):
    if where.startswith("http"):
        with urllib.request.urlopen(where, timeout=60) as fh:
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
        known = set(required) | set(optional) | conditional_inputs(required, optional)
        for key, value in n["inputs"].items():
            base = key.split(".")[0]
            if base not in known and key not in known:
                problems.append(f"{nid} ({ct}): unknown input '{key}'")
            if isinstance(value, list) and len(value) == 2 and isinstance(value[1], int):
                if value[0] not in graph:
                    problems.append(f"{nid} ({ct}): input '{key}' links to missing node '{value[0]}'")
        for key, spec_v in required.items():
            if key in n["inputs"] or any(k.split(".")[0] == key for k in n["inputs"]):
                continue
            problems.append(f"{nid} ({ct}): required input '{key}' is missing")
        # Combo values that must exist on this server.
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


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--object-info", default=OBJ_URL,
                    help="URL or path of a ComfyUI /object_info dump for validation")
    ap.add_argument("--check", action="store_true", help="validate only, do not write")
    args = ap.parse_args()

    src = load_source()
    graphs = [(DST_HUNT, build_hunt(src)), (DST_UPSCALE, build_upscale(src))]

    try:
        obj = fetch_object_info(args.object_info)
    except Exception as exc:                                     # noqa: BLE001 — advisory only
        print(f"! could not read object_info ({exc}); skipping validation")
        obj = None

    if obj:
        failed = False
        for dst, graph in graphs:
            problems = check(graph, obj)
            for p in problems:
                print(f"! {os.path.basename(dst)}: {p}")
            failed = failed or bool(problems)
            if not problems:
                print(f"validated {os.path.basename(dst)}: {len(graph)} nodes")
        if failed:
            return 1

    if args.check:
        return 0

    for dst, graph in graphs:
        with open(dst, "w", encoding="utf-8") as fh:
            json.dump(graph, fh, indent=2, ensure_ascii=False)
            fh.write("\n")
        print(f"wrote {dst} ({len(graph)} nodes)")
    print(f"  model    {src['unet']}")
    print(f"  sampling {src['sampler_name']} / {src['scheduler']} / {src['steps']} steps, "
          f"shift {src['shift_video']}/{src['shift_audio']}")
    print(f"  SLA      sparsity {src['sla_sparsity']}, block {src['sla_block']}, "
          f"min_seq_len {src['sla_min_seq']}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
