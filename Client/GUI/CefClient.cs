using System;
using System.Runtime.InteropServices;
using GTANetwork.GUI.DirectXHook.Hook.Common;
using GTANetwork.Util;
using GTANetworkShared.Cef;
using Point = System.Drawing.Point;

namespace GTANetwork.GUI
{
    internal static class CefUtil
    {
        public static bool DISABLE_CEF = true;
    }

    /// <summary>
    /// The picture of one browser in the DirectX overlay. The browser host paints into a shared-memory
    /// <see cref="CefFrameBuffer"/>; the CEF frame pump thread copies each new frame's changed rectangle into a
    /// <see cref="CefFrameStager"/>, and the overlay uploads that rectangle into a persistent texture on the render
    /// thread. No bitmaps, no texture re-creation: one copy on the pump thread, one upload in Present.
    /// </summary>
    internal class OverlayRenderHandler : IDisposable
    {
        private readonly object _lock = new object();
        private int _width;
        private int _height;
        private ImageElement _imageElement;
        private CefFrameStager _stager;
        private int _framesLogged;

        public Point Position { get; private set; }

        public OverlayRenderHandler(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
            _imageElement = new ImageElement(null, true);
            CEFManager.DirectXHook?.AddImage(_imageElement);
            LogManager.CefLog("-> Instantiated Renderer");
        }

        public void SetHidden(bool hidden)
        {
            var element = _imageElement;
            if (element != null) element.Hidden = hidden;
        }

        public void SetSize(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
        }

        public void SetPosition(int x, int y)
        {
            Position = new Point(x, y);
            var element = _imageElement;
            if (element != null) element.Location = Position;
        }

        /// <summary>The host announced a (new) frame buffer for this browser: switch to it.</summary>
        internal void AttachFrame(string name, int width, int height, int stride)
        {
            CefFrameStager fresh;
            try
            {
                fresh = new CefFrameStager(CefFrameBuffer.Open(name));
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF FRAME BUFFER " + name);
                return;
            }

            CefFrameStager old;
            lock (_lock)
            {
                old = _stager;
                _stager = fresh;
                var element = _imageElement;
                if (element != null) element.Surface = fresh;
            }
            old?.Dispose();
            LogManager.VerboseCefLog("-> Frame buffer " + name + " (" + width + "x" + height + ", stride " + stride + ")");
        }

        /// <summary>Called by the frame pump: stages the newest frame (or its changed part) for the overlay.</summary>
        internal void Pump()
        {
            CefFrameStager stager;
            lock (_lock) stager = _stager;
            if (stager == null) return;

            int x, y, w, h;
            if (!stager.Pump(out x, out y, out w, out h)) return;

            if (_framesLogged < 3 && LogManager.Verbose)
            {
                _framesLogged++;
                LogManager.CefLog("-> Frame " + stager.Width + "x" + stager.Height + " (copied " + w + "x" + h + " at " + x + "," + y + ", sequence " + stager.Sequence + ")");
            }
        }

        public void Dispose()
        {
            ImageElement element;
            CefFrameStager stager;
            lock (_lock)
            {
                element = _imageElement;
                _imageElement = null;
                stager = _stager;
                _stager = null;
            }
            if (element != null)
            {
                element.Surface = null;
                CEFManager.DirectXHook?.RemoveImage(element);
                element.Dispose();
            }
            stager?.Dispose();
        }
    }

    /// <summary>
    /// The game-side copy of one browser's frame: a staging image filled from the shared frame buffer, changed
    /// rectangle by changed rectangle, and handed to the overlay as an <see cref="IDynamicSurface"/>. Consecutive
    /// frames only move their dirty rectangle; after a missed frame (or a torn read) the whole frame is copied.
    /// </summary>
    internal sealed class CefFrameStager : IDynamicSurface, IDisposable
    {
        private readonly object _sync = new object();
        private CefFrameBuffer _frame;
        private IntPtr _data;
        private long _lastSequence = -1;
        private bool _hasUpdate;
        private int _dirtyX, _dirtyY, _dirtyW, _dirtyH;
        private bool _hadFrame;

        public CefFrameStager(CefFrameBuffer frame)
        {
            _frame = frame;
            Width = frame.Width;
            Height = frame.Height;
            Stride = frame.Stride;
            _data = Marshal.AllocHGlobal((IntPtr)((long)Stride * Height));
        }

        public object SyncRoot => _sync;
        public int Width { get; }
        public int Height { get; }
        public IntPtr Data => _data;
        public int Stride { get; }
        public bool HasUpdate => _hasUpdate;
        public long Sequence => _lastSequence;

        /// <summary>Frame pump thread: copy what changed in the shared buffer since the last copy.</summary>
        public bool Pump(out int x, out int y, out int w, out int h)
        {
            lock (_sync)
            {
                x = y = w = h = 0;
                var frame = _frame;
                if (frame == null || _data == IntPtr.Zero) return false;
                if (frame.Sequence == _lastSequence) return false; // one shared read, nothing to do

                // Partial copies are only safe once the staging image holds a complete frame.
                if (!frame.TryCopyTo(_data, Stride, ref _lastSequence, _hadFrame, out x, out y, out w, out h)) return false;
                _hadFrame = true;

                if (!_hasUpdate)
                {
                    _dirtyX = x; _dirtyY = y; _dirtyW = w; _dirtyH = h;
                }
                else
                {
                    // Not uploaded yet: widen the pending rectangle to cover this frame's changes too.
                    var right = Math.Max(_dirtyX + _dirtyW, x + w);
                    var bottom = Math.Max(_dirtyY + _dirtyH, y + h);
                    _dirtyX = Math.Min(_dirtyX, x);
                    _dirtyY = Math.Min(_dirtyY, y);
                    _dirtyW = right - _dirtyX;
                    _dirtyH = bottom - _dirtyY;
                }
                _hasUpdate = true;
                return true;
            }
        }

        public void GetDirty(out int x, out int y, out int width, out int height)
        {
            x = _dirtyX; y = _dirtyY; width = _dirtyW; height = _dirtyH;
        }

        public void MarkUploaded()
        {
            _hasUpdate = false;
        }

        public void InvalidateAll()
        {
            if (!_hadFrame) return;
            _dirtyX = 0; _dirtyY = 0; _dirtyW = Width; _dirtyH = Height;
            _hasUpdate = true;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _frame?.Dispose();
                _frame = null;
                if (_data != IntPtr.Zero) Marshal.FreeHGlobal(_data);
                _data = IntPtr.Zero;
                _hasUpdate = false;
            }
        }
    }
}
