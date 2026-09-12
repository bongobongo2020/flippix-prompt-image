using System;
using System.Collections.Generic;
using System.Linq;

namespace FlipPix.UI.Models
{
    /// <summary>
    /// One story's written clip prompts, filed under the story they were written from, so a story that has
    /// been through the clip writer once never has to go through it again.
    ///
    /// <para><b>The key is the story's text, not its file name.</b> <see cref="StoryHash"/> is a hash of the
    /// prose with its whitespace collapsed (see <c>StoryPromptStore.HashStory</c>), so a story renamed,
    /// moved to another folder or re-saved with different line endings is still recognised, and a story
    /// whose words have changed is not — its saved prompts describe the old version.</para>
    ///
    /// <para><b>The clips are stored as bodies, with no cast preamble.</b> Each one is what
    /// <c>CastPromptStamp.Strip</c> leaves: canonical <c>&lt;Picture 1&gt;</c>/<c>&lt;Picture 2&gt;</c> tags,
    /// no reference line, no wardrobe lock. Those are written again at recall from the cast loaded then,
    /// which is what lets one story's prompts be rendered with a fresh cast every time.</para>
    /// </summary>
    public sealed class SavedStoryPrompts
    {
        /// <summary>What the entry is looked up by. See the type remarks.</summary>
        public string StoryHash { get; set; } = string.Empty;

        /// <summary>The label in the library — the story file's name without its extension, user-editable.</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>The file the story was last seen in. Informational: it may have moved or been deleted,
        /// and the entry still matches the story wherever it turns up.</summary>
        public string SourceFileName { get; set; } = string.Empty;

        public string SourcePath { get; set; } = string.Empty;

        /// <summary>The prose the prompts were written from, so the library can show it beside them.</summary>
        public string StoryText { get; set; } = string.Empty;

        /// <summary>One prompt body per clip, in story order.</summary>
        public List<string> Clips { get; set; } = new();

        /// <summary>
        /// The wardrobe the clips were written against. Unlike the prompt library's entries, this one
        /// <b>is</b> put back on recall: the story is the same story, and its clip bodies describe these
        /// garments in their own prose, so a freshly derived wardrobe would dress the cast in one set of
        /// clothes while every clip describes another.
        /// </summary>
        public string Wardrobe { get; set; } = string.Empty;

        /// <summary>Character 1's and character 2's nouns ("man", "woman") when the prompts were written —
        /// compared at recall, because the clips' picture numbering follows who was character 1.</summary>
        public List<string> CastNouns { get; set; } = new();

        /// <summary>The per-clip length the prompts were written for. Their shot timestamps run to it, so
        /// a recalled story renders at this length rather than whatever the slider says.</summary>
        public double LengthSeconds { get; set; }

        /// <summary>"researched", "shipped", or empty when not known (an entry imported from a prompt library
        /// that did not record it).</summary>
        public string PromptBuild { get; set; } = string.Empty;

        public string VisualStyle { get; set; } = string.Empty;

        /// <summary>Where the prompts came from, in words: "Written by H3 Express", "Imported from the
        /// H3 Batch prompt library".</summary>
        public string Origin { get; set; } = string.Empty;

        /// <summary>True once any clip, the wardrobe or the title has been edited by hand.</summary>
        public bool EditedByHand { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime ModifiedAt { get; set; } = DateTime.Now;
        public DateTime? LastUsedAt { get; set; }

        /// <summary>How many renders have been started from these prompts instead of the clip writer.</summary>
        public int UseCount { get; set; }

        /// <summary>A copy that can be edited without touching the store's own.</summary>
        public SavedStoryPrompts Clone()
        {
            var copy = (SavedStoryPrompts)MemberwiseClone();
            copy.Clips = Clips.ToList();
            copy.CastNouns = CastNouns.ToList();
            return copy;
        }
    }
}
