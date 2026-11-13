using NUnit.Framework;
using UnityEngine;

namespace PuppyPi.Tests
{
    public class MapObstacleReplanGateTests
    {
        private static MapPathBlock Hit(float x = 1f, float y = 0f, int segment = 0) =>
            new MapPathBlock { Kind = MapPathBlockKind.Occupied, Position = new Vector2(x, y), SegmentIndex = segment };

        [Test] public void AFirstClearFrameDoesNotWaitForTheConfirmationWindow()
        {
            Assert.That(new MapObstacleReplanGate().Observe(0, 0, 0, null, false), Is.EqualTo(MapObstacleDecision.Clear));
        }

        [Test] public void OneHitAndThenClearRetainsNoObstacleVotes()
        {
            var gate = new MapObstacleReplanGate();
            Assert.That(gate.Observe(1, 0, 0, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(2, .2, .2, null, false), Is.EqualTo(MapObstacleDecision.Clear));
            Assert.That(gate.Observe(3, .4, .4, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void HitHitFreeFreeCannotTriggerABypass()
        {
            var gate = new MapObstacleReplanGate();
            Assert.That(gate.Observe(1, 0, 0, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(2, .2, .2, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(3, .4, .4, null, false), Is.EqualTo(MapObstacleDecision.Clear));
            Assert.That(gate.Observe(4, .6, .6, null, false), Is.EqualTo(MapObstacleDecision.Clear));
        }

        [Test] public void FourFrameWallBurstOverPointThreeSecondsCannotTriggerABypass()
        {
            var gate = new MapObstacleReplanGate();
            for (int i = 0; i < 4; i++)
                Assert.That(gate.Observe(i, i * .1, i * .1, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(4, .4, .4, null, false), Is.EqualTo(MapObstacleDecision.Clear));
        }

        [Test] public void ThreeDistinctPersistentHitsOverPointFourSecondsAllowReplanning()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            Assert.That(gate.Observe(2, .2, .2, Hit(1.1f), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(3, .4, .4, Hit(1.05f), false), Is.EqualTo(MapObstacleDecision.Replan));
        }

        [Test] public void TwoHitsSpanningTheWindowAreStillInsufficient()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            Assert.That(gate.Observe(2, .5, .5, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void ADelayedButFreshSourceCanAccumulateOriginalObservationTimes()
        {
            var gate = new MapObstacleReplanGate();
            Assert.That(gate.Observe(1, 0, .3, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(2, .25, .55, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(3, .5, .8, Hit(), false), Is.EqualTo(MapObstacleDecision.Replan),
                "accepted history must survive ordinary receipt delay while each new source remains fresh");
        }

        [Test] public void RepeatedSourceStampCannotAdvanceVotesOrTheObservationSpan()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            for (int i = 1; i <= 5; i++)
                Assert.That(gate.Observe(1, 0, i * .1, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void ARepeatedSourceCannotBeRestampedToAppearFresh()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            Assert.That(gate.Observe(1, .4, .4, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(gate.Observe(2, .5, .5, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void HighFrequencyInputsDoNotEvictTheRequiredTimeSpan()
        {
            var gate = new MapObstacleReplanGate();
            MapObstacleDecision decision = MapObstacleDecision.Clear;
            for (int i = 0; i <= 500; i++) decision = gate.Observe(i, i * .001, i * .001, Hit(), false);
            Assert.That(decision, Is.EqualTo(MapObstacleDecision.Replan));
        }

        [Test] public void RawBlockedForOneSecondDoesNotReplaceTheOriginalRoute()
        {
            var gate = new MapObstacleReplanGate();
            Assert.That(gate.Observe(1, 0, 0, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(2, 1, 1, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(3, 1.1, 1.1, null, false), Is.EqualTo(MapObstacleDecision.Clear));
        }

        [Test] public void RawBlockedWithoutMapEvidenceEventuallyStopsWithoutABypass()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, null, true);
            Assert.That(gate.Observe(2, 2.99, 2.99, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(3, 3, 3, null, true), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [Test] public void ClearingRawBlockedResetsItsContinuousTimeout()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, null, true);
            gate.Observe(2, 2.9, 2.9, null, false);
            Assert.That(gate.Observe(3, 3, 3, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(4, 5.9, 5.9, null, true), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void RobotBlockedCanReplanOnlyWithIndependentPersistentMapEvidence()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, null, true);
            gate.Observe(2, 1, 1, Hit(), true);
            gate.Observe(3, 1.2, 1.2, Hit(), true);
            Assert.That(gate.Observe(4, 1.4, 1.4, Hit(), true), Is.EqualTo(MapObstacleDecision.Replan));
        }

        [Test] public void ASpatialJumpStartsANewConfirmationWindow()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            gate.Observe(2, .2, .2, Hit(), false);
            Assert.That(gate.Observe(3, .4, .4, Hit(1.2f), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(4, .6, .6, Hit(1.2f), false), Is.EqualTo(MapObstacleDecision.Verifying));
            Assert.That(gate.Observe(5, .8, .8, Hit(1.2f), false), Is.EqualTo(MapObstacleDecision.Replan));
        }

        [Test] public void SpatialMatchingUsesTheFirstAnchorRatherThanDriftingAcrossTheMap()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            gate.Observe(2, .2, .2, Hit(1.1f), false);
            Assert.That(gate.Observe(3, .4, .4, Hit(1.2f), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void ASegmentChangeStartsANewConfirmationWindow()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            gate.Observe(2, .2, .2, Hit(), false);
            Assert.That(gate.Observe(3, .4, .4, Hit(segment: 1), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void ResetPreventsOldRouteVotesAndTimeoutsFromAffectingTheNextRun()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), true);
            gate.Observe(2, .2, .2, Hit(), true);
            gate.Reset();
            Assert.That(gate.Observe(0, 0, 0, null, false), Is.EqualTo(MapObstacleDecision.Clear));
            Assert.That(gate.Observe(1, .4, .4, Hit(), true), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [TestCase(MapPathBlockKind.Unknown)]
        [TestCase(MapPathBlockKind.Boundary)]
        [TestCase(MapPathBlockKind.Invalid)]
        public void NonOccupiedBlocksCannotAuthorizeAnAutomaticDetour(MapPathBlockKind kind)
        {
            var gate = new MapObstacleReplanGate();
            var block = Hit();
            block.Kind = kind;
            for (int i = 0; i < 4; i++)
                Assert.That(gate.Observe(i, i * .2, i * .2, block, false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [TestCase(.6)]
        [TestCase(1.0)]
        public void StaleCurrentFramesCannotConfirmAnObstacle(double age)
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            gate.Observe(2, .2, .2, Hit(), false);
            Assert.That(gate.Observe(3, .4, .4 + age, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [Test] public void ExpiredAcceptedVotesCannotConfirmAfterALongObservationGap()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(1, 0, 0, Hit(), false);
            gate.Observe(2, .2, .2, Hit(), false);
            Assert.That(gate.Observe(3, .55, 1, Hit(), false), Is.EqualTo(MapObstacleDecision.Verifying));
        }

        [Test] public void FutureSourceTimesCannotConfirmAnObstacle()
        {
            Assert.That(new MapObstacleReplanGate().Observe(1, 1, 0, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [Test] public void BackwardSourceOrReceiptTimesCannotConfirmAnObstacle()
        {
            var gate = new MapObstacleReplanGate();
            gate.Observe(10, 1, 1, Hit(), false);
            Assert.That(gate.Observe(9, 1.2, 1.2, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(gate.Observe(11, .9, 1.3, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(gate.Observe(11, 1, .9, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }

        [Test] public void InvalidCoordinatesAndMissingSourceIdentityCannotConfirmAnObstacle()
        {
            Assert.That(new MapObstacleReplanGate().Observe(-1, 0, 0, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(new MapObstacleReplanGate().Observe(1, 0, 0, Hit(float.NaN), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(new MapObstacleReplanGate().Observe(1, 0, 0, Hit(segment: -1), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(new MapObstacleReplanGate().Observe(1, double.NaN, 0, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
            Assert.That(new MapObstacleReplanGate().Observe(1, 0, double.PositiveInfinity, Hit(), false), Is.EqualTo(MapObstacleDecision.UnconfirmedStop));
        }
    }
}
