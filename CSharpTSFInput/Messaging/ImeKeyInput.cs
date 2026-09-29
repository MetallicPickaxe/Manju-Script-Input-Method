using System;
using CSharpTSFInput;

namespace CSharpTSFInput.Messaging
{
    // The base input-command structure; it is designed to be extended
    // a readonly record struct, for cheap passing and thread safety
    public readonly record struct ImeKeyInput
    {
        public int KeyCode { get; init; }
        public int Modifiers { get; init; } // carries the Ctrl/Shift/Alt state (as Rime does)
        public long Timestamp { get; init; } // used to handle out-of-order or timed-out input
        public string CompositionSnapshot { get; init; } // snapshot of the composition string at dispatch time
        // FocusUnitIndex carries the per-position focus signal from the UI presenter
        // through the worker channel so the worker can re-run candidate generation with the new focus.
        // -1 sentinel = the default (last unit). When the UI moves focus via ←/→ or 1-9, presenter
        // synthesizes a FocusChange ImeKeyInput with the same CompositionSnapshot but a new FocusUnitIndex,
        // which causes the worker to re-execute GetCandidatesFromSnapshot(snapshot, focusUnitIndex) and
        // emit a fresh RenderFrame. Without this channel hop, focus state would stay UI-local and the
        // candidate column would never refresh on focus change.
        public int FocusUnitIndex { get; init; }

        // Composition generation this input belongs to, stamped at enqueue.
        // The worker drops any input whose epoch no longer matches the engine's current epoch — an
        // in-flight message of a FINISHED composition must not write back stale focus/snapshot state
        // or publish a ghost frame.
        public int Epoch { get; init; }

        // Carries a reference to the originating engine: the worker is static, so the input itself
        // must say which engine it belongs to.
        public WeakReference<ManjuEngine> SourceEngine { get; init; }

        public ImeKeyInput(int keyCode, int modifiers, ManjuEngine engine, string snapshot)
            : this(keyCode, modifiers, engine, snapshot, focusUnitIndex: -1)
        {
        }

        public ImeKeyInput(int keyCode, int modifiers, ManjuEngine engine, string snapshot, int focusUnitIndex)
        {
            KeyCode = keyCode;
            Modifiers = modifiers;
            CompositionSnapshot = snapshot ?? string.Empty;
            Timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            SourceEngine = new WeakReference<ManjuEngine>(engine);
            FocusUnitIndex = focusUnitIndex;
            Epoch = engine.CurrentCompositionEpoch;   // stamp the generation at enqueue
        }
    }
}
