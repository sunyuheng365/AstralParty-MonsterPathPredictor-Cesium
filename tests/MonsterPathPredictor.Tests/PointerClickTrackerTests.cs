using MonsterPathPredictor.Core;
using Xunit;

namespace MonsterPathPredictor.Tests
{
    public sealed class PointerClickTrackerTests
    {
        [Fact]
        public void CleanClick_PressAndReleaseWithinThreshold_EmitsBoardClick()
        {
            var tracker = new PointerClickTracker(dragThresholdPx: 8f);

            var down = tracker.Update(
                isDownThisFrame: true, isHeld: true, isUpThisFrame: false,
                hasPosition: true, position: new Point2(200f, 300f),
                blockedByUiOnDown: false, downHitMonsterId: 1001);
            Assert.False(down.IsBoardClick);
            Assert.True(tracker.IsTracking);

            var held = tracker.Update(
                isDownThisFrame: false, isHeld: true, isUpThisFrame: false,
                hasPosition: true, position: new Point2(203f, 302f),
                blockedByUiOnDown: false);
            Assert.False(held.IsBoardClick);
            Assert.False(tracker.HasDragged);

            var up = tracker.Update(
                isDownThisFrame: false, isHeld: false, isUpThisFrame: true,
                hasPosition: true, position: new Point2(204f, 303f),
                blockedByUiOnDown: false);
            Assert.True(up.IsBoardClick);
            Assert.Equal(1001, up.DownHitMonsterId);
            Assert.False(tracker.IsTracking);
        }

        [Fact]
        public void MapDrag_ExceedingThreshold_DoesNotEmitBoardClick()
        {
            var tracker = new PointerClickTracker(dragThresholdPx: 8f);

            tracker.Update(true, true, false, true, new Point2(100f, 100f), false, downHitMonsterId: 1001);
            tracker.Update(false, true, false, true, new Point2(140f, 115f), false);
            Assert.True(tracker.HasDragged);

            // Even if user drags back to the exact starting point before releasing, it is still a drag.
            var up = tracker.Update(false, false, true, true, new Point2(100f, 100f), false);
            Assert.False(up.IsBoardClick);
            Assert.False(tracker.IsTracking);
        }

        [Fact]
        public void UiClick_BlockedOnDown_IsIgnoredEvenIfReleasedOverBoard()
        {
            var tracker = new PointerClickTracker(dragThresholdPx: 8f);

            tracker.Update(true, true, false, true, new Point2(50f, 50f), blockedByUiOnDown: true);
            Assert.False(tracker.IsTracking);

            var up = tracker.Update(false, false, true, true, new Point2(50f, 50f), blockedByUiOnDown: false);
            Assert.False(up.IsBoardClick);
        }

        [Fact]
        public void SingleFrameTap_DownAndUpSameFrame_EmitsClick()
        {
            var tracker = new PointerClickTracker(dragThresholdPx: 8f);

            var outcome = tracker.Update(
                isDownThisFrame: true, isHeld: false, isUpThisFrame: true,
                hasPosition: true, position: new Point2(320f, 240f),
                blockedByUiOnDown: false, downHitMonsterId: 2002);

            Assert.True(outcome.IsBoardClick);
            Assert.Equal(2002, outcome.DownHitMonsterId);
        }

        [Fact]
        public void WindowFocusLossMidPress_CancelsGestureWithoutClick()
        {
            var tracker = new PointerClickTracker(dragThresholdPx: 8f);

            tracker.Update(true, true, false, true, new Point2(120f, 180f), false, downHitMonsterId: 1001);
            Assert.True(tracker.IsTracking);

            // Button no longer held and no up event -> cancelled cleanly
            var lost = tracker.Update(false, false, false, true, new Point2(120f, 180f), false);
            Assert.False(lost.IsBoardClick);
            Assert.False(tracker.IsTracking);
        }

        [Fact]
        public void PersistentSelection_EmptyClickKeepsMonster_ReClickTogglesOff_OtherMonsterSwitches()
        {
            // Default settings: clearOnEmptyClick = false
            var defaults = new PredictorSettings();
            Assert.False(defaults.ClearOnEmptyClick);

            // 1. Click monster A -> locked onto A
            long sel = PointerClickTracker.ApplyBoardClickSelection(0, 101, defaults.ClearOnEmptyClick);
            Assert.Equal(101, sel);

            // 2. Click empty space -> stays locked onto A
            sel = PointerClickTracker.ApplyBoardClickSelection(sel, 0, defaults.ClearOnEmptyClick);
            Assert.Equal(101, sel);

            // 3. Click monster B -> switches to B
            sel = PointerClickTracker.ApplyBoardClickSelection(sel, 202, defaults.ClearOnEmptyClick);
            Assert.Equal(202, sel);

            // 4. Click monster B again -> toggles off
            sel = PointerClickTracker.ApplyBoardClickSelection(sel, 202, defaults.ClearOnEmptyClick);
            Assert.Equal(0, sel);

            // 5. Explicit opt-in clearOnEmptyClick=true still works when configured
            sel = PointerClickTracker.ApplyBoardClickSelection(101, 0, clearOnEmptyClick: true);
            Assert.Equal(0, sel);
        }
    }
}
