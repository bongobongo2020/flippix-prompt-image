using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using FlipPix.ComfyUI.Services;
using FlipPix.Core.Interfaces;
using FlipPix.UI.Models;
using FlipPix.UI.Services;
using MessageBox = System.Windows.MessageBox;

namespace FlipPix.UI.ViewModels.Video
{
    /// <summary>
    /// "H3 Experimental" tab — the 🪪🌀 H3 Duo flow with the story-to-clip-chain writing done
    /// <b>one clip per LLM call</b>, against a beat sheet derived from the story first.
    ///
    /// <para><b>Why one call per clip.</b> This tab used to write the whole chain in a single reply:
    /// a tool-call turn that produced a long creative brief, then one turn that was handed the H3
    /// Prompt Writer's system wrapper, the official MiniMax guide and a chain layer (~450 lines of
    /// system prompt) and asked for all N clips at once. A local model cannot hold that. Observed on a
    /// 12-clip run: the reply stopped at 7 clips, the two picture tags started swapping between the
    /// fighters around clip 2, arrived malformed from clip 3 (<c>&lt;Picture 2</c>, <c>&lt;P 1&gt;</c>),
    /// and clip 7 was tag-per-noun word-salad. None of that is fixable downstream, which is why this
    /// file used to carry a brief stabiliser, a runaway truncator, a clip dropper and an opponent
    /// re-tagger — four passes cleaning up after one call that was too big.</para>
    ///
    /// <para><b>The flow now.</b> Three deterministic steps, each small enough for a local model:</para>
    /// <list type="number">
    /// <item><b>The beat sheet.</b> One short call turns the story into a one-line SETTING and exactly
    /// N numbered beats — the story's own action divided across the chain, N being the tab's clip plan
    /// and not the model's to choose. A reply with the wrong count is re-asked once and then filled in
    /// deterministically from the story's own units (<see cref="FallbackBeats"/>), so the step cannot
    /// fail the run.</item>
    /// <item><b>N clip calls.</b> One call per clip, each given the fixed context (style, setting,
    /// cast tags, wardrobe lock) plus three lines of story: the previous beat for continuity, this
    /// clip's beat to write, and the next beat so the clip ends mid-action. ~400 tokens out. A clip
    /// that comes back without its description, or missing a fighter's tag, is asked for once more.
    /// The chain has N clips because the loop ran N times.</item>
    /// <item><b>Stamp and join.</b> Unchanged: each body gets its reference line and wardrobe block,
    /// and the clips are joined behind <c>=== CLIP n of N ===</c> headers into the prompt box, where
    /// Add to Queue turns each one into its own job.</item>
    /// </list>
    ///
    /// <para>The cheap deterministic passes are kept, because they cost nothing and a local model still
    /// slips: field labels are canonicalised, an unpunctuated runaway inside a field is cut back to its
    /// last complete sentence, timestamps are padded to <c>MM:SS.mmm</c>, and the keyframe leftovers the
    /// old base guide taught the model to write — the anchor line, the style block emitted as a second
    /// <c>[Shot 1]</c> — are folded away. Those two are what make H3 render the character sheet as the
    /// video's opening frame.</para>
    ///
    /// <para>Everything else is inherited: the story/scene inputs, the wardrobe derived once and locked
    /// (populated automatically the moment a story lands, as always), the two character cards and their
    /// panel-split sheets, the queue, and the turbo render (draft → 2× latent upscale → finish) on this
    /// tab's own copy of the Duo graph. <see cref="H3ErosViewModel"/> inherits this whole path.</para>
    ///
    /// <para><b>When the run starts.</b> Loading a story .txt derives the wardrobe and nothing else —
    /// the chain is written against the clip plan, and the plan is wrong until the video time says what
    /// it should be. Setting the per-clip length (debounced, so stepping through values is one run) is
    /// what starts the writer; the Analyze button re-runs it by hand at any time.</para>
    /// </summary>
    public partial class H3ExperimentalViewModel : H3DuoViewModel
    {
        public H3ExperimentalViewModel(
            ComfyUIService comfyUIService,
            LMStudioService lmStudioService,
            IAppLogger logger,
            FlipPix.Core.Services.SettingsService settingsService,
            IServiceProvider? serviceProvider,
            WorkflowQueueCoordinator workflowCoordinator,
            IFileDialogService fileDialogService)
            : base(comfyUIService, lmStudioService, logger, settingsService, serviceProvider,
                   workflowCoordinator, fileDialogService)
        {
            // The tab is for long stories — a two-minute chain is what it is for, and the H3 Cast
            // family's 15s default meant every run started by dragging the slider the whole way
            // across. Set here rather than in the base so the other cast tabs keep theirs.
            StoryDurationSeconds = 120;

            // The tab's own store, so its story chains and the I2V tab's takes never share a picker.
            _chainLibrary = new ScenePromptLibrary(AddLog, ScenePromptLibrary.FolderFor(ChainLibraryFolder));
            OpenChainLibraryCommand = new RelayCommand(async () => await OpenChainLibraryAsync());
            SaveChainCommand = new RelayCommand(async () => await SaveCurrentChainAsync(manual: true));

            QueueReadiness = DescribeQueueReadiness();

            AddLog("H3 Experimental initialized — a story is divided into one beat per clip and then " +
                   "written one clip per call; a story derives the wardrobe, and setting the video time " +
                   "runs the writer");

            // Off the constructor's thread: the index is read from disk and this tab is on the Video
            // Generator's startup path.
            _ = PrimeChainLibraryAsync();
        }

        /// <summary>The store the chain library files this tab's story chains under. Its own, so a derived
        /// tab's takes and this one's never share a picker.</summary>
        protected virtual string ChainLibraryFolder => "h3experimental";

        /// <summary>The Duo graph's copy under this tab's name — experiments cannot break the Duo tab's file.</summary>
        protected override string WorkflowFileName => "workflow/video/h3-minimax/h3-experimental.json";

        protected override string OutputSubfolder => "h3_experimental";

        protected override string OutputFileStem => "H3Experimental";

        protected override string OutputFolderName => "H3Experimental";

        protected override string TabDisplayName => "H3 Experimental";

        protected override string QueueFilePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FlipPix", "queue", "h3experimental_queue.json");

        // ── The trigger: the video time is what starts the pipeline ────────────────────────────────

        // Debounce for the auto-run below — cancelled and re-armed on every length change so stepping
        // through values fires one run, not one per click.
        private CancellationTokenSource? _autoAnalyzeCts;

        private bool _researchPrompts;

        /// <summary>
        /// The 📚 <b>Researched prompts</b> switch: which of the two prompt builds writes the chain.
        ///
        /// <para><b>Off</b> — the build this tab has always used: <c>h3pw_clip.md</c>, a cut roughly every
        /// 1.25 seconds, and no instruction about shot size, silence, screen sides or the audio fields.
        /// Nothing about a run with this off differs by a byte from before the switch existed.</para>
        ///
        /// <para><b>On</b> — <see cref="H3ResearchPrompt"/>: <c>h3pw_clip_research.md</c> plus a per-clip
        /// rule block, both written off the guides in <c>prompts/documents/</c>. The shot budget drops to
        /// the guide's own three-to-five cuts with the cut times handed over explicitly, every shot is held
        /// at medium or closer, non-speaking mouths are made explicitly silent, a spoken line gets the
        /// speaker's face alone in frame, the fighters are given screen sides, the lighting is locked to
        /// the beat sheet's setting line, and speech is kept out of <c>overall_soundscape:</c>.</para>
        ///
        /// <para>It is a switch rather than a replacement precisely so the two can be run against the same
        /// story, the same cast and the same seeds, and compared on the finished clips.</para>
        /// </summary>
        public bool ResearchPrompts
        {
            get => _researchPrompts;
            set
            {
                if (_researchPrompts == value) return;
                _researchPrompts = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResearchPromptsSummary));
            }
        }

        /// <summary>The line under the switch, saying what it will do to the run about to be written.</summary>
        public string ResearchPromptsSummary
        {
            get
            {
                var len = ClampLength(LengthSeconds);
                return ResearchPrompts
                    ? $"On — the MiniMax-H3 guide's build: {H3ResearchPrompt.ShotCount(len)} shots per " +
                      $"{len:0.#}s clip (cuts at " +
                      $"{string.Join(", ", H3ResearchPrompt.CutTimes(len, H3ResearchPrompt.ShotCount(len)))}" +
                      "), medium-or-closer framing, silence mandates, isolated speakers, screen sides, a " +
                      "lighting lock and voices kept out of the soundscape."
                    : $"Off — the shipped build: {ShippedShotCount(len)} shots per {len:0.#}s clip, no " +
                      "framing floor, no silence mandate, no screen sides. Leave it off for the clips you " +
                      "want to compare against.";
            }
        }

        /// <summary>The shot budget this tab has always used — a cut roughly every 1.25 seconds.</summary>
        private static int ShippedShotCount(double seconds) =>
            Math.Clamp((int)Math.Round(seconds * 0.8, MidpointRounding.AwayFromZero), 6, 14);

        /// <summary>
        /// A story .txt landing derives the wardrobe (the stock debounce on the story text already does
        /// that) but does <b>not</b> start the chain — the clip plan is wrong until the video time says
        /// what it should be, and a chain written against the wrong plan is a chain written again. The
        /// run starts here instead, once the per-clip length is set: debounced two seconds so arrowing
        /// through values fires a single run against the value you settle on.
        /// </summary>
        protected override void OnLengthSecondsChanged()
        {
            // The switch's line quotes this clip's shot count and cut times, so it moves with the slider.
            OnPropertyChanged(nameof(ResearchPromptsSummary));

            if (!HasStoryText) return; // the prompt-writer flow is story-driven; nothing to auto-run
            // A recall is restoring the length a saved chain was written at — the chain is already written,
            // and running the writer again would overwrite it two seconds later.
            if (_restoringChain) return;

            _autoAnalyzeCts?.Cancel();
            _autoAnalyzeCts?.Dispose();
            var cts = new CancellationTokenSource();
            _autoAnalyzeCts = cts;
            _ = AutoAnalyzeAfterLengthChangeAsync(cts);
        }

        private async Task AutoAnalyzeAfterLengthChangeAsync(CancellationTokenSource cts)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
                if (!CanAnalyze || IsAnalyzing) return;

                var len = ClampLength(LengthSeconds);
                AddLog($"Video time set — {len:0.#}s per clip → {PlannedClipCount} clips. " +
                       "Running the H3 Prompt Writer...");
                await AnalyzeAsync();
            }
            catch (OperationCanceledException) { }
            finally
            {
                if (ReferenceEquals(_autoAnalyzeCts, cts))
                {
                    _autoAnalyzeCts = null;
                    cts.Dispose();
                }
            }
        }

        /// <summary>
        /// The stock file-load behaviour plus one log line saying where the go button moved to — the
        /// wardrobe derives on its own as always, and the chain waits for the video time.
        /// </summary>
        protected override async Task LoadStoryFileAsync()
        {
            await base.LoadStoryFileAsync();
            if (HasStoryText)
                AddLog("Story loaded — the wardrobe is being derived. Set the video time (clip length) " +
                       "and the H3 Prompt Writer will run on its own.");
        }

        // ── The flow ────────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Story runs are written clip by clip: one call for the beat sheet, then one call per clip. A
        /// scene image with no story keeps the stock flow — there is nothing to divide into beats.
        /// </summary>
        protected override async Task AnalyzeAsync()
        {
            if (!HasStoryText)
            {
                await base.AnalyzeAsync();
                return;
            }

            IsAnalyzing = true;
            IsWritingPrompt = true;
            _analyzeCts?.Dispose();
            _analyzeCts = new CancellationTokenSource();
            var token = _analyzeCts.Token;
            // What the status line said before the writer borrowed it — a render in flight owns that line
            // and gets it back untouched when the writer is done.
            var statusBeforeWriting = ProcessingStatus;

            try
            {
                var model = await ResolveLlmModelAsync(token);
                if (model == null) return;

                var len = ClampLength(LengthSeconds);
                var clipCount = PlannedClipCount;

                AddLog($"H3 Prompt Writer: writing a {clipCount}-clip chain ({clipCount} × {len:0.#}s = " +
                       $"{clipCount * len:0.#}s continuous) one clip at a time — via " +
                       $"{_lmStudioService.DescribeTarget(model)}");
                if (!VisualStyle.IsAuto)
                    AddLog($"Visual style locked: {VisualStyle.Name}");
                // Always said out loud, both ways: a chain is filed to the library the moment it lands and
                // the only record of which build wrote it is this line.
                AddLog(ResearchPrompts
                    ? H3ResearchPrompt.DescribeRun(clipCount, len)
                    : H3ResearchPrompt.DescribeShippedRun(len));

                if (StoryText.Length > 20000)
                    AddLog($"WARNING: the story is {StoryText.Length:N0} characters — a local model will very " +
                           "likely truncate it. Cut it down to the beats you want on screen.");

                // The wardrobe is still decided here, ahead of the chain, exactly as on the Duo tab — every
                // clip call is handed it verbatim and the sheets are built from it.
                if (!await EnsureWardrobeAsync(token, model))
                    AddLog("WARNING: the wardrobe could not be written — the clips will each describe the " +
                           "outfits themselves, which is where between-clip costume changes come from.");

                // ── Step 1 — the beat sheet: the story divided into exactly clipCount beats ─────────
                ProcessingStatus = $"H3 Prompt Writer: dividing the story into {clipCount} beats...";
                var (setting, beats) = await BuildBeatSheetAsync(model, clipCount, len, token);

                // ── Step 2 — one call per clip ─────────────────────────────────────────────────────
                var system = await ReadSystemPromptAsync(
                    ResearchPrompts ? H3ResearchPrompt.ClipSystemPromptFile : ClipSystemPromptFile, token);
                // Off: this tab's own pacing — roughly one cut per 1.25s, floored at 6 so a short clip is
                // still cut like a fight and capped at 14 so a long one stays inside 500 words.
                // On: the MiniMax-H3 guide's pacing instead — three to five shots across the clip, which is
                // what its worked 15-second structures use (Rule 9 A–C, Rule 13, Rule 24).
                var shots = ResearchPrompts ? H3ResearchPrompt.ShotCount(len) : ShippedShotCount(len);

                var clipBodies = await ClipChainWriter.WriteAsync(
                    _lmStudioService, model, system, clipCount,
                    buildRequest: (i, reason) =>
                        BuildClipRequest(setting, beats, i, clipCount, len, shots, reason),
                    normalize: NormalizeClipBody,
                    validate: (_, body) => ValidateClip(body),
                    onProgress: (n, total) =>
                        ProcessingStatus = $"H3 Prompt Writer: writing clip {n} of {total}...",
                    log: AddLog,
                    describe: b => $"{b.Length:N0} chars, {CountShots(b)} shots",
                    token: token);

                // ── Step 3 — the deterministic passes, then stamp and join ─────────────────────────
                // Each is cheap and each catches something a local model still slips through, even one
                // clip at a time:
                // 1. Field labels canonicalised — every pass below matches them ordinally against the
                //    guide's spelling, and a model that writes "## Overall Soundscape:" would otherwise
                //    read as a clip with no fields at all.
                // 2. An unpunctuated runaway inside a field cut back to its last complete sentence.
                // 3. Timestamps padded to the guide's MM:SS.mmm shape — models write "00:9.3".
                // 4. The keyframe leftovers cut: an anchor line ahead of the fields, and a [Shot] marker
                //    with no timestamp behind it — which is how a writer emits its style/setting
                //    restatement, as a second [Shot 1] ahead of the real opening shot. Both tell H3 the
                //    reference photographs are the frame it opens on, and that is the character sheet
                //    showing up inside the video.
                // 5. Stamping is NON-selective on this tab: both cast members' references and wardrobe
                //    line land in every clip even if a body still forgot the tag, because a two-hander
                //    fight has both fighters on screen throughout — clipping either one's references is
                //    how the duplicate-of-self render happens.
                var bodies = clipBodies
                    .Select((b, i) => SanitizeClipFields(CanonicalizeFieldLabels(b), i + 1))
                    .ToList();
                bodies = KeepRenderableClips(bodies);
                bodies = bodies
                    .Select(b => NormalizeTimestamps(b, len))
                    .Select((b, i) => NormalizeShots(b, i + 1))
                    .ToList();

                var cleaned = JoinClips(bodies
                    .Select(b => CastPromptStamp.Apply(b, Panels1, Panels2, CastWardrobe,
                                                       selectiveCast: false, CastDescriptor))
                    .Where(c => c.Length > 0)
                    .ToList());
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    Prompt = cleaned;
                    var written = PromptClipCount;
                    AddLog(written > 1
                        ? $"Chain written by the H3 Prompt Writer ({written} clips, {cleaned.Length} chars, " +
                          $"{CountShots(cleaned)} shots total)"
                        : $"Prompt written by the H3 Prompt Writer ({cleaned.Length} chars, {CountShots(cleaned)} shots)");

                    if (written != clipCount)
                        AddLog($"WARNING: asked for {clipCount} clip(s) but the writer returned {written}. " +
                               "Add to Queue enqueues what is in the prompt box — re-run Analyze, or edit the " +
                               "headers by hand.");

                    // The both-fighters check, said out loud on the bodies: the stamped reference line names
                    // both characters in every clip by construction now, so this warning is about the prose —
                    // a body still naming only one fighter leaves the opponent's actions tied to the stamped
                    // line alone. Worth re-running Analyze when it appears.
                    if (HasCharacter2)
                    {
                        var untagged = SplitClips(cleaned)
                            .Select((body, i) => (Index: i + 1,
                                                 Tagged: body.Contains("<Picture 1>", StringComparison.Ordinal) &&
                                                        body.Contains("<Picture 2>", StringComparison.Ordinal)))
                            .Where(c => !c.Tagged)
                            .Select(c => c.Index)
                            .ToList();
                        if (untagged.Count > 0)
                            AddLog($"WARNING: clip(s) {string.Join(", ", untagged)} still name only one fighter — " +
                                   "the opponent in those clips rides on the stamped reference line instead of " +
                                   "their own tag. Re-run Analyze, or tag <Picture 2> in those clips by hand.");
                        else
                            AddLog("Cast check: every clip names both fighters — no duplicate-of-self renders.");
                    }

                    // Filed as soon as it exists: a chain costs a long llama-server turn and is not
                    // reproducible run to run, so it is worth keeping before anything else can go wrong.
                    await SaveCurrentChainAsync(manual: false);

                    var drift = DescribeWardrobeDrift(SplitClips(cleaned).Select(CastPromptStamp.Strip).ToList());
                    if (drift != null)
                        AddLog(HasCastWardrobe
                            ? $"Note: the clip bodies describe the cast's appearance and {drift}. Every clip " +
                              "carries the same wardrobe block ahead of its description and that block outranks " +
                              "them, so the outfits should still hold — but if a clip comes out wrong, " +
                              "harmonise those words too."
                            : $"WARNING: the clips describe the cast's appearance and {drift}, and there is no " +
                              "wardrobe locked to override them — they will change outfits between clips.");
                }
                else
                {
                    AddLog("WARNING: the H3 Prompt Writer returned an empty result");
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                AddLog($"ERROR during analysis: {ex.Message}");
                MessageBox.Show($"H3 Prompt Writer failed:\n{ex.Message}", "H3 Experimental",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsWritingPrompt = false;
                IsAnalyzing = false;
                _analyzeCts?.Dispose();
                _analyzeCts = null;
                // The writer's own status line is not left standing: it said "writing the clip chain..."
                // for as long as the tab stayed open, which reads as a run that never finished — and it
                // was the only thing on screen explaining why Add to Queue was still greyed out. It now
                // reports what the queue is actually waiting for.
                if (IsProcessing || IsProcessingQueue) ProcessingStatus = statusBeforeWriting;
                else RefreshQueueReadinessStatus(force: true);
            }
        }

        // The last readiness line this tab wrote. The status line is shared with the render pipeline, so it
        // is only ever rewritten while it still says what we last put there — a render's own progress is
        // never stomped.
        private string _readinessStatus = string.Empty;
        private string _queueReadiness = string.Empty;

        /// <summary>
        /// The reason Add to Queue is enabled or greyed out, always current, on a line of its own under the
        /// button.
        ///
        /// <para>This exists because the status line cannot carry it. That line belongs to whatever is
        /// rendering, so <see cref="RefreshQueueReadinessStatus"/> stays silent for the whole length of a
        /// queue run — and a queue run is exactly when this tab is used to prepare the next story, which is
        /// when the button being greyed out needs explaining most.</para>
        /// </summary>
        public string QueueReadiness
        {
            get => _queueReadiness;
            private set { if (_queueReadiness != value) { _queueReadiness = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// Puts the reason Add to Queue is greyed out on the status line, and keeps it current: the usual
        /// answer is the character sheets, and they finish building long after the writer has stopped.
        /// </summary>
        private void RefreshQueueReadinessStatus(bool force = false)
        {
            if (IsAnalyzing || IsBuildingSheets || IsProcessing || IsProcessingQueue) return;
            // The writer's own line is claimed on the way out (force); after that the line is only ever
            // updated while it still says what we last wrote.
            if (!force && !string.IsNullOrEmpty(ProcessingStatus) &&
                !string.Equals(ProcessingStatus, _readinessStatus, StringComparison.Ordinal)) return;

            _readinessStatus = DescribeQueueReadiness();
            ProcessingStatus = _readinessStatus;
        }

        protected override void OnCanExecuteChanged()
        {
            base.OnCanExecuteChanged();
            // Unconditional, unlike the status line: this one has no other owner to defer to.
            QueueReadiness = DescribeQueueReadiness();
            RefreshQueueReadinessStatus();
        }

        /// <summary>
        /// The reason <see cref="H3CastViewModel.CanGenerate"/> is false, said out loud. Building the sheets
        /// is the usual answer: the chain writes fine without them, but a job cannot be queued until every
        /// loaded character has one.
        /// </summary>
        private string DescribeQueueReadiness()
        {
            if (IsWritingPrompt)
                return "The H3 Prompt Writer is still writing — Add to Queue unlocks when the chain lands.";
            if (string.IsNullOrWhiteSpace(Prompt))
                return "No prompt written — press Analyze to run the H3 Prompt Writer.";

            var clips = PromptClipCount > 1 ? $"{PromptClipCount}-clip chain written" : "Prompt written";

            if (!HasCharacter1)
                return $"{clips} — load character 1 and build the sheets before queueing.";
            if (!AllSheetsReady)
                return $"{clips} — press 🃏 Build Sheets; Add to Queue stays off until every " +
                       "character has a sheet." +
                       (IsProcessing || IsProcessingQueue
                            ? " The build waits for the GPU and starts when the current render finishes."
                            : string.Empty);

            // Add to Queue only stages the job now — ▶ Generate is what starts the GPU — so the
            // readiness line has to say which of the two the tab is waiting on.
            if (HasPendingItems)
                return IsProcessingQueue
                    ? $"{clips} — ready to queue; the queue is running, so it renders after what is "
                      + "already in it."
                    : $"{clips} — ready to queue. Nothing is rendering: press ▶ Generate to start "
                      + "the queue when you have added everything you want in this run.";

            return $"{clips} — ready to queue. Queueing does not start a render; press ▶ Generate "
                   + "when the queue holds everything you want in this run.";
        }

        /// <summary>The base tab writes the whole chain in one reply; this one writes it clip by clip, and
        /// the line under the duration slider is the only place that difference is visible before a run
        /// starts.</summary>
        public override string ClipPlanSummary
        {
            get
            {
                var clip = ClampLength(LengthSeconds);
                var n = PlannedClipCount;
                if (n <= 1) return $"One clip of {clip:0.#}s — a single H3 pass.";
                return $"{n} clips × {clip:0.#}s → {n * clip:0.#}s of video. Analyze divides the story into " +
                       $"{n} beats and then writes one clip per beat, one call each; Add to Queue enqueues " +
                       "one job per clip, and they are joined into a single file when the last one lands.";
            }
        }

        // ── Step 1: the beat sheet ─────────────────────────────────────────────────────────────────

        /// <summary>The story divided into exactly <paramref name="clipCount"/> beats, plus the one-line
        /// setting every clip restates. All of the work is <see cref="StoryBeatSheet"/>'s, shared with the
        /// 🪪🎬 H3 Multi and 🪪👥⚡ H3 Cast Hybrid tabs; this only says how the cast is named here.</summary>
        private Task<(string Setting, List<StoryBeatSheet.StoryBeat> Beats)> BuildBeatSheetAsync(
            string model, int clipCount, double len, CancellationToken token)
        {
            var castBrief = HasCharacter2
                ? "There are two characters. Call them CHARACTER 1 and CHARACTER 2 and nothing else — never " +
                  "by the names the story gives them. Read which of the story's people is which from the " +
                  "order the story introduces them, and keep that mapping identical in every beat: whoever " +
                  "strikes in beat 3 carries the same number in beat 9."
                : "There is one character. Call them CHARACTER 1 and nothing else — never by the name the " +
                  "story gives them.";

            return StoryBeatSheet.WriteAsync(
                _lmStudioService, model, StoryText, clipCount, len, castBrief,
                perBeatCast: false,
                imagePath: HasSceneImage ? SceneImagePath : null,
                log: AddLog,
                token: token,
                // Two of the guide's failures are decided here rather than in the clip writer: a beat that
                // carries a multi-stage locomotion change tears the motion latent (Rule 35 / C2V §4.4), and
                // a beat that carries three narrative moments comes back rushed (Rule 44). Null with the
                // switch off, so the shared beat sheet is byte-for-byte what every other tab sends.
                extraRules: ResearchPrompts ? H3ResearchPrompt.BeatSheetRules : null);
        }

        // ── Step 2: one call per clip ──────────────────────────────────────────────────────────────

        /// <summary>The base cleanup plus this tab's field-label canonicalisation — a writer that gets
        /// terser as a chain goes on starts typing the three labels as headings or bullets
        /// ("## Overall Soundscape:"), and every pass after this one matches them ordinally against the
        /// guide's spelling.</summary>
        protected override string NormalizeClipBody(string raw) =>
            CanonicalizeFieldLabels(base.NormalizeClipBody(raw));

        /// <summary>
        /// What makes a clip renderable here: the description H3 renders from, and — in a two-hander —
        /// both fighters named by their tags. A fighter left as an untagged pronoun renders as a duplicate
        /// of the tagged one, which is the failure this tab exists to avoid.
        /// </summary>
        private string? ValidateClip(string body)
        {
            if (!HasFieldContent(body, ClipFieldLabels[0]))
                return "it carried no integrated_multimodal_description to render. Reply with the three " +
                       "fields and nothing else, starting with that label.";

            if (HasCharacter2 && !NamesBothFighters(body))
                return "it did not name both characters by their tags. Every character in the beat above " +
                       "appears as <Picture 1> or <Picture 2> — at their first appearance and wherever they " +
                       "are struck, grabbed, named or reacted to. Write it again.";

            return null;
        }

        /// <summary>
        /// One clip's user message: the fixed context every clip shares (style, setting, cast tags, wardrobe
        /// lock, length, shot count) and the three lines of story that make this clip this clip — the beat
        /// before it for continuity, its own beat to write, and the beat after it so it ends mid-action.
        ///
        /// <para>It is short on purpose. What used to be sent here was a thousand-word creative brief for the
        /// whole chain, and a model handed the whole story writes a little of all of it into every clip.</para>
        /// </summary>
        private string BuildClipRequest(
            string setting, IReadOnlyList<StoryBeatSheet.StoryBeat> beats, int index, int clipCount,
            double seconds, int shots, string rejection)
        {
            var beat = beats[index];

            var cast = HasCharacter2
                ? "CAST — two reference photographs are attached to this clip. <Picture 1> is CHARACTER 1 " +
                  $"(a {CastDescriptor.SexOf(1) ?? "person"}); <Picture 2> is CHARACTER 2 " +
                  $"(a {CastDescriptor.SexOf(2) ?? "person"}). The beat below says which of them does what — " +
                  "keep the numbers exactly as it uses them, and name BOTH by their tags in this clip."
                : "CAST — one reference photograph is attached to this clip. <Picture 1> is CHARACTER 1 " +
                  $"(a {CastDescriptor.SexOf(1) ?? "person"}).";

            var wardrobe = HasCastWardrobe
                ? "WARDROBE — already decided, not yours to choose. Each line opens 'Character N wears …'; " +
                  "attach the garments after that prefix to that character's tag the first time they appear " +
                  "in this clip — '<Picture N>, wearing <those garments>,' — in exactly these words. This is " +
                  "the only clothing wording you may use; where the beat describes clothing differently, this " +
                  "wins:\n" + CastWardrobe.Trim()
                : "WARDROBE — none was decided. Read the outfits off the setting, write them out once in full " +
                  "when each character first appears, and keep that wording for the rest of the clip.";

            var location = setting.Length > 0
                ? $"SETTING — the same in every clip of this chain, restated inside [Shot 1]:\n{setting}"
                : "SETTING — read it off the beat below, and restate it inside [Shot 1].";

            var previous = index > 0
                ? "THE CLIP BEFORE THIS ONE has already been rendered and showed this — do NOT show it " +
                  $"again:\n{beats[index - 1].Text}"
                : "This is the chain's FIRST clip: it opens the video, already in motion.";

            var next = index + 1 < beats.Count
                ? "THE CLIP AFTER THIS ONE will show this — do NOT reach into it; end this clip mid-action, " +
                  $"on its way there:\n{beats[index + 1].Text}"
                : "This is the chain's LAST clip: the story's final moment lands inside it.";

            var part = StoryBeatSheet.DescribePart(beat);

            var s = seconds.ToString("0.##", CultureInfo.InvariantCulture);
            var whole = (int)Math.Floor(seconds);
            var millis = (int)Math.Round((seconds - whole) * 1000);

            // The researched build's rule block sits between the fixed context and the beat: after the
            // cast and the wardrobe it refers to, and ahead of the action it constrains. Every rule in
            // it is cited to the guides in prompts/documents/ — see H3ResearchPrompt.
            var research = ResearchPrompts
                ? H3ResearchPrompt.RulesFor(index, HasCharacter2, seconds, shots, setting) + "\n\n"
                : string.Empty;

            // The shot line is the one piece of the fixed context the two builds word differently: the
            // researched build hands over the exact cut times, because a writer left to choose them
            // reuses the ones it saw in an example whatever this clip's length is (Rule 27's timestamp
            // note), which puts the last beat a third of the way in and leaves the tail as dead air.
            var pacing = ResearchPrompts
                ? $"THIS IS CLIP {index + 1} OF {clipCount}. It is {s} seconds long. Its shot count and " +
                  "its cut times are in the SHOT PLAN above and are not yours to change."
                : $"THIS IS CLIP {index + 1} OF {clipCount}. It is {s} seconds long and carries about " +
                  $"{shots} shots — one cut roughly every second and a half. Every timestamp after " +
                  $"[Shot 1] falls inside 00:00.000–{whole / 60:00}:{whole % 60:00}.{millis:000}.";

            return
                // The mode line first: it is the one thing that decides whether H3 opens on the scene or on
                // the reference photographs themselves.
                "Mode: character-reference video. The attached pictures are studio reference photographs of " +
                "the cast — plain backdrop, neutral standing pose, shot for identity alone. They are NOT " +
                "frames of this video and the viewer never sees them. Write no alignment or anchor line; " +
                "begin with integrated_multimodal_description: and open [Shot 1] on the setting and the " +
                "action below.\n\n" +
                H3VisualStyles.Rule(VisualStyle) + "\n" +
                $"{location}\n\n" +
                $"{cast}\n\n" +
                $"{wardrobe}\n\n" +
                research +
                $"{pacing}\n\n" +
                $"{previous}\n\n" +
                $"THIS CLIP'S ACTION — expand ONLY this, and fill the whole {s} seconds with it:\n" +
                $"{beat.Text}{part}\n\n" +
                $"{next}\n\n" +
                "Reply with the three fields and nothing else.";
        }


        /// <summary>The three H3 field labels, in the order the guide writes them.</summary>
        private static readonly string[] ClipFieldLabels =
        {
            "integrated_multimodal_description:",
            "overall_soundscape:",
            "non_diegetic_music:",
        };

        /// <summary>
        /// The same three labels as the writer actually types them: any case, any of the three word
        /// separators, and with a markdown heading or bullet in front. <c>CleanOutput</c> already strips
        /// <c>**</c>, so what survives is <c>## Overall Soundscape:</c>, <c>- non-diegetic music:</c>,
        /// <c>Integrated Multimodal Description:</c> and so on. Every downstream pass matches the labels
        /// with an ordinal comparison against the canonical spelling, so a clip written in any of those
        /// variants read as a clip with no fields at all — which is what dropped ten clips of a twelve-clip
        /// chain on the observed run. Anchored to the start of a line: a field label is something the
        /// writer puts on its own line, and an unanchored match would rewrite the words mid-description.
        /// </summary>
        private static readonly (Regex Pattern, string Canonical)[] FieldLabelVariants =
            ClipFieldLabels.Select(label => (
                new Regex(
                    @"^[ \t]*(?:[-*•>]\s*)?(?:#{1,6}\s*)?" +
                    Regex.Escape(label.TrimEnd(':')).Replace("_", @"[ _\-]") +
                    @"[ \t]*:[ \t]*",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
                label + " ")).ToArray();

        /// <summary>
        /// Rewrites every recognised spelling of the three field labels into the canonical one, so the
        /// sanitizer, the structure check and the shot pass all see the fields the writer meant to write.
        /// A body already in canonical form passes through unchanged.
        /// </summary>
        private static string CanonicalizeFieldLabels(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return body;
            foreach (var (pattern, canonical) in FieldLabelVariants)
                body = pattern.Replace(body, canonical);
            return body;
        }

        /// <summary>
        /// The base guide's keyframe alignment instructions — the I2VA anchor line and the FL2VA/L2VA
        /// alignment line. Both are meaningless on this tab, where the pictures are studio reference
        /// photographs rather than frames, and telling H3 that a reference is "fully referenced" at the
        /// 0.00-second mark is precisely how the character sheet ends up as the video's opening frame.
        /// The per-clip system prompt (<c>h3pw_clip.md</c>) forbids them in words; they are cut here as
        /// well, because models carry the habit in from the MiniMax guide they were trained on.
        /// </summary>
        private static readonly Regex KeyframeAnchorLineRegex = new(
            @"^[ \t]*(?:For the target video,[^\n]*?fully referenced\.?|How the reference pictures align[^\n]*)[ \t]*$",
            RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// The runaway guard applied field by field <i>inside</i> one clip.
        /// <see cref="TruncateDegenerateTail"/> only ever sees the end of the whole reply, so a clip that
        /// degenerated in the middle of the chain — the common case, because the model recovers at the next
        /// field label and writes the following clips normally — carried its word-salad through untouched
        /// and rendered it. Cutting each field back to its own last complete sentence removes the salad and
        /// keeps the clip: the structure survives, so the clip is not dropped for the sake of one bad field.
        /// A field that is salad end to end comes back empty, and
        /// <see cref="KeepRenderableClips"/> then decides whether what is left still renders.
        /// </summary>
        private string SanitizeClipFields(string body, int clipNumber)
        {
            var marks = new List<(string Label, int Index)>();
            foreach (var label in ClipFieldLabels)
            {
                var index = body.IndexOf(label, StringComparison.Ordinal);
                if (index >= 0) marks.Add((label, index));
            }
            if (marks.Count == 0) return body;
            marks.Sort((a, b) => a.Index.CompareTo(b.Index));

            var parts = new List<string>();
            var preamble = body[..marks[0].Index].Trim();
            if (preamble.Length > 0)
            {
                var kept = KeyframeAnchorLineRegex.Replace(preamble, string.Empty).Trim();
                if (kept.Length != preamble.Length)
                    AddLog($"Clip {clipNumber}: the base guide's keyframe anchor line was dropped "
                           + "— the attached pictures are reference photographs of the cast, not "
                           + "frames of the video, and an anchor line renders them as the first frame.");
                preamble = kept;
            }
            if (preamble.Length > 0) parts.Add(preamble);

            var removed = 0;
            for (var i = 0; i < marks.Count; i++)
            {
                var start = marks[i].Index + marks[i].Label.Length;
                var end = i + 1 < marks.Count ? marks[i + 1].Index : body.Length;
                var text = body[start..end].Trim();

                var cut = DegenerateCutIndex(text);
                if (cut >= 0)
                {
                    removed += text.Length - cut;
                    text = text[..cut].TrimEnd();
                }

                parts.Add($"{marks[i].Label} {text}".TrimEnd());
            }

            if (removed > 0)
                AddLog($"WARNING: clip {clipNumber} degenerated — {removed:N0} characters of unpunctuated " +
                       "word-salad cut back to the last complete sentence in the field(s) that ran away.");

            return string.Join("\n\n", parts);
        }

        /// <summary>Whether one field label is present AND followed by something to render. A label the
        /// runaway guard emptied is not a field the generator can use, so it does not count as present.</summary>
        private static bool HasFieldContent(string body, string label)
        {
            var index = body.IndexOf(label, StringComparison.Ordinal);
            if (index < 0) return false;

            var rest = body[(index + label.Length)..];
            foreach (var other in ClipFieldLabels)
            {
                if (string.Equals(other, label, StringComparison.Ordinal)) continue;
                var next = rest.IndexOf(other, StringComparison.Ordinal);
                if (next >= 0) rest = rest[..next];
            }
            return rest.Trim().Length > 0;
        }


        // ── The runaway guard ────────────────────────────────────────────────────────────────────

        /// <summary>
        /// The longest run of characters without sentence-ending punctuation a healthy reply can contain.
        /// Honest H3 prose punctuates every 100–200 characters (even a bare timestamp list carries the
        /// decimal points of <c>MM:SS.mmm</c>); the runaway this guard exists for produced 141,000
        /// characters with no terminator at all. 1,200 is a handful of sentences of headroom — far enough
        /// from anything an honest clip writes that the guard cannot fire on one, near enough that the
        /// guard trips within a paragraph of the model leaving the rails.
        /// </summary>
        private const int RunawaySuffixLimit = 1200;

        /// <summary>
        /// Where a reply's unpunctuated word-salad begins, as an index to cut at — the character after the
        /// last sentence terminator (<c>.</c> <c>!</c> <c>?</c>) before the runaway — or -1 when the text is
        /// healthy. A <c>.</c> between two digits is a decimal point, not a terminator: the guide's
        /// <c>MM:SS.mmm</c> timestamps are full of them, and counting one would cut the clip at the last
        /// timestamp instead of the last sentence — leaving a dangling <c>[Shot 5] At 00:10.</c> fragment
        /// at the end of the truncated body. The detector is otherwise deliberately dumb: a suffix with no
        /// sentence punctuation at all is not a property any honest H3 clip has, however creative the
        /// model gets.
        /// </summary>
        private static int DegenerateCutIndex(string text)
        {
            if (string.IsNullOrEmpty(text)) return -1;

            for (var i = text.Length - 1; i >= 0; i--)
            {
                var c = text[i];
                if (c != '.' && c != '!' && c != '?') continue;
                if (c == '.' && i > 0 && i + 1 < text.Length &&
                    char.IsDigit(text[i - 1]) && char.IsDigit(text[i + 1]))
                    continue; // decimal point — keep scanning for the real last sentence

                return text.Length - (i + 1) > RunawaySuffixLimit ? i + 1 : -1;
            }

            // No sentence terminator anywhere: degenerate iff the whole text is one long runway.
            return text.Length > RunawaySuffixLimit ? 0 : -1;
        }

        /// <summary>Whether a clip body (canonical form) names BOTH fighters by tag. The hard rule this
        /// tab enforces: a two-person fight has both fighters on screen in every clip, and a fighter who
        /// appears only as an untagged pronoun renders as the other fighter fighting a duplicate of
        /// themselves — H3 casts every unnamed body from whichever references the clip was sent.</summary>
        private static bool NamesBothFighters(string body) =>
            body.Contains("<Picture 1>", StringComparison.Ordinal) &&
            body.Contains("<Picture 2>", StringComparison.Ordinal);

        /// <summary>
        /// The clips of a written chain that are worth queueing, with what was missing from the rest said
        /// out loud.
        ///
        /// <para>The bar is <c>integrated_multimodal_description:</c> — the field H3 actually renders from.
        /// A clip without it is a clip with nothing to render and is dropped, as before. The two audio
        /// fields are not that: a clip missing <c>overall_soundscape:</c> or <c>non_diegetic_music:</c>
        /// renders its picture exactly as written and comes back quieter than the rest of the chain, which
        /// is worth far more than the hole it leaves in a story when the clip is thrown away. Dropping on
        /// all three is what turned a twelve-clip 120-second chain into two clips on the observed run: a
        /// local writer that gets terser as the chain goes on stops writing the audio fields long before
        /// it stops writing the description.</para>
        ///
        /// <para>Both outcomes name the clip numbers and the fields, because "10 clip(s) came back
        /// structurally incomplete" said nothing about which clips or what they were missing.</para>
        /// </summary>
        private List<string> KeepRenderableClips(List<string> bodies)
        {
            var kept = new List<string>();
            var dropped = new List<int>();
            var quiet = new List<string>();

            for (var i = 0; i < bodies.Count; i++)
            {
                var clipNumber = i + 1;
                var missing = ClipFieldLabels
                    .Where(label => !HasFieldContent(bodies[i], label))
                    .Select(label => label.TrimEnd(':'))
                    .ToList();

                if (!HasFieldContent(bodies[i], ClipFieldLabels[0]))
                {
                    dropped.Add(clipNumber);
                    continue;
                }

                if (missing.Count > 0)
                    quiet.Add($"{clipNumber} (no {string.Join(", no ", missing)})");
                kept.Add(bodies[i]);
            }

            if (quiet.Count > 0)
                AddLog($"Note: clip(s) {string.Join("; ", quiet)} came back without their audio field(s). " +
                       "They are kept and queued — the picture is written in full and only the sound is " +
                       "missing, so those clips render quieter than the rest of the chain.");

            if (dropped.Count > 0)
                AddLog($"WARNING: clip(s) {string.Join(", ", dropped)} came back with no " +
                       $"{ClipFieldLabels[0].TrimEnd(':')} to render and were dropped. Re-run Analyze, or " +
                       "write those clips into the prompt box by hand.");

            return kept;
        }

        /// <summary>Pads cut timestamps into the guide's <c>MM:SS.mmm</c> shape — and rescues the
        /// near-misses local models write: <c>At 00:9.3</c>, <c>at 00:05.80</c>, and the recurring
        /// seconds<b>:</b>sub-second family — <c>At 01:5000</c> (1.5s), <c>At 14:20</c> (14.20s),
        /// <c>At 01:00.400</c> (1.4s). Each match is read first as the guide's own MM:SS(.mmm); when that
        /// lands outside the clip's duration it is re-read as seconds plus a sub-second part (two digits =
        /// centiseconds, three or four = milliseconds). Whatever still does not fit is clamped just inside
        /// the clip's end, so no timestamp can point past the last frame.</summary>
        private static string NormalizeTimestamps(string text, double clipSeconds)
        {
            var limit = Math.Max(1.0, clipSeconds);

            // Plain decimal seconds — "At 01.35", "At 13.9" — converted to the guide's shape too. Run
            // after the colon pass, whose output ("At 00:01.350") cannot match this pattern.
            text = Regex.Replace(text, @"\b[Aa]t\s+(\d{1,3})\.(\d{1,3})\b", m =>
            {
                var seconds = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var millis = int.Parse(m.Groups[2].Value.PadRight(3, '0'), CultureInfo.InvariantCulture);
                if (seconds + millis / 1000.0 > limit)
                {
                    var clamped = Math.Max(0.0, limit - 0.75);
                    return Format(0, (int)clamped, (int)Math.Round((clamped - (int)clamped) * 1000));
                }
                return Format(seconds / 60, seconds % 60, millis);
            });

            return Regex.Replace(text, @"\b[Aa]t\s+(\d{1,2}):(\d{1,4})(?:\.(\d{1,3}))?\b", m =>
            {
                var first = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var secondRaw = m.Groups[2].Value;
                var second = int.Parse(secondRaw, CultureInfo.InvariantCulture);
                var fracMillis = m.Groups[3].Success
                    ? int.Parse(m.Groups[3].Value.PadRight(3, '0'), CultureInfo.InvariantCulture)
                    : 0;

                // The guide's own reading — only when the colon really is a minute separator and the
                // result fits inside the clip.
                if (secondRaw.Length <= 2 && second <= 59 && first * 60 + second + fracMillis / 1000.0 <= limit)
                    return Format(first, second, fracMillis);

                // The seconds:sub-second reading — two digits are centiseconds, three or four are millis.
                var subMillis = secondRaw.Length <= 2
                    ? second * 10
                    : int.Parse(secondRaw.PadRight(3, '0')[..3], CultureInfo.InvariantCulture);
                if (first + (subMillis + fracMillis) / 1000.0 <= limit)
                    return Format(0, first, subMillis + fracMillis);

                // Nothing honest fits — park it just inside the end and let the prose carry the beat.
                var clamped = Math.Max(0.0, limit - 0.75);
                return Format(0, (int)clamped, (int)Math.Round((clamped - (int)clamped) * 1000));
            });

            static string Format(int minutes, int seconds, int millis) =>
                $"At {minutes:00}:{seconds:00}.{millis:000}";
        }

        /// <summary>Matches a <c>[Shot n]</c> marker however it is spaced.</summary>
        private static readonly Regex ShotMarkerRegex =
            new(@"\[\s*Shot\s+\d+\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>The timestamp every shot but the first opens with. Read after
        /// <see cref="NormalizeTimestamps"/> has run, so the shape is already the guide's.</summary>
        private static readonly Regex ShotOpensWithTimestampRegex =
            new(@"^[\s,;:.\-–—]*[Aa]t\s+\d", RegexOptions.Compiled);

        /// <summary>
        /// Folds a clip's shot markers back into the shape the guide actually defines: exactly one
        /// <c>[Shot 1]</c>, and a timestamp on every marker after it.
        ///
        /// <para>MiniMax's own prompt-writing guide documents only the keyframe tasks, in which
        /// <c>[Shot 1]</c> opens by establishing the reference picture's own composition.
        /// <c>h3pw_clip.md</c> overrides that in words, but the habit survives as a subject-less
        /// style-and-setting block emitted as its own <c>[Shot 1]</c> ahead of the real opening shot —
        /// every clip of the observed chain carried two <c>[Shot 1]</c> markers. A clip
        /// whose first shot is a static anchor with no camera and no cast is a clip H3 resolves out of
        /// the reference photographs themselves: studio backdrop, neutral pose, the cast lined up — the
        /// character sheet turning up inside the video.</para>
        ///
        /// <para>A marker after the first whose text does not open with a timestamp is not a shot by the
        /// guide's own rule, so the marker is dropped and its text joins the shot before it — which puts
        /// the style and setting inside <c>[Shot 1]</c>, exactly where the chain layer asks for them.
        /// What survives is renumbered 1..N so the numbering is strictly increasing again.</para>
        /// </summary>
        private string NormalizeShots(string body, int clipNumber)
        {
            var label = ClipFieldLabels[0];
            var start = body.IndexOf(label, StringComparison.Ordinal);
            if (start < 0) return body;
            start += label.Length;

            var end = body.Length;
            for (var i = 1; i < ClipFieldLabels.Length; i++)
            {
                var next = body.IndexOf(ClipFieldLabels[i], start, StringComparison.Ordinal);
                if (next >= 0 && next < end) end = next;
            }

            var field = body[start..end];
            var markers = ShotMarkerRegex.Matches(field);
            if (markers.Count == 0) return body;

            // Anything ahead of the first marker is prose the writer put before its own shots; it stays.
            var lead = field[..markers[0].Index].Trim();

            var shots = new List<string>();
            var folded = 0;
            for (var i = 0; i < markers.Count; i++)
            {
                var from = markers[i].Index + markers[i].Length;
                var to = i + 1 < markers.Count ? markers[i + 1].Index : field.Length;
                var text = field[from..to].Trim();

                if (shots.Count > 0 && !ShotOpensWithTimestampRegex.IsMatch(text))
                {
                    shots[^1] = (shots[^1] + " " + text).Trim();
                    folded++;
                    continue;
                }
                shots.Add(text);
            }

            if (folded > 0)
                AddLog($"Clip {clipNumber}: {folded} timestamp-less [Shot] marker(s) folded into the shot " +
                       "before them — a style/setting block written as a shot of its own is what makes H3 " +
                       "open on the reference photographs instead of on the scene.");

            var rebuilt = string.Join(" ", shots.Select((s, i) => $"[Shot {i + 1}] {s}".TrimEnd()));
            if (lead.Length > 0) rebuilt = lead + " " + rebuilt;

            var tail = end < body.Length ? "\n\n" + body[end..].TrimStart() : string.Empty;
            return body[..start] + " " + rebuilt + tail;
        }
    }
}
