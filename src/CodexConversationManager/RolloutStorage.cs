using System;
using System.IO;
using ZstdSharp;

namespace CodexConversationManager;

// All offsets in consumers refer to decoded JSONL, never compressed bytes.
internal static class RolloutStorage
{
    internal const long MaximumDecodedBytes = 8L * 1024 * 1024 * 1024;
    public static bool IsCompressed(string path) => (path ?? string.Empty).EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
    public static string PlainPath(string path) => IsCompressed(path) ? path.Substring(0, path.Length - 4) : path;

    public static Stream OpenRead(string path)
    {
        Stream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);
        return Decode(input, IsCompressed(path));
    }

    public static Stream Decode(Stream input, bool compressed)
    {
        if (!compressed) return input;
        try { return new LimitedStream(new DecompressionStream(input, checkEndOfStream: true, leaveOpen: false)); }
        catch { input.Dispose(); throw; }
    }

    public static byte[] DecodeBytes(byte[] bytes, bool compressed)
    {
        if (!compressed) return bytes;
        using Stream input = Decode(new MemoryStream(bytes, false), true);
        using MemoryStream output = new MemoryStream();
        byte[] buffer = new byte[65536];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (output.Length + read > 512L * 1024 * 1024)
                throw new InvalidDataException("Compressed rollout exceeds the 512 MiB metadata rewrite limit.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public static void DecodeFile(string source, string destination)
    {
        using Stream input = OpenRead(source);
        using FileStream output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    public static void EncodeFile(string source, string destination)
    {
        using Stream input = File.OpenRead(source);
        using Stream output = File.Create(destination);
        using CompressionStream compressor = new CompressionStream(output);
        input.CopyTo(compressor);
    }

    private sealed class LimitedStream : Stream
    {
        private readonly Stream inner;
        private long position;
        public LimitedStream(Stream inner) { this.inner = inner; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int read;
            try { read = inner.Read(buffer, offset, count); }
            catch (Exception ex) when (ex is ZstdException || ex is EndOfStreamException)
            { throw new InvalidDataException("Invalid or truncated Zstandard rollout (.zst).", ex); }
            position += read;
            if (position > MaximumDecodedBytes) throw new InvalidDataException("Zstandard rollout exceeds the 8 GiB decoded size limit.");
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override long Length => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
