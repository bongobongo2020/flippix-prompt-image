# MiniMax-H3 Video Prompting Guide & Best Practices

_Version: 2.10.0_  
_Last Updated: August 2026_  
_Target Model: MiniMax-H3 (T2VA / I2VA / FL2VA / L2VA / R2V / C2V / REF_C2V)_

This document is the **authoritative master guide** for understanding and executing our MiniMax-H3 video prompting and movie production workflow. It integrates the official MiniMax-H3 specification (`VIDEO_PROMPT_WRITING_GUIDE_base_en.md`) with our production-tested bug fixes and workflow conventions.

---

## 1. Core Prompt Architecture & Alignment Instructions

A complete MiniMax-H3 prompt consists of optional Task Alignment Instructions, followed by three core plain-text sections:

> **T2V only.** The `already speaking, with no silent establishing beat` opener below applies to independent T2V clips. In a C2V chain the opening frames are inherited from the previous clip — using this opener there makes the new speaker's line come out of the previous person's face. See **Rule 29** for the required handoff.

```text
[Optional Task Alignment Instruction Header]

subject_definitions:
<Subject 1> [Character 1 Name] (played by [Actor Name]) from [Show Name]
<Subject 2> [Character 2 Name] (played by [Actor Name]) from [Show Name]

integrated_multimodal_description:
[Shot 1] [Visual style, lighting, setting]. A single medium close-up of <Subject 1> [Character 1] (S1) alone in frame, in [attire/visual features]. <Subject 2> is not in frame. The shot opens with [Character 1] already speaking, with no silent establishing beat. The camera holds a static shot as <Subject 1> [Character 1] (S1) says: <d>[English in [Character 1]'s voice from [Show Name]] Dialogue text.</d>

[Shot 2] At 00:05.500, the shot cuts to a single medium close-up of <Subject 2> [Character 2] (S2) alone in frame. <Subject 1> is not in frame. <Subject 2> [Character 2] (S2) remains completely silent with his mouth closed and lips sealed, speaking no dialogue. The camera pushes in with small amplitude at slow speed as <Subject 2> [Character 2] (S2) [Action].

overall_soundscape:
[Room tone, physical sounds, ambient environmental audio, audience laughter or silence]

non_diegetic_music:
N/A
```

### Task Alignment Headers (I2VA / FL2VA / L2VA)

For tasks involving reference images or keyframes, place the exact required header as the very first line of the prompt, followed by a blank line before `subject_definitions:`:

1. **T2VA (Text-to-Video-Audio)**: No alignment header. Starts directly with `subject_definitions:`.
2. **I2VA (Image-to-Video-Audio - Starts from Picture 1)**:
   ```text
   For the target video, at 0.00 seconds into the target video, <Picture 1> (from [Shot 1]) is fully referenced.
   ```
3. **FL2VA (First-and-Last-Frame - Interpolates between Picture 1 and Picture 2)**:
   ```text
   How the reference pictures align with the target video — Picture 1 (from Shot 1) aligns with the 0.00-second mark of the target video; Picture 2 (from Shot N) aligns with the S.SS-second mark of the target video.
   ```
4. **L2VA (Last-Frame - Lands on Picture 1 at video end)**:
   ```text
   How the reference pictures align with the target video — <Picture 1> (from [Shot N]) aligns with the S.SS-second mark of the target video.
   ```

---

## 2. Key Rules & Critical Issue Pinpoints

Based on extensive generation testing and official MiniMax-H3 specifications, follow these strict rules to prevent AI model bugs and artifacts:

### Issue 1: Non-Dialogue Character Mumbling & Vocal Gibberish
- **THE ISSUE**: In scenes without spoken dialogue (or when a character is on screen while someone else speaks), MiniMax-H3 often causes characters to move their lips unnaturally or produce mumbling audio artifacts.
- **THE SOLUTION**: Explicitly state in the prompt description what the character is doing physically, and explicitly add a non-speaking silence mandate:
  - `Both characters remain completely silent with their mouths closed, speaking no dialogue.` or
  - `<Subject 2> Dexter Morgan (S2) remains completely silent with his mouth closed, speaking no dialogue.`

### Issue 2: Character Blending, Voice Swapping & Facial Merging
- **THE ISSUE**: When multiple characters are on screen together, MiniMax-H3 can blend facial features, assign dialogue to the wrong person, or swap character voices.
- **THE SOLUTION**:
  1. **Actor Pinpointing & Physical Anchoring**: Always include `(played by Actor Name)` in `subject_definitions:` and mention explicit physical traits (e.g. `dark short hair, sharp jawline, wearing wire-rimmed glasses`) in shot descriptions. This locks in the face and voice clone pipeline.
  2. **Explicit Subject & Show Binding**: Define characters at the top in `subject_definitions:` and tag them on first mention (e.g. `<Subject 1> Fox Mulder (played by David Duchovny)`).
  3. **Explicit Dialogue Voice Tags**: Always enclose spoken text in `<d>[Language in Character's voice from Show] Dialogue text</d>` (e.g. `<d>[English in Fox Mulder's voice from The X-Files] Open the door!</d>`).
  4. **Speaker IDs `(Sx)` vs Subject Tags `<Subject N>`**:
     - `<Subject N>` (e.g. `<Subject 1>`, `<Subject 2>`) is the **Visual Subject / Asset Label** defined in `subject_definitions:`.
     - `(Sx)` (e.g. `(S1)`, `(S2)`) is the **Speaker ID / Vocal Source Tag** assigned sequentially in **chronological order of actual vocal events** in the video timeline.
     - **CRITICAL**: `S1` does **NOT** equal `Subject 1`! The first vocal source heard in the video timeline is `(S1)`. If `<Subject 2>` (e.g. Dana Scully) speaks first in Shot 1, she is assigned speaker ID `(S1)`, written as `<Subject 2> Dana Scully (S1) says: ...`.
     - **Non-speakers**: Visible characters who never speak or vocalize do **NOT** receive an `(Sx)` speaker ID.
     - **Stability**: Once assigned, `(Sx)` stays permanently attached to that vocal source for the entire video timeline/clip.
     - Use compound IDs like `(S1,S2)` for simultaneous speaking.
  5. **Single-Subject Shot Cuts & Off-Frame Declarations**: Cut to single-character frames (`a single medium close-up of <Subject 1> alone in frame`) and explicitly declare `<Subject 2> and <Subject 3> are not in frame`. This prevents unwanted background characters or facial blending. This is the **default, safest** technique for dialogue-heavy exchanges — but it is not mandatory for every single shot in a production; see Rule 16 for when and how to safely share a frame between multiple subjects.
  6. **Hidden Character Isolation**: If a character is meant to be behind a closed door or out of the room, explicitly state: `<Subject 3> Dexter Morgan (S3) is inside behind the locked door and strictly hidden from view, not in frame.`

### Issue 3: Obscured or Missed Physical Actions & Key Props
- **THE ISSUE**: Key physical actions (e.g. unhooking a keycard, picking up a vial, hiding an object) are skipped or rendered unclearly if the shot is zoomed out too far.
- **THE SOLUTION**:
  1. Frame the prop or action explicitly with a close camera angle (e.g. `[Shot 1] A close medium shot focused on the waist area...`).
  2. Describe the physical motion step-by-step (e.g. `<Subject 1> Malcolm (S1) unhooks the plastic keycard from Dwight's belt loop with his fingers...`).

### Rule 4: Advanced Dialogue Syntax (`<scenetrans>`, `<cutoff>`, Voiceovers & Text)
- **Voiceover Syntax**: For off-screen voiceovers, use `says in an off-screen voiceover:` and append `while his lips remain completely closed.` immediately after the `<d>` block:
  `The character (S1) says in an off-screen voiceover: <d>[English in House's voice from House M.D.] Everybody lies.</d> while his lips remain completely closed.`
- **Dialogue Across Cuts**: When speech continues across a cut, use `<scenetrans>` at connecting points in both shots and describe continuity:
  `[Shot 1] ...says: <d>[English] I knew that <scenetrans></d>`
  `[Shot 2] At 00:04.000, ...while his line carries over uninterrupted: <d><scenetrans> you were hiding something.</d>`
- **Truncated Speech**: Use `<cutoff>` when dialogue is cut off by the end of the clip e.g. `<d>[English] I told you not to touch that—<cutoff></d>`.
- **Visible On-Screen Text**: Enclose visible labels, signs, or subtitles in English double quotation marks e.g. `A red neon sign reading "QUARANTINE" glows above the door.`

### Rule 5: Camera Motion Vocabulary
Integrate camera motion naturally into shot descriptions using `[Motion Type] + [Amplitude] + [Speed]`:
- **Available Motion Types**: `Zoom In / Zoom Out`, `Push In / Pull Out`, `Pan Left / Pan Right`, `Truck Left / Truck Right`, `Tilt Up / Tilt Down`, `Pedestal Up / Pedestal Down`, `Arc Shot`, `Tracking Shot`, `Static Shot`, `Shake Slightly / Shake Strongly`, `POV`, `Roll Clockwise / Roll Counterclockwise`.
- **Amplitude**: `with small amplitude`, `with large amplitude` (omit medium).
- **Speed**: `at slow speed`, `at fast speed` (omit normal).
- **Usage**: `The camera pushes in with small amplitude at slow speed toward the glass beaker.`

### Rule 6: NO Markdown Bolding in Section Headers
- **CRITICAL**: Never use `**` around section titles (e.g. `**subject_definitions:**`).
- **WHY**: Bolding inserts asterisks into plain-text pastes in ComfyUI or API calls, causing parsing errors in the model's text encoder.
- **DO THIS**: Output section headers as plain text with colons (`subject_definitions:`, `integrated_multimodal_description:`, `overall_soundscape:`, `non_diegetic_music:`).

### Rule 7: Clean Newline Separation & Shot Formatting
- Insert a blank line between sections and between individual shots.
- **[Shot 1]**: NEVER include a timecode on Shot 1 (e.g. `[Shot 1] Live-action, cinematic...`).
- **[Shot 2+]**: MUST include explicit cut timecodes (e.g. `[Shot 2] At 00:05.500, the shot cuts to...`).

### Rule 8: Soundscape vs Non-Diegetic Music Separation
- **`overall_soundscape:`**: 1-4 sentences summarizing ambient room tone, physical action sounds (footsteps, glass clinking, paper rustle), and non-verbal human sounds (coughing, laughing, breathing).
- **`non_diegetic_music:`**: Strictly for audience-only background musical score (instrumentation, tempo, dynamics). Use `N/A` if no background score. Do NOT place dialogue or room tone here.
- **NEVER name dialogue or a character's speaking voice in `overall_soundscape:`** (e.g. `and dialogue`, `and Saul's fast-talking voice`, `and tense dramatic voice delivery`). The speech is already fully specified by the `<d>` tags; restating it here hands the audio encoder a second, untagged instruction to generate voice and is a known source of double-voice and mumbling artifacts. Non-verbal vocal sounds (`a quiet laugh`, `a sharp exhale`) are fine.
- **NEVER put a musical score in `overall_soundscape:`.** Orchestral stings, synth drops and crescendos belong in `non_diegetic_music:`, even on title cards and end credits where they are the only audio.
- **For title cards, credit cards and any shot with no people**, state the absence explicitly in the description (`No people are in frame at any point, and no spoken dialogue, narration or voiceover occurs.`) and in the soundscape (`No voices and no speech of any kind.`). Without it the model tends to invent a narrator.

### Rule 9: Eliminating Long Silent Stares & Dead Air at Clip Ends
- **THE ISSUE**: Placing a non-speaking 3–4 second reaction beat at the end of a 15-second clip where a character just stands silent and stares off-camera or smiles looks like a frozen, robotic AI glitch. It destroys narrative flow and pacing.
- **THE SOLUTION**:
  1. **FILL CLIP DURATION WITH DIALOGUE & ACTION**: Ensure the entire 15-second duration is filled with active dialogue, multi-beat exchanges, or continuous physical motion.
  2. **ACTIVE PHYSICAL ACTIONS INSTEAD OF STARES**: If dialogue finishes 1–2 seconds before clip end, fill those final seconds with **concrete physical movement** (e.g., turning and walking away, closing a briefcase, stepping out of frame, raising a device to scan, slinging a strap over shoulder, getting into a vehicle) rather than a static gaze into or off camera.
  3. **PREFERRED SHOT PACING STRUCTURES**:
     - *Structure A (Full 2-Shot Back-and-Forth Dialogue)*: [Shot 1] S1 speaks (00:00–00:07) -> [Shot 2] S2 speaks and acts up to clip end (00:07–00:15).
     - *Structure B (Dialogue + Physical Exit/Movement)*: [Shot 1] S1 speaks (00:00–00:06) -> [Shot 2] S2 speaks (00:06–00:11) -> [Shot 2] S2 actively pockets a prop, turns, and walks toward a door/car (00:11–00:15).
     - *Structure C (Continuous 3-Beat Dialogue Exchange)*: [Shot 1] S1 speaks (00:00–00:05) -> [Shot 2] S2 replies (00:05–00:10) -> [Shot 3] S1 delivers a quick closing punchline or counter-line while stepping forward (00:10–00:15).
- **Rule on Silence Mandates**: Use non-speaking silence mandates (`remains completely silent with his mouth closed`) *only* during another character's spoken shot or brief physical beats—NEVER as a standalone multi-second static staring shot at the end of a clip.

### Rule 10: `subject_definitions:` for Shots With No People
- Title cards, credit cards and pure-insert shots have no subjects. Write `subject_definitions:` followed by `N/A` on its own line — keep the section header present so the four-section structure stays intact for the text encoder.

### Rule 11: Setting Variety & Dynamic Staging (Avoiding Static Monologues)
- **LOCATION & SETTING VARIETY**: While consistency across scenes is critical, multi-scene productions must incorporate **diverse locations and sub-environments**. Avoid keeping characters in a single static spot (e.g. next to the dumpster in the parking lot) for many consecutive scenes.
  - *Setting Rotation Rule*: It is acceptable to spend 2–3 scenes in one area (e.g. near the dumpster) and return to it later, but the film MUST move between distinct environments (e.g. parking lot near dumpster, parking lot near Impala trunk, building entrance, reception area interior, TARDIS interior, executive office, breakroom, hallway, etc.).
- **LIVELY PHYSICAL STAGING & ACTION**: Characters must **NEVER** just stand static and deliver monologues straight into camera. Every scene must feature active physical staging and character interaction:
  - *Physical Hand Props & Actions*: Uncorking a flask, inspecting a file, adjusting sunglasses, unlatching a briefcase, tapping a screen, pacing, gesturing, stepping into frame, offering a card/badge, handling tools.
  - *Environmental Interaction*: Leaning against a vehicle hood, opening a door, crouching near an object, walking across frame, adjusting clothing/lapels, slinging a strap over shoulder.
  - *Dynamic Camera Framing*: Pushing in, tracking movement, arc shots, tilting down to hand props, alternating between medium close-ups and tight close-ups to maintain visual energy.

### Rule 12: Cinematic Narrative & Visual Flow (Avoiding "Spliced Clip" Syndrome)
- **THE ISSUE**: Rapidly rotating character pairs every 15 seconds with identical 3-beat single close-ups makes a movie feel like a collection of isolated TikTok/Reels clips stitched together rather than a cohesive film.
- **THE SOLUTION & FLOW PROTOCOL**:
  1. **Multi-Scene Story Blocks (30–60s Group Anchoring)**: Group characters into multi-scene sequence blocks (2–4 consecutive scenes / 30–60 seconds) that focus on the same 2–3 characters resolving a specific beat before transitioning to a new group.
  2. **Visual & Physical Scene Handoffs**: Adjacent scenes MUST share visual connective tissue:
     - *Spatial/Physical Bridges*: End Scene N with a character looking off-screen toward an entering character, walking toward a new spot, or handing over an object. Start Scene N+1 from that exact visual beat.
     - *Verbal & Dialogue Continuity*: Use questions, setups, or `<scenetrans>` carry-overs across scene boundaries so Scene N+1 directly answers or reacts to Scene N.
     - *Prop Continuity Flow*: Keep key props (e.g., the extradition ledger, sonic screwdriver, briefcase, file) active across scene cuts rather than disappearing/reappearing.
  3. **Shot Structure Variety**: Vary the shot breakdown across scenes. Do not use the exact same 3-shot pattern (S1 speaks -> S2 speaks -> S1 reacts) in every scene. Mix in over-the-shoulder angles, two-shots held apart with clear positioning, movement/pacing shots, and environmental reaction cuts.

### Rule 13: Flexible Scene Durations (Dynamic Timing 5s – 15s, prefer long)
- **THE RULE**: Scenes should land **between 5 and 15 seconds on the file you watch**. Prefer the **long end (12s–15s)** unless the beat is a short transition, punch, or cutaway.
- **DURATION GUIDELINES**:
  - *Transitions / punches (5s – 8s delivered)*: Arrivals, exits, a single reaction, a handoff between locations.
  - *Default scenes (12s – 15s delivered)*: Dialogue, glamour continuations, character joins. Use this unless there is a reason to go short.
  - *Maximum Cap*: Cap a single clip at **20 seconds requested** to preserve generation quality, audio sync, and render stability.
  - *Full Utilization*: Fill the whole duration with speech and physical movement — no trailing dead air.
- **C2V REQUESTED vs DELIVERED**: C2V pins ~22 frames (~0.92s at 24fps) and **trims that head off** the mp4. The number you pass as `durationSeconds` is the **untrimmed** render. To get a ~15s delivered clip, request **~16s**. To get ~12s delivered, request **~13s**. An 8s request delivers ~7s — that is why the early Paris chain felt short. Spread `[Shot 2]` / `[Shot 3]` timestamps across the **requested** duration (for a 16s request, cuts near `00:05.500` and `00:10.500`, hold until `16.00`), not leftover 8s times (`00:03` / `00:06`).

### Rule 14: "Show, Don't Tell" Visual Storytelling Mandate
- **THE PRINCIPLE**: Never rely on characters standing still and verbally explaining the plot or action ("I am filing an emergency stay", "I am scanning this portal", "This is an alien artifact"). **Show the action happening visually on screen.**
- **VISUAL ACTION EXECUTION**:
  - *Act Out the Story*: Have characters physically perform actions—Saul unrolling a paper legal scroll across a car hood; Tony Stark tapping a glowing holographic interface that casts blue light onto his face; Dean unhinging the Impala weapons trunk and physically lifting a wooden rack; the Doctor aiming his sonic screwdriver as blue light pulses across asphalt.
  - *Show Cause & Effect*: When a portal fluctuates, show light reflecting off characters' clothing and hair; when someone speaks off-screen, show the listener turn their head and step toward them; when a prop is handed over, frame the physical exchange close-up.
  - *Visual Emotion & Body Language*: Convey feelings through physical gestures (rubbing temples, tightening coat collars, dusting lapels, pacing back and forth) rather than static, emotionless facial expressions.

### Rule 15: Grounded Character Introductions (No "Drop From the Sky" Entrances)
- **THE ISSUE**: Crossover casts assembled for a single scenario can feel arbitrary or like a checklist if characters simply appear on screen with no reason to be there. This breaks suspension of disbelief and undermines narrative quality.
- **THE SOLUTION**:
  1. **Give Every Character an On-Ramp**: Before drafting scene prompts, write one sentence per named character in `synopsis.md` explaining *why* they are physically present at the story's setting (a job assigned there, a personal relationship to another character, an invitation, a professional assignment tied to the plot, a coincidence explicitly justified by the premise).
  2. **Stagger Entrances Across Early Scenes**: Do not introduce the entire cast within Scene 01. Let the opening scenes establish characters arriving or already settled into place with a visible reason (badge scan at an entrance, greeting a colleague, stepping out of a car, being paged over a radio). It is fine — and often better — for secondary or reactive characters (e.g. external coordinators, late-arriving backup) to enter later once the plot justifies their appearance.
  3. **State the Reason On-Screen at Least Once**: Somewhere in a character's first scene, make their reason for being there legible through dialogue, an action, or a visual detail (a lanyard/badge indicating their role, a line referencing their job or invitation, an action implying their purpose) rather than leaving it to be inferred.

### Rule 16: Multi-Subject Shared-Frame Compositions (Group Shots Beyond Single-Subject Isolation)
- **THE ISSUE**: Cutting every dialogue beat to an isolated single-character close-up (Issue 2's baseline anti-blending fix) is the safest technique, but relying on it for 100% of shots across an entire movie produces a flat, disconnected feel — like two isolated faces being cut together rather than people sharing physical space.
- **THE SOLUTION**: Multi-subject shots holding two or more characters in the same frame **are allowed and encouraged** for scene variety, as long as blending-prevention safeguards are still applied:
  1. **When to Use Group Framing**: Establishing shots, walk-and-talk scenes, physical handoffs (handing over an object, a handshake, standing side by side reacting to the same event), reunions, and any beat where the characters' shared presence in space is itself part of the story and should be shown, not told.
  2. **Held-Apart Positioning**: Explicitly describe each subject's position relative to the frame and to each other (e.g. `<Subject 1> stands on the left of frame, <Subject 2> stands roughly three feet to his right`) so the model has unambiguous spatial anchors instead of overlapping or merging bodies.
  3. **One Active Speaker at a Time**: Even in a shared frame, only one subject should carry a `<d>` dialogue tag at a time (the rare compound `(S1,S2)` ID from Issue 2 remains reserved for a single simultaneous word/short phrase between visually distinct characters). Every non-speaking subject still in frame must carry an explicit silence mandate (e.g. `<Subject 2> remains completely silent with his mouth closed, speaking no dialogue, while listening and reacting physically`).
  4. **Distinct Physical/Visual Anchors**: Restate each subject's distinguishing wardrobe/physical trait every time they share a frame, even briefly, so the text encoder retains a strong per-subject anchor despite the shared composition.
  5. **Mix Group and Isolated Shots Within a Scene**: A natural pattern opens a scene on a two-shot/group establishing frame (showing the physical relationship), cuts to isolated single-subject close-ups for the core dialogue exchange, and can return to a group frame for a physical reaction beat, handoff, or exit. Do not force an entire scene into only one mode.
  6. **Group Size Cap**: Prefer 2 subjects sharing a frame. 3 is usable with clearly staggered depth/positioning. Avoid more than 3 characters visible simultaneously in one shot — blending risk increases sharply beyond that.

### Rule 17: Explicit Character Identity & Wardrobe Anchoring in Every Shot
- **THE ISSUE**: In multi-subject scenes, background action shots, or combat sequences, MiniMax-H3 can subtly alter character hair, skin tone, ethnicity, or costume colors between shots if the prompt relies on generic terms (e.g., "a mercenary", "the agent", "in a shirt").
- **THE SOLUTION**:
  1. **Original / Unlicensed Characters**: Always explicitly restate complete physical descriptors in every shot description. For example, for an antagonist like Viktor Kessler, describe him as: `a tall, broad-shouldered Caucasian man with a shaved bald head and a thick salt-and-pepper beard, wearing a black tactical vest over a charcoal henley shirt`.
  2. **Licensed Characters**: Restate key wardrobe details in every shot prompt (e.g. `James Bond in a light blue dress shirt with top buttons unbuttoned`, `Steve Rogers in his dark navy-blue Captain America tactical suit with silver star emblem`).
  3. **Demographic & Ethnicity Anchors**: Explicitly include ethnicity ("Caucasian", "African-American", etc.), hair style ("shaved bald head", "braided pigtails"), and facial hair ("thick salt-and-pepper beard") to prevent facial or racial default shifting across shots.

### Rule 18: Razor-Sharp Tactical Action & Physical Cause-and-Effect
- **THE ISSUE**: Vague or timid action prompts (e.g. "he dodges", "he rolls away", "he gets hit") lead to clumsy, slow-motion, or unnatural "dodgy" animations.
- **THE SOLUTION**:
  1. **High-Precision Tactical Descriptions**: Write vivid, epic physical mechanics. Instead of "he dodges gunfire", write: `executes an epic, razor-sharp tactical drop, ducking under the gunfire in one lightning-fast fluid motion and sliding smoothly into a low crouching posture behind the concrete support pillar on the left of frame as heavy automatic rounds tear up the drywall directly above his head`.
  2. **Explicit Physical Cause-and-Effect**: When an action hits a target (a punch, a gunshot, a knife throw), explicitly describe the impact and immediate physical reaction in the same shot before cutting: `The thrown knife strikes <Subject 2> Viktor Kessler (S2) squarely in his shoulder, sinking deep into his tactical vest padding, making him groan in pain and instantly drop heavily to the tile floor, releasing his grip on the steel case.`

### Rule 19: Preventing Voice Swapping in Post-Combat/Action Dialogue
- **THE ISSUE**: When a character delivers a punchline or spoken line immediately after defeating an opponent in a two-shot, MiniMax-H3 sometimes assigns the spoken line or mouth animation to the defeated opponent standing or slumped nearby.
- **THE SOLUTION**:
  1. **Cut to Single Close-Up for the Spoken Line**: After the physical hit/collapse occurs in Shot 1, cut Shot 2 to a **single medium close-up of the victor alone in frame** (`a single medium shot of <Subject 1> John McClane (S1) alone in frame... <Subject 2> is not in frame`).
  2. **Directional Eye-Line & Off-Camera Target**: Have the victor look off-camera toward the defeated enemy or ally while speaking: `looking down at the fallen mercenary off-camera, then glancing up toward Wednesday off-camera`. This completely isolates the audio/facial generation pipeline onto the correct character.

### Rule 20: Exterior, Entrance & Exit Directional Camera Framing
- **THE ISSUE**: When characters exit a building or room (e.g., leaving a lobby out to a plaza), prompts that vaguely state "walking through the glass doors" often cause MiniMax-H3 to render them *entering* the building instead of leaving.
- **THE SOLUTION**:
  1. **Anchor the Camera Position**: Explicitly state the camera location and perspective relative to the building: `Live-action, bright sunlit outdoor concrete plaza outside Sabre Tower, looking back at the main glass entrance doors from the outside...`
  2. **Explicit Movement Vector**: Describe the movement relative to the building: `A medium two-shot tracking backward in front of both subjects as they walk out through the glass double doors from the dark lobby into the bright morning outdoor air, moving away from the building entrance behind them.`

### Rule 21: Sub-Scene Insertions (`_a`, `_b`) & Duration Parameter Discipline
- **THE ISSUE**:
  - Missing connective beats identified during production (e.g., showing a door closing, a character separation, or a bad guy approaching) can break narrative flow if skipped, but renumbering 70+ scene files is error-prone.
  - Submitting prompts via API/MCP without specifying explicit clip durations causes generation servers to fall back to a default duration (e.g., 5 seconds), truncating dialogue and action beats.
- **THE SOLUTION**:
  1. **Sub-Scene Naming**: Insert sub-scenes using letter suffixes (`scene_23_a.md`, `scene_28_a.md`, `scene_68_a.md`). Create companion dialogue files (`scene_XX_a_dialogue.md`) and register them in `synopsis.md`.
  2. **Explicit Duration Passing**: Every API call or MCP `comfy_submit_t2v` tool call MUST explicitly pass the `durationSeconds` parameter matching the scene prompt's defined duration (e.g., `durationSeconds: 10`, `durationSeconds: 12`). Never rely on server defaults.

### Rule 22: Environmental Lighting & Time of Day Consistency
- **THE ISSUE**: Lighting, sun angles, or time of day jumping between shots or scenes (e.g., from bright midday glare to sunset amber glow to night) within the same scene or immediate sequence creates severe visual jarring.
- **THE SOLUTION**:
  1. **Explicit Lighting Declaration**: Every shot description MUST explicitly specify the environment's lighting temperature and time of day (e.g. `Live-action, bright morning 10:00 AM sunlight filtering through high glass windows...` or `dusk sunset golden hour lighting...` or `nighttime ambient overhead fluorescents...`).
  2. **Sequence Uniformity**: Keep the lighting and time of day 100% consistent across all connected scenes taking place in the same location/sequence unless an explicit story time-jump occurs.

### Rule 23: Actor Era & Age Anchoring (Preventing Age Jumping)
- **THE ISSUE**: For actors who played iconic roles across long careers spanning multiple decades (e.g. Bruce Willis in 1988 *Die Hard* vs 2007 *Live Free or Die Hard*; Harrison Ford in 1981 *Raiders* vs 2023 *Dial of Destiny*; Nathan Fillion in 2002 *Firefly* vs 2018 *The Rookie*), MiniMax-H3 may randomly jump between young, middle-aged, or elderly portrayals of the actor from shot to shot or scene to scene if the prompt merely says "played by Bruce Willis".
- **THE SOLUTION**:
  1. **Era & Age Anchor in `subject_definitions:`**: Always specify the exact era/age in the character tag (e.g. `<Subject 1> John McClane (played by 35-year-old Bruce Willis, 1988 Die Hard era) from Die Hard`).
  2. **Age-Defining Physical Trait Anchoring in Every Shot**: Restate age-defining physical characteristics in every shot prompt (e.g. `receding dark brown hair, energetic young 30s facial features` vs `bald shaved head, weathered 50s face`). This prevents the AI model from defaulting to a different era of the actor's life.

### Rule 24: High-End Cinematic Glamour & Portrait Lighting Presets
- **THE DIRECTIVE**: When client specifications request high-end cinematic glamour, fashion portraits, and elegant visual aesthetics:
  1. **Cinematic Aesthetics**: Focus on elegant framing, authentic poses, natural soft lighting, warm studio/golden-hour glow, fine wardrobe styling, and authentic 35mm film grain.
  2. **No Render Buzzwords**: Banned terms include `masterpiece`, `8k`, `hyperrealistic`, `unreal engine`, `CGI`, `render`.
  3. **Authentic Dialogue**: Enclose character-authentic spoken dialogue in `<d>[English in [Character]'s voice from [Show]] dialogue line</d>`.
  4. **Active pacing (prefer 12s–15s delivered)**: Use 3-shot storyboards across the full clip. For C2V request ~16s to deliver ~15s (`00:00–00:05.500`, `00:05.500–00:10.500`, `00:10.500–00:16.000` on the untrimmed clock). Short 5s–8s clips are for transitions only. Fill the duration with dialogue, pose shifts, and camera movement — no silent staring lead-ins or clip-end dead air.

### Rule 25: Base Model Character Usability Benchmarking & MP4 Metadata Sorting Protocol
- **THE PURPOSE**: Before featuring any character or celebrity in a multi-scene movie or crossover prompt, run a standardized 8-second "character roll" usability test to determine if the base MiniMax-H3 model renders sufficient facial likeness and voice fidelity without a custom LoRA.
- **BENCHMARKING PROTOCOL**:
  1. **8-Second Single-Shot Roll Template**: Draft an 8-second T2VA prompt opening immediately with dialogue:
     ```text
     subject_definitions:
     <Subject 1> [Character Name] (played by [Actor Name]) from [Show Name]

     integrated_multimodal_description:
     [Shot 1] Live-action, 35mm cinematic photograph, [Setting]. A single medium close-up of <Subject 1> [Character Name] (S1) alone in frame, wearing [iconic attire]. The shot opens with <Subject 1> [Character Name] (S1) looking directly into the camera lens in iconic character demeanor, already speaking, with no silent establishing beat. The camera holds a static shot as <Subject 1> [Character Name] (S1) says clearly: <d>[English in [Character]'s voice from [Show Name]] [Iconic quote]</d>

     overall_soundscape:
     [Ambient room acoustic tone, subtle movement rustle, and character's distinct spoken voice.]

     non_diegetic_music:
     N/A
     ```
  2. **Batch Queue Execution**: Save test prompts in structured JSON batch files (`prompts_usability_tests_batch_h3_X.json`) in `sd-backend/src/public/prompts/programmatic-minimaxh3/` and queue via `comfy_submit_t2v`.
  3. **Directory Sorting**: Triage rendered MP4 outputs from ComfyUI into `good/` (high base model fidelity) or `bad/` (poor likeness requiring custom LoRA) subfolders (e.g. `C:\Development\ComfyUI\output\video\characters\new\good` and `bad`).
  4. **Automated MP4 Metadata Recovery & Index Update**: MiniMax-H3 MP4 outputs embed the full ComfyUI API-format workflow graph in MP4 metadata (`udta/meta/ilst` atom). Use the backend metadata parser (`readVideoMetadata` / `extractVideoGraph`) to extract the exact `subject_definitions` from `good/` and `bad/` files and automatically update status tags (✅ **Usable** vs ❌ **Not Usable**) in `CHARACTER_USABILITY_INDEX.md`.

### Rule 26: Spatial Positioning & Subject Placement Continuity across C2V Joins
- **THE ISSUE**: In Clip-to-Video (C2V) motion context chaining and shot cuts within a sequence, characters can unexpectedly swap spatial positions (e.g. a character seated on screen-left suddenly jumping to screen-right, or another character replacing them on screen-left when the camera cuts).
- **THE CONTINUITY PROTOCOL**:
  1. **Document Screen Placement in Prompt Text**: Always explicitly state spatial coordinates / positions for every character in multi-subject shots (e.g., `<Subject 1> Daenerys (S1) seated on screen-left, <Subject 2> Kate (S2) seated in screen-center, <Subject 3> Penny (S3) lounging on screen-right`).
  2. **Track Placement Across C2V Joins**: Before drafting a continuation prompt (e.g. Clip N -> Clip N+1), inspect the ending frame of Clip N to identify who was positioned on screen-left, screen-center, and screen-right.
  3. **Maintain Spatial Anchors in Continuation [Shot 1]**: In Clip N+1 [Shot 1], explicitly describe the incoming subjects relative to those spatial anchors (e.g. `[Shot 1] Continuous movement from the previous scene. <Subject 1> Daenerys (S1) remains seated on screen-left, while <Subject 2> Kate (S2) sits on screen-center...`).
  4. **Preserve Spatial Geometry Across Internal Cuts**: When cutting between shots inside a clip (e.g., [Shot 1] to [Shot 2]), if a character stays on screen or appears in a two-shot, explicitly instruct their position to remain on their assigned side (e.g. `<Subject 3> Penny remains positioned on screen-right as <Subject 1> Daenerys speaks from screen-left`).
  5. **Verifiable Prompt Record**: Documenting explicit spatial placement (`screen-left`, `screen-center`, `screen-right`) in every prompt creates a searchable, recheckable prompt audit trail for visual continuity across multi-clip C2V chains.

### Rule 27: C2V Join = Previous Ending — Do Not Re-boilerplate Start and End
- **THE ACTUAL BUG**: Sandwiching did not come from "any wide shot" or "mentioning the location." It came from pasting the **same establishing boilerplate** at **both ends of every clip** (and therefore at the join between clips). Typical copy-paste: *Live-action 35mm, Paris penthouse terrace, Eiffel Tower, medium/wide of three women on the daybed* in Shot 1 **and** again in Shot 3.
- **THE JOIN FACT (write it into Shot 1)**: In C2V, the start of clip N+1 **is** the end of clip N. The workflow pins the previous tail (`contextLengthFrames`, default 22 frames ≈ 0.92s at 24fps) onto the new timeline, then trims that head off the delivered file. Shot 1 still opens on that same composition. **Do not re-describe a fresh establishing shot of the location.** State the join explicitly, e.g. `[Shot 1] Opens on the last frame of the previous clip: <Subject 2> Pam still in screen-center, glass at her lips. The camera continues from that exact framing as Rose leans in from screen-left...` Then describe **what changes** (who enters, who speaks, a small camera move) — not a second copy of the terrace/vista.
- **DO NOT CLOSE WITH THE SAME SHOT**: Shot 3 must not repeat Shot 1's framing or the previous clip's ending tableau. If the join is a medium two-shot on the daybed, do not end on that same two-shot "with the tower behind them." If you end on it, the **next** clip's start is that shot again, and the loop continues.
- **TOO BROAD (do not do this)**: Banning all landmarks, all medium shots, and forcing ECU-only for the whole chain. Location and daylight may still be mentioned when they are not a framed vista that copies the join. A medium two-shot is fine **once**, as the inherited start — not again as the closer.
- **TIMESTAMP NOTE**: Shot times are on the **untrimmed** requested duration. After C2V trims ~0.92s, a closer at `00:06` on an 8s request eats the last third of a ~7s file. On a 16s request (≈15s delivered), put the last beat near `00:10.500`–`00:16.00`, not at `00:06`.
- **VERIFY**: If the first second and the last third show the same scenery, the boilerplate closer is still there. Prompt labels (`OTS`, `detail`) do not override that.

### Rule 28: C2V Continuity Movies — Playbook (Paris chain, August 2026)
Use this when making a **long scene that continues** (one location, many characters rotating through) rather than a T2V episode of disconnected takes. Proven on the Paris penthouse sequence chain (`h3_context/paris_penthouse_3clips`, clips through `MiniMax_H3_C2V_00174_`).

#### How the chain is run
1. **One `chainFolder` per movie/sequence.** Never mix two stories in the same latent folder.
2. **Clip 1 seeds** (omit `continueFromClipIndex`). Clip N+1 always `continueFromClipIndex: N`, `clipIndex: N+1`, `contextLengthFrames: 22`.
3. **Wait until clip N has finished** before submitting N+1 — the latent has to exist. Sequential only.
4. **Review in batches of 3.** Do not queue 20 clips blind. Confirm flow, positions, lighting, and duration, then continue.
5. **Fresh noise seed per clip** (do not leave the workflow template seed).
6. **Re-roll** overwrites the same `clipIndex` slot. After a good take, only then bump both indices.

#### What to write in each continuation prompt
1. **Shot 1 = the previous ending, then the change.** First sentence: `Opens on the last frame of the previous clip:` plus who is where, what they hold, what the frame is. Then who enters, who speaks, a small camera move. Do **not** re-establish the location as if this were a new T2V clip.
2. **C2V is a continuous take, not a T2V 3-cut storyboard.** Hard cuts (`the shot cuts to`) fight the motion-context pin. The model starts on the previous ending, wanders on the cut, then **falls back to the pin** for the last third — that is the “diverges then snaps back” failure. Write **one unbroken camera move** from the join to a new closer. If you mark `[Shot 2]` / `[Shot 3]` for timing, say `without cutting` / `the camera keeps moving`. Never `the shot cuts to`.
3. **Shot 3 / the last beat = a new closer class, and rotate it.** This closer **is** Shot 1 of the next clip. Rotate: sip/glass ECU, hands+prop filling the frame, single face into lens, profile, eyes only, looking down. **Illegal as a default closer:** a held two-shot of two people looking at each other. **Do not freeze.** “Until 16.00” on a still ECU is a tableau. Bodies and camera keep moving through the last beat (a sip in progress, a turn, a truck, a sit). The last frame is a moving closer, not a hold.
4. **New dialogue every clip.** Do not recycle lines already spoken in this chain (even from discarded takes). Keep a running list of used quotes per character.
5. **Screen positions in every multi-person shot:** `screen-left` / `screen-center` / `screen-right`. Keep them unless someone clearly enters or leaves that seat. Read the previous ending before writing the next Shot 1.
6. **Lighting lock for the sequence.** One string for the whole chain (e.g. `bright daylight on skin`). Do not drift morning → sunset → night unless the story actually jumps time.
7. **Cast:** only ✅ Usable names from `CHARACTER_USABILITY_INDEX.md`. Max 3–4 on screen. Stagger arrivals — typically **one new person per clip**. Give them a spoken reason to be there. The plot must **advance** (new person, new action, new closer) — not a remix of the previous two-shot.
8. **Props:** skip fiddly held instruments or objects that smear (guitar-in-hands failed; a cup or notebook is safer).
9. **Duration:** delivered **5–15s**, prefer **12–15s** unless it is a short transition. C2V request ≈ delivered + 1s. Default: **`durationSeconds: 16`**. Times on the untrimmed clock: `00:05.500` / `00:10.500` / keep moving to `16.00`. Those are beats inside **one take**, not editorial cuts, and not freeze points.
10. **Spectrum `historyStorage`:** Templates already default C2V/REF_C2V to `system_ram` and T2V/I2V/R2V to `vram`. **Short standalone clips** (T2V/R2V tests, ~8s) may pass `historyStorage: "vram"` when the GPU is clean. **Long C2V / REF_C2V** (16s request / ~15s delivered, multi-clip chains) must stay on **`system_ram`** — omit the field (template default) or pass it explicitly. Do **not** pass `vram` on a long continuation chain: clip 3 completes with no file and Comfy stays up.
11. **Noise seed must change every clip.** If every mp4 still has `noise_seed: 836251032571729`, the backend is serving a stale process and C2V will stay in the same visual basin. Restart `sd-backend` after seed changes; confirm the new seed in video metadata before rolling a batch.

#### Continuity log (keep this between batches)
Before the next 3 clips, write down from the last mp4 (not from memory of the prompt):
- `clipIndex` and output filename
- Ending frame: left / center / right, framing (ECU / close / two-shot), prop in hand, lighting
Shot 1 of the next clip must cite that ending. Metadata proves what was asked; **the mp4 proves the join**.

#### When a clip fails: triage before you retry (burned twice — movie0003 and movie0005)
**Step 1, always: check for stray runner processes. Do not read the ComfyUI log and do not edit a prompt until you have done this.** A backgrounded runner does **not** die when the shell that started it returns. Every failed launch leaves a live Node process that keeps submitting on its own schedule, so after two or three "fix and relaunch" cycles you have several runners racing on one ComfyUI queue and one latent folder. The errors you then read in the ComfyUI log belong to a runner you already tried to replace, which sends you chasing a bug in the code you just fixed.

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'node.exe'" |
  Where-Object { $_.CommandLine -like '*run_movie*' } |
  Select-Object ProcessId, CommandLine
```

If more than one runner is alive, recover in this order — a partial cleanup leaves a stale latent that the next clip silently continues from:
1. `Stop-Process -Id <each pid> -Force` for **every** runner, including the one you believe is correct.
2. Clear the queue so orphaned jobs stop landing: `POST http://127.0.0.1:8188/queue` with `{"clear": true}`, then confirm `queue_running` and `queue_pending` are both empty.
3. Delete the `.lock` and `_progress.json` for the chain. A killed runner leaves a lock that makes the next launch abort with `another runner (pid N) is already active`, and a progress file whose `lastCompletedClipIndex` counted failed clips as done.
4. Delete any latents written under a wrong `chainFolder`, and list the correct folder to see how far the chain really got. `dir` the folder — file timestamps tell you which clips are from this run and which are leftovers.
5. Relaunch **one** runner and verify clip 1 completes and clip 2 reports `continueFromClipIndex: 1` before you walk away.

**`chainFolder` must include the `h3_context/` prefix** — `h3_context/movie0005_flat_earth`, not `movie0005_flat_earth`. Without it the node resolves to `output/<name>` and fails with `'<name>' is neither a file nor a folder`. The `no saved latent for clip N (no *_0000N.safetensors in ...)` error is the same bug one clip later: read the **path** in that message before assuming the chain logic is wrong.

**Do not trust "the queue is empty" as proof a clip succeeded.** A prompt that fails validation leaves the queue immediately, so a runner polling only queue depth marches through the whole movie recording failures as completions. Poll the prompt's history entry and require an actual output file.

#### What not to do (already burned)
- Launching a replacement runner without killing the previous one. Two runners on one `chainFolder` overwrite each other's latents, and the older one's errors get blamed on the newer one's code.
- Ending several clips in a row on the same closer class (especially a held close two-shot of two women looking at each other). C2V compounds that “home” look and artifacting stacks.
- Writing `not the previous two-shot` while Shot 3 still requests a two-shot of the next pair.
- Hard-cutting C2V (`the shot cuts to`) like a T2V 3-shot. The pin is the previous ending; a cut lets the model wander then **fall back to that ending**. Write one continuous take.
- Serving C2V from a stale `sd-backend` so every clip keeps template `noise_seed` `836251032571729`. Restart the API after seed wiring; confirm metadata.
- Treat prompt labels (`OTS`, `detail`) as proof the sandwich is gone.
- Ban all landmarks / force ECU-only for the whole movie (too broad). Medium coverage is allowed as the **inherited start**, not as a matching closer.
- Request 8s and expect 8s on disk (you get ~7s). Request 16s for a ~15s scene.
- Advance time of day in the prose while the chain is still the same afternoon.
- Submit the next C2V job while the previous one is still rendering.
- Open a clip with `already speaking, with no silent establishing beat` when the previous clip ended on a **different** person. See Rule 29 — this is the single most common C2V failure.

### Rule 29: C2V Speaker Handoff — never open a subject change on dialogue (movie0005, August 2026)
**The failure:** clip N ends on Kanye talking. Clip N+1 is written as `[Shot 1] ... Joe Rogan (S1) alone in frame ... already speaking, with no silent establishing beat: <d>Wait, hold on... really?</d>`. The motion-context pin means the first ~22 frames of clip N+1 still render **Kanye's** face. The dialogue starts on frame 1, so Joe's line comes out of Kanye's mouth in Joe's voice. The prompt named Joe; the picture had not arrived yet.

**Why the standard opener causes it:** `already speaking, with no silent establishing beat` is a **T2V** rule (Rule 3) written to stop dead air at the top of an independent clip. In C2V the top of the clip is not yours — it is inherited. Applying the T2V opener to an inherited frame is what mis-attributes the line.

#### The decision you must make before writing any continuation Shot 1
Read the **last frame of the previous mp4** (not the prompt) and ask: *is the person who speaks first in this clip already the person on screen?*

- **Same speaker on screen → continuation opener.** `already speaking` is correct and works. Nothing changes.
- **Different person speaks → mandatory handoff.** Never let dialogue begin before the camera has arrived on the new speaker.

#### The handoff structure (three mandatory parts)
1. **Inherited silent beat, `00:00.000`–`00:02.000`.** Reference the **previous** clip's subject by their `<Subject N>` tag (declare them — see below), state what they are finishing, and mark them explicitly silent: `mouth closed, completely silent, speaking no dialogue`. Add `No dialogue, narration or voice of any kind is heard during this opening beat.` The negative has to be explicit — the model will otherwise fill inherited lip motion with the next line it is given.
2. **A named transition out of them.** `Without cutting, the camera trucks left away from him, leaving <Subject 2> Kanye West (S2) out of frame, and settles into <new framing>.` A whip pan, truck, arc, or rack focus all work. Keep it continuous per Rule 28 — do not write `the shot cuts to`.
3. **New speaker's first `<d>` at `00:02.000` or later**, and say so out loud: `At 00:02.000, now that the camera is settled on <Subject 1> Joe Rogan (S1) alone in frame, he says: <d>...</d>`. The clause "now that the camera is settled on" is doing real work — it ties the audio to the arrival, not to the clip start.

#### Always tag the outgoing person as a `<Subject>`
The person you hand off *from* is on screen during the inherited beat, so they are a subject and must be declared in `subject_definitions:` like anyone else. **Never refer to them by bare name** — an untagged name is not reliably bound to a face, and the inherited head is exactly where identity is most fragile.

- **The primary speaker MUST ALWAYS be `<Subject 1>`.** In multi-subject shots (or scenes with multiple defined subjects), MiniMax-H3 binds dialogue audio generation to `<Subject 1>`. If a silent character is listed as `<Subject 1>` and the actual speaker as `<Subject 2>`, the model will misattribute the spoken line to `<Subject 1>`'s face. ALWAYS put the active speaker first in `cast` / `subject_definitions:`, and explicitly write `and <Subject 1> Name (S1) says:` in the shot description.
- **Give the outgoing person the next free subject number**, after the clip's actual cast. The speaker stays `<Subject 1>`, a two-shot keeps the speaker as `<Subject 1>` and the listener as `<Subject 2>`, and the outgoing person takes the number after that.
- **Keep them out of the rest of the clip with isolation phrasing, not by omitting the tag.** Name them leaving in the transition (`leaving <Subject 2> Kanye West (S2) out of frame`), then add `<Subject 2> is not in frame.` to every shot after the handoff — the same isolation phrasing used for off-screen characters elsewhere in this guide.
- **If the outgoing person is already in this clip's cast, do not re-declare them.** Previous clip ends on Anthony alone; next clip is Dave + Anthony. Anthony is already `<Subject 2>` — reference that tag in the inherited beat and widen to reveal Dave.

```text
subject_definitions:
<Subject 1> Joe Rogan (played by Joe Rogan) from Real Person / Actor
<Subject 2> Kanye West (played by Kanye West) from Real Person / Actor

[Shot 1] HANDOFF BEAT, 00:00.000 to 00:02.000. Opens on the last frame of the previous clip:
<Subject 2> Kanye West (S2) still in close-up at screen-right with his palm flat on the wood, his
mouth closed and completely silent, speaking no dialogue. No dialogue, narration or voice of any
kind is heard during this opening beat, and no lips move. Without cutting, the camera whip-pans
left across the studio to the host station, leaving <Subject 2> Kanye West (S2) out of frame, and
settles into a single medium shot of <Subject 1> Joe Rogan (S1) alone in frame. <Subject 2> Kanye
West (S2) is not in frame for the entire remainder of the clip.

[Shot 2] At 00:02.000, now that the camera has settled on <Subject 1> Joe Rogan (S1) alone in
frame and only now, he grips his headphones and says: <d>...</d> <Subject 2> is not in frame.
```

#### Consequences for scene planning
- **Budget the handoff into the duration.** A subject-change clip needs ~2s of untrimmed head before its first line, so request **+2s** over a same-speaker clip (e.g. 12s request for the same two spoken beats a 10s continuation clip carries).
- **A shared subject is still a change if the speaker changes.** Previous clip ends on Anthony alone; next clip is Dave + Anthony with **Dave** speaking. Anthony is on screen, but Dave is not — you still widen or truck to reveal Dave before he talks.
- **Group same-speaker clips deliberately.** Two consecutive clips on the same person costs no handoff and reads more naturally. Alternating speaker every clip means paying 2s of transition every clip.
- **Check the whole chain, not one join.** Write the ending subject of every clip in the continuity log, then walk the list and mark each join `same` or `handoff` before writing a single prompt.

#### Verify on the mp4
Watch the **first two seconds** of every continuation clip. If a face that is not the credited speaker is moving its lips while the new line plays, the handoff is missing or the first `<d>` is too early. Prompt labels do not prove it — only the file does.

### Rule 30: C2V Latent Identity Persistence — Retain Adjacent/Panel Subjects in Continuation Clips (movie0005, August 2026)
**The failure:** Clip N shows a two-shot of Dave Chappelle (S1) and Neil deGrasse Tyson (S2). Clip N+1 continues on Dave Chappelle (`kind: 'same'`), but drops `<Subject 2> Neil deGrasse Tyson` from `subject_definitions:`. Because C2V carries motion-context latents from Clip N into Clip N+1, the person sitting next to Dave (Neil) remains in the visual latent space. Without `<Subject 2> Neil deGrasse Tyson` defined in Clip N+1's prompt, the AI model has no identity anchor for that person, causing Neil's face to degenerate into a random, hallucinated stranger sitting next to Dave.

**The rule:** In a C2V continuous chain, whenever Clip N+1 continues from a multi-subject shot or stays at a desk/panel where an adjacent character was visible in the latent:
1. **Retain all visible/adjacent characters in `subject_definitions:`**: Do not drop a character from `subject_definitions:` if they were in the frame/latent of the previous clip and remain adjacent or on screen.
2. **Primary Speaker MUST be `<Subject 1>`**: Keep the speaker as `<Subject 1> Speaker Name (S1)`.
3. **Adjacent Panel Member is `<Subject 2>`**: Define `<Subject 2> Adjacent Name (S2)` in `subject_definitions:` and explicitly describe their passive presence (e.g., `<Subject 2> Neil deGrasse Tyson (S2) remains seated on screen-left with his mouth closed, completely silent, speaking no dialogue`).

### Rule 31: TNG division colors — pin Data's gold uniform in `subject_definitions:` (movie0010, August 2026)
**The failure:** Data (Brent Spiner) rendered in a red command tunic. Shot body already said "yellow and black Operations uniform." That was not enough — a speaker-change truck off Picard in red bled the command color onto Data.

**The rule:**
- Put the uniform in `subject_definitions:` every clip Data is in, even when he is S2 and not in frame: `Data (played by Brent Spiner) from Star Trek: The Next Generation, pale gold android skin, bright yellow eyes, wearing a mustard-yellow and black Starfleet Operations division uniform tunic`.
- Restate on every Data MCU: mustard-yellow / gold-yellow Operations, **strictly not a red command uniform and not a teal science uniform**.
- Data's TNG uniform is **always** Operations gold/mustard-yellow and black. Never red. Never teal.

TNG division cheat-sheet for this model: Picard and Riker in uniform = red command. Data, Geordi, Worf = gold/mustard-yellow Operations. Crusher (if ever used) = teal science. Do not let a truck off a red-uniform speaker recolor the next person.

### Rule 32: Do not pad duration with silence — unused seconds become dead air between sentences (movie0010, August 2026)
**The failure:** Bridge clips were requested long on purpose so Picard/Data would not rush. MiniMax-H3 filled the leftover seconds with slow trucks and pauses between sentences. Dialogue cadence was fine; the off-time was not.

**The rule:** Request only spoken time plus **about 1s**. MiniMax-H3 will insert pauses *between sentences inside a single `<d>`* to fill leftover duration — that is the usual "too much breathing room" failure, not slow delivery. Size `durationSeconds` to the words. If there are two `<d>` tags, place the second immediately after the first line would end, not at the midpoint of a long clip. Seed opens: first line by **00:01.000**. Speaker-change first `<d>` at **00:01.250**. Do not add extra seconds as breathing room. Do not speed up the spoken line itself.

### Rule 33: New C2V seeds must not pre-declare off-screen cast (movie0010, August 2026)
**The failure:** Riker's turbolift entrance (`00502`) listed Picard and Data in `subject_definitions:` as "not in frame." The seed was supposed to be Riker alone. The take was a face amalgamation, not Jonathan Frakes walking in.

**The rule:** On a **new seed**, only define people who are actually on camera. Do not pre-load off-screen bridge crew "for later." Rule 30 (keep adjacent subjects) applies to **continuations** of a shot where those people were already visible, not to empty-frame declarations on a seed. Add Picard when Riker sits down. Add Data only if Data is in frame.

### Rule 34: Reference Images + Multi-LoRA Stacking Calibration (August 2026)
**The failure:** When stacking multiple LoRAs (e.g. 3 LoRAs on Miley Cyrus (milecyrus) or 2 LoRAs on Gillian Anderson (gilliananderson)/Felicia Day (feliciaday)) together with a full set of reference images (8 images), high strengths totaling **~1.05–1.35** cause **prompt collapse / baked-in poses**. The model becomes hyper-rigid, ignores prompt instructions entirely, and outputs erratic movements or frozen stances from the dataset instead of following the scripted actions.

**The rule:**
1. **Calibrate combined LoRA strength when using Reference Images**:
   - Single LoRA + 8 refs: **0.60–0.70** (e.g. Gillian Anderson (gilliananderson) preset #1 at 0.70).
   - 2-LoRA Stack + 8 refs: **start at 0.40 each (0.80 combined)** (Felicia Day (feliciaday) preset #1). After refs are locked, A/B **+0.05 per LoRA** on one seed clip. Natalia preset #1 confirmed at **0.45 / 0.45 (0.90 combined)** — `MiniMax_H3_01962_.mp4` beat the 0.40 seed `01957` with no collapse.
   - 3-LoRA Stack + 8 refs: **0.15 / 0.15 / 0.20 (0.50 combined)** (Miley Cyrus (milecyrus) preset #2). Do not treat Natalia's 0.90 as a 3-stack budget.
2. **0.80 is a starting default, not a hard ceiling.** Collapse still shows up around **1.05+ combined**. Do not jump a new character straight to 0.90 — lock refs first, then bump one test clip.
3. **Dataset Image Rotation**: Rotate through distinct sets of reference images (e.g. pools of 8) during lock-in so the model doesn't overfit to one pose or angle.

### Rule 35: C2V Physical Motion Breakdown — Gradual Transitions per Clip (August 2026)
**The failure:** Asking the model in a single 16s C2V clip to perform a drastic, multi-stage physical transition (e.g. starting from an intimate close-up reclining on a bed -> propping up -> sitting up -> swinging legs over -> standing up -> walking across the penthouse into the kitchen). The C2V motion-context pin carries the previous posture and camera framing into the start of the clip, causing severe body warping, floating limbs, or visual artifacts when forced to morph too quickly.

**The rule:**
Break complex character locomotion and posture changes across sequential C2V clips into **one physical beat per clip**:
- **Clip A (Prop Up)**: Lying down -> prop up on elbow, turning to side.
- **Clip B (Sit Up & Rise)**: Propped up -> sit up smoothly and swing feet to floor.
- **Clip C (Locomotion / Walk)**: Stand up -> begin walking, camera tracking alongside in medium framing.
- **Clip D (Arrival & Prop Interaction)**: Arrive at destination (e.g. kitchen island, sofa, vanity table) -> engage with object (cup, book, mirror).

### Rule 36: Framing Mandate for Character Face Clarity (Avoid Far-Away Shots) (August 2026)
**The failure:** MiniMax-H3 produces low-resolution, warped, or distorted facial features when characters are framed in extreme wide or distant full-body shots.

**The rule:**
- **Default to Medium Shots (waist-up), Medium Close-ups (chest-up), and Close-ups**: For all character-focused dialogue and glamour shots, keep the camera close enough to resolve clear facial features and expressions.
- **Dynamic Tracking over Distance**: When a character traverses a large room or moves between locations, use a smooth tracking shot that stays in a **medium profile (waist-up)** rather than pulling back to a wide room shot.

### Rule 37: C2V Latent Rescue & Grid-Aligned Tail Trimming (August 2026)
**The issue:** A long C2V clip renders cleanly for 12–13 seconds, but the final 1–2 seconds experience a glitch, face distortion, or motion collapse. Regenerating the entire clip costs render time and may lose a perfect take.

**The solution:** Trim the underlying saved latent file to a clean VAE grid step before the corruption:
1. **VAE Grid Constraints**: The MiniMax-H3 video VAE latent steps follow the formula `steps = 5 * g + 2` covering pixel frames `sum(FRAME_PER_TOKEN[k % 5])` where `FRAME_PER_TOKEN = (1, 4, 4, 4, 4)`.
   - At \(g=17\): 87 latent steps = 294 frames (~12.25s).
   - At \(g=18\): 92 latent steps = 311 frames (~12.96s).
2. **Latent Slicing Protocol**:
   - Backup the original latent `h3_context/<chainFolder>/clip_XXXXX.safetensors`.
   - Slice video tensor to `[:1, :, :92, :, :]` and audio tensor to `[:1, :, :, :518]` (where `round(311 * 5/3) = 518`).
   - Save back to `clip_XXXXX.safetensors` with metadata `{"format": "h3_motion_context_av_v1"}`.
   - Trim the corresponding MP4 with `ffmpeg -i input.mp4 -t 00:00:12.958 -c copy output.mp4`.
3. **Continuation**: The subsequent clip continues seamlessly from `clip_XXXXX` using standard `contextLengthFrames: 22` (or `56`), pinning the cleanly trimmed 12.96s frame as its inherited start.

### Rule 38: Mirror Reflections and Virtual Camera Depth in C2V (August 2026)
**The failure:** Clip N ends looking into a mirror reflection of the character. Clip N+1 describes the character from the front without acknowledging the camera is currently looking at glass, confusing the model's spatial alignment.

**The rule:**
When Clip N ends on a mirror reflection:
1. **Explicit Opening Acknowledgment**: Open Clip N+1 describing the mirror reflection: `[Shot 1] ... Opens framed on the gilded vanity mirror showing the clear reflection of <Subject 1> Name (S1) ...`
2. **Explicit Spatial Turnaround**: Describe the transition out of the reflection into real physical space: `Without cutting, she catches the camera's gaze in the glass with a smile, then smoothly turns around on the stool to face the camera directly. The camera glides around to reframe the real <Subject 1> Name (S1) in a luminous medium close-up ...`

### Rule 39: Splitting Physical Entrance / Repositioning from New Character Dialogue in C2V (movie0010, August 2026)
**The failure:** In movie0010, when Malcolm Reynolds emerged from the TARDIS doorway while the Tenth Doctor was standing aside, attempting to execute both the physical emergence and Mal's spoken dialogue in a single 8-second C2V clip resulted in severe voice mixing (Nathan Fillion's voice blended with David Tennant, or the spoken line misattributed to Tennant's face / unnatural facial morphing).

**The rule:** When introducing or revealing a new character from an entrance, doorway, vehicle hatch, or background into a scene where a previous character is present:
1. **Clip A (Silent Emerge / Physical Settle):** Run a dedicated silent transition clip. The previous character moves aside/clears the doorway with mouth closed, and the new character steps out onto the deck/floor to establish their physical placement in frame. Both characters carry strict silence mandates:
   `Both mouths remain closed, completely silent, speaking no dialogue. No dialogue, narration or voice of any kind is heard.`
2. **Clip B (Dialogue on Established Speaker):** Once the new character is physically visible on the last frame of Clip A, C2V into the dialogue clip as a continuation where `<Subject 1>` is the already-visible new character. The previous character remains silent in `subject_definitions:` as `<Subject 2>`. This eliminates facial morphing, voice swapping, or identity blend.

### Rule 40: Locked Same-Speaker Continuity & Background Retention in C2V (movie0010, August 2026)
**The failure:** In movie0010 scene 45b (Data at ops), continuing a tight MCU on Data with a subtle action description like "turns slightly toward his combadge" or general movement caused the model to hallucinate a background cut/room swap or twitch away from the LCARS ops console.

**The rule:** In same-speaker C2V continuations intended to stay on the exact same composition:
1. **Locked-Off Continuity Mandate:** Write explicitly in Shot 1:
   `The camera is locked off and continues from that exact framing without cutting, without reframing, and without any change of location. This is one continuous take, not a freeze. Do not show a different room. Do not show a wide bridge. Do not change the background.`
2. **Verbatim Background Anchors:** Explicitly restate the exact background objects and their positions:
   `the same glowing LCARS console panels beside and behind him in the same positions`.
3. **Minimize Extraneous Rotation:** Keep physical actions focused on eyes, facial expression, and subtle head tilts rather than torso rotations that encourage the model to re-render the room.

### Rule 41: Mandatory Silent Inherited Lead-In to Suppress Top-of-Clip Vocal Gibberish in C2V (movie0010, August 2026)
**The failure:** In movie0010 clip 68 (scene 45b), Data vocalized unscripted mumbling/gibberish during the first 0.5s–1.0s before delivering his scripted line. This happens because the model inherits active or resting mouth latents from the tail of the previous clip and attempts to fill the early frames with speech.

**The rule:**
1. **Explicit "Previous Line Finished" Declaration:** In Shot 1 of any continuation clip, explicitly write:
   `He has finished his previous report [or line]. His mouth is closed, completely silent, speaking no dialogue. No dialogue, narration or voice of any kind is heard during this opening beat, and no lips move.`
2. **Timecode & Arrival Lock:** Never place the `<d>` dialogue tag before `00:01.000` (or `00:01.250` for handoffs). Enforce it with the phrase:
   `At 00:01.250, without cutting, now that his mouth has been closed and only now, speaking in a [tone]...`. This suppresses phantom vocalizations during the VAE latent join.

### Rule 42: Singular Object & Vehicle Anchoring Across Continuations (Preventing Duplication & Hallucinated Interiors) (movie0010, August 2026)
**The failure:** In movie0010 cargo bay scenes, continuing clips near the TARDIS or shuttlecraft would occasionally spawn a *second* police box or draw weird beige interior rooms through open doors when the prompt loosely mentioned "a police box" or "the box."

**The rule:**
1. **Strict Singular Phrasing in `subject_definitions:`:** Always define the object as `the same single [color/type object] already in the previous shot, [details]`.
2. **Negative Duplication & Interior Guards:** Explicitly add in the description:
   `Exactly one police box. Never two boxes. Do not show any interior through the box doors. No room inside the box.`
3. **Do not re-declare background objects as new arriving props:** If an object is established, treat it as static set dressing rather than a dynamic subject unless actively operated.

### Rule 43: Same-Uniform Distinction & Prosthetic/Prop Contrast Protocol (Twins Prevention) (movie0010, August 2026)
**The failure:** When two characters share the same uniform tunic (e.g. Geordi and Worf both in TNG mustard-yellow Operations tunics), MiniMax-H3 tends to duplicate features or blend them into twins unless high-contrast physical and prosthetic anchors are reinforced in every prompt.

**The rule:**
1. **Pin High-Contrast Anatomical / Prop Anchors in `subject_definitions:`:** For every shot featuring either or both characters, mandate their unique identifiers:
   - Geordi: `(played by LeVar Burton), African-American, smooth forehead, wearing a silver metallic VISOR across his eyes, wearing a mustard-yellow and black Starfleet Operations division uniform tunic`.
   - Worf: `(played by Michael Dorn), prominent ridged Klingon forehead, dark hair, metal/leather warrior baldric sash across his torso, wearing a mustard-yellow and black Starfleet Operations division uniform tunic`.
2. **Explicit Separation in Multi-Subject Descriptions:** Never refer to them generically as "two Starfleet officers." Always identify them with their distinct prosthetic/prop traits in the description body to prevent visual homogenization.

### Rule 44: Multi-Beat Scene Decomposition for Narrative Pacing and Tension (movie0010, August 2026)
**The failure:** Rushing complex narrative beats (e.g. observation -> cascade of failures -> factual report -> strategic deduction -> paging the captain) into a single dense 8s clip results in rapid-fire unnatural dialogue or missed physical actions.

**The rule:** Deconstruct sequence moments into distinct, breathing micro-clips:
- **Beat 1 (Alert / Curiosity):** Short punchy reaction (e.g. Data: "Curious.") with initial sensory cue.
- **Beat 2 (Silent Sensory Cascade):** Pure visual/auditory escalation (e.g. indicators dying across the console, sonic screwdriver frequencies pulsing) with silence mandates.
- **Beat 3 (Objective Status):** Measured factual line detailing the direct observation.
- **Beat 4 (Analytical Deduction / Forward Action):** Strategic conclusion setting up the next narrative move.
This mirrors high-caliber cinema pacing and gives each MiniMax-H3 latent step optimal density.

### Rule 45: Custom Character Onboarding — 15-clip mix-and-match lock-in, then C2V (Asia, August 2026)
**The drill** (see also `docs/characters/CHARACTER_LOCKIN_DRILL.md`):

1. **Lock-in grid (R2V):** Fifteen **5-second** mid / MCU clips. **Rotate 8 refs** across the dataset. Stack **both** LoRAs and **mix strengths independently in 0.40–0.50** — not one equal pair. Cover the 3×3 of 0.40 / 0.45 / 0.50 plus six off-diagonal intermediates. Pick **one winner**. That clip’s refs **and** both strengths become preset #1. Asia lock-in: `submit_asia_15_lockin.js`.
2. **First continuity series (REF_C2V):** One story, **one location**, **one physical beat per clip**, MCU throughout. Do **not** walk rooms or cook breakfast on the intro chain.
3. **Optional extra bump:** Only if the winner was both-LoRA **0.40**. Otherwise the 15-clip grid already tested 0.45 / 0.50 mixes. Natalia’s later equal-pair A/B (`01957` vs `01962`) is the fallback when the lock-in was a single strength.

**The rule:** Refs and LoRA strengths lock from the **same** winning clip. Do not mix refs from clip A with strengths from clip B without a re-test.

---

## 3. Multi-Scene Movie Production Workflow (`docs/movies/movieXYZ/`)

When starting a multi-scene episode or movie (e.g., `movie0001`, `movie0002`):

### Directory Structure
```
docs/movies/movieXYZ/
├── synopsis.md           # Master overview: cast, act breakdown, scene index, duration
├── scene_01.md           # MiniMax-H3 prompt for Scene 01 (MCP ready)
├── scene_01_dialogue.md  # Companion dialogue & action breakdown for Scene 01
├── scene_02.md           # MiniMax-H3 prompt for Scene 02
├── scene_02_dialogue.md  # Companion dialogue & action breakdown for Scene 02
└── ...
```

### Operational Protocol
1. **Drafting Phase**:
   - Create `docs/movies/movieXYZ/`.
   - Write `synopsis.md` with overall storyline, cast list from `docs/shows/`, act breakdown, and scene duration index.
   - Per Rule 15, include a one-sentence **reason for presence** for every named character (why they are at the setting) in `synopsis.md`, and stagger cast entrances across the opening scenes rather than introducing everyone in Scene 01.
   - For each scene, create two companion files: `scene_XX.md` (the prompt) and `scene_XX_dialogue.md` (human-editable dialogue & action breakdown).
2. **User Review Phase**:
   - Present `synopsis.md` and the scene index to the user for review.
   - **CRITICAL:** DO NOT submit prompts via MCP until the user explicitly reviews the plan and says "go", "run", or approves generation.
3. **Dialogue & Action Updates**:
   - When updating dialogue for a scene (e.g., `update scene 25`), modify `scene_XX_dialogue.md` and re-compile `scene_XX.md` while maintaining all section rules, single-character shot cuts, and non-speaking silence mandates.
4. **Batch MCP Execution**:
   - Upon user approval, submit scene prompts sequentially via the MCP `comfy_submit_t2v` tool.
   - **C2V continuity movies** (one scene that keeps going): do **not** use a T2V batch. Follow **Rule 28** — one `chainFolder`, sequential `comfy_submit_c2v` / `/api/v1/comfy/c2v`, wait for each latent, review every 3 clips.

---

## 4. Show & Character Repository Index & Usability Rules

Always verify character usability in [`docs/CHARACTER_USABILITY_INDEX.md`](CHARACTER_USABILITY_INDEX.md) before writing scene scripts. Characters marked as ❌ **Not Usable** must not be featured in prompts due to poor base model likeness without custom LoRAs.

Pull visual descriptions, attire, voice tags, and visual style from character profiles in `docs/shows/`:

| Show File | Show Name | ✅ Usable Characters | ⚠️ / ❌ Do Not Use |
| :--- | :--- | :--- | :--- |
| `alias.md` | Alias | — | Sydney Bristow |
| `alien.md` | Alien | — | Ellen Ripley |
| `always_sunny.md` | It's Always Sunny in Philadelphia | — | Dennis Reynolds, Charlie Kelly, Frank Reynolds |
| `arcane.md` | Arcane | Jinx, Vi, Caitlyn Kiramman, Silco | — |
| `arrested_development.md` | Arrested Development | — | Michael Bluth, Gob Bluth |
| `avengers.md` | Marvel's Avengers | Loki, Doctor Strange, Deadpool, Wolverine, Tony Stark / Iron Man, Steve Rogers / Captain America, Thor, Natasha Romanoff / Black Widow, Wanda Maximoff / Scarlet Witch, Nick Fury | ⚠️ Star-Lord / Peter Quill, Bruce Banner |
| `babylon_5.md` | Babylon 5 | — | Captain John Sheridan |
| `banshee.md` | Banshee | — | Lucas Hood |
| `big_bang_theory.md` | The Big Bang Theory | Amy Farrah Fowler, Bernadette Rostenkowski, Sheldon Cooper, Leonard Hofstadter, Penny, Howard Wolowitz, Raj Koothrappali | — |
| `bones.md` | Bones | Dr. Temperance Brennan, Seeley Booth | — |
| `breaking_bad.md` | Breaking Bad | Walter White, Jesse Pinkman, Saul Goodman | Gustavo Fring, Hank Schrader, Mike Ehrmantraut, Kim Wexler |
| `brooklyn_nine_nine.md` | Brooklyn Nine-Nine | Jake Peralta, Captain Raymond Holt | — |
| `buffy.md` | Buffy the Vampire Slayer | Buffy Summers, Angel | — |
| `columbo.md` | Columbo | — | Lieutenant Columbo |
| `community.md` | Community | — | Abed Nadir, Troy Barnes, Jeff Winger |
| `curb_your_enthusiasm.md` | Curb Your Enthusiasm | Larry David | — |
| `deadwood.md` | Deadwood | — | Al Swearengen |
| `dexter.md` | Dexter | Dexter Morgan | Debra Morgan, Angel Batista, Vince Masuka |
| `die_hard.md` | Die Hard | John McClane | — |
| `doctor_who.md` | Doctor Who | The Tenth Doctor, Rose Tyler | — |
| `farscape.md` | Farscape | — | John Crichton |
| `fawlty_towers.md` | Fawlty Towers | — | Basil Fawlty |
| `firefly.md` | Firefly | Malcolm Reynolds | Zoe Washburne, Hoban Washburne (Wash), Jayne Cobb, Inara Serra |
| `fresh_prince.md` | The Fresh Prince of Bel-Air | Will Smith | — |
| `friends.md` | Friends | Ross Geller, Joey Tribbiani, Chandler Bing, Rachel Green, Phoebe Buffay | Monica Geller |
| `game_of_thrones.md` | Game of Thrones | Tyrion Lannister, Daenerys Targaryen, Jon Snow | Jaime Lannister, Cersei Lannister, Arya Stark, Ned Stark, Petyr Baelish, Sansa Stark |
| `gladiator.md` | Gladiator | Maximus Decimus Meridius | — |
| `greys_anatomy.md` | Grey's Anatomy | — | Meredith Grey |
| `house_md.md` | House M.D. | Dr. Gregory House | Dr. Lisa Cuddy, Dr. James Wilson |
| `house_of_cards.md` | House of Cards | Frank Underwood | Claire Underwood |
| `how_i_met_your_mother.md` | How I Met Your Mother | Barney Stinson, Ted Mosby | — |
| `hunger_games.md` | The Hunger Games | — | Katniss Everdeen |
| `indiana_jones.md` | Indiana Jones | Indiana Jones | — |
| `james_bond.md` | James Bond | James Bond (Daniel Craig) | James Bond (Sean Connery), James Bond (Timothy Dalton), James Bond (Pierce Brosnan) |
| `kill_bill.md` | Kill Bill | — | The Bride / Beatrix Kiddo |
| `last_week_tonight.md` | Last Week Tonight | John Oliver | — |
| `lord_of_the_rings.md` | Lord of the Rings | Gandalf | Aragorn |
| `lost.md` | Lost | Jack Shephard, John Locke | James "Sawyer" Ford, Kate Austen |
| `lucifer.md` | Lucifer | Lucifer Morningstar, Chloe Decker | — |
| `mad_men.md` | Mad Men | Don Draper | Peggy Olson, Joan Holloway |
| `married_with_children.md` | Married... with Children | — | Al Bundy, Peggy Bundy |
| `mash.md` | M*A*S*H | — | Hawkeye Pierce |
| `monk.md` | Monk | Adrian Monk | Sharona Fleming, Captain Leland Stottlemeyer, Lieutenant Randy Disher, Natalie Teeger |
| `monty_python.md` | Monty Python | — | King Arthur, Sir Lancelot |
| `mr_bean.md` | Mr. Bean | Mr. Bean | — |
| `mr_robot.md` | Mr. Robot | Elliot Alderson, Darlene Alderson | — |
| `parks_and_recreation.md` | Parks and Recreation | — | Leslie Knope, Ron Swanson |
| `pirates_of_the_caribbean.md` | Pirates of the Caribbean | Captain Jack Sparrow | — |
| `psych.md` | Psych | — | Shawn Spencer |
| `pulp_fiction.md` | Pulp Fiction | — | Jules Winnfield |
| `rambo.md` | Rambo | John Rambo | — |
| `remington_steele.md` | Remington Steele | — | Remington Steele |
| `rick_and_morty.md` | Rick and Morty | Rick Sanchez, Morty Smith, Summer Smith, Beth Smith | Jerry Smith |
| `robocop.md` | RoboCop | RoboCop / Alex Murphy | — |
| `scrubs.md` | Scrubs | — | Dr. John "J.D." Dorian |
| `seinfeld.md` | Seinfeld | Jerry Seinfeld, George Costanza, Elaine Benes, Cosmo Kramer | — |
| `sex_and_the_city.md` | Sex and the City | Carrie Bradshaw | Samantha Jones, Charlotte York, Miranda Hobbes |
| `sherlock.md` | Sherlock (BBC) | Sherlock Holmes, Dr. John Watson | — |
| `silence_of_the_lambs.md` | The Silence of the Lambs | Dr. Hannibal Lecter | Clarice Starling |
| `silicon_valley.md` | Silicon Valley | — | Richard Hendricks, Erlich Bachman, Dinesh Chugtai |
| `smallville.md` | Smallville | Clark Kent | — |
| `star_trek_ds9.md` | Star Trek: DS9 | — | Captain Benjamin Sisko |
| `star_trek_tng.md` | Star Trek: The Next Generation | — | Q, Wesley Crusher |
| `stargate_sg1.md` | Stargate SG-1 | — | Teal'c |
| `superman.md` | Superman | — | Superman |
| `supernatural.md` | Supernatural | Bobby Singer, Dean Winchester, Sam Winchester, Castiel | — |
| `terminator.md` | Terminator 2 | — | Sarah Connor |
| `the_cosby_show.md` | The Cosby Show | — | Dr. Cliff Huxtable |
| `the_matrix.md` | The Matrix | — | Trinity |
| `the_office.md` | The Office | Andy Bernard, Michael Scott, Dwight Schrute, Jim Halpert, Pam Beesly | Stanley Hudson, Ryan Howard, Jan Levinson, Angela Martin |
| `the_sopranos.md` | The Sopranos | Tony Soprano | Silvio Dante, Paulie Gualtieri |
| `the_thick_of_it.md` | The Thick of It | — | Malcolm Tucker |
| `the_wire.md` | The Wire | — | Omar Little, Jimmy McNulty |
| `thirty_rock.md` | 30 Rock | Liz Lemon, Jack Donaghy | — |
| `tomb_raider.md` | Lara Croft: Tomb Raider | — | Lara Croft |
| `true_detective.md` | True Detective | — | Rust Cohle |
| `twenty_four.md` | 24 | Jack Bauer | — |
| `wednesday.md` | Wednesday | Wednesday Addams | — |
| `x_files.md` | The X-Files | Fox Mulder, Dana Scully | — |

> This table is generated from the files in `docs/shows/` cross-referenced against [`CHARACTER_USABILITY_INDEX.md`](CHARACTER_USABILITY_INDEX.md). Characters in the right-hand column must not appear in prompts. ⚠️ marks characters with good face likeness but inaccurate voice cloning — usable only in non-speaking roles.

---

## 5. Canonical Prompt Examples

### Example 1: Non-Speaking Silent Fix & Single-Subject Shot Cuts (House & Dexter)

```text
subject_definitions:
<Subject 1> Dr. Gregory House from House M.D.
<Subject 2> Dexter Morgan from Dexter

integrated_multimodal_description:
[Shot 1] Live-action, Princeton-Plainsboro main lobby corridor. A single medium shot of <Subject 1> Dr. Gregory House (S1) alone in frame. House pops a Vicodin pill into his mouth as he limps down the corridor. He pauses, turning his head off-camera, and says in a low, knowing voice: <d>[English in Dr. Gregory House's voice from House M.D.] You had a big secret, didn't you, blood guy?</d>

[Shot 2] At 00:05.000, the shot cuts to a single medium close-up of <Subject 2> Dexter Morgan (S2) alone in frame near the elevator. Dexter Morgan (S2) remains completely silent with his mouth closed, speaking no dialogue. His face is pale and sweating, his eyes wide and distraught as he stares off-camera in quiet shock.

[Shot 3] At 00:09.500, the shot cuts back to a single medium close-up of <Subject 1> Dr. Gregory House (S1) alone in frame. House gives a sly, knowing grin, winks, and says: <d>[English in Dr. Gregory House's voice from House M.D.] Don't worry... everybody lies.</d> House turns and limps away down the corridor with his cane as the screen fades to black.

overall_soundscape:
Pill swallowing sound, cane thumping rhythmically on polished floor, House's low voice, and fading footsteps.

non_diegetic_music:
N/A
```

---

### Example 2: Keycard Physical Action & Close Framing (Malcolm & Dwight)

```text
subject_definitions:
<Subject 1> Malcolm Reynolds from Firefly
<Subject 2> Dwight Schrute from The Office

integrated_multimodal_description:
[Shot 1] Live-action, close medium shot focused on the waist area. <Subject 2> Dwight Schrute (S2) stands distracted, wearing his mustard short-sleeved shirt with a bright white master security keycard prominently clipped to his dark leather belt loop. <Subject 1> Malcolm Reynolds (S1) sidles up beside him. Both characters remain entirely silent with their lips sealed, uttering no spoken dialogue or mumbling. <Subject 1> Malcolm Reynolds (S1) quietly reaches his hand toward the keycard.

[Shot 2] At 00:06.500, the shot cuts to a medium close-up. <Subject 1> Malcolm Reynolds (S1) unhooks the plastic keycard from Dwight's belt loop with his fingers. As <Subject 2> Dwight Schrute (S2) suddenly twitches and turns his torso, Malcolm swiftly slips the keycard into his pocket and smoothly redirects his hand upward to Dwight's collar, adjusting Dwight's necktie with a charming smile. <Subject 1> Malcolm Reynolds (S1) says smoothly: <d>[English in Malcolm Reynolds's voice from Firefly] Just fixing your tie, Commander. Looks a bit lopsided.</d>

overall_soundscape:
Subtle fabric rustle, plastic keycard unclipping snap, and Malcolm's suave voice.

non_diegetic_music:
N/A
```

---

### Example 3: Multi-Subject Shared-Frame Composition (Rule 16)

```text
subject_definitions:
<Subject 1> John McClane (played by Bruce Willis) from Die Hard
<Subject 2> Wednesday Addams (played by Jenna Ortega) from Wednesday

integrated_multimodal_description:
[Shot 1] Live-action, warm ballroom string-light glow against a rain-streaked glass wall. A medium two-shot holding both subjects together in frame: <Subject 1> John McClane (S1) stands on the left of frame in a rumpled tuxedo jacket with an undone bowtie, <Subject 2> Wednesday Addams (S2) stands roughly three feet to his right in a black lace gothic gown, arms crossed, deadpan expression. <Subject 2> Wednesday Addams (S2) remains completely silent with her mouth closed, speaking no dialogue, while listening and reacting only with a single raised eyebrow. <Subject 1> John McClane (S1) turns his head toward her and says: <d>[English in John McClane's voice from Die Hard] Look, I know galas aren't your thing, but could you at least pretend to have fun for ten minutes?</d>

[Shot 2] At 00:05.000, the shot cuts to a single medium close-up of <Subject 2> Wednesday Addams (S2) alone in frame, black lace gothic gown visible at the shoulders. <Subject 1> is not in frame. Wednesday Addams (S2) says flatly, without a trace of a smile: <d>[English in Wednesday Addams's voice from Wednesday] I am having fun, Father. This is my fun face.</d>

[Shot 3] At 00:10.500, the shot cuts back to the same medium two-shot as Shot 1, both subjects held apart in frame at their original positions. <Subject 1> John McClane (S1) lets out a short, tired laugh and shakes his head, then his expression sharpens as he glances past her shoulder toward the darkened hallway. <Subject 2> Wednesday Addams (S2) remains completely silent with her mouth closed, speaking no dialogue, and slowly turns to follow his gaze.

overall_soundscape:
Distant string quartet fading under the dialogue, glass clinking from a nearby bar cart, and rain ticking against the window.

non_diegetic_music:
N/A
```

---

## 6. Pre-Submission Verification Checklist

- [ ] Are task alignment headers (I2VA / FL2VA / L2VA) included at the top when reference images are used?
- [ ] Are section titles written in **plain text** without `**` bolding (`subject_definitions:`, `integrated_multimodal_description:`, `overall_soundscape:`, `non_diegetic_music:`)?
- [ ] Are all characters listed in `subject_definitions:` with show names?
- [ ] Are non-speaking characters explicitly instructed to remain silent with mouths closed (`speaking no dialogue`) to prevent mumbling?
- [ ] Are multi-character rendering glitches avoided by using single-subject shot cuts or clear frame positioning?
- [ ] Are speaker IDs `(S1)`, `(S2)` or compound IDs `(S1,S2)` assigned consistently to all dialogue tags?
- [ ] Is dialogue wrapped in `<d>[Language in Character's voice from Show] text</d>`?
- [ ] Are advanced tags (`<scenetrans>`, `<cutoff>`, off-screen voiceover rules) used when appropriate?
- [ ] Does [Shot 1] omit timecodes while [Shot 2+] include explicit `00:SS.mmm` cut times?
- [ ] Is delivered duration 5s–15s, preferring 12s–15s unless this beat is a short transition, with C2V `durationSeconds` ≈ delivered target + 1s and shot times spread across the requested length?
- [ ] Is each cut timed to its own line (~2.5 words/sec + 1s lead-in) rather than a fixed timecode reused across every scene?
- [ ] Does every shot that outlasts its dialogue close with an explicit `falls silent ... speaking no further dialogue` mandate?
- [ ] Is `overall_soundscape:` free of dialogue and character-voice references, and is any musical score in `non_diegetic_music:` instead?
- [ ] Do shots with no people declare it explicitly and use `subject_definitions:` `N/A`?
- [ ] Are key props or physical actions explicitly framed in tight/medium shots?
- [ ] Are locations, objects/props, and character outfits documented in `synopsis.md` and identically re-used across prompts for visual continuity — **verbatim**, with no shortened or generic restatements?
- [ ] Is there visual location variety across scenes (no more than 2-3 consecutive scenes in the exact same micro-spot without rotating sets or sub-areas)?
- [ ] Does every scene feature lively physical action, environmental interaction, or prop handling rather than static monologues ("Show, Don't Tell")?
- [ ] Are long silent stares at clip ends eliminated by filling the duration with dialogue, multi-beat exchanges, or active physical movement (walking away, closing items, stepping out of frame)?
- [ ] Is delivered duration 5s–15s, preferring 12s–15s unless this beat is a short transition, with C2V `durationSeconds` ≈ delivered target + 1s and shot times spread across the requested length?
- [ ] Are scenes grouped into multi-scene story blocks (30-60s) with clear visual/verbal scene-to-scene handoffs to ensure natural narrative flow?
- [ ] Does the production use more than one shot pattern and more than one camera motion across its scenes?
- [ ] Has every character been checked against `CHARACTER_USABILITY_INDEX.md` (not just the show table) for ❌ / ⚠️ status?
- [ ] Does every named character have a stated, on-screen-legible reason for being present rather than appearing arbitrarily ("dropping from the sky")?
- [ ] Are cast entrances staggered across the opening scenes rather than the full cast appearing in Scene 01?
- [ ] Where a scene calls for it, are multi-subject shared-frame shots used (not only single-subject isolation), with held-apart positioning, one active speaker at a time, and explicit silence mandates for non-speaking subjects in frame?
- [ ] Is environmental lighting and time of day (e.g. 10:00 AM sunlight vs sunset vs night) explicitly specified and kept 100% consistent across all connected scenes in the same location?
- [ ] Are actors who spanned multiple decades explicitly anchored to an exact age/era in `subject_definitions:` and shot descriptions (e.g., "35-year-old Bruce Willis, 1988 Die Hard era") to prevent age-jumping between shots or scenes?
- [ ] Has new/untested character usability been benchmarked via an 8s character roll, sorted into `good/` vs `bad/`, and synced to `CHARACTER_USABILITY_INDEX.md`?
- [ ] In C2V continuations and internal shot cuts, are spatial screen positions (screen-left, screen-center, screen-right) explicitly tracked and specified across scene joins to prevent character position-swapping?
- [ ] In C2V continuations, does Shot 1 state that it opens on the previous clip's last frame (who/where) and describe only what changes — without re-pasting the same establishing location boilerplate at Shot 1 and again at Shot 3?
- [ ] For C2V continuity movies: one chainFolder, sequential wait, batches of 3, positions + lighting logged from the last mp4, one new arrival per clip, 16s request for ~15s delivered (Rule 28)?
- [ ] Does `chainFolder` start with `h3_context/` (Rule 28 triage)?
- [ ] Has every C2V join been classified `same speaker` vs `speaker change`, and does every speaker-change clip carry a silent inherited beat, a named camera transition, and its first `<d>` at 00:02.000 or later (Rule 29)?
- [ ] Is `already speaking, with no silent establishing beat` absent from every speaker-change continuation clip (Rule 29)?
- [ ] Before diagnosing any generation failure: has `Get-CimInstance Win32_Process` been run to confirm exactly one runner is alive, with stale `.lock` / `_progress.json` cleared (Rule 28 triage)?
- [ ] Is Data's mustard-yellow Operations uniform pinned in `subject_definitions:` and restated on his MCU, never red (Rule 31)?
- [ ] Does requested duration match spoken time plus a short settle, without padded silence that the model will spend as dead air between sentences (Rule 32)?
- [ ] On a new C2V seed, are off-screen people omitted from `subject_definitions:` (Rule 33)?
- [ ] For character reveals from doors/entrances in C2V, is the physical emergence split into a silent clip first before prompting dialogue on the established face (Rule 39)?
- [ ] In locked same-speaker C2V continuations, are locked-off camera mandates and verbatim background object descriptors present to prevent room drift (Rule 40)?
- [ ] On continuation clips, is an explicit silent inherited lead-in ("finished previous report, mouth closed, no lips move") included to suppress phantom gibberish before dialogue (Rule 41)?
- [ ] Are prominent props/vehicles strictly declared singular ("the same single...", "Never two...", no interior) to prevent duplicate spawning (Rule 42)?
- [ ] For characters sharing the same division/uniform color (e.g. Geordi and Worf in yellow ops), are high-contrast prosthetic and prop traits pinned in every prompt to prevent twins/merging (Rule 43)?
- [ ] Are complex sequence moments decomposed into focused micro-clips (alert -> silent cascade -> status -> deduction) rather than crammed into a single rushed take (Rule 44)?
- [ ] For a new custom-LoRA character: 15× 5s R2V lock-in with rotating 8 refs and mix-and-match 0.40–0.50 on each LoRA, then a same-location C2V continuity chain (Rule 45 / CHARACTER_LOCKIN_DRILL.md)?
- [ ] Is 2-LoRA + 8-ref strength the character's confirmed preset (Natalia 0.45/0.45), not a blind 0.80 default (Rule 34)?

---

## 7. Location, Object & Clothing Continuity Protocol

When producing multi-scene movies, episodes, or minisodes (`docs/movies/movieXYZ/`), maintaining strict visual continuity across scenes is critical for seamless video generation.

### 1. Location Continuity Registry
- When a location is introduced in a scene (e.g. "Isolation Ward 4B" or "Princeton-Plainsboro Main Lobby"), immediately record its detailed visual specifications in `docs/movies/movieXYZ/synopsis.md` under a **Locations Registry** section.
- **Specification Details**: Include wall color/material, floor type, lighting temperature/sources, background doors/windows, furniture placement, and fixed wall signs or posters.
- **Prompt Re-use**: Every subsequent scene taking place in that location must copy and reference the exact same description string in its prompt text.

### 2. Object & Key Prop Continuity Registry
- When a key object or prop is introduced (e.g. a white security keycard, silver USB drive, leather medical pouch, or specific weapon), record its exact physical details in `synopsis.md` under an **Objects & Props Registry**.
- **Specification Details**: Include material, color, finish, size, attachments, condition, and worn location (e.g. "clipped to right belt loop").
- **Prompt Re-use**: Every scene where that object is held, stolen, or examined must describe the object identically.

### 3. Clothing & Wardrobe Continuity Registry
- Record character attire for every in-universe scene/timeline in `synopsis.md` under a **Wardrobe Registry**.
- **Specification Details**: Include outer layer (jacket/coat), top shirt, collar style, pants, belt, shoes, and worn accessories (glasses, jewelry, badges, watches).
- **Prompt Re-use**: Every scene set within the same timeline/sequence must prompt the character in identical clothing descriptions. Do not rely on generic terms like "suit" or "shirt"—always specify the exact colors, cut, and details.

### 4. Environmental Lighting & Time of Day Continuity Registry
- Record the exact time of day and lighting environment for each location in `synopsis.md` under a **Lighting Registry** (e.g., `bright 10:00 AM morning sunlight filtering through high glass windows`).
- **Prompt Re-use**: Ensure all prompts set in that sequence explicitly include the exact lighting and time of day descriptor string to prevent ambient lighting shifts between scenes.

### 5. Actor Age & Era Continuity Registry
- For any actor whose career spans multiple iconic ages/eras (e.g., Bruce Willis, Harrison Ford, Nathan Fillion), define and register their exact age/era in `synopsis.md` under a **Character Era Registry** (e.g. `Bruce Willis: 35-year-old 1988 Die Hard era with receding dark hair`).
- **Prompt Re-use**: Include this exact era anchor tag in `subject_definitions:` and restate age-defining physical traits in every shot description to ensure the AI model does not render the character at different ages across scenes.

### 6. Setting Rotation & Lively Physical Staging Protocol
- **Setting Rotation Limit (T2V episodes)**: Do not sequence more than 2–3 consecutive **disconnected T2V** scenes in the exact same micro-spot without rotating sets. That is for spliced-clip movies, not C2V.
- **C2V exception**: A continuity chain **stays in one location on purpose**. Rotate coverage (close vs medium, who is in frame) and who enters, not the set. Re-establishing the same wide vista every clip is sandwiching (Rule 27–28), not "setting variety."
- **Physical Action Mandate**: Avoid static "talking heads". Every scene script must include a distinct physical action (handling a prop, a sip, writing, a glance, an entrance) so clips feel alive.

