using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Agency;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.18g — unit tests pinning <see cref="AgencyCurrencyRouter.IsNonFinite"/>.
    /// Closes the BLOCKING currency-trust gap surfaced by the 2026-05-19 pre-multiplayer
    /// full-analysis: pre-5.18g the router wrote <c>agency.Funds = msg.Funds</c> with no
    /// guard, so a wire-corrupted or cheat-client value (NaN / +∞ / -∞) flowed straight
    /// to persisted state. The fix's decision surface is this pure helper; integration
    /// with the wire echo + lock is covered in
    /// <c>MockClientTest/AgencyCurrencyRoutingTest.cs</c>.
    ///
    /// <para><b>Minimum-scope policy (operator-confirmed 2026-05-19).</b> Only NaN and
    /// ±Infinity are rejected. <c>double.MaxValue</c> and any finite value (including
    /// negative) pass through unchanged because KSP + Contract Configurator + KCT
    /// legitimately mutate career scalars across wide ranges (vessel-recovery bounties,
    /// large CC rewards, strategy commits). A future hardening slice can add a
    /// configurable <c>MaxAbsAgencyValue</c> setting if telemetry shows operators
    /// repeatedly observing high-magnitude grief.</para>
    /// </summary>
    [TestClass]
    public class AgencyCurrencyRouterValidationTest
    {
        [TestMethod]
        public void IsNonFinite_NaN_ReturnsTrue()
        {
            // Wire-corruption + cheat-client primary case. Persisting NaN would
            // immediately propagate to Funding.Instance via the 5.18a apply path.
            Assert.IsTrue(AgencyCurrencyRouter.IsNonFinite(double.NaN));
        }

        [TestMethod]
        public void IsNonFinite_PositiveInfinity_ReturnsTrue()
        {
            // Easiest grief vector: setting Funds = +Infinity instantly funds any
            // build. The router must reject before the persist.
            Assert.IsTrue(AgencyCurrencyRouter.IsNonFinite(double.PositiveInfinity));
        }

        [TestMethod]
        public void IsNonFinite_NegativeInfinity_ReturnsTrue()
        {
            // Symmetric grief: -Infinity on Reputation locks the target into the
            // worst possible standing. Symmetric defense.
            Assert.IsTrue(AgencyCurrencyRouter.IsNonFinite(double.NegativeInfinity));
        }

        [TestMethod]
        public void IsNonFinite_DoubleMaxValue_ReturnsFalse()
        {
            // Documented minimum-scope limitation: finite values pass. operator-
            // confirmed 2026-05-19. A wire value of double.MaxValue would persist;
            // future hardening can add a configurable cap.
            Assert.IsFalse(AgencyCurrencyRouter.IsNonFinite(double.MaxValue));
        }

        [TestMethod]
        public void IsNonFinite_DoubleMinValue_ReturnsFalse()
        {
            // Symmetric pass-through for the most-negative finite double.
            Assert.IsFalse(AgencyCurrencyRouter.IsNonFinite(double.MinValue));
        }

        [TestMethod]
        public void IsNonFinite_NegativeFiniteValue_ReturnsFalse()
        {
            // KSP allows brief negative Funds (failed contracts), so finite
            // negatives must pass through. Regression guard against an over-
            // zealous future "clamp to zero" change.
            Assert.IsFalse(AgencyCurrencyRouter.IsNonFinite(-12345.6789));
        }

        [TestMethod]
        public void IsNonFinite_Zero_ReturnsFalse()
        {
            // Zero is the canonical legitimate starting value.
            Assert.IsFalse(AgencyCurrencyRouter.IsNonFinite(0.0));
        }

        [TestMethod]
        public void IsNonFinite_TypicalCareerValue_ReturnsFalse()
        {
            // Sanity guard for a normal mutation (StartingFunds default).
            Assert.IsFalse(AgencyCurrencyRouter.IsNonFinite(25_000d));
        }
    }
}
