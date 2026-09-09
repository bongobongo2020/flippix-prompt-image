# MiniMax-H3 RefMod Creation & Extraction Guide

_Version: 1.2.0_  
_Last Updated: September 2026_  
_Target Architecture: MiniMax-H3 (T2V & C2V) + `ComfyUI-MiniMaxH3Mod`_

---

## 1. Overview: What Is a RefMod?

A **RefMod (Reference Latent Adapter)** is a lightweight, pre-encoded `.safetensors` file (~1.1 MB – 1.6 MB) that stores compressed conditioning latents extracted through the MiniMax-H3 3D Video VAE. 

Instead of resizing, transmitting, and VAE-encoding 8+ raw image files on every single generation step (which causes VRAM spikes, latency, and restricts multi-character scaling), a RefMod is **pre-encoded once** from a dataset and loaded instantaneously into `MiniMaxH3RefModsLoader`.

### Key Specifications
| Parameter | Value / Recommendation | Description |
|---|---|---|
| **File Format** | `.safetensors` | Self-contained latent tensor + JSON header metadata |
| **File Size** | **~1.1 MB – 1.6 MB** | Highly compact, portable across GPU hosts |
| **Token Budget** | **Up to 8,192 tokens** | Automatically fits within cross-attention limit |
| **Short-Edge Resolution** | **1024px** (multiples of 32) | Optimized for MiniMax-H3 DiT patch resolution |
| **Concept Types** | `identity` (default), `style`, `general` | Isolates facial geometry and biometric features |
| **Target Directory** | `ComfyUI/models/refmods/` | Standard directory scanned by ComfyUI loader |

---

## 2. Prerequisites & Setup

To create RefMods, you need:

1. **ComfyUI Installation** (Standard git install, Portable `.7z`, or Easy-Install).
2. **`ComfyUI-MiniMaxH3Mod` Custom Node**:
   ```bash
   cd ComfyUI/custom_nodes
   git clone https://github.com/Luisacaotica/ComfyUI-MiniMaxH3Mod.git
   ```
3. **MiniMax-H3 Video VAE**:
   - Location: `ComfyUI/models/vae/minimax_h3_video_vae_fp16.safetensors`
4. **RefMods Output Folder**:
   - Location: `ComfyUI/models/refmods/` (created automatically if missing).

---

## 3. Dataset Preparation Guidelines

### Persona / Character Identity RefMods
- **Image Count**: **8 to 20 high-quality images**.
- **Angles & Views**:
  - 40% Straight-on / front portraits (clear eye contact, neutral and smiling).
  - 30% Three-quarter angle views (left and right).
  - 20% Profile angles.
  - 10% Full-body or waist-up framing.
- **Lighting & Backgrounds**: Diverse lighting and varied backgrounds so the latent extractor focuses on invariant facial geometry.
- **Resolution**: High-res originals (1024x1024 or higher).
- **Supported Formats**: `.png`, `.jpg`, `.jpeg`, `.webp`, `.bmp`.

---

## 4. Method 1: Automated Whole-Folder Extraction (`generate_refmod.py`)

`generate_refmod.py` automatically scans an entire image folder, handles aspect ratios, token allocation, and writes the output `.safetensors` directly into `ComfyUI/models/refmods/`.

### Option A: ComfyUI Easy-Install / Portable (Windows)
Portable and Easy-Install builds do **not** use a virtualenv; instead, they have a `python_embeded` folder. Run via Command Prompt or PowerShell:

```cmd
:: 1. Navigate to your ComfyUI directory
cd /d "I:\ComfyUI Video New\ComfyUI-Easy-Install\ComfyUI"

:: 2. Run with embedded python (point to the script and your dataset folder)
python_embeded\python.exe scripts\generate_refmod.py "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512"
```

If running from another directory, use full paths:
```cmd
"I:\ComfyUI Video New\ComfyUI-Easy-Install\python_embeded\python.exe" "C:\path\to\generate_refmod.py" "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512" --comfy-dir "I:\ComfyUI Video New\ComfyUI-Easy-Install\ComfyUI"
```

### Option B: Standard ComfyUI (Git Install / Virtualenv)
If you installed ComfyUI with a standard Python `venv`:

```bash
# 1. Activate ComfyUI environment
cd C:\Development\ComfyUI
.\venv\Scripts\activate

# 2. Run script
python generate_refmod.py "C:\Datasets\GabrielleAplin"

# Optional flags:
# Preview discovered files without encoding (Dry run):
python generate_refmod.py "C:\Datasets\GabrielleAplin" --dry-run

# Custom RefMod name:
python generate_refmod.py "C:\Datasets\GabrielleAplin" --name minimaxh3_gabrielle_aplin_refmod
```

### CLI Options Reference
| Flag | Default | Description |
|---|---|---|
| `--comfy-dir <path>` | Auto-detected | Path to ComfyUI root |
| `--name <string>` | Auto-derived | Output RefMod name (e.g. `minimaxh3_gabrielle_aplin_v1_refmod`) |
| `--resolution <int>` | `1024` | Short-edge resolution cap |
| `--max-tokens <int>` | `8192` | Maximum token budget |
| `--concept-type <type>` | `identity` | `identity`, `style`, or `general` |
| `--dry-run` | `False` | Preview images without encoding |

---

## 5. Method 2: Direct Extraction with `extract_mod.py`

If you want to use the native `extract_mod.py` script provided inside `custom_nodes/ComfyUI-MiniMaxH3Mod/`:

### Option A: ComfyUI Easy-Install / Portable (Windows)
Open Command Prompt (`cmd`) in your ComfyUI root:

```cmd
cd /d "I:\ComfyUI Video New\ComfyUI-Easy-Install\ComfyUI"

python_embeded\python.exe custom_nodes\ComfyUI-MiniMaxH3Mod\extract_mod.py ^
  --vae models\vae\minimax_h3_video_vae_fp16.safetensors ^
  --name minimaxh3_gabrielle_aplin_refmod ^
  --mode encode ^
  --concept-type identity ^
  --resolution 1024 ^
  --max-tokens 8192 ^
  --output models\refmods ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\01.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\02.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\03.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\04.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\05.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\06.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\07.png" ^
  --image "E:\Images\LoraTraining\MiniMax\0Done\GabrielleAplin\512\08.png"
```

### Option B: Standard ComfyUI (with venv)
```bash
cd C:\Development\ComfyUI
.\venv\Scripts\activate

python custom_nodes/ComfyUI-MiniMaxH3Mod/extract_mod.py \
  --vae models/vae/minimax_h3_video_vae_fp16.safetensors \
  --name minimaxh3_gabrielle_aplin_refmod \
  --mode encode \
  --concept-type identity \
  --resolution 1024 \
  --max-tokens 8192 \
  --output models/refmods \
  --image "C:\Datasets\GabrielleAplin\01.png" \
  --image "C:\Datasets\GabrielleAplin\02.png" \
  --image "C:\Datasets\GabrielleAplin\03.png" \
  --image "C:\Datasets\GabrielleAplin\04.png" \
  --image "C:\Datasets\GabrielleAplin\05.png" \
  --image "C:\Datasets\GabrielleAplin\06.png" \
  --image "C:\Datasets\GabrielleAplin\07.png" \
  --image "C:\Datasets\GabrielleAplin\08.png"
```

---

## 6. Method 3: In-Memory Batch Extraction (Python Script)

For processing dozens of dataset folders in bulk, use this script to load the VAE **once** into VRAM and encode everything in a loop:

```python
import os
import sys
import torch

COMFY_ROOT = r"I:\ComfyUI Video New\ComfyUI-Easy-Install\ComfyUI"
CUSTOM_NODE_DIR = os.path.join(COMFY_ROOT, "custom_nodes", "ComfyUI-MiniMaxH3Mod")
sys.path.insert(0, CUSTOM_NODE_DIR)
sys.path.insert(0, COMFY_ROOT)

import comfy.model_management
import comfy.sd
import comfy.utils
from common import load_image_file
from core import H3RefMod, fit_token_budget

DATASETS_ROOT = r"E:\Images\LoraTraining\MiniMax\0Done"
VAE_PATH = os.path.join(COMFY_ROOT, "models", "vae", "minimax_h3_video_vae_fp16.safetensors")
OUTPUT_DIR = os.path.join(COMFY_ROOT, "models", "refmods")

RESOLUTION = 1024
MAX_TOKENS = 8192
device = comfy.model_management.get_torch_device()

# 1. Load VAE model ONCE into GPU memory
sd, metadata = comfy.utils.load_torch_file(VAE_PATH, return_metadata=True)
vae = comfy.sd.VAE(sd=sd, metadata=metadata, device=device)

# 2. Iterate through dataset folders
for entry in sorted(os.listdir(DATASETS_ROOT)):
    folder = os.path.join(DATASETS_ROOT, entry)
    if not os.path.isdir(folder):
        continue
    
    images = [os.path.join(folder, f) for f in sorted(os.listdir(folder)) if f.lower().endswith(('.png', '.jpg', '.jpeg', '.webp'))]
    if not images:
        continue

    mod_name = f"minimaxh3_{entry}_v1_refmod"
    out_file = os.path.join(OUTPUT_DIR, f"{mod_name}.safetensors")
    if os.path.exists(out_file):
        continue

    frames = []
    shapes = []
    # Load and encode images
    for img_path in images:
        src = load_image_file(img_path, RESOLUTION * 2)
        samples = src[..., :3].movedim(-1, 1)
        samples = comfy.utils.common_upscale(samples, 1024, 1024, "lanczos", "disabled").movedim(1, -1)
        with torch.no_grad():
            z = vae.encode(samples.to(device)).float().cpu()
        frames.append(z.to(torch.float16))
        shapes.append(f"{z.shape[2]}x{z.shape[3]}x{z.shape[4]}")

    # Concatenate temporal latent frames and fit token budget
    latent = torch.cat(frames, dim=2)
    latent = fit_token_budget(latent, MAX_TOKENS, mod_name)

    # Instantiate RefMod container and save .safetensors
    mod = H3RefMod(
        name=mod_name,
        kind="video" if latent.shape[2] > 1 else "image",
        latent=latent,
        latent_h=latent.shape[3],
        latent_w=latent.shape[4],
        latent_t=latent.shape[2],
        mode="encode",
        source="stack",
        source_shape=" +".join(shapes),
        pool=f"full-res {latent.shape[4]*16}x{latent.shape[3]*16}px",
        optimize_steps=0,
        tags=[f"{len(frames)} img"],
        description=f"RefMod for {entry}",
        concept_type="identity",
    )
    mod.save(os.path.join(OUTPUT_DIR, mod_name))
    print(f"Generated {mod_name} ({len(frames)} images, {mod.token_count} tokens)")
```

---

## 7. Using RefMods in ComfyUI

In your ComfyUI workflow (`workflow_api_minimaxh3_t2v.json` or `workflow_api_minimaxh3_c2v.json`):
- **Node `MiniMaxH3RefModsLoader` (Node 200)**:
  - `mod_1`: Select your created RefMod (e.g. `minimaxh3_gabrielle_aplin_refmod`)
  - `strength_1`: `1.0`
  - `copies_1`: `1` (or `2` for stronger face lock)
- **Node `MiniMaxH3RefModApply` (Node 201)**:
  - Connects `REFMODS` output from Node 200 into the DiT conditioning chain.

---

## 8. Troubleshooting & FAQ

### Q: Why do I get `npm error ENOENT: no such file or directory, open package.json`?
**A:** `npm run` commands require a Node.js development environment. For standard ComfyUI setups, execute Python directly using `python generate_refmod.py <folder>` or `python_embeded\python.exe generate_refmod.py <folder>`.

### Q: Where is `venv` in ComfyUI Easy-Install / Portable?
**A:** Portable ComfyUI packages do not have a `venv/` folder. Instead, Python is bundled inside `python_embeded/python.exe`. Run commands using `python_embeded\python.exe <script.py>`.

### Q: What should the resulting file size be?
**A:** A valid RefMod `.safetensors` file is **~1.1 MB to 1.6 MB**. If it is smaller than 100 KB, the encoding failed or no valid images were found.
