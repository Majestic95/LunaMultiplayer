using Lidgren.Network;
using LmpCommon.Message;
using LmpCommon.Message.Client;
using LmpCommon.Enums;
using LmpCommon.Message.Data.Agency;
using LmpCommon.Message.Data.Chat;
using LmpCommon.Message.Data.Kerbal;
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
            // [Stage 5.17e-2 review-finding A.3] Backward read-compat for the tail
            // PerAgencyCareerEnabled bool. A peer that doesn't ship the byte —
            // older 0.31.0 server, mixed-dev-build, or any future tail-bump we drop
            // — must NOT throw on deserialize; defaults the field to false (gate
            // off). Matches the VesselProtoMsgData.Reason / HandshakeRequest /
            // WarpNewSubspace tail-bit guard pattern.
            //
            // Setup: serialize a full payload with the flag TRUE, then truncate the
            // receiver's LengthBits by 1 to drop the trailing bool bit. The earlier
            // fields stay intact; only the guarded tail read sees "no bits left."
            var msgData = BuildRepresentativeSettingsReply();
            msgData.PerAgencyCareerEnabled = true;

            var msg = Factory.CreateNew<SetingsSrvMsg>(msgData);
            var sendBuf = Client.CreateMessage(msg.GetMessageSize());
            msg.Serialize(sendBuf);
            var sentLengthBits = sendBuf.LengthBits;

            var bytes = sendBuf.ReadBytes(sendBuf.LengthBytes);
            var recvBuf = Client.CreateIncomingMessage(NetIncomingMessageType.Data, bytes);
            recvBuf.LengthBits = sentLengthBits - 1; // drop trailing 1-bit bool

            msg.Recycle();

            var deserialised = (SettingsReplyMsgData)Factory.Deserialize(recvBuf, Environment.TickCount).Data;

            // The flag must default to false — proves the Position<LengthBits guard
            // short-circuits the ReadBoolean. Pre-fix, ReadBoolean was unconditional
            // and would either return a stray bit value or throw past end-of-stream.
            Assert.IsFalse(deserialised.PerAgencyCareerEnabled,
                "PerAgencyCareerEnabled must default to false when the tail bit is absent from the wire.");

            // Earlier fields remain correctly decoded — confirms the truncation only
            // affected the guarded tail read and the rest of the wire layout was
            // honoured.
            Assert.AreEqual(GameMode.Career, deserialised.GameMode);
            Assert.AreEqual("test-server", deserialised.ConsoleIdentifier);
            Assert.AreEqual(true, deserialised.PrintMotdInChat,
                "Previous tail field PrintMotdInChat must still decode correctly.");
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
