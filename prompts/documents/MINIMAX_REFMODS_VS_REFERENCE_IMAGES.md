# MiniMax-H3: RefMods vs Reference Images Master Guide

_Version: 1.0.0_  
_Last Updated: September 2026_  
_Target Workflows: `workflow_api_minimaxh3_t2v.json` / `workflow_api_minimaxh3_c2v.json` + `ComfyUI-MiniMaxH3Mod`_

---

## 1. Overview & Architectural Shift

Previously, character consistency in MiniMax-H3 video generation relied on **R2V (Reference-to-Video)** workflows by supplying raw PNG/JPEG reference images (typically 8 images per persona) into `MiniMaxH3ReferenceToVideo`.

With the introduction of **`ComfyUI-MiniMaxH3Mod`** and pre-extracted **RefMod (`.safetensors`) adapters**, **RefMods are now the preferred standard for character and identity reference in all MiniMax-H3 video generation (T2V, solo clips, multi-character scenes, and C2V continuous chains).**

---

## 2. Why RefMods Are Superior to Reference Images

| Dimension | Raw Reference Images (R2V) | RefMods (`.safetensors`) |
| :--- | :--- | :--- |
| **Generation Latency** | Must load, resize, and VAE-encode 8+ images **on every single generation run**. | **Zero encode latency during generation.** Latents are pre-encoded and loaded instantly into memory. |
| **VRAM & Memory Stability** | High VRAM spikes during live VAE encoding; prone to fragmentation & CUDA OOM on long chains. | Lightweight, flat memory usage; latents stay compressed in `.safetensors` format. |
| **Multi-Character Scaling** | **Hard limit**: Workflow graph only supports 8 images total across all subjects. Two characters get only 4 images each; 3+ characters collapse. | **Up to 8 full personas** loaded simultaneously (`mod_1` through `mod_8` in `MiniMaxH3RefModsLoader`), each with full identity budget (8192 tokens) and independent strength. |
| **Prompt Cleanliness** | Prompts require positional `<Picture 1>` ... `<Picture 8>` references and alignment boilerplate. | **Clean, natural prompts**: Just use `<Subject 1> Name...` with natural biometric look descriptions. No `<Picture N>` tags required. |
| **Cross-Host Deployment** | Raw image files must exist in ComfyUI `input/` folder on the specific rendering GPU machine. | Single portable `.safetensors` file placed in `ComfyUI/models/refmods/`. |
| **Likeness & Pose Bleed** | Prone to pose bleed and prompt collapse if reference images contain background distractions. | Pre-encoded with `--mode encode` / `--concept-type identity`, isolating facial geometry and features cleanly. |

---

## 3. How the RefMod Pipeline Works

### Step 1: Pre-Extraction (Once per Dataset)
Run `extract_mod.py` (or batch extraction script `batch_extract_all_datasets_refmod.py`) inside the ComfyUI virtual environment:

```bash
python custom_nodes/ComfyUI-MiniMaxH3Mod/extract_mod.py \
  --vae models/vae/minimax_h3_video_vae_fp16.safetensors \
  --name minimaxh3_milecyrus_v1_refmod \
  --mode encode \
  --concept-type identity \
  --resolution 1024 \
  --max-tokens 8192 \
  --image path/to/img1.png --image path/to/img2.png ... \
  --output models/refmods/
```

- **Output**: `models/refmods/minimaxh3_<name>_v1_refmod.safetensors` (~1.1 MB to 1.6 MB).

### Step 2: ComfyUI Graph Wiring

In any MiniMax-H3 workflow (T2V or C2V), insert two custom nodes between `MiniMaxH3ImageToVideo` and `BasicGuider`:

```text
[MiniMaxH3ImageToVideo] (conditioning out)
         │
         ▼
[MiniMaxH3RefModApply] ◄── [MiniMaxH3RefModsLoader] (loads mod_1..mod_8)
         │ (conditioning out)
         ▼
[BasicGuider] / [MiniMaxH3MotionContext]
```

#### Node 1: `MiniMaxH3RefModsLoader`
- **`mod_1`** .. **`mod_8`**: Select pre-extracted RefMod files (e.g. `minimaxh3_milecyrus_v1_refmod`).
- **`strength_1`** .. **`strength_8`**: Multiplier for each RefMod (default `1.0`).
- **`copies_1`** .. **`copies_8`**: Number of token copies (default `1`).

#### Node 2: `MiniMaxH3RefModApply`
- **`conditioning`**: Connected to `MiniMaxH3ImageToVideo` conditioning output `0`.
- **`mods`**: Connected to `MiniMaxH3RefModsLoader` output `0`.
- **`retention`**: `1.0` (constant retention across generation steps).
- **`curve_direction`**: `constant`.

---

## 4. Multi-Character Scene Composition

RefMods dramatically simplify multi-character scene orchestration:

```text
subject_definitions:
<Subject 1> Gillian Anderson, fair skin, auburn hair, tailored suit, expressive eyes...
<Subject 2> Miley Cyrus, fair skin, bright clear blue eyes, blonde hair...

integrated_multimodal_description:
[Shot 1] Live-action, 35mm cinematic photograph, modern lounge... <Subject 1> Gillian Anderson on screen-left, <Subject 2> Miley Cyrus on screen-right...
```

**Wiring**:
- In `MiniMaxH3RefModsLoader`:
  - `mod_1`: `minimaxh3_gilliananderson_v1_refmod` (strength `1.0`)
  - `mod_2`: `minimaxh3_milecyrus_v1_refmod` (strength `1.0`)
- The model automatically maps the identity latents to the defined visual subjects based on prompt attributes without token starvation or reference collisions.

---

## 5. Dual-RefMod Stacking: Combining Persona Likeness & Style Boosters

RefMods are not limited to standalone facial identity; they can also be stacked to combine **persona identity with modular style or texture adapters**.

### Stacking Architecture:
```text
[MiniMaxH3RefModsLoader]
  ├─ mod_1: "minimaxh3_milecyrus_v1_refmod"        (1.0) ──> Sets Facial Likeness & Identity
  └─ mod_2: "minimaxh3_cinematic_style_v1_refmod"  (0.8) ──> Enhances Cinematic Texture & Lighting
```

Because RefMods inject reference tokens into the cross-attention layers, the model cleanly separates facial identity guidance and stylistic guidance based on prompt context. In dynamic camera tracks and continuous takes, identity and lighting render with pristine fidelity and zero cross-token bleed.

---

## 6. Integrating RefMods into C2V Continuous Chains

When running continuous C2V chains (e.g., 5-clip continuous movies):
1. **Seed Clip (Clip 1)**:
   - Wire `MiniMaxH3RefModApply` $\to$ `BasicGuider` $\to$ `SamplerCustomAdvanced` $\to$ `MiniMaxH3MotionContextSaveLatent`.
2. **Continuation Clips (Clip $N > 1$)**:
   - `MiniMaxH3MotionContextLoadLatent` loads Clip $N-1$ latent.
   - `MiniMaxH3RefModApply` feeds into `MiniMaxH3MotionContext` (`conditioning` input).
   - `MiniMaxH3MotionContext` pins tail frames ($\approx 22$ frames) and passes conditioning to `BasicGuider`.
   - `MiniMaxH3MotionContextTrim` removes head overlap before saving final MP4.

This gives the ultimate combination: **RefMods ensure flawless character facial likeness and identity stability**, while **MotionContext guarantees continuous seamless camera and body locomotion across clips**.

---

## 7. Summary of Best Practices

1. **Always extract RefMods with `--mode encode`, `--concept-type identity`, `--resolution 1024`, and `--max-tokens 8192`** for human faces/personas.
2. **For new characters with multiple dataset variations (e.g. standard vs hoppe vs large), run a 10s benchmark comparison sweep** to identify and lock the best RefMod (e.g. `milecyrus` v1, `adele`).
3. **Record locked RefMods in persona look sheets** (`docs/personas/<name>.md`) and character profiles (`docs/characters/<name>.md`).
4. **Avoid Generic Beauty Buzzwords in Prompts:** Do NOT use generic beauty buzzwords like `"attractive Caucasian young woman"`, `"soft oval face"`, `"straight slender nose"`. These keywords activate MiniMax's generalized beauty prior and overwrite unique RefMod facial features. Always prompt with authentic biometric traits directly (see [`MINIMAX_H3_PROMPTING_GUIDE.md`](MINIMAX_H3_PROMPTING_GUIDE.md)).
5. **Deprecate raw image lists (`referenceImages: [...]`) in favor of RefMod loader nodes** for all new production scripts and workflows.
