using System;
using System.Runtime.InteropServices;
using OcidCabinet.Delta;

namespace OcidCabinet
{
    /// <summary>
    /// Applies a PA30 delta to a basis buffer.
    /// </summary>
    /// <remarks>
    /// The project-owned decoder (<see cref="Pa30Decoder"/>) covers the payloads that the CBS/Tools
    /// code paths need: file type RAW, single or multi format, no rift table. Windows Update container
    /// cabinets additionally carry PE payloads (<c>fileType</c> 8 and friends) that need the PE
    /// pre-processor and post-processor, and a handful of multi-format manifests whose format switch
    /// is not reversed yet; those still fall back to <c>msdelta.dll</c> as a transitional step.
    /// </remarks>
    public static class Pa30Delta
    {
        /// <summary>True when the buffer starts with the <c>PA30</c> delta signature.</summary>
        public static bool IsPa30(ReadOnlySpan<byte> data)
        {
            return data.Length >= 4 && data[0] == (byte)'P' && data[1] == (byte)'A' && data[2] == (byte)'3' && data[3] == (byte)'0';
        }

        /// <summary>
        /// Applies <paramref name="delta"/> to <paramref name="basis"/>.
        /// Returns null when the delta does not accept the basis (the usual case while searching
        /// for the right basis in a container cab) or when the payload is malformed.
        /// </summary>
        public static byte[]? TryApply(byte[] basis, byte[] delta)
        {
            if (basis == null)
                throw new ArgumentNullException(nameof(basis));
            if (delta == null)
                throw new ArgumentNullException(nameof(delta));

            if (!IsPa30(delta))
                return null;

            var owned = Pa30Decoder.TryApply(basis, delta);
            if (owned != null)
                return owned;

            return OperatingSystem.IsWindows() ? ApplyWithMsDelta(basis, delta) : null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeltaInput
        {
            public IntPtr Start;
            public UIntPtr Size;
            public uint Editable;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeltaOutput
        {
            public IntPtr Start;
            public UIntPtr Size;
        }

        [DllImport("msdelta.dll", ExactSpelling = true)]
        private static extern int ApplyDeltaB(uint flags, DeltaInput source, DeltaInput delta, out DeltaOutput target);

        [DllImport("msdelta.dll", ExactSpelling = true)]
        private static extern void DeltaFree(IntPtr buffer);

        // Transitional path: PE payloads (fileType != 1) still need msdelta.dll.
        private static byte[]? ApplyWithMsDelta(byte[] basis, byte[] delta)
        {
            var source = new DeltaInput();
            var patch = new DeltaInput();
            var sourceHandle = GCHandle.Alloc(basis, GCHandleType.Pinned);
            var deltaHandle = GCHandle.Alloc(delta, GCHandleType.Pinned);
            try
            {
                source.Start = sourceHandle.AddrOfPinnedObject();
                source.Size = (UIntPtr)(uint)basis.Length;
                patch.Start = deltaHandle.AddrOfPinnedObject();
                patch.Size = (UIntPtr)(uint)delta.Length;

                if (ApplyDeltaB(0, source, patch, out var result) == 0 || result.Start == IntPtr.Zero)
                    return null;

                try
                {
                    var length = checked((int)(long)result.Size);
                    var target = new byte[length];
                    Marshal.Copy(result.Start, target, 0, length);
                    return target;
                }
                finally
                {
                    DeltaFree(result.Start);
                }
            }
            finally
            {
                if (deltaHandle.IsAllocated)
                    deltaHandle.Free();
                if (sourceHandle.IsAllocated)
                    sourceHandle.Free();
            }
        }
    }
}
