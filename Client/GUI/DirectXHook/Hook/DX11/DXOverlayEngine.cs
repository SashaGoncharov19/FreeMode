using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GTANetwork.GUI.DirectXHook.Hook.Common;
using GTANetworkShared;
using GTANetwork.Util;
using System.Runtime.InteropServices;
using SharpDX;
using SharpDX.Direct3D11;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;

namespace GTANetwork.GUI.DirectXHook.Hook.DX11
{
    internal class DXOverlayEngine: DisposeCollector
    {
        public List<IOverlay> Overlays { get; set; }
        public bool DeferredContext
        {
            get
            {
                return _deviceContext.TypeInfo == DeviceContextType.Deferred;
            }
        }

        bool _initialised = false;
        bool _initialising = false;

        Device _device;
        DeviceContext _deviceContext;
        SharpDX.DXGI.SwapChain _swapChain;
        // Per frame: the current back buffer and a view on it, released again before Present returns. Holding them
        // across frames kept a reference on the back buffer (ResizeBuffers of the game fails then) and went stale
        // after a resize; creating a view per frame is cheap.
        Texture2D _renderTarget;
        RenderTargetView _renderTargetView;
        DXSprite _spriteEngine;
        Dictionary<string, DXFont> _fontCache = new Dictionary<string, DXFont>();
        Dictionary<Element, DXImage> _imageCache = new Dictionary<Element, DXImage>();
        DXHookD3D11 _hook;

        // Shared textures of the browser host, opened on the game's device: one entry per handle, released when the
        // host's Chromium stops using them (RetireSharedTextures, or unused for a few seconds).
        Device1 _device1;
        readonly Dictionary<IntPtr, SharedTextureEntry> _sharedTextures = new Dictionary<IntPtr, SharedTextureEntry>();
        readonly List<IntPtr> _retire = new List<IntPtr>();
        long _frame;
        int _sharedErrorsLogged;

        sealed class SharedTextureEntry
        {
            public Texture2D Texture;
            public long LastUsed;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        /// <summary>Release the textures behind these handles (and the handles) on the next frame; any thread.</summary>
        public void RetireSharedTextures(IEnumerable<IntPtr> handles)
        {
            lock (_retire) _retire.AddRange(handles);
        }

        private void ReleaseRetired()
        {
            IntPtr[] handles;
            lock (_retire)
            {
                if (_retire.Count == 0) return;
                handles = _retire.ToArray();
                _retire.Clear();
            }
            foreach (var handle in handles) ReleaseSharedTexture(handle);
        }

        private void ReleaseSharedTexture(IntPtr handle)
        {
            SharedTextureEntry entry;
            if (_sharedTextures.TryGetValue(handle, out entry))
            {
                _sharedTextures.Remove(handle);
                try { entry.Texture?.Dispose(); } catch { }
            }
            CloseHandle(handle);
        }

        public DXOverlayEngine(DXHookD3D11 hook)
        {
            _hook = hook;
            Overlays = new List<IOverlay>();
        }

        private void EnsureInitiliased()
        {
            Debug.Assert(_initialised);
        }

        public bool Initialise(SharpDX.DXGI.SwapChain swapChain)
        {
            if (_initialised || _initialising) return false;

            _initialising = true;

            try
            {
                _swapChain = swapChain;
                _device = Collect(swapChain.GetDevice<Device>());

                try
                {
                    // A deferred context records our draw calls; executing the command list with state restore
                    // leaves the game's pipeline state untouched.
                    _deviceContext = Collect(new DeviceContext(_device));
                }
                catch (SharpDXException)
                {
                    _deviceContext = Collect(_device.ImmediateContext);
                }

                _spriteEngine = Collect(new DXSprite(_device, _deviceContext));
                if (!_spriteEngine.Initialize())
                    return false;

                // Initialise any resources required for overlay elements
                IntialiseElementResources();

                _initialised = true;
                return true;
            }
            finally
            {
                _initialising = false;
            }
        }

        /// <summary>One line for the log: device feature level, back buffer size and format.</summary>
        public string Describe()
        {
            try
            {
                using (var backBuffer = _swapChain.GetBackBuffer<Texture2D>(0))
                {
                    var desc = backBuffer.Description;
                    return "feature level " + _device.FeatureLevel + ", back buffer " + desc.Width + "x" + desc.Height + " " + desc.Format +
                           ", " + (DeferredContext ? "deferred" : "immediate") + " context";
                }
            }
            catch (Exception ex)
            {
                return "describe failed: " + ex.Message;
            }
        }

        private void IntialiseElementResources()
        {
            lock (_hook._overlayLock)
            foreach (var overlay in Overlays)
            {
                foreach (var element in overlay.Elements)
                {
                    var textElement = element as TextElement;
                    var imageElement = element as ImageElement;

                    if (textElement != null)
                    {
                        GetFontForTextElement(textElement);
                    }
                    else if (imageElement != null)
                    {
                        GetImageForImageElement(imageElement);
                    }
                }
            }
        }

        private bool Begin()
        {
            _renderTarget = _swapChain.GetBackBuffer<Texture2D>(0);
            if (_renderTarget == null) return false;

            _renderTargetView = new RenderTargetView(_device, _renderTarget);

            SharpDX.Mathematics.Interop.RawViewportF[] viewportf = { new ViewportF(0, 0, _renderTarget.Description.Width, _renderTarget.Description.Height, 0, 1) };
            _deviceContext.Rasterizer.SetViewports(viewportf);
            _deviceContext.OutputMerger.SetTargets(_renderTargetView);
            return true;
        }

        /// <summary>
        /// Draw the overlay(s)
        /// </summary>
        private int _framesDrawn;

        public void Draw()
        {
            if (!_initialised) return;

            _frame++;
            ReleaseRetired();

            if (!Begin()) return;

            try
            {
                if (_framesDrawn < 3 && LogManager.Verbose) LogFrame();

                DrawElements();
                End();
                _framesDrawn++;
            }
            finally
            {
                _renderTargetView?.Dispose();
                _renderTargetView = null;
                _renderTarget?.Dispose();
                _renderTarget = null;
            }
        }

        /// <summary>The geometry of the first frames, for the log: viewport, back buffer and every element.</summary>
        private void LogFrame()
        {
            try
            {
                var desc = _renderTarget.Description;
                var text = "CEF overlay frame " + (_framesDrawn + 1) + ": back buffer " + desc.Width + "x" + desc.Height + " " + desc.Format + ", elements:";
                lock (_hook._overlayLock)
                {
                    for (var o = 0; o < Overlays.Count; o++)
                    {
                        foreach (var element in Overlays[o].Elements)
                        {
                            var image = element as ImageElement;
                            var textElement = element as TextElement;
                            if (image != null)
                                text += " [overlay " + o + " image at " + image.Location.X + "," + image.Location.Y + " " +
                                        (image.Bitmap != null ? image.Bitmap.Width + "x" + image.Bitmap.Height : (image.NextBitmap != null ? "pending " + image.NextBitmap.Width + "x" + image.NextBitmap.Height : "no bitmap")) +
                                        (image.Hidden ? " hidden" : "") + "]";
                            else if (textElement != null)
                                text += " [overlay " + o + " text \"" + textElement.Text + "\"" + (textElement.Hidden ? " hidden" : "") + "]";
                        }
                    }
                }
                LogManager.RuntimeLog(text);
            }
            catch (Exception ex)
            {
                LogManager.RuntimeLog("CEF overlay frame log failed: " + ex.Message);
            }
        }

        private void DrawElements()
        {

            lock (_hook._overlayLock)
            foreach (var overlay in Overlays)
            {
                foreach (var element in overlay.Elements)
                {
                    if (element.Hidden)
                        continue;

                    var textElement = element as TextElement;
                    var imageElement = element as ImageElement;
                    
                    if (textElement != null)
                    {
                        DXFont font = GetFontForTextElement(textElement);
                        if (font != null && !String.IsNullOrEmpty(textElement.Text))
                            _spriteEngine.DrawString(textElement.Location.X, textElement.Location.Y, textElement.Text, textElement.Color, font);
                    }
                    else if (imageElement != null)
                    {
                        lock (_imageCache)
                        {
                            DXImage image = GetImageForImageElement(imageElement);
                            if (image != null)
                                _spriteEngine.DrawImage(imageElement.Location.X, imageElement.Location.Y,
                                    imageElement.Scale, imageElement.Angle, imageElement.Tint, image);
                        }
                    }
                }
            }
        }

        private void End()
        {
            if (DeferredContext)
            {
                // Device.ImmediateContext is a wrapper cached inside the SharpDX device: it must not be disposed
                // (alpha.2 did, and every later frame worked on a released wrapper).
                using (var commandList = _deviceContext.FinishCommandList(true))
                {
                    _device.ImmediateContext.ExecuteCommandList(commandList, true);
                }
            }
        }

        DXFont GetFontForTextElement(TextElement element)
        {
            DXFont result = null;

            string fontKey = String.Format("{0}{1}{2}", element.Font.Name, element.Font.Size, element.Font.Style, element.AntiAliased);

            if (!_fontCache.TryGetValue(fontKey, out result))
            {
                result = Collect(new DXFont(_device, _deviceContext));
                result.Initialize(element.Font.Name, element.Font.Size, element.Font.Style, element.AntiAliased);
                _fontCache[fontKey] = result;
            }
            return result;
        }

        public bool Disposable;

        public void FlushCache()
        {
            
        }

        DXImage GetImageForImageElement(ImageElement element)
        {
            var shared = element.SharedTexture;
            if (shared != null) return GetImageForSharedTexture(element, shared);

            var surface = element.Surface;
            if (surface != null)
            {
                lock (surface.SyncRoot)
                {
                    if (surface.Width <= 0 || surface.Height <= 0 || surface.Data == System.IntPtr.Zero) return element.Image;

                    if (element.Image == null || element.Image.Width != surface.Width || element.Image.Height != surface.Height)
                    {
                        element.Image?.Dispose();
                        element.Image = new DXImage(_device, _deviceContext);
                        if (!element.Image.InitialiseDynamic(surface.Width, surface.Height))
                        {
                            element.Image.Dispose();
                            element.Image = null;
                            return null;
                        }
                        surface.InvalidateAll();
                    }

                    if (surface.HasUpdate)
                    {
                        int x, y, w, h;
                        surface.GetDirty(out x, out y, out w, out h);
                        // The immediate context: the update must land before the deferred command list that draws the
                        // texture executes (End), and we are on the game's render thread inside Present anyway.
                        element.Image.UpdateRegion(_device.ImmediateContext, surface.Data, surface.Stride, x, y, w, h);
                        surface.MarkUploaded();
                    }
                }
                return element.Image;
            }

            if (element.Dirty)
            {
                lock (element.SwitchLock)
                {
                    element.Image?.Dispose();
                    element.Image = null;

                    element.Bitmap?.Dispose();
                    element.Bitmap = element.NextBitmap;
                    element.NextBitmap = null;

                    element.Dirty = false;
                }
            }

            if (element.Image == null && element.Bitmap != null)
            {
                // Owned by the element (disposed when the next bitmap arrives or the element goes away).
                element.Image = new DXImage(_device, _deviceContext);
                element.Image.Initialise(element.Bitmap);
            }


            return element.Image;
        }

        /// <summary>
        /// The newest frame of a browser is in a D3D11 texture of the browser host: open it on the game's device (once per
        /// handle), copy it GPU-side into the element's own texture and draw that. Zero CPU work per frame.
        /// </summary>
        DXImage GetImageForSharedTexture(ImageElement element, SharedTextureSurface shared)
        {
            lock (shared.SyncRoot)
            {
                var handle = shared.Pending;
                if (handle == IntPtr.Zero) return element.Image;
                shared.Pending = IntPtr.Zero;

                SharedTextureEntry entry;
                if (!_sharedTextures.TryGetValue(handle, out entry))
                {
                    try
                    {
                        if (_device1 == null) _device1 = Collect(_device.QueryInterface<Device1>());
                        entry = new SharedTextureEntry { Texture = _device1.OpenSharedResource1<Texture2D>(handle) };
                        _sharedTextures[handle] = entry;
                        if (LogManager.Verbose)
                        {
                            var d = entry.Texture.Description;
                            LogManager.CefLog("-> Shared texture 0x" + handle.ToInt64().ToString("X") + " opened: " + d.Width + "x" + d.Height + " " + d.Format + " " + d.OptionFlags);
                        }
                    }
                    catch (Exception ex)
                    {
                        shared.Failed = true;
                        shared.LastError = ex.Message.Trim();
                        if (_sharedErrorsLogged++ < 3) LogManager.CefLog("-> Shared texture 0x" + handle.ToInt64().ToString("X") + " could not be opened on the game's device: " + shared.LastError);
                        return element.Image;
                    }
                }
                entry.LastUsed = _frame;

                var desc = entry.Texture.Description;
                if (element.Image == null || element.Image.Width != desc.Width || element.Image.Height != desc.Height)
                {
                    element.Image?.Dispose();
                    element.Image = new DXImage(_device, _deviceContext);
                    if (!element.Image.InitialiseDynamic(desc.Width, desc.Height))
                    {
                        element.Image.Dispose();
                        element.Image = null;
                        return null;
                    }
                }

                // A full GPU copy (a few microseconds): the host's texture may be rendered into again at any moment,
                // our copy is stable for the draw and for the frames until the next one.
                _device.ImmediateContext.CopyResource(entry.Texture, element.Image.Texture);

                // Chromium cycles through a small pool and re-creates it now and then; a texture it has not handed us
                // for a few seconds is gone on its side, and our duplicated handle would pin its memory.
                if (shared.Handles.Count > 4)
                {
                    for (var i = shared.Handles.Count - 1; i >= 0; i--)
                    {
                        var h = shared.Handles[i];
                        SharedTextureEntry e;
                        var stale = _sharedTextures.TryGetValue(h, out e) ? _frame - e.LastUsed > 240 : h != handle;
                        if (!stale) continue;
                        shared.Handles.RemoveAt(i);
                        ReleaseSharedTexture(h);
                    }
                }
            }
            return element.Image;
        }

        /// <summary>
        /// Releases unmanaged and optionally managed resources
        /// </summary>
        /// <param name="disposing">true if disposing both unmanaged and managed</param>
        protected override void Dispose(bool disposing)
        {
            foreach (var entry in _sharedTextures.Values)
            {
                try { entry.Texture?.Dispose(); } catch { }
            }
            _sharedTextures.Clear();
            _device1 = null;

            // Releases everything that was Collect()ed: the sprite engine, the deferred context, the device
            // reference and the fonts. Element images belong to the elements.
            base.Dispose(disposing);

            _renderTargetView = null;
            _renderTarget = null;
            _spriteEngine = null;
            _deviceContext = null;
            _device = null;
            _swapChain = null;
            _initialised = false;
        }

        void SafeDispose(DisposeBase disposableObj)
        {
            if (disposableObj != null)
                disposableObj.Dispose();
        }
    }
}
