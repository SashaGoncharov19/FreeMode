using System;
using System.Collections.Generic;
using System.Linq;
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
    /// The picture of one browser in the DirectX overlay. Two ways in: the browser host paints into a shared-memory
    /// <see cref="CefFrameBuffer"/>, the CEF frame pump thread copies each new frame's changed rectangle into a
    /// <see cref="CefFrameStager"/>, and the overlay uploads that rectangle into a persistent texture on the render
    /// thread (one copy on the pump thread, one upload in Present); or, with the GPU on, the host copies each frame
    /// into a ring of D3D11 shared textures (<see cref="SharedTextureSurface"/>) and the overlay copies the announced
    /// slot GPU-side, no CPU work per frame.
    /// </summary>
    internal class OverlayRenderHandler : IDisposable
    {
        private readonly object _lock = new object();
        private int _width;
        private int _height;
        private ImageElement _imageElement;
        private CefFrameStager _stager;
        private SharedTextureSurface _shared;
        private int _framesLogged;
        private int _texturesLogged;
        private int _staleLogged;

        /// <summary>Called (once) when the overlay could not open a shared texture: the browser falls back to CPU frames.</summary>
        internal Action<string> SharedTextureFailed;

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

        /// <summary>
        /// The host (re)created its ring of shared textures for this browser: these handles are ours to open, the
        /// previous ring's are closed. Without handles the host cannot relay textures at all: CPU frames instead.
        /// </summary>
        internal void AttachTextures(long[] handles, int width, int height, string error)
        {
            if (handles == null || handles.Length == 0)
            {
                SharedTextureFailed?.Invoke(error ?? "the host announced no textures");
                return;
            }
            SharedTextureSurface shared;
            lock (_lock)
            {
                var element = _imageElement;
                if (element == null) return;
                if (_shared == null)
                {
                    _shared = new SharedTextureSurface();
                    element.SharedTexture = _shared;
                    // shared textures replace the shared-memory path for this browser
                    var stager = _stager;
                    _stager = null;
                    element.Surface = null;
                    stager?.Dispose();
                }
                shared = _shared;
            }
            var fresh = new List<IntPtr>(handles.Length);
            foreach (var h in handles) if (h != 0) fresh.Add(new IntPtr(h));
            IntPtr[] retired;
            lock (shared.SyncRoot)
            {
                retired = shared.Handles.Where(h => !fresh.Contains(h)).ToArray();
                shared.Handles.Clear();
                shared.Handles.AddRange(fresh);
                if (!fresh.Contains(shared.Pending)) shared.Pending = IntPtr.Zero;
            }
            if (retired.Length > 0) Retire(retired);
            if (LogManager.Verbose)
                LogManager.CefLog("-> Texture ring " + width + "x" + height + ": " + string.Join(", ", fresh.Select(h => "0x" + h.ToInt64().ToString("X")).ToArray()) + (retired.Length > 0 ? "; " + retired.Length + " previous texture(s) retired" : ""));
        }

        /// <summary>The host copied a frame into one of the ring's textures: the overlay copies it on the render thread.</summary>
        internal void AttachTexture(long handle, int width, int height)
        {
            if (handle == 0) return;
            SharedTextureSurface shared;
            lock (_lock) shared = _shared;
            if (shared == null) return; // no ring announced: a late event of a browser already switched or closed
            bool failed, known;
            string error;
            lock (shared.SyncRoot)
            {
                var h = new IntPtr(handle);
                known = shared.Handles.Contains(h);
                if (known) shared.Pending = h;
                failed = shared.Failed;
                error = shared.LastError;
            }
            if (!known)
            {
                if (_staleLogged++ < 3) LogManager.CefLog("-> Texture frame in 0x" + handle.ToString("X") + ", which is not in the current ring; ignored");
                return;
            }
            if (_texturesLogged < 3 && LogManager.Verbose)
            {
                _texturesLogged++;
                LogManager.CefLog("-> Texture frame " + width + "x" + height + " in shared texture 0x" + handle.ToString("X"));
            }
            if (failed) SharedTextureFailed?.Invoke(error);
        }

        /// <summary>Back to CPU frames: releases the shared textures; the next "frame" event attaches a frame buffer.</summary>
        internal void DropSharedTexture()
        {
            SharedTextureSurface shared;
            lock (_lock)
            {
                shared = _shared;
                _shared = null;
                var element = _imageElement;
                if (element != null) element.SharedTexture = null;
            }
            ReleaseShared(shared);
        }

        private static void ReleaseShared(SharedTextureSurface shared)
        {
            if (shared == null) return;
            IntPtr[] handles;
            lock (shared.SyncRoot)
            {
                handles = shared.Handles.ToArray();
                shared.Handles.Clear();
                shared.Pending = IntPtr.Zero;
            }
            Retire(handles);
        }

        /// <summary>Close these handles (and the textures opened from them) on the render thread; any thread.</summary>
        private static void Retire(IntPtr[] handles)
        {
            if (handles.Length == 0) return;
            var engine = CEFManager.DirectXHook?.OverlayEngine;
            if (engine != null) engine.RetireSharedTextures(handles);
            else foreach (var h in handles) NativeMethods.CloseHandle(h);
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
            SharedTextureSurface shared;
            lock (_lock)
            {
                element = _imageElement;
                _imageElement = null;
                stager = _stager;
                _stager = null;
                shared = _shared;
                _shared = null;
            }
            if (element != null)
            {
                element.Surface = null;
                element.SharedTexture = null;
                CEFManager.DirectXHook?.RemoveImage(element);
                element.Dispose();
            }
            stager?.Dispose();
            ReleaseShared(shared);
        }
    }

    internal static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);
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
