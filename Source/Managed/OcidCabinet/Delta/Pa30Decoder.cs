using System;
using System.IO;

namespace OcidCabinet.Delta
{
    /// <summary>
    /// Project-owned decoder for PA30 deltas (the pseudo-LZX patch format used by msdelta).
    /// </summary>
    /// <remarks>
    /// The reference implementation is read-only: the source (basis) and the target live in one
    /// address space, so a match distance is measured from the start of the source. The layout
    /// and bit-level semantics were reversed from msdelta.dll; see
    /// <c>Source/Native/OcidServicing/Delta-Format.md</c>.
    /// </remarks>
    internal static class Pa30Decoder
    {
        private const uint AlignedSourceCopy = 0x54000;
        private const uint SourceDistanceBase = 0x2A000;
        private const uint RunDistanceBase = 0x54000;

        public static byte[]? TryApply(byte[] source, byte[] delta)
        {
            if (delta == null || delta.Length < 12)
                return null;
            if (delta[0] != (byte)'P' || delta[1] != (byte)'A' || delta[2] != (byte)'3' || delta[3] != (byte)'0')
                return null;

            try
            {
                return Apply(source, delta);
            }
            catch (InvalidDataException)
            {
                return null;
            }
        }

        private static byte[] Apply(byte[] source, byte[] delta)
        {
            var payload = new byte[delta.Length - 12];
            Array.Copy(delta, 12, payload, 0, payload.Length);

            var reader = new BitReader(payload, skipBits: 3);
            _ = reader.ReadBit(); // file type set
            ulong fileType = reader.ReadBit();
            _ = reader.ReadBit(); // flags
            ulong targetSizeValue = reader.ReadBit();
            _ = reader.ReadBit(); // hash algorithm id
            if (targetSizeValue > int.MaxValue)
                throw new InvalidDataException("PA30: target size is out of range");
            int targetSize = (int)targetSizeValue;

            _ = reader.ReadBuffer(); // target hash (may be empty)
            byte[] preProcess = reader.ReadBuffer();
            byte[] patch = reader.ReadBuffer();

            // Non-RAW payloads need msdelta's PE pre-/post-processors; leave them to the fallback.
            if (fileType != 1)
                throw new InvalidDataException($"PA30: file type {fileType} needs the PE pre-processor");

            var patchReader = new BitReader(patch, skipBits: 3);
            var rift = new RiftTable(patchReader);
            var format = new CompositeFormat(patchReader);

            // The pre-process buffer is an identity transform for RAW payloads; nothing to run.
            _ = preProcess;

            var buffer = new byte[source.Length + targetSize];
            Array.Copy(source, buffer, source.Length);

            int basePosition = source.Length;
            int position = basePosition;
            int end = basePosition + targetSize;
            uint r0 = 0, r1 = 0, r2 = 0;

            while (position < end)
            {
                CompressionFormat current = format.FormatAt(position - basePosition);
                (uint marker, int length) = current.ReadSymbol(patchReader);

                if (length == 1)
                {
                    buffer[position++] = (byte)marker;
                    continue;
                }

                if (position + length > end)
                    throw new InvalidDataException("PA30: match runs past the end of the target");

                if (marker == AlignedSourceCopy)
                {
                    // The source region is copied onto itself at the same offset; this still
                    // pushes the source size into the R0/R1/R2 history.
                    int sourceIndex = position - basePosition;
                    for (int k = 0; k < length; k++)
                        buffer[position + k] = buffer[sourceIndex + k];

                    uint alignedDistance = (uint)basePosition;
                    if (r0 != alignedDistance)
                    {
                        if (r1 != alignedDistance)
                            r2 = r1;
                        r1 = r0;
                        r0 = alignedDistance;
                    }

                    position += length;
                    continue;
                }

                uint distance;
                if (marker >= RunDistanceBase)
                {
                    uint index = marker - (RunDistanceBase + 1);
                    distance = index <= 2 ? index switch { 0 => r0, 1 => r1, _ => r2 } : marker - (RunDistanceBase + 3);
                }
                else
                {
                    long riffDelta = rift.DeltaAt(position - basePosition);
                    distance = (uint)(marker - SourceDistanceBase - riffDelta);
                }

                if (distance == 0 || distance > (uint)position)
                    throw new InvalidDataException("PA30: match distance is outside the decoded data");

                if (r0 != distance)
                {
                    if (r1 != distance)
                        r2 = r1;
                    r1 = r0;
                    r0 = distance;
                }

                for (int k = 0; k < length; k++)
                    buffer[position + k] = buffer[position - (int)distance + k];
                position += length;
            }

            var target = new byte[targetSize];
            Array.Copy(buffer, basePosition, target, 0, targetSize);
            return target;
        }
    }
}
