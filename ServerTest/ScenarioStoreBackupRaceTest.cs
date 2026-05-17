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
    /// This test exercises the lock contract directly (no disk, no network). If
    /// someone removes the lock from <c>SerializeUnderWriterLock</c> the test will
    /// start flaking within milliseconds — which is the warning signal.
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
                            // Cycle: add a new child, and once we've accumulated more
                            // than 100 also evict the oldest. Keeps tree size bounded
                            // while continuously perturbing the children list that
                            // ToString iterates.
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
                        // Force the consumer to actually read the result, so the JIT
                        // cannot elide the work as dead code.
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

            // 500ms is long enough that an unprotected ToString would throw thousands
            // of times. Short enough that the test stays well under MSTest defaults.
            Thread.Sleep(500);

            Volatile.Write(ref stop, true);
            writer.Join(TimeSpan.FromSeconds(5));
            reader.Join(TimeSpan.FromSeconds(5));

            Assert.IsNull(writerException, $"Writer thread threw: {writerException}");
            Assert.IsNull(readerException, $"Reader thread threw — BUG-033 regression: {readerException}");
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
