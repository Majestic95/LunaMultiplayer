using Microsoft.VisualStudio.TestTools.UnitTesting;
using Server.System.Vessel.Classes;
using System;
using System.IO;
using System.Linq;

namespace ServerTest
{
    /// <summary>
    /// Stage 5.16b — round-trip pins for the new <see cref="Vessel.OwningAgencyId"/>
    /// accessor. The on-disk form is the 32-char hex "N" Guid format that matches the
    /// canonical <c>Universe/Agencies/{guid}.txt</c> filename so operators can grep
    /// across both stores without conversion. Cross-process behaviour (server stamps
    /// the sender's agency on first ingest, preserves the existing owner on
    /// subsequent protos) is covered end-to-end in <c>MockClientTest/VesselOwningAgencyTest</c>.
    /// </summary>
    [TestClass]
    public class VesselOwningAgencyTest
    {
        private static readonly string XmlExamplePath = Path.Combine(Directory.GetCurrentDirectory(), "XmlExampleFiles", "Others");

        [TestMethod]
        public void OwningAgencyId_DefaultsToEmpty_ForExampleFiles()
        {
            var vessel = LoadSampleVessel();
            // Sample files predate the lmpOwningAgency field; they should load with Guid.Empty.
            Assert.AreEqual(Guid.Empty, vessel.OwningAgencyId);
        }

        [TestMethod]
        public void OwningAgencyId_RoundTripsThroughToStringAndParse()
        {
            var vessel = LoadSampleVessel();
            var assigned = Guid.NewGuid();

            vessel.OwningAgencyId = assigned;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(assigned, reparsed.OwningAgencyId);
        }

        [TestMethod]
        public void OwningAgencyId_PersistsAsNFormat()
        {
            // The on-disk field value must be the 32-char hex string (no hyphens, no braces) so
            // it can be eyeballed against the Universe/Agencies/{guid}.txt filename directly.
            var vessel = LoadSampleVessel();
            var assigned = new Guid("0123456789abcdef0123456789abcdef");

            vessel.OwningAgencyId = assigned;
            var serialized = vessel.ToString();

            Assert.IsTrue(serialized.Contains(Vessel.OwningAgencyFieldName + " = 0123456789abcdef0123456789abcdef"),
                $"Expected '{Vessel.OwningAgencyFieldName} = 0123456789abcdef0123456789abcdef' in serialized vessel.");
        }

        [TestMethod]
        public void OwningAgencyId_OverwritesPreviousValue()
        {
            var vessel = LoadSampleVessel();
            var first = Guid.NewGuid();
            var second = Guid.NewGuid();

            vessel.OwningAgencyId = first;
            vessel.OwningAgencyId = second;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(second, reparsed.OwningAgencyId);
        }

        [TestMethod]
        public void OwningAgencyId_EmptyGuidIsValidSentinel()
        {
            var vessel = LoadSampleVessel();

            vessel.OwningAgencyId = Guid.NewGuid();
            vessel.OwningAgencyId = Guid.Empty;
            var reparsed = new Vessel(vessel.ToString());

            // Setter writes the all-zero 32-char hex form; getter parses it back as Guid.Empty.
            // Equivalent to the AuthoritativeSubspaceId=0 sentinel: persisted explicitly, not
            // by field removal. The "should the proto stamp this vessel?" decision lives in
            // VesselDataUpdater.RawConfigNodeInsertOrUpdate, not in this accessor.
            Assert.AreEqual(Guid.Empty, reparsed.OwningAgencyId);
        }

        [TestMethod]
        public void OwningAgencyId_RoundTripsAlongsideAuthoritativeSubspaceId()
        {
            // Both lmp-prefixed fields share the same top-level Fields collection. Setting
            // one must not perturb the other.
            var vessel = LoadSampleVessel();
            var agency = Guid.NewGuid();

            vessel.AuthoritativeSubspaceId = 17;
            vessel.OwningAgencyId = agency;
            var reparsed = new Vessel(vessel.ToString());

            Assert.AreEqual(17, reparsed.AuthoritativeSubspaceId);
            Assert.AreEqual(agency, reparsed.OwningAgencyId);
        }

        [TestMethod]
        public void OwningAgencyId_MalformedFieldValueParsesAsEmpty()
        {
            // A bare string "not-a-guid" written into the lmpOwningAgency field (e.g. via a
            // hand-edit) must not crash the getter — return Guid.Empty so the existing-stamp
            // preserve branch in RawConfigNodeInsertOrUpdate falls through and the server
            // re-stamps from the sender's agency.
            var vessel = LoadSampleVessel();

            // Inject a bad value by going through the same Fields collection the setter uses.
            // We can't call the setter with a "string" here because the API is strongly typed
            // to Guid; instead reuse AuthoritativeSubspaceId's setter pattern but on the
            // OwningAgencyId field via Fields directly.
            vessel.OwningAgencyId = Guid.NewGuid();  // ensures the field exists
            vessel.Fields.Update(Vessel.OwningAgencyFieldName, "not-a-guid");

            Assert.AreEqual(Guid.Empty, vessel.OwningAgencyId);

            // Round-trip the bad value through ToString/parse to confirm it isn't silently
            // discarded — the field stays on disk; only the getter coerces to Guid.Empty.
            var reparsed = new Vessel(vessel.ToString());
            Assert.AreEqual(Guid.Empty, reparsed.OwningAgencyId);
        }

        private static Vessel LoadSampleVessel()
        {
            var samplePath = Directory.GetFiles(XmlExamplePath).First();
            return new Vessel(File.ReadAllText(samplePath));
        }
    }
}
