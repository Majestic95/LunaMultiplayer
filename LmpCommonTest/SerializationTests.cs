using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Locks;
using LmpCommon.Message.Data.Kerbal;
using LmpCommon.Message.Data.Lock;
using LmpCommon.Message.Data.Settings;
using LmpCommon.Message.Data.ShareProgress;
using LmpCommon.Message.Data.Vessel;
using LmpCommon.Message.Server;
using LmpCommonTest.Properties;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Text;
using System.Threading.Tasks;

namespace LmpCommonTest
{
    [TestClass]
    public class SerializationTests
    {
        private static readonly ServerMessageFactory Factory = new ServerMessageFactory();
        private static readonly ClientMessageFactory ClientFactory = new ClientMessageFactory();
        private static readonly Random Rnd = new Random();
        private static readonly NetClient Client = new NetClient(new NetPeerConfiguration("TESTS"));

        [TestMethod]
        public void TestSerializeDeserializeVesselUpdateMsg()
        {
            var msgData = Factory.CreateNewMessageData<VesselUpdateMsgData>();
            msgData.VesselId = Guid.NewGuid();
            msgData.Name = "Name";
            msgData.Type = "Type";
            msgData.DistanceTraveled = 222;
            msgData.Situation = "Situation";
            msgData.Landed = true;
            msgData.LandedAt = "LandedAt";
            msgData.DisplayLandedAt = "DisplayLandedAt";
            msgData.Splashed = false;
            msgData.MissionTime = Rnd.NextDouble();
            msgData.LaunchTime = Rnd.NextDouble();
            msgData.LastUt = Rnd.NextDouble();
            msgData.Persistent = false;
            msgData.RefTransformId = (uint)Rnd.Next();
            msgData.AutoClean = false;
            msgData.AutoCleanReason = string.Empty;
            msgData.WasControllable = true;
            msgData.Stage = 0;
            msgData.Com[0] = 0;
            msgData.Com[1] = 0;
            msgData.Com[2] = 0;
            msgData.BodyName = "Kerbin";

            var msg = Factory.CreateNew<VesselCliMsg>(msgData);

            //Serialize
            var expectedDataSize = msg.GetMessageSize();
            var lidgrenMsgSend = Client.CreateMessage(expectedDataSize);
            msg.Serialize(lidgrenMsgSend);
            var realSize = lidgrenMsgSend.LengthBytes;

            //Usually the expected size will be a bit more as Lidgren writes the size of the strings in a base128 int (so it uses less bytes)
            Assert.IsTrue(expectedDataSize >= realSize);

            //Simulate sending
            var data = lidgrenMsgSend.ReadBytes(lidgrenMsgSend.LengthBytes);
            var lidgrenMsgRecv = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            lidgrenMsgRecv.LengthBytes = lidgrenMsgSend.LengthBytes;

            msg.Recycle();

            //Deserialize
            var msgDes = Factory.Deserialize(lidgrenMsgRecv, Environment.TickCount);

        }

        [TestMethod]
        public void TestSerializeDeserializeChatChannelMsg()
        {
            var msgData = Factory.CreateNewMessageData<ChatMsgData>();
            msgData.Text = "T";

            var msg = Factory.CreateNew<ChatCliMsg>(msgData);

            //Serialize
            var expectedDataSize = msg.GetMessageSize();
            var lidgrenMsgSend = Client.CreateMessage(expectedDataSize);
            msg.Serialize(lidgrenMsgSend);
            var realSize = lidgrenMsgSend.LengthBytes;

            //Usually the expected size will be a bit more as Lidgren writes the size of the strings in a base128 int (so it uses less bytes)
            Assert.IsTrue(expectedDataSize >= realSize);

            //Simulate sending
            var data = lidgrenMsgSend.ReadBytes(lidgrenMsgSend.LengthBytes);
            var lidgrenMsgRecv = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            lidgrenMsgRecv.LengthBytes = lidgrenMsgSend.LengthBytes;

            msg.Recycle();

            //Deserialize
            var msgDes = Factory.Deserialize(lidgrenMsgRecv, Environment.TickCount);
        }

        // ---- Stage 5.15b: Agency wire messages ----
        // Each test populates a message, serializes via the factory, deserializes via the
        // SAME factory (Server side — same one ServerMessageFactory uses in production),
        // and asserts every payload field round-tripped lossless. The existing
        // VesselUpdate/Chat tests above only verify "no throw" — these are stricter
        // because the Agency wire is brand-new and any silent field-drop today would
        // surface as a wire-protocol bug across the next dozen stage-5 commits.

        [TestMethod]
        public void TestSerializeDeserializeAgencyHandshakeMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyHandshakeMsgData>();
            msgData.AssignedAgencyId = Guid.NewGuid();
            msgData.OtherAgencyCount = 2;
            msgData.OtherAgencies = new[]
            {
                new AgencyInfo { AgencyId = Guid.NewGuid(), OwningPlayerName = "Alice", DisplayName = "Alice Space Agency" },
                new AgencyInfo { AgencyId = Guid.NewGuid(), OwningPlayerName = "Bob",   DisplayName = "Bob Space Agency"   },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyHandshakeMsgData>(msgData);

            Assert.AreEqual(msgData.AssignedAgencyId, roundTripped.AssignedAgencyId);
            Assert.AreEqual(2, roundTripped.OtherAgencyCount);
            Assert.AreEqual(msgData.OtherAgencies[0].AgencyId, roundTripped.OtherAgencies[0].AgencyId);
            Assert.AreEqual("Alice", roundTripped.OtherAgencies[0].OwningPlayerName);
            Assert.AreEqual("Alice Space Agency", roundTripped.OtherAgencies[0].DisplayName);
            Assert.AreEqual(msgData.OtherAgencies[1].AgencyId, roundTripped.OtherAgencies[1].AgencyId);
            Assert.AreEqual("Bob Space Agency", roundTripped.OtherAgencies[1].DisplayName);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyHandshakeMsg_EmptyOtherAgenciesArray()
        {
            // First-player-on-server case: assigned agency exists, but no other agencies
            // to enumerate. Pin that an empty array round-trips cleanly (count=0, no
            // entries serialized, no bogus-zero AgencyInfo materialized on the read side).
            var msgData = Factory.CreateNewMessageData<AgencyHandshakeMsgData>();
            msgData.AssignedAgencyId = Guid.NewGuid();
            msgData.OtherAgencyCount = 0;
            msgData.OtherAgencies = new AgencyInfo[0];

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyHandshakeMsgData>(msgData);

            Assert.AreEqual(msgData.AssignedAgencyId, roundTripped.AssignedAgencyId);
            Assert.AreEqual(0, roundTripped.OtherAgencyCount);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyCreateRequestMsg()
        {
            // Client-to-server path. Use RoundTripClient (AgencyCliMsg + ClientMessageFactory)
            // so the on-wire messageType byte (ClientMessageType.Agency = 20) AND the
            // AgencyCliMsg.SubTypeDictionary lookup are both exercised — the wire-symmetry
            // surface that BUG-010 taught us to test on both sides. Server-side direction
            // is covered by the other three Agency tests through RoundTripServer.
            var msgData = ClientFactory.CreateNewMessageData<AgencyCreateRequestMsgData>();
            msgData.DisplayName = "Майор95 🚀 Space Agency";

            var roundTripped = RoundTripClient<AgencyCliMsg, AgencyCreateRequestMsgData>(msgData);

            Assert.AreEqual("Майор95 🚀 Space Agency", roundTripped.DisplayName);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyCreateReplyMsg_Success()
        {
            var msgData = Factory.CreateNewMessageData<AgencyCreateReplyMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.DisplayName = "My Cool Agency";
            msgData.Success = true;
            msgData.Reason = string.Empty;

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyCreateReplyMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual("My Cool Agency", roundTripped.DisplayName);
            Assert.IsTrue(roundTripped.Success);
            Assert.AreEqual(string.Empty, roundTripped.Reason);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyCreateReplyMsg_Failure()
        {
            // The failure path carries a Reason string; pin that the bool + reason
            // round-trip independently so a future serializer "optimization" that
            // skips the reason field on Success=true gets caught immediately.
            var msgData = Factory.CreateNewMessageData<AgencyCreateReplyMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.DisplayName = string.Empty;
            msgData.Success = false;
            msgData.Reason = "Display name already taken by another agency";

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyCreateReplyMsgData>(msgData);

            Assert.IsFalse(roundTripped.Success);
            Assert.AreEqual("Display name already taken by another agency", roundTripped.Reason);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyContractMsg()
        {
            // Stage 5.17d — owner-only contract batch S→C. Pin that AgencyId, count,
            // and per-entry Guid + bytes all round-trip. The Data array compresses
            // on serialize and decompresses on deserialize via Common.ThreadSafeCompress,
            // so the post-deserialize bytes should match the pre-serialize bytes.
            //
            // Caveat: ContractInfo.Serialize mutates Data IN-PLACE via Common.ThreadSafeCompress.
            // The source byte arrays we hand to msgData.Contracts will be compressed bytes
            // by the time Serialize returns. Snapshot the expected bytes BEFORE round-trip
            // so the post-round-trip assertion compares against the original (pre-compress)
            // payload, not the mutated source. This in-place mutation is a wire-layer
            // quirk shared with CraftInfo / VesselProtoMsgData (every CachedQlz wire path).
            var agencyId = Guid.NewGuid();
            var c1Guid = Guid.NewGuid();
            var c2Guid = Guid.NewGuid();
            var c1Original = Encoding.UTF8.GetBytes("guid = " + c1Guid.ToString("N") + "\nstate = Active");
            var c2Original = Encoding.UTF8.GetBytes("guid = " + c2Guid.ToString("N") + "\nstate = Completed\nvalues = 1,2,3");
            var c1Bytes = (byte[])c1Original.Clone();
            var c2Bytes = (byte[])c2Original.Clone();

            var msgData = Factory.CreateNewMessageData<AgencyContractMsgData>();
            msgData.AgencyId = agencyId;
            msgData.ContractCount = 2;
            msgData.Contracts = new[]
            {
                new ContractInfo { ContractGuid = c1Guid, Data = c1Bytes, NumBytes = c1Bytes.Length },
                new ContractInfo { ContractGuid = c2Guid, Data = c2Bytes, NumBytes = c2Bytes.Length },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyContractMsgData>(msgData);

            Assert.AreEqual(agencyId, roundTripped.AgencyId);
            Assert.AreEqual(2, roundTripped.ContractCount);
            Assert.AreEqual(c1Guid, roundTripped.Contracts[0].ContractGuid);
            Assert.AreEqual(c1Original.Length, roundTripped.Contracts[0].NumBytes,
                "Contract bytes did not round-trip (NumBytes after compress+decompress should match original).");
            for (var i = 0; i < c1Original.Length; i++)
                Assert.AreEqual(c1Original[i], roundTripped.Contracts[0].Data[i], $"byte mismatch at offset {i}");
            Assert.AreEqual(c2Guid, roundTripped.Contracts[1].ContractGuid);
            Assert.AreEqual(c2Original.Length, roundTripped.Contracts[1].NumBytes);
            for (var i = 0; i < c2Original.Length; i++)
                Assert.AreEqual(c2Original[i], roundTripped.Contracts[1].Data[i], $"c2 byte mismatch at offset {i}");
        }

        [TestMethod]
        public void TestSerializeDeserializeLockRejectMsg()
        {
            // Stage 5.18d slice (c) — server-side reject feedback. Pin lock
            // round-trip + reason byte + owning-agency id.
            var owningAgency = Guid.NewGuid();
            var vesselId = Guid.NewGuid();
            var msgData = Factory.CreateNewMessageData<LockRejectMsgData>();
            msgData.Lock = new LockDefinition(LockType.Control, "Alice", vesselId);
            msgData.Reason = LockRejectReason.CrossAgency;
            msgData.OwningAgencyId = owningAgency;

            var roundTripped = RoundTripServer<LockSrvMsg, LockRejectMsgData>(msgData);

            Assert.AreEqual(LockType.Control, roundTripped.Lock.Type);
            Assert.AreEqual(vesselId, roundTripped.Lock.VesselId);
            Assert.AreEqual("Alice", roundTripped.Lock.PlayerName);
            Assert.AreEqual(LockRejectReason.CrossAgency, roundTripped.Reason);
            Assert.AreEqual(owningAgency, roundTripped.OwningAgencyId);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyVisibilityMsg()
        {
            // Stage 5.18d — broadcast S→C ownership push (transferagency / deleteagency
            // cascade). Round-trip three entries covering the three meaningful states:
            // X → Y (real → real, transferagency), X → Empty (deleteagency cascade
            // demote), Empty → Y (Unassigned-sentinel claim).
            var v1 = Guid.NewGuid(); var newAgency1 = Guid.NewGuid();
            var v2 = Guid.NewGuid(); // demote to Empty
            var v3 = Guid.NewGuid(); var newAgency3 = Guid.NewGuid();

            var msgData = Factory.CreateNewMessageData<AgencyVisibilityMsgData>();
            msgData.ChangeCount = 3;
            msgData.Changes = new[]
            {
                new VesselOwnershipChange { VesselId = v1, NewOwningAgencyId = newAgency1 },
                new VesselOwnershipChange { VesselId = v2, NewOwningAgencyId = Guid.Empty },
                new VesselOwnershipChange { VesselId = v3, NewOwningAgencyId = newAgency3 },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyVisibilityMsgData>(msgData);

            Assert.AreEqual(3, roundTripped.ChangeCount);
            Assert.AreEqual(v1, roundTripped.Changes[0].VesselId);
            Assert.AreEqual(newAgency1, roundTripped.Changes[0].NewOwningAgencyId);
            Assert.AreEqual(v2, roundTripped.Changes[1].VesselId);
            Assert.AreEqual(Guid.Empty, roundTripped.Changes[1].NewOwningAgencyId,
                "Empty NewOwningAgencyId (deleteagency cascade demotion) MUST round-trip as Empty, " +
                "not be silently dropped by the wire — ForceRecordOwnership relies on this.");
            Assert.AreEqual(v3, roundTripped.Changes[2].VesselId);
            Assert.AreEqual(newAgency3, roundTripped.Changes[2].NewOwningAgencyId);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyVisibilityMsg_EmptyBatch()
        {
            // Defensive empty-batch case. AgencySystemSender.BroadcastVisibilityChange
            // early-returns on empty input so this path shouldn't fire in practice, but
            // pin the wire shape anyway — a future caller bypassing the early-return
            // must not crash the receiver.
            var msgData = Factory.CreateNewMessageData<AgencyVisibilityMsgData>();
            msgData.ChangeCount = 0;
            msgData.Changes = new VesselOwnershipChange[0];

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyVisibilityMsgData>(msgData);

            Assert.AreEqual(0, roundTripped.ChangeCount);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyContractMsg_EmptyBatch()
        {
            // First-connect / no-contracts case: agency exists, owner has zero contracts,
            // sender opportunistically sends an empty batch. Pin that count=0 round-trips
            // without dereferencing into the entries array.
            var msgData = Factory.CreateNewMessageData<AgencyContractMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.ContractCount = 0;
            msgData.Contracts = new ContractInfo[0];

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyContractMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual(0, roundTripped.ContractCount);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyOrbitalStateMsg()
        {
            // Phase 3 Slice D-1 — full populated batch through the wire to pin the
            // 7+1-field round-trip: TransferGuid + Origin/Destination Guids + Status
            // (opaque int) + StartTime/Duration (doubles) + NumBytes-prefixed
            // PayloadBytes. Multi-entry case catches off-by-one regressions in the
            // count-driven array read.
            var msgData = Factory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 2;
            var transferGuidA = Guid.NewGuid();
            var transferGuidB = Guid.NewGuid();
            var payloadA = Encoding.UTF8.GetBytes("status = Launched\nMarkerA = 1");
            var payloadB = Encoding.UTF8.GetBytes("status = Returning\nMarkerB = 2");
            msgData.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuidA,
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusLaunched,
                    StartTime = 12345.678,
                    Duration = -987.654,  // negative + decimal stress on the double serializer
                    PayloadBytes = payloadA,
                    NumBytes = payloadA.Length,
                },
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuidB,
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusReturning,
                    StartTime = 0,
                    Duration = 0,
                    PayloadBytes = payloadB,
                    NumBytes = payloadB.Length,
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyOrbitalStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual(2, roundTripped.EntryCount);
            Assert.AreEqual(transferGuidA, roundTripped.Entries[0].TransferGuid);
            Assert.AreEqual(AgencyOrbitalTransferEntry.StatusLaunched, roundTripped.Entries[0].Status);
            Assert.AreEqual(12345.678, roundTripped.Entries[0].StartTime);
            Assert.AreEqual(-987.654, roundTripped.Entries[0].Duration);
            Assert.AreEqual(payloadA.Length, roundTripped.Entries[0].NumBytes);
            CollectionAssert.AreEqual(payloadA, roundTripped.Entries[0].PayloadBytes,
                "PayloadBytes must round-trip byte-for-byte on the wire.");
            Assert.AreEqual(transferGuidB, roundTripped.Entries[1].TransferGuid);
            Assert.AreEqual(AgencyOrbitalTransferEntry.StatusReturning, roundTripped.Entries[1].Status);
            CollectionAssert.AreEqual(payloadB, roundTripped.Entries[1].PayloadBytes);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyOrbitalStateMsg_EmptyBatch()
        {
            // Connect-time catch-up under gate=on ships an empty batch when the
            // owner has zero pending transfers — pin that EntryCount=0 round-trips
            // without dereferencing into the entries array. Mirrors the contract /
            // kolony / planetary empty-batch case.
            var msgData = Factory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 0;
            msgData.Entries = new AgencyOrbitalTransferEntry[0];

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyOrbitalStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual(0, roundTripped.EntryCount);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyOrbitalStateMsg_CliMsgSubTypeDictionary()
        {
            // BUG-010 wire-symmetry coverage — verifies AgencyCliMsg.SubTypeDictionary
            // has slot 8 (OrbitalState) so client-to-server posts (postfix emit)
            // deserialize on the server side. Mirrors the Slice 5.15b
            // TestSerializeDeserializeAgencyCreateRequestMsg shape — without the Cli
            // entry the deserializer would silently drop the message.
            var msgData = ClientFactory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = Guid.Empty;  // C→S: server ignores
            msgData.EntryCount = 1;
            var transferGuid = Guid.NewGuid();
            var payload = Encoding.UTF8.GetBytes("status = Delivered");
            msgData.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = transferGuid,
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusDelivered,
                    PayloadBytes = payload,
                    NumBytes = payload.Length,
                },
            };

            var roundTripped = RoundTripClient<AgencyCliMsg, AgencyOrbitalStateMsgData>(msgData);

            Assert.AreEqual(1, roundTripped.EntryCount);
            Assert.AreEqual(transferGuid, roundTripped.Entries[0].TransferGuid);
            Assert.AreEqual(AgencyOrbitalTransferEntry.StatusDelivered, roundTripped.Entries[0].Status);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyOrbitalStateMsg_RemovedTransfersTail_Populated()
        {
            // [Phase 3 Slice E-1] The Guid-keyed removal tail must round-trip
            // alongside a populated forward-tail. Pins the new
            // RemovedTransferCount / RemovedTransferGuids fields and confirms
            // the dual-payload case (per-router migration can produce both
            // added-entries-to-dest AND removed-guids-to-source in the same
            // owner-only echo, even though the typical caller targets each
            // owner separately).
            var msgData = Factory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            var addedTransferGuid = Guid.NewGuid();
            var payload = Encoding.UTF8.GetBytes("status = Launched\nMigrationAdded = 1");
            msgData.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = addedTransferGuid,
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusLaunched,
                    PayloadBytes = payload,
                    NumBytes = payload.Length,
                },
            };
            var removedA = Guid.NewGuid();
            var removedB = Guid.NewGuid();
            var removedC = Guid.NewGuid();
            msgData.RemovedTransferCount = 3;
            msgData.RemovedTransferGuids = new[] { removedA, removedB, removedC };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyOrbitalStateMsgData>(msgData);

            Assert.AreEqual(1, roundTripped.EntryCount);
            Assert.AreEqual(addedTransferGuid, roundTripped.Entries[0].TransferGuid);
            Assert.AreEqual(3, roundTripped.RemovedTransferCount);
            Assert.AreEqual(removedA, roundTripped.RemovedTransferGuids[0]);
            Assert.AreEqual(removedB, roundTripped.RemovedTransferGuids[1]);
            Assert.AreEqual(removedC, roundTripped.RemovedTransferGuids[2]);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyOrbitalStateMsg_RemovedTransfersTail_TruncatedWireDefaultsEmpty()
        {
            // [Phase 3 Slice E-1] Forward-compat for the receive path: a
            // pre-Slice-E-1 sender ends the wire at the Entries loop and never
            // writes the RemovedTransferCount int. The Position<LengthBits guard
            // in InternalDeserialize must catch that case and default the tail
            // to empty WITHOUT throwing past end-of-stream. Same shape as the
            // SettingsReplyMsg TruncatedPayloadMissingTail precedent at
            // line 491 of this file.
            //
            // Setup: serialize a populated forward-tail + empty removal tail
            // (count=0, so the tail is just the 4-byte count). Truncate those
            // 32 bits from the receive buffer to simulate the pre-E-1 wire.
            var msgData = Factory.CreateNewMessageData<AgencyOrbitalStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            var payload = Encoding.UTF8.GetBytes("status = Launched");
            msgData.Entries = new[]
            {
                new AgencyOrbitalTransferEntry
                {
                    TransferGuid = Guid.NewGuid(),
                    OriginVesselId = Guid.NewGuid(),
                    DestinationVesselId = Guid.NewGuid(),
                    Status = AgencyOrbitalTransferEntry.StatusLaunched,
                    PayloadBytes = payload,
                    NumBytes = payload.Length,
                },
            };
            msgData.RemovedTransferCount = 0;
            msgData.RemovedTransferGuids = new Guid[0];

            var msg = Factory.CreateNew<AgencySrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            // Drop the trailing 32 bits — the 4-byte RemovedTransferCount Int32.
            // A pre-Slice-E-1 sender never emitted those bits; the Position
            // guard must default the tail without reading past end-of-stream.
            recvBuf.LengthBits = sentLengthBits - 32;
            msg.Recycle();

            var deserialised = (AgencyOrbitalStateMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            Assert.AreEqual(1, deserialised.EntryCount,
                "Pre-tail fields must decode normally — the truncation only dropped the tail bytes.");
            Assert.AreEqual(0, deserialised.RemovedTransferCount,
                "RemovedTransferCount must default to 0 when the tail is absent from the wire.");
            Assert.IsNotNull(deserialised.RemovedTransferGuids);
            Assert.AreEqual(0, deserialised.RemovedTransferGuids.Length,
                "RemovedTransferGuids must default to an empty array (NOT null) for caller convenience.");
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyKolonyStateMsg_RemovedKeysTail_Populated()
        {
            // [Phase 3 Slice E-1] String-keyed removal tail for kolony. The key
            // shape mirrors the server-side AgencyState.KolonyEntries dict-key:
            // $"{vesselId:N}|{bodyIndex}". Pin a multi-entry removal-batch to
            // catch off-by-one regressions and confirm UTF-8 round-trip
            // through Lidgren's variable-length string serialization.
            var vesselId = Guid.NewGuid().ToString("N");
            var msgData = Factory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyKolonyEntry
                {
                    VesselId = Guid.NewGuid().ToString("N"),
                    BodyIndex = 5,
                    GeologyResearch = 1234.56,
                    Funds = 78.9,
                },
            };
            msgData.RemovedKolonyKeyCount = 3;
            msgData.RemovedKolonyKeys = new[]
            {
                $"{vesselId}|5",
                $"{vesselId}|8",
                $"{vesselId}|13",
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyKolonyStateMsgData>(msgData);

            Assert.AreEqual(1, roundTripped.EntryCount);
            Assert.AreEqual(3, roundTripped.RemovedKolonyKeyCount);
            Assert.AreEqual($"{vesselId}|5", roundTripped.RemovedKolonyKeys[0]);
            Assert.AreEqual($"{vesselId}|8", roundTripped.RemovedKolonyKeys[1]);
            Assert.AreEqual($"{vesselId}|13", roundTripped.RemovedKolonyKeys[2]);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyKolonyStateMsg_RemovedKeysTail_TruncatedWireDefaultsEmpty()
        {
            // [Phase 3 Slice E-1] Forward-compat for kolony — same shape as the
            // orbital truncated test. Serialize with empty tail (count=0, 4
            // bytes on the wire), truncate the trailing 32 bits, deserialize,
            // confirm default-to-empty.
            var msgData = Factory.CreateNewMessageData<AgencyKolonyStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyKolonyEntry
                {
                    VesselId = Guid.NewGuid().ToString("N"),
                    BodyIndex = 1,
                },
            };
            msgData.RemovedKolonyKeyCount = 0;
            msgData.RemovedKolonyKeys = new string[0];

            var msg = Factory.CreateNew<AgencySrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            recvBuf.LengthBits = sentLengthBits - 32;
            msg.Recycle();

            var deserialised = (AgencyKolonyStateMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            Assert.AreEqual(1, deserialised.EntryCount);
            Assert.AreEqual(0, deserialised.RemovedKolonyKeyCount,
                "RemovedKolonyKeyCount must default to 0 when the tail is absent from the wire.");
            Assert.IsNotNull(deserialised.RemovedKolonyKeys);
            Assert.AreEqual(0, deserialised.RemovedKolonyKeys.Length);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyPlanetaryStateMsg_RemovedKeysTail_Populated()
        {
            // [Phase 3 Slice E-1] Planetary tail is forward-compat only under
            // the Q2 NO-MIGRATE policy (no Slice E-1 producer emits to it),
            // but the wire shape must still round-trip cleanly for the
            // hypothetical future cleanplanetaryentries admin command. Key
            // shape is $"{bodyIndex}|{resourceName}".
            var msgData = Factory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyPlanetaryEntry
                {
                    BodyIndex = 5,
                    ResourceName = "Hydrates",
                    StoredQuantity = 1234.56,
                    OwningVesselId = Guid.NewGuid(),
                },
            };
            msgData.RemovedPlanetaryKeyCount = 2;
            msgData.RemovedPlanetaryKeys = new[]
            {
                "5|Hydrates",
                "8|Karbonite",
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyPlanetaryStateMsgData>(msgData);

            Assert.AreEqual(1, roundTripped.EntryCount);
            Assert.AreEqual(2, roundTripped.RemovedPlanetaryKeyCount);
            Assert.AreEqual("5|Hydrates", roundTripped.RemovedPlanetaryKeys[0]);
            Assert.AreEqual("8|Karbonite", roundTripped.RemovedPlanetaryKeys[1]);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyPlanetaryStateMsg_RemovedKeysTail_TruncatedWireDefaultsEmpty()
        {
            // [Phase 3 Slice E-1] Forward-compat for planetary — same shape
            // as orbital + kolony.
            var msgData = Factory.CreateNewMessageData<AgencyPlanetaryStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyPlanetaryEntry
                {
                    BodyIndex = 1,
                    ResourceName = "Hydrates",
                    StoredQuantity = 100.0,
                    OwningVesselId = Guid.NewGuid(),
                },
            };
            msgData.RemovedPlanetaryKeyCount = 0;
            msgData.RemovedPlanetaryKeys = new string[0];

            var msg = Factory.CreateNew<AgencySrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            recvBuf.LengthBits = sentLengthBits - 32;
            msg.Recycle();

            var deserialised = (AgencyPlanetaryStateMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            Assert.AreEqual(1, deserialised.EntryCount);
            Assert.AreEqual(0, deserialised.RemovedPlanetaryKeyCount,
                "RemovedPlanetaryKeyCount must default to 0 when the tail is absent from the wire.");
            Assert.IsNotNull(deserialised.RemovedPlanetaryKeys);
            Assert.AreEqual(0, deserialised.RemovedPlanetaryKeys.Length);
        }

        // -----------------------------------------------------------------------
        // [Phase 4 Slice A — WOLF] Wire round-trip pinning for the 5 new
        // MsgData types. Mirrors the Phase 3 KolonyState / PlanetaryState /
        // OrbitalState shape — one populated-batch test per type covers
        // wire-protocol + nested-list preservation. Forward-compat tails +
        // empty-batch + Cli-direction symmetry are implicitly covered by the
        // shared helpers (RoundTripServer / RoundTripClient) — if any of the
        // 5 SubTypeDictionary appends desyncs, the round-trip would throw.
        // -----------------------------------------------------------------------

        [TestMethod]
        public void TestSerializeDeserializeAgencyWolfDepotStateMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyWolfDepotStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyWolfDepotEntry
                {
                    Body = "Duna",
                    Biome = "Lowlands",
                    IsEstablished = true,
                    IsSurveyed = true,
                    ResourceStreams = new System.Collections.Generic.List<AgencyWolfResourceStreamEntry>
                    {
                        new AgencyWolfResourceStreamEntry { ResourceName = "Hydrates", Incoming = 1000, Outgoing = 250 },
                    },
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyWolfDepotStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual(1, roundTripped.EntryCount);
            Assert.AreEqual("Duna", roundTripped.Entries[0].Body);
            Assert.AreEqual("Lowlands", roundTripped.Entries[0].Biome);
            Assert.IsTrue(roundTripped.Entries[0].IsEstablished);
            Assert.IsTrue(roundTripped.Entries[0].IsSurveyed);
            Assert.AreEqual(1, roundTripped.Entries[0].ResourceStreams.Count);
            Assert.AreEqual("Hydrates", roundTripped.Entries[0].ResourceStreams[0].ResourceName);
            Assert.AreEqual(1000, roundTripped.Entries[0].ResourceStreams[0].Incoming);
            Assert.AreEqual(250, roundTripped.Entries[0].ResourceStreams[0].Outgoing);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyWolfRouteStateMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyWolfRouteStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyWolfRouteEntry
                {
                    OriginBody = "Duna",
                    OriginBiome = "Lowlands",
                    DestinationBody = "Ike",
                    DestinationBiome = "Highlands",
                    Payload = 5000,
                    Resources = new System.Collections.Generic.List<AgencyWolfRouteResourceEntry>
                    {
                        new AgencyWolfRouteResourceEntry { ResourceName = "Fuel", Quantity = 2000 },
                    },
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyWolfRouteStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual("Duna", roundTripped.Entries[0].OriginBody);
            Assert.AreEqual("Ike", roundTripped.Entries[0].DestinationBody);
            Assert.AreEqual(5000, roundTripped.Entries[0].Payload);
            Assert.AreEqual(1, roundTripped.Entries[0].Resources.Count);
            Assert.AreEqual("Fuel", roundTripped.Entries[0].Resources[0].ResourceName);
            Assert.AreEqual(2000, roundTripped.Entries[0].Resources[0].Quantity);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyWolfHopperStateMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyWolfHopperStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyWolfHopperEntry
                {
                    Id = "550e8400-e29b-41d4-a716-446655440000",   // Guid WITH hyphens (WOLF source convention)
                    Body = "Mun",
                    Biome = "Highlands",
                    Recipe = "Hydrates,100,MetallicOre,50",
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyWolfHopperStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual("550e8400-e29b-41d4-a716-446655440000", roundTripped.Entries[0].Id,
                "Hopper Id must preserve hyphens on wire round-trip");
            Assert.AreEqual("Mun", roundTripped.Entries[0].Body);
            Assert.AreEqual("Hydrates,100,MetallicOre,50", roundTripped.Entries[0].Recipe);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyWolfTerminalStateMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyWolfTerminalStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            msgData.Entries = new[]
            {
                new AgencyWolfTerminalEntry
                {
                    Id = "550e8400e29b41d4a716446655440000",   // Guid "N" form (no hyphens — WOLF source convention)
                    Body = "Eve",
                    Biome = "Foothills",
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyWolfTerminalStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual("550e8400e29b41d4a716446655440000", roundTripped.Entries[0].Id,
                "Terminal Id must preserve N-form (no hyphens) on wire round-trip");
            Assert.AreEqual("Eve", roundTripped.Entries[0].Body);
            Assert.AreEqual("Foothills", roundTripped.Entries[0].Biome);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyWolfCrewRouteStateMsg()
        {
            var msgData = Factory.CreateNewMessageData<AgencyWolfCrewRouteStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.EntryCount = 1;
            var uniqueId = Guid.NewGuid().ToString("N");
            msgData.Entries = new[]
            {
                new AgencyWolfCrewRouteEntry
                {
                    ArrivalTime = 123456.789012,
                    OriginBody = "Kerbin",
                    OriginBiome = "Shores",
                    DestinationBody = "Mun",
                    DestinationBiome = "Crater",
                    Duration = 21600.5,
                    EconomyBerths = 4,
                    LuxuryBerths = 2,
                    FlightNumber = "7AB",
                    FlightStatus = "Enroute",
                    UniqueId = uniqueId,
                    Passengers = new System.Collections.Generic.List<AgencyWolfPassengerEntry>
                    {
                        new AgencyWolfPassengerEntry { Name = "Jebediah Kerman", DisplayName = "Jebediah Kerman", IsTourist = false, Occupation = "Pilot", Stars = 5 },
                        new AgencyWolfPassengerEntry { Name = "Tourist Alice", DisplayName = "Tourist Alice", IsTourist = true, Occupation = "Tourist", Stars = 0 },
                    },
                },
            };

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyWolfCrewRouteStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual(123456.789012, roundTripped.Entries[0].ArrivalTime, 1e-9);
            Assert.AreEqual(21600.5, roundTripped.Entries[0].Duration, 1e-9);
            Assert.AreEqual("Kerbin", roundTripped.Entries[0].OriginBody);
            Assert.AreEqual("Mun", roundTripped.Entries[0].DestinationBody);
            Assert.AreEqual(4, roundTripped.Entries[0].EconomyBerths);
            Assert.AreEqual(2, roundTripped.Entries[0].LuxuryBerths);
            Assert.AreEqual("7AB", roundTripped.Entries[0].FlightNumber);
            Assert.AreEqual("Enroute", roundTripped.Entries[0].FlightStatus);
            Assert.AreEqual(uniqueId, roundTripped.Entries[0].UniqueId);
            Assert.AreEqual(2, roundTripped.Entries[0].Passengers.Count);
            Assert.AreEqual("Jebediah Kerman", roundTripped.Entries[0].Passengers[0].Name);
            Assert.IsFalse(roundTripped.Entries[0].Passengers[0].IsTourist);
            Assert.AreEqual(5, roundTripped.Entries[0].Passengers[0].Stars);
            Assert.AreEqual("Tourist Alice", roundTripped.Entries[0].Passengers[1].Name);
            Assert.IsTrue(roundTripped.Entries[0].Passengers[1].IsTourist);
        }

        [TestMethod]
        public void TestSerializeDeserializeAgencyStateMsg()
        {
            // Mirror Stage 5.14c AgencyState's scalar surface — all six fields must
            // round-trip lossless. Funds/Science/Reputation are doubles; pin a
            // fractional value to catch any future serializer that drops to float.
            var msgData = Factory.CreateNewMessageData<AgencyStateMsgData>();
            msgData.AgencyId = Guid.NewGuid();
            msgData.OwningPlayerName = "Majestic95";
            msgData.DisplayName = "Majestic95 Space Agency";
            msgData.Funds = 25_000.5;
            msgData.Science = 12.25;
            msgData.Reputation = 7.125;

            var roundTripped = RoundTripServer<AgencySrvMsg, AgencyStateMsgData>(msgData);

            Assert.AreEqual(msgData.AgencyId, roundTripped.AgencyId);
            Assert.AreEqual("Majestic95", roundTripped.OwningPlayerName);
            Assert.AreEqual("Majestic95 Space Agency", roundTripped.DisplayName);
            Assert.AreEqual(25_000.5, roundTripped.Funds);
            Assert.AreEqual(12.25, roundTripped.Science);
            Assert.AreEqual(7.125, roundTripped.Reputation);
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_PerAgencyCareerEnabled_True()
        {
            // [Stage 5.17e-2] The PerAgencyCareerEnabled field is the tail-positional
            // bool that lets the 5.18a client mirror gate its agency UI / wire on the
            // server's functionally-active state (combined PerAgencyCareer + GameMode
            // check). Round-trip a representative payload with the flag ON to pin the
            // wire layout — if a future serializer regression drops the tail byte or
            // miscounts InternalGetMessageSize, this catches it.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;

            var roundTripped = RoundTripServer<SetingsSrvMsg, SettingsReplyMsgData>(msgData);

            Assert.IsTrue(roundTripped.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled=true must round-trip through Serialize/Deserialize.");
            // Sanity that the rest of the payload didn't shift one byte left/right.
            Assert.AreEqual(GameMode.Career, roundTripped.GameMode);
            Assert.AreEqual("test-server", roundTripped.ConsoleIdentifier);
            Assert.AreEqual(true, roundTripped.PrintMotdInChat,
                "PrintMotdInChat (the previous tail field) must remain stable when a new tail field is appended.");
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_PerAgencyCareerEnabled_FalseIsDefault()
        {
            // Mirror with the flag OFF. Default-initialised payload should round-trip
            // false → false; serializer must not coerce to true. This is the gate=off
            // and the misconfig (PerAgencyCareer=true + non-Career) case from the
            // server's perspective.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = false;

            var roundTripped = RoundTripServer<SetingsSrvMsg, SettingsReplyMsgData>(msgData);

            Assert.IsFalse(roundTripped.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled=false must round-trip through Serialize/Deserialize.");
            Assert.AreEqual(GameMode.Career, roundTripped.GameMode);
            Assert.AreEqual("test-server", roundTripped.ConsoleIdentifier);
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_TruncatedPayloadMissingTail_DefaultsFalse()
        {
            // [Stage 5.17e-2 review-finding A.3 + Stage 6 Phase 6.6] Backward
            // read-compat for tail-positional bool fields. A peer that doesn't ship
            // the final byte — older 0.31.0 server, mixed-dev-build, or any future
            // tail-bump we drop — must NOT throw on deserialize; defaults the
            // affected field to false (gate off). Matches the
            // VesselProtoMsgData.Reason / HandshakeRequest / WarpNewSubspace tail-bit
            // guard pattern.
            //
            // Setup: serialize a full payload with both gate flags TRUE, then
            // truncate by exactly 1 bit. After Phase 6.6 the new tail is
            // PerAgencyKerbalRosterEnabled, so the 1-bit truncation drops THAT bit
            // (formerly dropped PerAgencyCareerEnabled when it was the tail in
            // 5.17e-2). PerAgencyCareerEnabled is now one position back from the
            // tail and must still decode correctly. This pins the tail-bit-read
            // guard against the most recently added field — any future tail
            // additions follow the same pattern.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;
            msgData.PerAgencyKerbalRosterEnabled = true;

            var msg = Factory.CreateNew<SetingsSrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            recvBuf.LengthBits = sentLengthBits - 1; // drop trailing 1-bit bool

            msg.Recycle();

            var deserialised = (SettingsReplyMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            // The NEW tail field defaults to false — proves the Position<LengthBits
            // guard short-circuits the ReadBoolean for the most recently appended
            // field.
            Assert.IsFalse(deserialised.PerAgencyKerbalRosterEnabled,
                "PerAgencyKerbalRosterEnabled (Phase 6.6 tail field) must default to false when its bit is absent from the wire.");

            // PerAgencyCareerEnabled is no longer the tail, so its bit IS present
            // in the truncated buffer and must decode as TRUE — confirms the
            // 1-bit truncation dropped only the new tail field, not the previous one.
            Assert.IsTrue(deserialised.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled is one position back from the new Phase 6.6 tail and must still decode correctly when only the trailing 1 bit is dropped.");

            // Earlier fields remain correctly decoded — confirms the truncation only
            // affected the guarded tail read and the rest of the wire layout was
            // honoured.
            Assert.AreEqual(GameMode.Career, deserialised.GameMode);
            Assert.AreEqual("test-server", deserialised.ConsoleIdentifier);
            Assert.AreEqual(true, deserialised.PrintMotdInChat,
                "Previous tail field PrintMotdInChat must still decode correctly.");
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_TruncatedPayloadMissingBothTails_BothDefaultFalse()
        {
            // [Stage 6 Phase 6.6] Backward read-compat against a pre-5.17e-2 server
            // shape (or a future protocol-tail rewrite that drops BOTH tail fields).
            // Truncating 2 bits drops both PerAgencyKerbalRosterEnabled AND
            // PerAgencyCareerEnabled — both guards must short-circuit and default
            // false. Catches a future regression where one guard is added but the
            // other is removed, or where the field order in the deserialize body
            // gets reordered relative to the serialize body.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;
            msgData.PerAgencyKerbalRosterEnabled = true;

            var msg = Factory.CreateNew<SetingsSrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            recvBuf.LengthBits = sentLengthBits - 2; // drop both trailing 1-bit bools

            msg.Recycle();

            var deserialised = (SettingsReplyMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            Assert.IsFalse(deserialised.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled must default to false when its bit is also truncated off the wire.");
            Assert.IsFalse(deserialised.PerAgencyKerbalRosterEnabled,
                "PerAgencyKerbalRosterEnabled must default to false when its bit is truncated off the wire.");
            Assert.AreEqual(true, deserialised.PrintMotdInChat,
                "Previous-previous tail field PrintMotdInChat must still decode correctly when both gate tails are truncated.");
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_PerAgencyKerbalRosterEnabled_True()
        {
            // [Stage 6 Phase 6.6] The PerAgencyKerbalRosterEnabled tail field
            // mirrors the server's combined `PerAgencyEnabled AND PerAgencyKerbalRoster`
            // gate so the client's VesselLoader scrub site can distinguish "shared-
            // roster transient scrub" from "per-agency partition scrub." Round-trip
            // representative payloads with the flag ON pins the wire layout against
            // any future tail-byte miscount or InternalGetMessageSize regression.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;
            msgData.PerAgencyKerbalRosterEnabled = true;

            var roundTripped = RoundTripServer<SetingsSrvMsg, SettingsReplyMsgData>(msgData);

            Assert.IsTrue(roundTripped.PerAgencyKerbalRosterEnabled,
                "PerAgencyKerbalRosterEnabled=true must round-trip through Serialize/Deserialize.");
            Assert.IsTrue(roundTripped.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled (previous tail field) must remain stable when a new tail field is appended.");
            Assert.AreEqual(GameMode.Career, roundTripped.GameMode);
            Assert.AreEqual("test-server", roundTripped.ConsoleIdentifier);
        }

        [TestMethod]
        public void TestSerializeDeserializeSettingsReplyMsg_PerAgencyKerbalRosterEnabled_FalseIsDefault()
        {
            // [Stage 6 Phase 6.6] Mirror of the True case with the flag OFF.
            // Important precondition: under the intermediate Stage 5 → 6 ramp config
            // (PerAgencyCareer=on / PerAgencyKerbalRoster=off), the per-career gate
            // is true but the kerbal-roster gate is false — and the false value MUST
            // round-trip false so the client's VesselLoader scrub site doesn't seed
            // BUG-023-race transient mislabels.
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;
            msgData.PerAgencyKerbalRosterEnabled = false;

            var roundTripped = RoundTripServer<SetingsSrvMsg, SettingsReplyMsgData>(msgData);

            Assert.IsFalse(roundTripped.PerAgencyKerbalRosterEnabled,
                "PerAgencyKerbalRosterEnabled=false must round-trip through Serialize/Deserialize.");
            Assert.IsTrue(roundTripped.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled (previous tail field) must remain stable in the OFF/ON intermediate config.");
        }

        private static SettingsReplyMsgData BuildRepresentativeSettingsReply()
        {
            // Fill enough fields to make the round-trip meaningful — most importantly
            // ConsoleIdentifier (string with byte-count) and a recognisable tail value
            // (PrintMotdInChat) so a wire-layout shift would surface immediately.
            var msgData = Factory.CreateNewMessageData<SettingsReplyMsgData>();
            msgData.WarpMode = WarpMode.Subspace;
            msgData.GameMode = GameMode.Career;
            msgData.TerrainQuality = TerrainQuality.High;
            msgData.AllowCheats = false;
            msgData.AllowAdmin = true;
            msgData.AllowSackKerbals = false;
            msgData.MaxNumberOfAsteroids = 12;
            msgData.MaxNumberOfComets = 3;
            msgData.ConsoleIdentifier = "test-server";
            msgData.GameDifficulty = GameDifficulty.Normal;
            msgData.SafetyBubbleDistance = 100f;
            msgData.MaxVesselParts = 1000;
            msgData.VesselUpdatesMsInterval = 100;
            msgData.SecondaryVesselUpdatesMsInterval = 500;
            msgData.StartingFunds = 25000f;
            msgData.StartingReputation = 0f;
            msgData.StartingScience = 0f;
            msgData.PrintMotdInChat = true;
            return msgData;
        }

        /// <summary>
        /// Serialise via <typeparamref name="TMsg"/>, deserialise via the
        /// ServerMessageFactory in this test class, return the typed payload. Pulled
        /// out as a helper so the six Agency tests stay focused on the fields they pin.
        /// </summary>
        private static TData RoundTripServer<TMsg, TData>(TData msgData)
            where TMsg : class, LmpCommon.Message.Interface.IMessageBase
            where TData : class, LmpCommon.Message.Interface.IMessageData
            => RoundTripVia<TMsg, TData>(msgData, Factory);

        /// <summary>
        /// Client→server direction of the wire — uses the ClientMessageFactory so the
        /// on-wire <c>messageType</c> field is the client-side enum value and the
        /// matching CliMsg's SubTypeDictionary is consulted on deserialize. Critical
        /// for BUG-010-style wire-symmetry coverage: a missing entry on the Cli
        /// SubTypeDictionary would otherwise silently drop the message on the receiver.
        /// </summary>
        private static TData RoundTripClient<TMsg, TData>(TData msgData)
            where TMsg : class, LmpCommon.Message.Interface.IMessageBase
            where TData : class, LmpCommon.Message.Interface.IMessageData
            => RoundTripVia<TMsg, TData>(msgData, ClientFactory);

        private static TData RoundTripVia<TMsg, TData>(TData msgData, LmpCommon.Message.Base.FactoryBase factory)
            where TMsg : class, LmpCommon.Message.Interface.IMessageBase
            where TData : class, LmpCommon.Message.Interface.IMessageData
        {
            var msg = factory.CreateNew<TMsg>(msgData);

            var expectedDataSize = msg.GetMessageSize();
            var lidgrenMsgSend = Client.CreateMessage(expectedDataSize);
            msg.Serialize(lidgrenMsgSend);

            Assert.IsTrue(expectedDataSize >= lidgrenMsgSend.LengthBytes,
                $"GetMessageSize ({expectedDataSize}) must be an upper bound on the actual serialized size ({lidgrenMsgSend.LengthBytes})");

            var data = lidgrenMsgSend.ReadBytes(lidgrenMsgSend.LengthBytes);
            var lidgrenMsgRecv = Client.CreateIncomingMessage(NetIncomingMessageType.Data, data);
            lidgrenMsgRecv.LengthBytes = lidgrenMsgSend.LengthBytes;

            msg.Recycle();

            var deserialised = factory.Deserialize(lidgrenMsgRecv, Environment.TickCount);
            return (TData)deserialised.Data;
        }

        [TestMethod]
        public void TestSerializeCompressThreadSafety()
        {
            const int iterations = 2000;

            var msgData = Factory.CreateNewMessageData<KerbalProtoMsgData>();
            msgData.Kerbal.KerbalName = "TEST";
            msgData.Kerbal.KerbalData = Encoding.UTF8.GetBytes(Resources.Jebediah_Kerman);
            msgData.Kerbal.NumBytes = msgData.Kerbal.KerbalData.Length;

            var msg = Factory.CreateNew<KerbalCliMsg>(msgData);

            Parallel.For(0, iterations, _ =>
            {
                try
                {
                    msg.Serialize(Client.CreateMessage());
                }
                catch (Exception ex)
                {
                    throw new AggregateException("Serialize failed under parallel load", ex);
                }
            });
        }
    }
}
