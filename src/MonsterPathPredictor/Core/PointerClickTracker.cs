using System;

namespace MonsterPathPredictor.Core
{
    /// <summary>
    /// Outcome produced by <see cref="PointerClickTracker"/> when a left-mouse press+release
    /// completes as a clean board click (without exceeding the drag threshold).
    /// </summary>
    public readonly struct PointerGestureOutcome
    {
        public bool IsBoardClick { get; }
        public Point2 ReleasePosition { get; }
        public long DownHitMonsterId { get; }

        public PointerGestureOutcome(bool isBoardClick, Point2 releasePosition, long downHitMonsterId)
        {
            IsBoardClick = isBoardClick;
            ReleasePosition = releasePosition;
            DownHitMonsterId = downHitMonsterId;
        }
    }

    /// <summary>
    /// Pure click-vs-drag gesture filter for board selection so dragging the camera map
    /// (whether starting on empty ground or over a monster) never accidentally selects,
    /// switches, or clears the tracked monster.
    /// </summary>
    public sealed class PointerClickTracker
    {
        private readonly float _dragThresholdSq;
        private bool _tracking;
        private bool _dragged;
        private Point2 _downPos;
        private long _downHitMonsterId;

        public PointerClickTracker(float dragThresholdPx = 8f)
        {
            float t = dragThresholdPx > 0f ? dragThresholdPx : 8f;
            _dragThresholdSq = t * t;
        }

        public bool IsTracking => _tracking;
        public bool HasDragged => _dragged;

        public void Reset()
        {
            _tracking = false;
            _dragged = false;
            _downPos = default;
            _downHitMonsterId = 0;
        }

        /// <summary>
        /// Advances the gesture state machine for the current frame.
        /// Call on every frame with stage/screen coordinates so out-and-back drags are caught too.
        /// </summary>
        public PointerGestureOutcome Update(
            bool isDownThisFrame,
            bool isHeld,
            bool isUpThisFrame,
            bool hasPosition,
            Point2 position,
            bool blockedByUiOnDown,
            long downHitMonsterId = 0)
        {
            if (isDownThisFrame)
            {
                if (!hasPosition || blockedByUiOnDown)
                {
                    Reset();
                    return default;
                }

                _tracking = true;
                _dragged = false;
                _downPos = position;
                _downHitMonsterId = downHitMonsterId;

                if (!isUpThisFrame) return default;
            }

            if (!_tracking) return default;

            if (hasPosition && !_dragged)
            {
                float dx = position.X - _downPos.X;
                float dy = position.Y - _downPos.Y;
                if (dx * dx + dy * dy > _dragThresholdSq)
                {
                    _dragged = true;
                }
            }

            if (isUpThisFrame)
            {
                bool isClick = !_dragged && hasPosition;
                Point2 releasePos = hasPosition ? position : _downPos;
                long capturedDownHit = _downHitMonsterId;
                Reset();
                return isClick ? new PointerGestureOutcome(true, releasePos, capturedDownHit) : default;
            }

            if (!isHeld)
            {
                // Window lost focus mid-gesture; cancel cleanly without emitting a click.
                Reset();
            }

            return default;
        }

        /// <summary>
        /// Resolves the next selected monster ID after a confirmed board click:
        /// clicking the already-selected monster toggles it off, clicking another monster
        /// switches to it, and clicking empty board space preserves selection unless
        /// <paramref name="clearOnEmptyClick"/> is explicitly enabled.
        /// </summary>
        public static long ApplyBoardClickSelection(
            long currentSelectedMonsterId,
            long hitMonsterId,
            bool clearOnEmptyClick)
        {
            if (hitMonsterId != 0)
            {
                return currentSelectedMonsterId == hitMonsterId ? 0 : hitMonsterId;
            }

            return clearOnEmptyClick ? 0 : currentSelectedMonsterId;
        }
    }
}
