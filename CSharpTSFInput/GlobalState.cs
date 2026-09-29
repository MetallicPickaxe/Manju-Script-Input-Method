using System.Threading;
using CSharpTSFInput.Messaging;

namespace CSharpTSFInput
{
    // [Atomic] Process-Global UI State Container
    internal static class GlobalState
    {
        private static volatile RenderFrame? _latestFrame;
        private static int _frameSeq = 0; // Monotonic sequence for tracing

        // Backend calls this to commit a new frame (Atomic Switch)
        public static void UpdateFrame(RenderFrame newFrame)
        {
            int seq = Interlocked.Increment(ref _frameSeq);
            newFrame.FrameId = seq;
            Interlocked.Exchange(ref _latestFrame, newFrame);
        }

        // Frontend calls this to get the current snapshot
        public static RenderFrame? GetLatestFrame()
        {
            var frame = Interlocked.CompareExchange(ref _latestFrame, null, null);
            return frame;
        }
    }
}
