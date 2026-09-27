
namespace Img2Ffu.Writer.Streams
{
    internal class PartialStream : Stream
    {
        private Stream? innerStream;

        private bool disposed;
        private readonly long startOffset;
        private readonly long endOffset;

        public PartialStream(Stream stream, long StartOffset, long EndOffset)
        {
            _ = stream.Seek(StartOffset, SeekOrigin.Begin);
            startOffset = StartOffset;
            endOffset = EndOffset;
            innerStream = stream;
        }

        public override bool CanRead => innerStream.CanRead;
        public override bool CanSeek => innerStream.CanSeek;
        public override bool CanWrite => innerStream.CanWrite;
        public override long Length => endOffset - startOffset;
        public override long Position
        {
            get => innerStream.Position - startOffset; set => innerStream.Position = value + startOffset;
        }

        public override void Flush()
        {
            innerStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return innerStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return origin == SeekOrigin.Begin
                ? innerStream.Seek(offset + startOffset, origin)
                : origin == SeekOrigin.End ? innerStream.Seek(endOffset + offset, origin) : innerStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            innerStream.SetLength(value);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            innerStream.Write(buffer, offset, count);
        }

        public override void Close()
        {
            innerStream.Dispose();
            innerStream = null;
            base.Close();
        }

        private new void Dispose()
        {
            Dispose(true);
            base.Dispose();
            GC.SuppressFinalize(this);
        }

        private new void Dispose(bool disposing)
        {
            if (!disposed)
            {
                if (disposing)
                {
                    if (innerStream != null)
                    {
                        innerStream.Dispose();
                        innerStream = null;
                    }
                }
                disposed = true;
            }
        }
    }
}