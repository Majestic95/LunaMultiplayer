using LunaConfigNode.CfgNode;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System;
using Server.System.Scenario;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ServerTest
{
    /// <summary>
    /// Regression coverage for [BUG-033] — the periodic backup task used to call
    /// <c>ConfigNode.ToString()</c> without taking the same per-scenario lock that
    /// the <see cref="ScenarioDataUpdater"/> writers compete for. With a writer
    /// concurrently mutating the node's children list, ToString's iteration would
    /// throw <c>InvalidOperationException</c> (or worse, emit a partial serialization
    /// to disk). The fix exposes the per-scenario lock via
    /// <see cref="ScenarioDataUpdater.GetSemaphore"/> and routes backup-side
    /// serialization through <see cref="ScenarioStoreSystem.SerializeUnderWriterLock"/>.
    ///
    /// Two tests pin the two halves of the contract:
    /// <list type="bullet">
    /// <item><see cref="SerializeUnderWriterLock_DoesNotRaceConcurrentMutation"/> —
    /// positive test: with the SUT's lock acquisition in place, the reader and a
    /// writer (modeling production writers) cooperate via mutual exclusion and no
    /// exception fires.</item>
    /// <item><see cref="UnprotectedToString_DemonstratesRaceUnderConcurrentMutation"/> —
    /// negative control: calling <c>scenario.ToString()</c> directly (no lock,
    /// modeling the pre-fix code path) races the writer and is expected to throw.
    /// If this test ever stops failing, either LunaConfigNode became thread-safe
    /// (unlikely) or the test setup no longer produces enough contention — the
    /// positive test's coverage becomes unreliable in that case and both tests
    /// should be revisited.</item>
    /// </list>
    /// </summary>
    [TestClass]
    public class ScenarioStoreBackupRaceTest
    {
        private const string ScenarioName = "BackupRaceTestScen";

        [TestMethod]
        public void SerializeUnderWriterLock_DoesNotRaceConcurrentMutation()
        {
            var scenario = BuildPopulatedScenario(initialChildren: 50);

            var stop = false;
            Exception writerException = null;
            Exception readerException = null;

            // Writer takes the per-scenario lock — this models EVERY production
            // ScenarioDataUpdater.Write*DataToFile writer. Mutual exclusion requires
            // both sides to take the same monitor; if the SUT (SerializeUnderWriterLock)
            // drops its lock, the reader's ToString runs concurrent with the writer's
            // in-lock mutation and the race fires. See the negative control test below
            // for the demonstration.
            var writer = new Thread(() =>
            {
                try
                {
                    var addedNodes = new Queue<ConfigNode>();
                    var counter = 0;
                    while (!Volatile.Read(ref stop))
                    {
                        lock (ScenarioDataUpdater.GetSemaphore(ScenarioName))
                        {
                            var child = new ConfigNode("") { Name = $"Child_{counter++}" };
                            scenario.AddNode(child);
                            addedNodes.Enqueue(child);
                            if (addedNodes.Count > 100)
                            {
                                var oldest = addedNodes.Dequeue();
                                scenario.RemoveNode(oldest);
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    writerException = e;
                }
            });

            var reader = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop))
                    {
                        var serialized = ScenarioStoreSystem.SerializeUnderWriterLock(ScenarioName, scenario);
                        if (serialized == null)
                            throw new InvalidOperationException("SerializeUnderWriterLock returned null");
                    }
                }
                catch (Exception e)
                {
                    readerException = e;
                }
            });

            writer.Start();
            reader.Start();

            Thread.Sleep(500);

            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(5));
            reader.Join(TimeSpan.FromSeconds(5));

            Assert.IsNull(writerException, $"Writer thread threw: {writerException}");
            Assert.IsNull(readerException, $"Reader thread threw — BUG-033 regression: {readerException}");
        }

        [TestMethod]
        public void UnprotectedToString_DemonstratesRaceUnderConcurrentMutation()
        {
            // Negative control. Models the pre-fix code path: reader calls
            // scenario.ToString() with no lock. Writer takes the per-scenario lock
            // (same as production writers). Reader's iterator races the writer's
            // in-lock mutation and is expected to throw InvalidOperationException
            // (or an internal NRE if LunaConfigNode's children collection re-enters
            // a transient null state during AddNode). If this assertion ever stops
            // firing, the bug is no longer reproducible by this harness — see the
            // class-level summary for the implication.
            var scenario = BuildPopulatedScenario(initialChildren: 50);

            var stop = false;
            Exception readerException = null;

            var writer = new Thread(() =>
            {
                var addedNodes = new Queue<ConfigNode>();
                var counter = 0;
                while (!Volatile.Read(ref stop))
                {
                    lock (ScenarioDataUpdater.GetSemaphore(ScenarioName + "_unprotected"))
                    {
                        var child = new ConfigNode("") { Name = $"Child_{counter++}" };
                        scenario.AddNode(child);
                        addedNodes.Enqueue(child);
                        if (addedNodes.Count > 100)
                        {
                            var oldest = addedNodes.Dequeue();
                            scenario.RemoveNode(oldest);
                        }
                    }
                }
            });

            var reader = new Thread(() =>
            {
                try
                {
                    while (!Volatile.Read(ref stop) && readerException == null)
                    {
                        // No lock — this is the buggy serialization path.
                        var _ = scenario.ToString();
                    }
                }
                catch (Exception e)
                {
                    readerException = e;
                }
            });

            writer.Start();
            reader.Start();

            // Give the race up to 2 seconds to fire. In practice it fires within
            // tens of ms on this workstation; the headroom is for slow CI runners.
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (readerException == null && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }

            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(5));
            reader.Join(TimeSpan.FromSeconds(5));

            Assert.IsNotNull(
                readerException,
                "Expected unprotected ToString to race the writer and throw within 2s. " +
                "If this assertion fails, the BUG-033 race is no longer reproducible by " +
                "this harness — revisit the positive test's coverage assumptions.");
        }

        [TestMethod]
        public void GetSemaphore_IsIdempotentForSameName()
        {
            // The fix relies on ScenarioDataUpdater.GetSemaphore returning the SAME object
            // instance for the same scenario name across calls — otherwise the backup-side
            // lock and the writer-side lock would be distinct objects and the race would
            // remain. ConcurrentDictionary.GetOrAdd guarantees this, but pin the contract.
            var first = ScenarioDataUpdater.GetSemaphore("idempotency-check");
            var second = ScenarioDataUpdater.GetSemaphore("idempotency-check");

            Assert.AreSame(first, second);
        }

        [TestMethod]
        public void GetSemaphore_DistinctNamesReturnDistinctLocks()
        {
            // Per-scenario locking is the whole point — Funds writers do not block Reputation
            // writers. Pin the contract that two scenario names produce two lock objects.
            var fundsLock = ScenarioDataUpdater.GetSemaphore("Funding");
            var repLock = ScenarioDataUpdater.GetSemaphore("Reputation");

            Assert.AreNotSame(fundsLock, repLock);
        }

        private static ConfigNode BuildPopulatedScenario(int initialChildren)
        {
            var scenario = new ConfigNode("") { Name = ScenarioName };
            for (var i = 0; i < initialChildren; i++)
            {
                scenario.AddNode(new ConfigNode("") { Name = $"Seed_{i}" });
            }
            return scenario;
        }
    }
}
