using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Threading;

namespace GTANetworkShared.Cef
{
    /// <summary>
    /// The pixels of one off-screen browser, shared between GTANetwork.CefHost.exe (writer) and the game (reader)
    /// through a named memory-mapped file. BGRA, top-down, <see cref="Stride"/> bytes per row, after a 64-byte header.
    /// A sequence counter guards the pixels like a seqlock: the writer makes it odd, copies, makes it even; a reader
    /// that sees a different or odd value after its copy simply copies again. No locks, no waiting on either side.
    /// </summary>
    public sealed unsafe class CefFrameBuffer : IDisposable
    {
        public const int HeaderSize = 64;
        private const int Magic = 0x4E415447; // "GTAN"

        // header offsets
        private const int OffMagic = 0, OffWidth = 4, OffHeight = 8, OffStride = 12, OffSequence = 16,
            OffDirtyX = 24, OffDirtyY = 28, OffDirtyW = 32, OffDirtyH = 36;

        private MemoryMappedFile _file;
        private MemoryMappedViewAccessor _view;
        private byte* _base;

        public string Name { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride { get; private set; }

        private CefFrameBuffer() { }

        /// <summary>Host side: a new buffer for a W x H browser.</summary>
        public static CefFrameBuffer Create(string name, int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            var stride = width * 4;
            var size = HeaderSize + (long)stride * height;

            var buffer = new CefFrameBuffer { Name = name, Width = width, Height = height, Stride = stride };
            buffer._file = MemoryMappedFile.CreateNew(name, size, MemoryMappedFileAccess.ReadWrite);
            buffer.Map(size);
            *(int*)(buffer._base + OffWidth) = width;
            *(int*)(buffer._base + OffHeight) = height;
            *(int*)(buffer._base + OffStride) = stride;
            *(long*)(buffer._base + OffSequence) = 0;
            Thread.MemoryBarrier();
            *(int*)(buffer._base + OffMagic) = Magic;
            return buffer;
        }

        /// <summary>Game side: attach to a buffer the host announced.</summary>
        public static CefFrameBuffer Open(string name)
        {
            var buffer = new CefFrameBuffer { Name = name };
            buffer._file = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite);
            buffer.Map(0);
            if (*(int*)(buffer._base + OffMagic) != Magic)
            {
                buffer.Dispose();
                throw new InvalidOperationException("Frame buffer " + name + " has no valid header");
            }
            buffer.Width = *(int*)(buffer._base + OffWidth);
            buffer.Height = *(int*)(buffer._base + OffHeight);
            buffer.Stride = *(int*)(buffer._base + OffStride);
            if (buffer.Width <= 0 || buffer.Height <= 0 || buffer.Stride < buffer.Width * 4 ||
                buffer._view.Capacity < HeaderSize + (long)buffer.Stride * buffer.Height)
            {
                buffer.Dispose();
                throw new InvalidOperationException("Frame buffer " + name + " has an inconsistent header");
            }
            return buffer;
        }

        private void Map(long size)
        {
            _view = _file.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
        }

        /// <summary>The frame counter: even = a complete frame is in place, odd = the host is writing one.</summary>
        public long Sequence => Interlocked.Read(ref *(long*)(_base + OffSequence));

        /// <summary>
        /// Host side: publish a new frame. The first call copies the whole W x H BGRA image (srcStride bytes per row);
        /// later calls copy only the rectangle that changed, since the buffer already holds the previous complete
        /// frame. The dirty rectangle is published with the frame so a reader that saw the previous frame can update
        /// just that part; a reader that missed frames copies everything.
        /// </summary>
        public void Write(IntPtr source, int srcStride, int width, int height, int dirtyX, int dirtyY, int dirtyW, int dirtyH)
        {
            if (width != Width || height != Height) throw new ArgumentException("frame size " + width + "x" + height + " does not match the buffer " + Width + "x" + Height);

            var seq = *(long*)(_base + OffSequence);
            var first = seq == 0;
            Clamp(ref dirtyX, ref dirtyY, ref dirtyW, ref dirtyH);
            if (first || dirtyW <= 0 || dirtyH <= 0)
            {
                dirtyX = 0; dirtyY = 0; dirtyW = Width; dirtyH = Height;
            }

            Interlocked.Exchange(ref *(long*)(_base + OffSequence), seq + 1); // odd: writing

            var src = (byte*)source;
            var dst = _base + HeaderSize;
            var rowBytes = (long)dirtyW * 4;
            var xOffset = (long)dirtyX * 4;
            for (var y = dirtyY; y < dirtyY + dirtyH; y++)
                Buffer.MemoryCopy(src + (long)y * srcStride + xOffset, dst + (long)y * Stride + xOffset, rowBytes, rowBytes);

            *(int*)(_base + OffDirtyX) = dirtyX;
            *(int*)(_base + OffDirtyY) = dirtyY;
            *(int*)(_base + OffDirtyW) = dirtyW;
            *(int*)(_base + OffDirtyH) = dirtyH;

            Interlocked.Exchange(ref *(long*)(_base + OffSequence), seq + 2); // even: complete
        }

        private void Clamp(ref int x, ref int y, ref int w, ref int h)
        {
            if (x < 0) { w += x; x = 0; }
            if (y < 0) { h += y; y = 0; }
            if (x + w > Width) w = Width - x;
            if (y + h > Height) h = Height - y;
        }

        /// <summary>
        /// Game side: copy the newest frame into <paramref name="destination"/> (BGRA, destStride bytes per row, at
        /// least Width x Height) if there is one newer than <paramref name="lastSequence"/>. Returns false when there
        /// is nothing new or the host was writing all the time. The whole frame is copied.
        /// </summary>
        public bool TryCopyTo(IntPtr destination, int destStride, ref long lastSequence, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH)
        {
            return TryCopyTo(destination, destStride, ref lastSequence, false, out dirtyX, out dirtyY, out dirtyW, out dirtyH);
        }

        /// <summary>
        /// As above; with <paramref name="allowPartial"/> only the published dirty rectangle is copied when the
        /// destination already holds the frame just before this one (sequence + 2), so it stays complete. The
        /// rectangle actually copied comes back in the dirty values.
        /// </summary>
        public bool TryCopyTo(IntPtr destination, int destStride, ref long lastSequence, bool allowPartial, out int dirtyX, out int dirtyY, out int dirtyW, out int dirtyH)
        {
            dirtyX = dirtyY = dirtyW = dirtyH = 0;
            var dst = (byte*)destination;
            var src = _base + HeaderSize;

            for (var attempt = 0; attempt < 4; attempt++)
            {
                var before = Sequence;
                if (before == lastSequence) return false;      // nothing new
                if ((before & 1) != 0) { Thread.SpinWait(200); continue; } // being written right now

                int x = 0, y = 0, w = Width, h = Height;
                if (allowPartial && before == lastSequence + 2)
                {
                    x = *(int*)(_base + OffDirtyX);
                    y = *(int*)(_base + OffDirtyY);
                    w = *(int*)(_base + OffDirtyW);
                    h = *(int*)(_base + OffDirtyH);
                    Clamp(ref x, ref y, ref w, ref h);
                    if (w <= 0 || h <= 0) { x = 0; y = 0; w = Width; h = Height; }
                }

                var rowBytes = (long)w * 4;
                var xOffset = (long)x * 4;
                for (var row = y; row < y + h; row++)
                    Buffer.MemoryCopy(src + (long)row * Stride + xOffset, dst + (long)row * destStride + xOffset, rowBytes, rowBytes);
                Thread.MemoryBarrier();

                if (Sequence != before)                        // torn: the host wrote meanwhile
                {
                    if (allowPartial && x == 0 && y == 0 && w == Width && h == Height) continue;
                    // A partial copy may now be inconsistent with the header: copy everything next time.
                    lastSequence = -1;
                    continue;
                }
                lastSequence = before;
                dirtyX = x; dirtyY = y; dirtyW = w; dirtyH = h;
                return true;
            }
            return false;
        }

        public void Dispose()
        {
            if (_view != null)
            {
                if (_base != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
                _base = null;
                _view.Dispose();
                _view = null;
            }
            _file?.Dispose();
            _file = null;
        }
    }
}
