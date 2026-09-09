# MiniMax-H3 C2V (Clip-to-Video) Continuous Chain Master Guide

_Version: 1.3.0_  
_Last Updated: September 2026_  
_Target Workflow: `workflow_api_minimaxh3_ref_c2v.json` (`MINIMAXH3_REF_C2V`), `workflow_api_minimaxh3_c2v.json`, and `MiniMaxH3RefModsLoader` + `MiniMaxH3RefModApply`_

> **Important Update (September 2026)**: For character and identity conditioning in C2V chains, **RefMods (`.safetensors` via `MiniMaxH3RefModsLoader` / `MiniMaxH3RefModApply`) are now the preferred standard over raw 8-image reference arrays (`referenceImages: [...]`)**. RefMods eliminate live VAE encoding overhead, allow scaling up to 8 distinct personas per shot, prevent VRAM fragmentation, and produce significantly higher facial fidelity. See [`MINIMAX_REFMODS_VS_REFERENCE_IMAGES.md`](MINIMAX_REFMODS_VS_REFERENCE_IMAGES.md).

This guide documents the battle-tested engineering practices, prompting rules, and troubleshooting protocols for generating **unbroken, continuous multi-clip long takes** (15s to 3+ minutes) using MiniMax-H3 Clip-to-Video (C2V).

---

## 1. How C2V Works in Stabilis / ComfyUI

Unlike standard Text-to-Video (T2V) which renders isolated clips, C2V creates an unbroken cinematic experience by carrying over the **audio and video latent motion context** from one clip into the next.

1. **Latent Storage**:
   - Each clip render automatically saves an audio+video latent `.safetensors` file into `<comfy output>/<chainFolder>/clip_<clipIndex>.safetensors` (e.g. `C:\Development\ComfyUI\output\h3_context\milecyrus_c2v_paris_01\clip_00014.safetensors`).
2. **Motion-Context Pinning**:
   - The subsequent clip loads the prior clip's latent and pins its tail frames (`contextLengthFrames`: **22** or **56**) onto the start of its timeline as non-denoised conditioning rows.
   - The pinned head is trimmed during delivery so the visible output begins exactly where the previous take ended.
3. **Sequential Execution Requirement**:
   - Clip \(N+1\) **cannot be queued simultaneously** with Clip \(N\). You must wait until Clip \(N\) finishes rendering and its latent is written to disk before submitting Clip \(N+1\).

---

## 2. API Submission Architecture

When submitting via the backend `/api/v1/comfy/c2v` endpoint:

```json
{
  "workflowId": "workflow_api_minimaxh3_ref_c2v.json",
  "workflowType": "MINIMAXH3_REF_C2V",
  "prompt": "...",
  "clipIndex": 15,
  "continueFromClipIndex": 14,
  "contextLengthFrames": 22,
  "chainFolder": "h3_context/milecyrus_c2v_paris_01",
  "durationSeconds": 16,
  "aspectRatio": "16:9",
  "referenceImages": [
    "datasets/milecyrus/20181120_163930.png",
    "datasets/milecyrus/DSCN5203 kopia.png",
    ...
  ],
  "loras": [
    { "lora": "MinimaxH3\\loras\\minimaxh3_milecyrus_v1_large-000100.safetensors", "strength": 0.15 },
    { "lora": "MinimaxH3\\loras\\minimaxh3_milecyrus_v2.safetensors", "strength": 0.15 },
    { "lora": "MinimaxH3\\loras\\minimax_h3_milecyrus_1500.safetensors", "strength": 0.20 }
  ],
  "seed": 184729104
}
```

### Critical Submission Rules:
- **`chainFolder` Path**: Must be prefixed with `h3_context/` (e.g. `h3_context/milecyrus_c2v_paris_01`).
- **`clipIndex` vs `continueFromClipIndex`**: For seed clip (Clip 1), omit `continueFromClipIndex`. For Clip \(N\), set `clipIndex: N` and `continueFromClipIndex: N-1`.
- **`contextLengthFrames` Options**: Must be one of `[5, 22, 39, 56]`. **22 frames (~0.92s)** is the recommended golden balance. Use **56 frames (~2.33s)** when deep posture/motion anchoring is required.

---

## 3. LoRA Stacking & Reference Image Calibration

When conditioning C2V clips with both **Reference Images (8 images)** and **LoRA Models**, exceeding threshold strengths causes **prompt collapse and baked-in poses** (the model ignores prompt instructions and repeats static dataset poses or produces chaotic movements).

### Calibrated Strength Ceilings:
| Configuration | Reference Images | Starting Combined | Confirmed / Notes |
| :--- | :--- | :--- | :--- |
| **Single LoRA** | 8 Reference Images | **0.60 – 0.70** | Gillian Anderson (gilliananderson) preset #1 @ 0.70 |
| **2-LoRA Stack** | 8 Reference Images | **0.80** (2× 0.40) | **Start here.** Felicia Day (feliciaday) stays 0.40/0.40. Natalia A/B won at **0.45/0.45 (0.90)** on `MiniMax_H3_01962_.mp4` vs `01957` — no collapse. Collapse still appears around **1.05+**. |
| **3-LoRA Stack** | 8 Reference Images | **0.50** | 0.15 / 0.15 / 0.20 (Miley Cyrus (milecyrus) preset #2). Do not copy Natalia's 0.90 onto a 3-stack. |

> **Pro-Tip**: Rotate 8-ref sets during lock-in. After refs are locked, A/B **+0.05 per LoRA** on one C2V seed before committing the character preset (Rule 45).

---

## 4. Camera Framing & Locomotion Rules

### 1. Avoid Distant / Extreme Wide Shots (Facial Degradation)
MiniMax-H3 significantly degrades facial likeness, texture, and lip/facial animation when subjects are framed from afar.
- **Always Frame Medium to Close**: Use Medium Shots (waist-up), Medium Close-ups (chest-up), and intimate Close-ups.
- **Frontal & 3/4 Camera Angles**: Ensure key personas are oriented toward the camera rather than viewed strictly in profile, which obscures biometric features.
- **Lively Interaction Directives**: When multiple characters share the frame, prompt active micro-actions (laughing, animated hand gestures, head tilting, glancing at co-stars) to prevent characters standing in static/idle poses.
- **Dynamic Tracking for Movement**: When a character walks across a room, do **not** pull out to an extreme wide shot. Use a **smooth tracking camera** that follows alongside at medium distance.

### 2. Multi-Subject & Hybrid Stylization in Single Frame
MiniMax-H3 seamlessly supports mixing different aesthetic representations across multiple RefMods in the same shot (e.g. real human Kamil + 2D anime Motoko Kusanagi + 3D Arcane Jinx). Define distinct art style descriptions in `subject_definitions` for each subject while keeping the environment photoreal.

### 3. Reference Audio & Music Video Chaining
MiniMax-H3 supports lip-synced singing and speech across C2V chains by supplying aligned audio slices. See [`MINIMAX_AUDIO_SYNC_AND_C2V_MUSIC_VIDEOS.md`](MINIMAX_AUDIO_SYNC_AND_C2V_MUSIC_VIDEOS.md) for full mathematical overlap rules and workflow configuration.

### 4. Gradual Physical Transitions (One Beat per Clip)
Drastic physical changes in a single clip (e.g. going from lying flat on a bed to walking into another room) will tear the motion latent and create body warping.
- **Clip A**: Lying down $\to$ Prop up on elbow.
- **Clip B**: Propped up $\to$ Sit up and swing legs to floor.
- **Clip C**: Sit $\to$ Rise to feet and begin walking.
- **Clip D**: Walking $\to$ Arrive at destination (island, sofa, vanity) and engage with a prop.

### 3. Mirror Reflection Transitions
If Clip \(N\) ends looking into a mirror reflection:
- Open Clip \(N+1\) explicitly acknowledging the mirror: `Opens framed on the gilded vanity mirror showing the clear reflection of <Subject 1>...`
- Describe the transition turning into real camera space: `She catches the camera's gaze in the glass with a smile, then smoothly turns around on the stool to face the camera directly. The camera glides around to reframe the real <Subject 1>...`

---

## 5. Latent Rescue & Grid-Aligned Tail Trimming

If a 15-second clip renders beautifully for 12–13 seconds but the final 2 seconds suffer a glitch or face artifact: **do not discard the clip or regenerate the chain**. You can trim the latent to a clean step and continue seamlessly.

### The VAE Latent Grid Formula
The MiniMax-H3 video VAE latent steps follow the formula:
$$\text{steps} = 5 \times g + 2$$
$$\text{pixel\_frames} = \sum_{k=0}^{\text{steps}-1} \text{FRAME\_PER\_TOKEN}[k \bmod 5] \quad \text{where } \text{FRAME\_PER\_TOKEN} = (1, 4, 4, 4, 4)$$

- **At $g=17$**: $87\text{ latent steps} = 294\text{ frames} = \mathbf{12.25\text{ seconds}}$
- **At $g=18$**: $92\text{ latent steps} = 311\text{ frames} = \mathbf{12.96\text{ seconds}}$

### Rescue Python Script (`trim_clip_latent.py`)
```python
import os
import shutil
import safetensors.torch as st

latent_path = r"C:\Development\ComfyUI\output\h3_context\<chainFolder>\clip_00014.safetensors"
backup_path = r"C:\Development\ComfyUI\output\h3_context\<chainFolder>\clip_00014_backup.safetensors"

if not os.path.exists(backup_path):
    shutil.copyfile(latent_path, backup_path)

d = st.load_file(latent_path)
video, audio = d["video"], d["audio"]

# Slice to g=18 (92 steps = 311 frames = 12.96s)
steps = 92
frames = 311
audio_steps = round(frames * 5.0 / 3.0) # 518

v_trimmed = video[:, :, :steps, :, :].clone()
a_trimmed = audio[:, :, :, :audio_steps].clone()

st.save_file(
    {"video": v_trimmed, "audio": a_trimmed},
    latent_path,
    metadata={"format": "h3_motion_context_av_v1"}
)
print(f"Trimmed latent saved! Video shape: {v_trimmed.shape}, Audio shape: {a_trimmed.shape}")
```

### Trim Corresponding MP4
```bash
ffmpeg -i MiniMax_H3_01883_.mp4 -t 00:00:12.958 -c copy -y MiniMax_H3_01883_trimmed.mp4
```

Now submit Clip 15 with `continueFromClipIndex: 14` and `contextLengthFrames: 22`. It will anchor cleanly onto the perfect 12.96s point with zero artifacts!

---

## 6. Concatenating Completed C2V Chains

To join all generated clips into a seamless master movie without re-encoding:

```javascript
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

const clips = [
  'C:\\Development\\ComfyUI\\output\\video\\MiniMax_H3_01865_.mp4',
  'C:\\Development\\ComfyUI\\output\\video\\MiniMax_H3_01866_.mp4',
  'C:\\Development\\ComfyUI\\output\\video\\MiniMax_H3_01867_.mp4',
  'C:\\Development\\ComfyUI\\output\\video\\MiniMax_H3_01873_.mp4',
  // ... all clips in sequence ...
];

const listPath = path.join(__dirname, 'concat_list.txt');
fs.writeFileSync(listPath, clips.map(p => `file '${p.replace(/\\/g, '/')}'`).join('\n'));

execSync(`ffmpeg -f concat -safe 0 -i "${listPath}" -c copy -y "C:\\Development\\ComfyUI\\output\\video\\Full_Continuous_Movie.mp4"`);
fs.unlinkSync(listPath);
```

---

## 7. Advanced Production Protocols & Audio/Visual Artifact Prevention

Learned and battle-tested during continuous movie production (`movie0010` / `movie0005`):

### 1. Splitting Physical Entrance / Reveal from Dialogue
When a character emerges from an entrance, doorway, or vehicle into an active shot:
- **Never combine emergence and speech into one clip.** MiniMax-H3 will blend the voice and face of the emerging character with whoever is already on screen.
- **Clip A (Silent Emerge):** Dedicated clip where the established character steps aside and the new character emerges and plants feet. Both carry strict silence mandates (`mouths closed, completely silent`).
- **Clip B (Dialogue on Established Face):** Once the new character is physically in frame on the last frame of Clip A, C2V into their dialogue clip with `<Subject 1>` as the active speaker.

### 2. Locked Same-Speaker Continuity & Background Retention
When holding on the same speaker across multiple analytical/dialogue clips in one location (e.g. Data at ops console):
- **Prevent background hallucination / cuts**: Mandate in Shot 1:
  `The camera is locked off and continues from that exact framing without cutting, without reframing, and without any change of location. This is one continuous take, not a freeze. Do not show a different room. Do not change the background.`
- Restate the exact background anchors verbatim (`the same glowing LCARS console panels beside and behind him in the same positions`).

### 3. Suppressing Top-of-Clip Vocal Gibberish in Continuations
The model inherits active or resting mouth latents from the tail of the previous clip and may invent unscripted mumbles at the very start:
- Explicitly write: `He has finished his previous report. His mouth is closed, completely silent, speaking no dialogue. No dialogue, narration or voice of any kind is heard during this opening beat, and no lips move.`
- Delay `<d>` dialogue start to `00:01.000`–`00:01.250` with `now that his mouth has been closed and only now, speaking in...`.

### 4. Preventing Object / Vehicle Duplication
Mentioning background vehicles/props loosely in continuation prompts can spawn duplicates:
- Define as `the same single [object] already in the previous shot` in `subject_definitions:`.
- Mandate: `Exactly one [object]. Never two [objects]. Do not show any interior through the doors.`

### 5. Same-Uniform Distinction (High-Contrast Anchors)
Characters sharing the same division/uniform color (e.g. Geordi and Worf in yellow ops):
- Pin unique prosthetics/accessories in every prompt: Geordi (`silver metallic VISOR, smooth forehead`) vs Worf (`prominent ridged Klingon forehead, warrior baldric sash`). Never refer to them generically as "two officers."

### 6. Video-Driven Continuity (`continueFromVideo`)
In production scripts, prefer passing `continueFromVideo: "MiniMax_H3_C2V_XXXXX_.mp4"` over manual slot math. The backend derives the chain folder, previous clip index, and motion context directly from the validated video metadata.

### 7. First Continuity Chain for a New Custom Character (Natalia, August 2026)
Do **not** copy the Miley Cyrus full-day walk (terrace → kitchen → living → lounge) as the intro.

1. Lock 8 refs **and** both LoRA strengths from a **15-clip R2V mix-and-match** (0.40–0.50 each). See `CHARACTER_LOCKIN_DRILL.md`.
2. Seed a REF_C2V chain in a dedicated `h3_context/<name>_c2v_sequence_01` folder.
3. Stay on **one set**, MCU, **one beat per clip**.
4. After clip 1, A/B LoRA +0.05 on that seed only. Natalia: 0.45/0.45 (`01962`) beat 0.40/0.40 (`01957`).
5. Only then continue the chain or start a longer day series.


