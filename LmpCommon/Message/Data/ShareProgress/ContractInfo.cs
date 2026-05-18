using Lidgren.Network;
using LmpCommon.Message.Base;
using System;

namespace LmpCommon.Message.Data.ShareProgress
{
    /// <summary>
    /// Wrapper for transmitting the ksp Contract objects.
    /// </summary>
    public class ContractInfo
    {
        public Guid ContractGuid;
        public int NumBytes;
        public byte[] Data = new byte[0];

        public ContractInfo() { }

        /// <summary>
        /// Copy constructor.
        /// </summary>
        public ContractInfo(ContractInfo copyFrom)
        {
            ContractGuid = copyFrom.ContractGuid;
            NumBytes = copyFrom.NumBytes;
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            Array.Copy(copyFrom.Data, Data, NumBytes);
        }

        public void Serialize(NetOutgoingMessage lidgrenMsg)
        {
            GuidUtil.Serialize(ContractGuid, lidgrenMsg);

            Common.ThreadSafeCompress(this, ref Data, ref NumBytes);

            lidgrenMsg.Write(NumBytes);
            lidgrenMsg.Write(Data, 0, NumBytes);
        }

        public void Deserialize(NetIncomingMessage lidgrenMsg)
        {
            ContractGuid = GuidUtil.Deserialize(lidgrenMsg);

            NumBytes = lidgrenMsg.ReadInt32();
            if (Data.Length < NumBytes)
                Data = new byte[NumBytes];

            lidgrenMsg.ReadBytes(Data, 0, NumBytes);

            Common.ThreadSafeDecompress(this, ref Data, NumBytes, out NumBytes);
        }

        public int GetByteCount()
        {
            // Upper bound on the SERIALIZED bytes (not the in-memory bytes). Serialize
            // runs Common.ThreadSafeCompress on Data, which can INFLATE small or
            // incompressible payloads — QuickLZ has a fixed-per-call header (~9 bytes)
            // plus a multiplicative overhead for incompressible data, so a 70-byte
            // decompressed contract often serialises as ~95+ bytes on the wire.
            // The Lidgren NetOutgoingMessage buffer is sized off this value, so an
            // under-estimate forces a reallocation; a SerializationTests round-trip
            // assertion (Stage 5.17d AgencyContractMsgData) caught the under-count.
            // 400 bytes of slack matches the QuickLZ output-buffer overallocation
            // convention used internally and gives generous headroom for any payload
            // size + future-protocol fields appended after the bytes block.
            return GuidUtil.ByteSize + sizeof(int) + NumBytes + 400;
        }
    }
}
