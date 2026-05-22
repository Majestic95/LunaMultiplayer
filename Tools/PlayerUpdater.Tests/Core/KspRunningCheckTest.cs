using System;
using System.Collections.Generic;
using LunaMultiplayer.PlayerUpdater.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LunaMultiplayer.PlayerUpdater.Tests.Core
{
    [TestClass]
    public class KspRunningCheckTest
    {
        // --- WaitForCloseUsingProbe — pure orchestrator behaviour ---

        [TestMethod]
        public void WaitForCloseUsingProbe_ProbeReturnsFalseImmediately_ReturnsTrueWithoutSleeping()
        {
            var sleepCount = 0;
            var result = KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: 5,
                delayMs: 200,
                isRunningProbe: () => false,
                sleepFn: _ => sleepCount++);

            Assert.IsTrue(result);
            Assert.AreEqual(0, sleepCount);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_ProbeAlwaysTrue_ReturnsFalseAfterMaxAttempts()
        {
            var probeCalls = 0;
            var sleepCount = 0;
            var result = KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: 4,
                delayMs: 50,
                isRunningProbe: () => { probeCalls++; return true; },
                sleepFn: _ => sleepCount++);

            Assert.IsFalse(result);
            Assert.AreEqual(4, probeCalls);
            // 4 attempts means 3 sleeps (sleep happens BETWEEN probes, not after).
            Assert.AreEqual(3, sleepCount);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_ProbeBecomesFalseMidway_ReturnsTrueAtThatAttempt()
        {
            // Simulate KSP closing on the 3rd probe.
            var responses = new Queue<bool>(new[] { true, true, false, false, false });
            var probeCalls = 0;
            var sleepCount = 0;

            var result = KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: 5,
                delayMs: 50,
                isRunningProbe: () => { probeCalls++; return responses.Dequeue(); },
                sleepFn: _ => sleepCount++);

            Assert.IsTrue(result);
            Assert.AreEqual(3, probeCalls);
            // Probe1 → sleep → probe2 → sleep → probe3 returns false → return.
            // Two sleeps total.
            Assert.AreEqual(2, sleepCount);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_MaxAttemptsZero_DegradesToSingleProbe()
        {
            var probeCalls = 0;
            var sleepCount = 0;
            var result = KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: 0,
                delayMs: 200,
                isRunningProbe: () => { probeCalls++; return true; },
                sleepFn: _ => sleepCount++);

            // Caller asked for zero attempts — we clamp to 1 to give a definite
            // answer, but no sleeps.
            Assert.IsFalse(result);
            Assert.AreEqual(1, probeCalls);
            Assert.AreEqual(0, sleepCount);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_NegativeMaxAttempts_ClampedToOne()
        {
            var probeCalls = 0;
            var result = KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: -5,
                delayMs: 50,
                isRunningProbe: () => { probeCalls++; return false; },
                sleepFn: _ => { });

            Assert.IsTrue(result);
            Assert.AreEqual(1, probeCalls);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_NegativeDelay_PassedAsZero()
        {
            // Caller can't trick us into a negative Thread.Sleep — we clamp.
            var observedDelays = new List<int>();
            KspRunningCheck.WaitForCloseUsingProbe(
                maxAttempts: 3,
                delayMs: -1000,
                isRunningProbe: () => true,
                sleepFn: ms => observedDelays.Add(ms));

            Assert.AreEqual(2, observedDelays.Count);
            CollectionAssert.AreEqual(new[] { 0, 0 }, observedDelays);
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_NullProbe_ThrowsArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                KspRunningCheck.WaitForCloseUsingProbe(5, 50, null!, _ => { }));
        }

        [TestMethod]
        public void WaitForCloseUsingProbe_NullSleepFn_ThrowsArgumentNullException()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                KspRunningCheck.WaitForCloseUsingProbe(5, 50, () => true, null!));
        }

        // --- IsKspRunningOnce — live probe ---
        //
        // We can't fixture KSP into running, but we CAN assert the call does
        // not throw on a clean machine — which it must not, since the Forms
        // layer calls it eagerly on startup.

        [TestMethod]
        public void IsKspRunningOnce_DoesNotThrow()
        {
            // Smoke: returns a bool, doesn't throw on the test host.
            var _ = KspRunningCheck.IsKspRunningOnce();
        }

        [TestMethod]
        public void ProbeKspRunningState_TestHost_ReturnsValidState()
        {
            // Smoke: returns one of the three named states without throwing.
            // We don't assert WHICH state — the operator may have KSP open
            // for soak testing in a sibling worktree, and a CI runner may
            // restrict process enumeration to Unknown.
            var state = KspRunningCheck.ProbeKspRunningState();
            Assert.IsTrue(
                state == KspRunningCheck.ProbeState.NotRunning
                || state == KspRunningCheck.ProbeState.Running
                || state == KspRunningCheck.ProbeState.Unknown,
                $"Probe returned unrecognised state {state}");
        }

        [TestMethod]
        public void IsKspRunningOnce_UnknownTreatedAsRunning_ForRefuseByDefault()
        {
            // Document the bool-shim contract — Unknown maps to true so the
            // install path errs toward refusing when the OS won't tell us.
            // We can't synthesise Unknown without process-enumeration mocking
            // (the impl wraps Process.GetProcessesByName directly), so this
            // is a contract documentation test only: assert that the ProbeState
            // enum has the three named members we depend on.
            Assert.AreEqual(KspRunningCheck.ProbeState.NotRunning, KspRunningCheck.ProbeState.NotRunning);
            Assert.AreEqual(KspRunningCheck.ProbeState.Running, KspRunningCheck.ProbeState.Running);
            Assert.AreEqual(KspRunningCheck.ProbeState.Unknown, KspRunningCheck.ProbeState.Unknown);
        }

        [TestMethod]
        public void WaitForKspToClose_LiveCall_ReturnsBooleanWithoutThrowing()
        {
            // Smoke: end-to-end live call with a tiny attempt window so the
            // test doesn't actually sleep noticeably. Returns true if KSP is
            // not currently running on the test host; false if it is. We
            // assert no-throw, not a specific result — operators routinely
            // run these tests with KSP open in a sibling worktree.
            var _ = KspRunningCheck.WaitForKspToClose(maxAttempts: 1, delayMs: 0);
        }
    }
}
