using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Rupture.Clothing
{
    internal static class ClothingBundleDecompression
    {
        private static readonly object Initialization = new object();
        private static bool _initialized;
        private static IntPtr _module;
        private static DecodeDelegate _decode;

        internal static bool Initialize(string pluginRoot, Action<string> report)
        {
            lock (Initialization)
            {
                if (_initialized) return _decode != null;
                _initialized = true;
                try
                {
                    string path = Path.GetFullPath(Path.Combine(pluginRoot, "rpt_physx_bridge.dll"));
                    if (!File.Exists(path)) throw new FileNotFoundException("native library is missing", path);
                    _module = LoadLibraryExW(path, IntPtr.Zero, 0x00000100 | 0x00000800);
                    if (_module == IntPtr.Zero) throw new InvalidOperationException("native library load failed: " + Marshal.GetLastWin32Error());
                    IntPtr export = GetProcAddress(_module, "rptpx_lzma_decode_v1");
                    if (export == IntPtr.Zero) throw new EntryPointNotFoundException("native LZMA decoder is missing");
                    _decode = Marshal.GetDelegateForFunctionPointer<DecodeDelegate>(export);
                    // Keep our library reference for the process-lifetime loading workers,
                    // independently of the optional physics backend's library reference.
                    return true;
                }
                catch (Exception e)
                {
                    if (_module != IntPtr.Zero) { FreeLibrary(_module); _module = IntPtr.Zero; }
                    report?.Invoke("Native clothing decompression unavailable; using buffered managed LZMA. " + e.Message);
                    return false;
                }
            }
        }

        internal static void Unpack(BundleFileInstance bundle, CancellationToken cancellation = default, bool allowNative = true)
        {
            var file = bundle.file;
            if (!file.DataIsCompressed) return;
            cancellation.ThrowIfCancellationRequested();
            var blocks = file.BlockAndDirInfo.BlockInfos;
            var decode = _decode;
            // Leave mixed/other codecs to AssetsTools. Only replace its LZMA data path.
            bool native = allowNative && decode != null && blocks.All(b =>
                (b.GetCompressionType() == 0 || b.GetCompressionType() == 1) && b.DecompressedSize > 0);
            if (native)
            {
                long total = blocks.Sum(b => (long)b.DecompressedSize);
                if (total > int.MaxValue) throw new InvalidDataException("clothing bundle exceeds the in-memory decoder limit");
                var output = new MemoryStream((int)total);
                try
                {
                    file.DataReader.Position = 0;
                    foreach (var block in blocks)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        byte[] input = file.DataReader.ReadBytes(checked((int)block.CompressedSize));
                        if (input.Length != block.CompressedSize) throw new InvalidDataException("truncated clothing compression block");
                        byte[] data = block.GetCompressionType() == 1
                            ? Decode(decode, input, checked((int)block.DecompressedSize)) : input;
                        if (data.Length != block.DecompressedSize) throw new InvalidDataException("clothing block size mismatch");
                        output.Write(data, 0, data.Length);
                    }
                    output.Position = 0;
                    file.DataReader.Close();
                    file.DataReader = new AssetsFileReader(output);
                    file.DataIsCompressed = false;
                }
                catch { output.Dispose(); throw; }
            }
            else
            {
                // Buffer above SegmentStream: its inherited ReadByte allocates and
                // routes every compressed byte through a separate segmented Read.
                file.DataReader = new AssetsFileReader(new BufferedStream(file.DataReader.BaseStream, 64 * 1024));
            }
            cancellation.ThrowIfCancellationRequested();
            bundle.file = BundleHelper.UnpackBundle(file);
        }

        private static byte[] Decode(DecodeDelegate decode, byte[] input, int size)
        {
            var output = new byte[size];
            var source = GCHandle.Alloc(input, GCHandleType.Pinned);
            try
            {
                var destination = GCHandle.Alloc(output, GCHandleType.Pinned);
                try
                {
                    int result = decode(source.AddrOfPinnedObject(), (uint)input.Length, destination.AddrOfPinnedObject(), (uint)size);
                    if (result != 0) throw new InvalidDataException("native clothing LZMA decode failed: " + result);
                }
                finally { destination.Free(); }
            }
            finally { source.Free(); }
            return output;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DecodeDelegate(IntPtr source, uint sourceSize, IntPtr destination, uint destinationSize);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr module);
    }
}
