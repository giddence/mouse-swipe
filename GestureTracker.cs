using System;

namespace MouseSwipe
{
    /// <summary>
    /// Turns a stream of horizontal pointer movement into discrete swipe strokes.
    /// A continuous stroke triggers at most once, regardless of its length.
    /// A pause or a clear direction reversal starts the next stroke.
    /// </summary>
    internal sealed class GestureTracker
    {
        public const int PauseMs = 150;
        public const int ReverseDeadzone = 20;

        private int _threshold;
        private int _x;
        private int _y;
        private int _originX;
        private int _originY;
        private int _peakX;
        private int _peakY;
        private int _lastTick;
        private bool _fired;
        private int _lastDirection;

        public void Begin(int threshold, int now)
        {
            _threshold = Math.Max(30, threshold);
            _x = 0;
            _y = 0;
            _originX = 0;
            _originY = 0;
            _peakX = 0;
            _peakY = 0;
            _lastTick = now;
            _fired = false;
            _lastDirection = 0;
        }

        /// <summary>
        /// Feeds one pointer delta. Returns +1 for a right swipe, -1 for a left
        /// swipe, or 0 when no new switch should happen.
        /// </summary>
        public int Move(int dx, int dy, int now)
        {
            if (unchecked((uint)(now - _lastTick)) > PauseMs)
            {
                _fired = false;
                _originX = _x;
                _originY = _y;
            }
            _lastTick = now;

            _x += dx;
            _y += dy;

            if (_fired)
            {
                // Remember the far end of the current stroke. The opposite
                // stroke is measured from that turning point, so a left swipe
                // as long as the right swipe also counts as one full stroke.
                if (_lastDirection > 0 && _x > _peakX)
                {
                    _peakX = _x;
                    _peakY = _y;
                }
                else if (_lastDirection < 0 && _x < _peakX)
                {
                    _peakX = _x;
                    _peakY = _y;
                }

                int sincePeak = _x - _peakX;
                if (_lastDirection != 0 &&
                    sincePeak * _lastDirection < 0 &&
                    Math.Abs(sincePeak) >= ReverseDeadzone)
                {
                    // A new stroke starts at the turning point.
                    _fired = false;
                    _originX = _peakX;
                    _originY = _peakY;
                }
                else
                {
                    return 0;
                }
            }

            int strokeDx = _x - _originX;
            int strokeDy = _y - _originY;

            if (Math.Abs(strokeDx) < _threshold)
            {
                return 0;
            }

            // Only a mostly horizontal stroke counts.
            if (Math.Abs(strokeDx) <= Math.Abs(strokeDy))
            {
                return 0;
            }

            _fired = true;
            _lastDirection = strokeDx > 0 ? 1 : -1;
            _peakX = _x;
            _peakY = _y;
            return _lastDirection;
        }
    }
}
