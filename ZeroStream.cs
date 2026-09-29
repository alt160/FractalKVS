using System;
using System.IO;

namespace FractalKVS
{
    /// <summary>
    /// Read-only stream of zeros of a given length. Uses a shared static zero buffer to avoid allocations.
    /// </summary>
    internal sealed class ZeroStream : Stream
    {
        static readonly byte[] s_zeroBuffer = new byte[64 * 1024]; // 64KB shared zero buffer

        readonly long _length;
        long _position;

        public ZeroStream(long length)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            _length = length;
            _position = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(nameof(value));
                _position = value;
            }
        }

        public override void Flush() { /* no-op */ }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer is null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length) throw new ArgumentOutOfRangeException();
            if (_position >= _length) return 0;

            int remaining = (int)Math.Min(count, _length - _position);
            int written = 0;
            while (written < remaining)
            {
                int toCopy = Math.Min(remaining - written, s_zeroBuffer.Length);
                Array.Copy(s_zeroBuffer, 0, buffer, offset + written, toCopy);
                written += toCopy;
            }

            _position += written;
            return written;
        }

#if NETCOREAPP || NETSTANDARD2_1_OR_GREATER
        public override int Read(Span<byte> buffer)
        {
            if (_position >= _length) return 0;
            int remaining = (int)Math.Min(buffer.Length, _length - _position);
            int written = 0;
            while (written < remaining)
            {
                int toCopy = Math.Min(remaining - written, s_zeroBuffer.Length);
                s_zeroBuffer.AsSpan(0, toCopy).CopyTo(buffer.Slice(written, toCopy));
                written += toCopy;
            }

            _position += written;
            return written;
        }
#endif

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (newPos < 0 || newPos > _length) throw new IOException("Attempted to seek outside the stream bounds.");
            _position = newPos;
            return _position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
