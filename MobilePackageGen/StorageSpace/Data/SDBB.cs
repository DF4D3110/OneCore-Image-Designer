using System.Text;

namespace StorageSpace.Data
{
    public class SDBB
    {
        public string Signature
        {
            get; private set;
        }

        public uint CurrentSDBBBlockPosition
        {
            get; private set;
        }

        public uint ParentSDBBIndex
        {
            get; private set;
        }

        public ushort CurrentSDBBBlockIndex
        {
            get; private set;
        }

        public ushort CurrentSDBBBlockCount
        {
            get; private set;
        }

        public byte[] Data
        {
            get; private set;
        }

        private SDBB()
        {
        }

        public static SDBB Parse(Stream stream)
        {
            using BinaryReader reader = new(stream);

            byte[] SDBBSignature = reader.ReadBytes(4);
            string SDBBSignatureStr = Encoding.ASCII.GetString(SDBBSignature, 0, SDBBSignature.Length);

            if (SDBBSignatureStr != "SDBB")
            {
                throw new Exception("Invalid SDBB!");
            }

            uint CurrentSDBBPosition = BitConverter.ToUInt32(reader.ReadBytes(4).Reverse().ToArray());
            uint SDBBPositionIndex = BitConverter.ToUInt32(reader.ReadBytes(4).Reverse().ToArray());
            ushort CurrentChainPosition = BitConverter.ToUInt16(reader.ReadBytes(2).Reverse().ToArray());
            ushort CurrentChainSize = BitConverter.ToUInt16(reader.ReadBytes(2).Reverse().ToArray());

            byte[] entryDataPart = reader.ReadBytes(0x30);

            SDBB SDBB = new()
            {
                Signature = SDBBSignatureStr,
                CurrentSDBBBlockPosition = CurrentSDBBPosition,
                ParentSDBBIndex = SDBBPositionIndex,
                CurrentSDBBBlockIndex = CurrentChainPosition,
                CurrentSDBBBlockCount = CurrentChainSize,
                Data = entryDataPart
            };

            return SDBB;
        }
    }
}
