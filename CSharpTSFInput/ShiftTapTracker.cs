using System;

namespace CSharpTSFInput
{
    /// <summary>
    /// Lone-Shift-tap state machine. Its decision follows the DOCUMENTED TSF delivery contract, not
    /// whatever a lenient host happens to deliver.
    ///
    /// The case it is built for: in EN mode, Shift+A makes OnTestKeyDown(A) return
    /// pfEaten=0 (letters are only owned in manju mode), and a contract-strict host then NEVER calls
    /// OnKeyDown(A), because ITfKeystrokeMgr::KeyDown is only sent after TestKeyDown ate the key.
    /// Tracking that latch ("another key was pressed while Shift was held") ONLY in OnKeyDown would
    /// leave the flag false under such a host: the Shift release would read as a lone tap, and the IME
    /// would flip back to manju mode in the middle of the user's Shift+A.
    ///
    /// So key traffic is observed in OnTESTKeyDown too. That callback IS delivered for every keystroke
    /// regardless of ownership, which is the whole point of the TestKeyDown/KeyDown split, so latching
    /// there is contract-guaranteed. OnKeyDown is observed as well, as belt-and-braces for hosts that
    /// skip OnTestKeyDown entirely.
    ///
    /// Bias note: a speculative TestKeyDown probe (a host asking without a real keystroke) can only set
    /// OtherKeyWhileShift=true, which SUPPRESSES a lone-tap. That fails safe: a missed tap costs the
    /// user one more tap, while a phantom tap flips their input mode under them.
    /// </summary>
    public sealed class ShiftTapTracker
    {
        /// <summary> How long Shift may be held and still count as a TAP.
        ///
        /// Without a limit a lone down→up flips the input mode however long the key has been held, so
        /// resting a finger on Shift silently switches Manchu and English.
        ///
        /// 500 ms is not a taste call. It is Windows' own default keyboard repeat DELAY
        /// (SPI_GETKEYBOARDDELAY = 1), the point at which the OS itself decides a key is being held
        /// instead of pressed, and therefore the point at which the user's own machine starts sending
        /// auto-repeat for it. TextService overrides this with the live system setting, so a user who has
        /// tuned their repeat delay gets a threshold that matches the feel of their own keyboard.
        ///
        /// LEFT AND RIGHT SHIFT ARE THE SAME HERE, and deliberately so: both carry the escape/toggle.
        /// The duration threshold applies identically to both.</summary>
        public const int DefaultTapMaxHoldMs = 500;

        private bool _shiftIsDown;
        private bool _otherKeyWhileShift;
        private long _shiftDownAtMs;
        private bool _heldTooLong;      // latched by auto-repeat, so a clock that lies cannot un-hold it

        private readonly Func<long> _nowMs;
        private readonly int _tapMaxHoldMs;

        public ShiftTapTracker() : this(null, DefaultTapMaxHoldMs) { }

        /// <param name="nowMs">Monotonic millisecond clock; null uses Environment.TickCount64. The hold
        /// threshold reads the time only through this function.</param>
        /// <param name="tapMaxHoldMs">Hold ceiling for a tap. Values below 1 fall back to the default.</param>
        public ShiftTapTracker(Func<long>? nowMs, int tapMaxHoldMs = DefaultTapMaxHoldMs)
        {
            _nowMs = nowMs ?? (() => Environment.TickCount64);
            _tapMaxHoldMs = tapMaxHoldMs > 0 ? tapMaxHoldMs : DefaultTapMaxHoldMs;
        }

        public bool OtherKeyWhileShift => _otherKeyWhileShift;

        private static bool IsShift(uint vk) => vk == 0x10 || vk == 0xA0 || vk == 0xA1;

        /// <summary>Contract-guaranteed observation point: called from OnTestKeyDown for EVERY key.</summary>
        public void ObserveTestKeyDown(uint vk) => Observe(vk);

        /// <summary>Belt-and-braces for hosts that deliver OnKeyDown without a preceding OnTestKeyDown.</summary>
        public void ObserveKeyDown(uint vk) => Observe(vk);

        // ONE rule for both callbacks: a Shift arrival starts a fresh tap-candidate ONLY on the
        // down-edge (was up → now down). While Shift is already down, further Shift arrivals are the
        // second callback's echo of the same press or keyboard auto-repeat, and neither may erase an
        // interruption observed in between (that would resurrect a dead lone-tap). Resetting only on
        // the down-edge also keeps hosts that skip OnTestKeyDown working across cycles: their
        // OnKeyDown IS the down-edge.
        private void Observe(uint vk)
        {
            if (IsShift(vk))
            {
                if (!_shiftIsDown)
                {
                    _shiftIsDown = true; _otherKeyWhileShift = false;
                    _shiftDownAtMs = _nowMs(); _heldTooLong = false;
                }
                else
                {
                    // Shift arriving while already down is either the second callback echoing the
                    // same press or the OS auto-repeating a HELD key. The echo lands in the same
                    // millisecond; a repeat cannot arrive before the system's repeat delay has elapsed.
                    // So a repeat past the threshold is the keyboard itself testifying that this is a
                    // hold, independent of our clock, which is what makes it worth latching. It is
                    // latched and not recomputed, so a later reading can never un-hold it.
                    if (_nowMs() - _shiftDownAtMs >= _tapMaxHoldMs) _heldTooLong = true;
                }
            }
            else if (_shiftIsDown) { _otherKeyWhileShift = true; }
        }

        /// <summary>Non-key interruption (focus lost, etc.) while Shift is held → not a lone tap.
        /// Covers the mouse-click-that-moves-focus case; an in-place caret click generates no signal any
        /// IME can see.</summary>
        public void ObserveInterruption()
        {
            if (_shiftIsDown) _otherKeyWhileShift = true;
        }

        /// <summary> Hard reset. Used on focus REGAIN when Shift is not physically held: if the
        /// Shift-up was delivered to another window while we were unfocused, the tracker would otherwise
        /// keep a stale down-state and swallow the user's next genuine lone tap.</summary>
        public void Reset()
        {
            _shiftIsDown = false;
            _otherKeyWhileShift = false;
            _heldTooLong = false;
            _shiftDownAtMs = 0;
        }

        /// <summary>Key-up. Returns TRUE exactly when this is a Shift up completing a lone tap
        /// (Shift went down and nothing else was observed in between).</summary>
        public bool OnKeyUp(uint vk)
        {
            if (!IsShift(vk))
            {
                // The up-stroke of another key while Shift is held implies that key was pressed.
                if (_shiftIsDown) _otherKeyWhileShift = true;
                return false;
            }
            // A tap is a lone press RELEASED IN TIME. Three ways to fail, and the hold is
            // judged on two independent signals so losing one does not lose the decision:
            //   · something else happened in between            → not lone
            //   · the OS auto-repeated Shift past the threshold → held, latched at observation time
            //   · the elapsed hold reached the threshold        → held, by the clock
            // Both hold signals fail SAFE in the same direction as the bias noted on the class: a missed tap costs
            // the user one more tap, a phantom tap flips their input mode under them mid-sentence.
            long heldMs = _shiftIsDown ? _nowMs() - _shiftDownAtMs : 0;
            bool held = _heldTooLong || heldMs >= _tapMaxHoldMs;
            bool lone = _shiftIsDown && !_otherKeyWhileShift && !held;
            _shiftIsDown = false;
            _heldTooLong = false;
            return lone;
        }
    }
}
