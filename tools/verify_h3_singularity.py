r"""Offline check that both graphs 🗂️ H3 Batch submits in Singularity mode would pass ComfyUI
validation — the hunt and the finish, with the VR LoRA off and on.

Mirrors H3ErosViewModel.ApplyCommonInputs / HuntAsync / UpscaleFinishAsync and
H3VrViewModel.ApplyCommonInputs against workflow/video/h3-minimax/h3-singularity.json: the same node
ids, the same reference wiring, the same relinking and the same reachability prune. Then checks the
result the way ComfyUI's validate_prompt does — every required input present, every link pointing at
a node that exists and has that output index, every COMBO widget holding one of the server's options.

The point is the contract: the Singularity checkbox changes nothing but the file name, so if these
four submissions validate, the tab drives this stack exactly as it drives h3-eros.json.

Usage:  python tools/verify_h3_singularity.py [http://10.0.0.10:8188 | path/to/object_info.json]
"""

import copy
import json
import math
import os
import sys
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORKFLOW = os.path.join(ROOT, "workflow", "video", "h3-minimax", "h3-singularity.json")

# H3ErosViewModel's node ids.
NODE_PROMPT = "22:11"
NODE_SECONDS = "22:23"
NODE_STEPS = "22:8"
NODE_RESOLUTION = "22:9"
NODE_REF2V = "5"
NODE_LATENT_SPLIT = "242"
NODE_UPSCALER = "243"
NODE_UPSCALE_SAMPLER = "135:26"
NODE_UPSCALE_NOISE = "135:27"
NODE_SINGLE_VIDEO = "259"
NODE_SINGLE_AUDIO = "258"
NODE_UPSCALED_VIDEO = "189"
NODE_UPSCALED_AUDIO = "190"
NODE_RIFE = "165"
NODE_FINAL_SAVE = "34"
NODE_UNET = "171:4"
NODE_POWER_LORA = "21"
NODE_VR_LORA = "h3vr_lora"
NODE_CANVAS_W = "eros_canvas_w"
NODE_CANVAS_H = "eros_canvas_h"

BRANCHES = [("125:12", "18", "125:17"),
            ("133:129", "134", "133:128"),
            ("143:139", "144", "143:138")]
SIGMA_SCHEDULES = {3: "222", 4: "221", 5: "220"}
DENOISED_SLOT = 1
DRAFT_FPS = 24

VR_LORA_NAME = "H3/h3-vr180-sbs-lora-v2.safetensors"

# H3Canvas: the aspects the tab offers and the pairs it divides the megapixel budget by. The two
# stereo ones are not in ResolutionSelector's combo, so the tab feeds literal PrimitiveInt canvases
# instead — which is the branch this script also has to exercise.
RATIO_PAIRS = {
    "1:1 (Square)": (1, 1),
    "2:3 (Portrait Photo)": (2, 3),
    "3:2 (Photo)": (3, 2),
    "3:4 (Portrait Standard)": (3, 4),
    "4:3 (Standard)": (4, 3),
    "9:16 (Portrait Widescreen)": (9, 16),
    "16:9 (Widescreen)": (16, 9),
    "21:9 (Ultrawide)": (21, 9),
}
LITERAL_ASPECTS = {"2:1 (Stereo SBS)", "1:2 (Stereo Over-Under)"}


# ── the tab's own graph patching, in Python ─────────────────────────────────────────────────────

def resolve_canvas(aspect, megapixels, multiple=32):
    w_r, h_r = RATIO_PAIRS.get(aspect, (16, 9))
    scale = math.sqrt(megapixels * 1_000_000 / (w_r * h_r))
    w = max(multiple, int(round(w_r * scale / multiple)) * multiple)
    h = max(multiple, int(round(h_r * scale / multiple)) * multiple)
    return w, h


def retarget(graph, source, slot, new_id):
    for n in graph.values():
        for key, value in list(n["inputs"].items()):
            if isinstance(value, list) and len(value) == 2 and value[0] == source and value[1] == slot:
                n["inputs"][key] = [new_id, 0]


def apply_common(graph, item):
    """H3ErosViewModel.ApplyCommonInputs, plus H3VrViewModel's override when VR is on."""
    prompt = item["prompt"]
    if item.get("vr"):
        prompt = "vr180sbs Stereoscopic VR180 video shown side by side: ... " + prompt

    graph[NODE_UNET]["inputs"]["unet_name"] = item["model"]

    for i, name in enumerate(item["refs"]):
        graph[f"eros_ref_{i}"] = {
            "inputs": {"image": name},
            "class_type": "LoadImage",
            "_meta": {"title": f"Picture {i + 1}"},
        }
    ref_inputs = graph[NODE_REF2V]["inputs"]
    for key in [k for k in ref_inputs if k.startswith("ref_images.ref_image_")]:
        del ref_inputs[key]
    for i in range(len(item["refs"])):
        ref_inputs[f"ref_images.ref_image_{i}"] = [f"eros_ref_{i}", 0]
    ref_inputs["ref_image_size"] = "max" if item.get("max_fidelity") else "match"

    graph[NODE_PROMPT]["inputs"]["value"] = prompt
    graph[NODE_SECONDS]["inputs"]["value"] = item["seconds"]
    graph[NODE_STEPS]["inputs"]["value"] = item["first_pass_steps"]

    if item["aspect"] in LITERAL_ASPECTS:
        cw, ch = resolve_canvas(item["aspect"], item["preview_megapixels"])
        graph[NODE_CANVAS_W] = {"inputs": {"value": cw}, "class_type": "PrimitiveInt",
                                "_meta": {"title": "Draft width"}}
        graph[NODE_CANVAS_H] = {"inputs": {"value": ch}, "class_type": "PrimitiveInt",
                                "_meta": {"title": "Draft height"}}
        retarget(graph, NODE_RESOLUTION, 0, NODE_CANVAS_W)
        retarget(graph, NODE_RESOLUTION, 1, NODE_CANVAS_H)
    else:
        graph[NODE_RESOLUTION]["inputs"].update(
            aspect_ratio=item["aspect"], megapixels=item["preview_megapixels"], multiple=32)

    if item.get("vr") and item.get("vr_lora_strength", 1.0) > 0:
        retarget(graph, NODE_POWER_LORA, 0, NODE_VR_LORA)
        graph[NODE_VR_LORA] = {
            "inputs": {"lora_name": VR_LORA_NAME,
                       "strength_model": item["vr_lora_strength"],
                       "model": [NODE_POWER_LORA, 0]},
            "class_type": "LoraLoaderModelOnly",
            "_meta": {"title": "VR180 SBS LoRA"},
        }


def prune(graph, keep):
    reachable, stack = set(), list(keep)
    while stack:
        nid = stack.pop()
        if nid in reachable:
            continue
        reachable.add(nid)
        node = graph.get(nid)
        if not node:
            continue
        for value in node["inputs"].values():
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str):
                stack.append(value[0])
    return {nid: n for nid, n in graph.items() if nid in reachable}


def build_hunt(base, item, slots=(1, 2, 3)):
    graph = copy.deepcopy(base)
    apply_common(graph, item)
    for slot in slots:
        _sampler, out, noise = BRANCHES[slot - 1]
        graph[noise]["inputs"]["noise_seed"] = 1_000 + slot
        graph[out]["inputs"].update(frame_rate=DRAFT_FPS, save_output=True,
                                    filename_prefix=f"h3_batch/hunt_p{slot}")
    return prune(graph, [BRANCHES[s - 1][1] for s in slots])


def build_finish(base, item, chosen=2):
    graph = copy.deepcopy(base)
    apply_common(graph, item)
    sampler, _out, noise = BRANCHES[chosen - 1]
    graph[noise]["inputs"]["noise_seed"] = 1_000 + chosen

    graph[NODE_LATENT_SPLIT]["inputs"]["av_latent"] = [sampler, DENOISED_SLOT]
    graph[NODE_SINGLE_VIDEO]["inputs"]["samples"] = [sampler, DENOISED_SLOT]
    graph[NODE_SINGLE_AUDIO]["inputs"]["samples"] = [sampler, DENOISED_SLOT]

    graph[NODE_UPSCALER]["inputs"]["mode"] = "megapixels"
    graph[NODE_UPSCALER]["inputs"]["mode.megapixels"] = item["megapixels"]
    graph[NODE_UPSCALE_NOISE]["inputs"]["noise_seed"] = 4242
    graph[NODE_UPSCALE_SAMPLER]["inputs"]["sigmas"] = [SIGMA_SCHEDULES[item["upscale_steps"]], 0]

    if item.get("rife", True):
        graph[NODE_RIFE]["inputs"].update(source_fps=float(DRAFT_FPS), target_fps=float(DRAFT_FPS * 2))
        graph[NODE_RIFE]["inputs"]["images"] = [NODE_UPSCALED_VIDEO, 0]
        graph[NODE_FINAL_SAVE]["inputs"]["images"] = [NODE_RIFE, 0]
        graph[NODE_FINAL_SAVE]["inputs"]["frame_rate"] = DRAFT_FPS * 2
    else:
        graph[NODE_FINAL_SAVE]["inputs"]["images"] = [NODE_UPSCALED_VIDEO, 0]
        graph[NODE_FINAL_SAVE]["inputs"]["frame_rate"] = DRAFT_FPS
    graph[NODE_FINAL_SAVE]["inputs"]["audio"] = [NODE_UPSCALED_AUDIO, 0]
    graph[NODE_FINAL_SAVE]["inputs"]["save_output"] = True
    graph[NODE_FINAL_SAVE]["inputs"]["filename_prefix"] = "h3_batch/h3batch_final"
    return prune(graph, [NODE_FINAL_SAVE])


# ── validation ──────────────────────────────────────────────────────────────────────────────────

def fetch_object_info(where):
    if where.startswith("http"):
        url = where.rstrip("/")
        if not url.endswith("/object_info"):
            url += "/object_info"
        with urllib.request.urlopen(url, timeout=120) as fh:
            return json.load(fh)
    with open(where, encoding="utf-8") as fh:
        return json.load(fh)


def widget_only(class_type):
    if class_type == "Power Lora Loader (rgthree)":
        return {"PowerLoraLoaderHeaderWidget", "âž• Add Lora"}
    return set()


def autogrow_prefixes(spec):
    """`ref_images.ref_image_0` and friends: the autogrow template names them, object_info does not."""
    prefixes = set()
    for section in ("required", "optional"):
        for name, entry in ((spec.get("input") or {}).get(section) or {}).items():
            meta = entry[1] if len(entry) > 1 and isinstance(entry[1], dict) else {}
            template = meta.get("template") or {}
            if template.get("prefix"):
                prefixes.add(f"{name}.{template['prefix']}")
    return prefixes


def conditional_inputs(spec):
    names = set()
    for section in ("required", "optional"):
        for entry in ((spec.get("input") or {}).get(section) or {}).values():
            meta = entry[1] if len(entry) > 1 and isinstance(entry[1], dict) else {}
            for entries in (meta.get("formats") or {}).values():
                for e in entries:
                    if isinstance(e, list) and e and isinstance(e[0], str):
                        names.add(e[0])
    return names


def validate(graph, obj, label):
    problems = []
    for nid, n in sorted(graph.items()):
        ct = n["class_type"]
        spec = obj.get(ct)
        if spec is None:
            problems.append(f"{nid}: class '{ct}' is not installed")
            continue
        required = (spec.get("input") or {}).get("required") or {}
        optional = (spec.get("input") or {}).get("optional") or {}
        known = set(required) | set(optional) | conditional_inputs(spec) | widget_only(ct)
        prefixes = autogrow_prefixes(spec)

        for key, value in n["inputs"].items():
            base = key.split(".")[0]
            if (key not in known and base not in known
                    and not any(key.startswith(p) for p in prefixes)):
                problems.append(f"{nid} ({ct}): unknown input '{key}'")
            if isinstance(value, list) and len(value) == 2 and isinstance(value[1], int):
                src = graph.get(value[0])
                if src is None:
                    problems.append(f"{nid} ({ct}): '{key}' links to missing node '{value[0]}'")
                else:
                    outs = (obj.get(src["class_type"]) or {}).get("output") or []
                    if value[1] >= len(outs):
                        problems.append(
                            f"{nid} ({ct}): '{key}' reads output {value[1]} of "
                            f"{value[0]} ({src['class_type']}), which has {len(outs)}")
        for key in required:
            if key in n["inputs"] or any(k.split(".")[0] == key for k in n["inputs"]):
                continue
            problems.append(f"{nid} ({ct}): required input '{key}' is missing")
        for key, value in n["inputs"].items():
            if not isinstance(value, str):
                continue
            entry = required.get(key) or optional.get(key)
            if not entry:
                continue
            # LoadImage.image lists what is already in ComfyUI's input folder. The tab uploads each
            # cast panel immediately before it submits, so a name absent from a dump taken now says
            # nothing about the name that will be there then.
            if ct == "LoadImage" and key == "image":
                continue
            options = entry[0] if isinstance(entry[0], list) else None
            if options is None and len(entry) > 1 and isinstance(entry[1], dict):
                options = entry[1].get("options")
            if options and all(isinstance(o, str) for o in options) and value not in options:
                problems.append(f"{nid} ({ct}): '{key}' = '{value}' is not offered by the server")

    print(("  OK   " if not problems else "  FAIL ") + f"{label}: {len(graph)} nodes")
    for p in problems:
        print(f"       ! {p}")
    return problems


def main():
    where = sys.argv[1] if len(sys.argv) > 1 else "http://10.0.0.10:8188"
    with open(WORKFLOW, encoding="utf-8") as fh:
        base = json.load(fh)
    obj = fetch_object_info(where)

    # One story clip as 🗂️ H3 Batch queues it: two characters, four panels each way, the tab's own
    # canvas dials. The VR case is the same clip with the checkbox ticked.
    flat = dict(prompt="A woman crosses the rain-slicked alley. <Picture 1>",
                model="h3-minimax/Minimax-h3_Singularity_ref2va_Pruned_v1.3_int8.safetensors",
                refs=["char1_front.png", "char1_side.png", "char2_front.png"],
                seconds=8.0, first_pass_steps=10, aspect="16:9 (Widescreen)",
                preview_megapixels=0.15, megapixels=1.0, upscale_steps=4, rife=True)
    vr = dict(flat, vr=True, vr_lora_strength=1.0, aspect="21:9 (Ultrawide)",
              preview_megapixels=0.3, megapixels=1.3)
    stereo = dict(flat, aspect="2:1 (Stereo SBS)")
    nine = dict(flat, refs=[f"panel{i}.png" for i in range(9)], max_fidelity=True,
                rife=False, upscale_steps=5)

    failures = []
    print(f"h3-singularity.json: {len(base)} nodes, validated against {where}")
    for label, item in (("flat", flat), ("VR180", vr), ("stereo canvas", stereo),
                        ("9 refs, max fidelity, no RIFE", nine)):
        failures += validate(build_hunt(base, item), obj, f"hunt · {label}")
        failures += validate(build_finish(base, item), obj, f"finish · {label}")
    # A re-roll of one slot: the hunt is submitted pruned to a single preview sink.
    failures += validate(build_hunt(base, flat, slots=(2,)), obj, "hunt · one re-rolled take")

    if failures:
        print(f"\n{len(failures)} problem(s).")
        return 1
    print("\nEvery submission the tab makes in Singularity mode validates.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
