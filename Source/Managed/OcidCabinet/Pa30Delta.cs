using System;
using System.Runtime.InteropServices;

namespace OcidCabinet
{
    /// <summary>
    /// Applies a PA30 delta to a basis buffer.
    /// </summary>
    /// <remarks>
    /// This is a transitional shim: it forwards to the OS <c>msdelta.dll</c> so the container-cab
    /// reader can be finished and verified against <c>expand.exe</c> while the project-owned PA30
    /// decoder is still being written. Once <c>OcidDelta</c> lands this class must not touch
    /// msdelta.dll any more; the API surface is intentionally tiny so the switch is a one-file change.
    /// </remarks>
    public static class Pa30Delta
    {
        private const uint DeltaFlagNone = 0;

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

        [DllImport("msdelta.dll", SetLastError = true)]
        private static extern int ApplyDeltaB(uint flags, DeltaInput source, DeltaInput delta, out DeltaOutput target);

        [DllImport("msdelta.dll")]
        private static extern void DeltaFree(IntPtr memory);

        /// <summary>True when the buffer starts with the <c>PA30</c> delta signature.</summary>
        public static bool IsPa30(ReadOnlySpan<byte> data)
        {
            return data.Length >= 4 && data[0] == (byte)'P' && data[1] == (byte)'A' && data[2] == (byte)'3' && data[3] == (byte)'0';
        }

        /// <summary>
        /// Applies <paramref name="delta"/> to <paramref name="basis"/>.
        /// Returns null when the delta rejects the basis (the usual case while searching for the
        /// right basis in a container cab).
        /// </summary>
        public static byte[]? TryApply(byte[] basis, byte[] delta)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("PA30 deltas need msdelta.dll; this build only runs on Windows.");

            byte[] source = basis.Length == 0 ? Array.Empty<byte>() : basis;
            GCHandle sourceHandle = default;
            GCHandle deltaHandle = default;
            try
            {
                sourceHandle = GCHandle.Alloc(source.Length == 0 ? new byte[1] : source, GCHandleType.Pinned);
                deltaHandle = GCHandle.Alloc(delta, GCHandleType.Pinned);

                var sourceInput = new DeltaInput
                {
                    Start = sourceHandle.AddrOfPinnedObject(),
                    Size = (UIntPtr)(uint)source.Length,
                    Editable = 0,
                };
                var deltaInput = new DeltaInput
                {
                    Start = deltaHandle.AddrOfPinnedObject(),
                    Size = (UIntPtr)(uint)delta.Length,
                    Editable = 0,
                };

                if (ApplyDeltaB(DeltaFlagNone, sourceInput, deltaInput, out DeltaOutput output) == 0 || output.Start == IntPtr.Zero)
                    return null;

                try
                {
                    var result = new byte[(int)output.Size];
                    Marshal.Copy(output.Start, result, 0, result.Length);
                    return result;
                }
                finally
                {
                    DeltaFree(output.Start);
                }
            }
            finally
            {
                if (sourceHandle.IsAllocated)
                    sourceHandle.Free();
                if (deltaHandle.IsAllocated)
                    deltaHandle.Free();
            }
        }
    }
}
