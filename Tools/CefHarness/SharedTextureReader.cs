using System;
using System.Collections.Generic;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace GTANetwork.CefHarness
{
    /// <summary>
    /// What the game's overlay does with the browser host's shared textures: its own D3D11 device opens each slot of
    /// the host's ring by the duplicated NT handle (once; the handles are stable until the host announces a new ring),
    /// copies the announced slot GPU-side into a texture of its own, and draws that. Here the copy goes to a staging
    /// texture and is read back to prove the content is there.
    /// </summary>
    internal sealed class SharedTextureReader : IDisposable
    {
        private readonly Device _device;
        private readonly Device1 _device1;
        private readonly Dictionary<IntPtr, Texture2D> _opened = new Dictionary<IntPtr, Texture2D>();
        private Texture2D _copy;
        private Texture2D _staging;

        public SharedTextureReader()
        {
            _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _device1 = _device.QueryInterface<Device1>();
            Program.Log("D3D11 device for shared textures: feature level " + _device.FeatureLevel + ", adapter " + AdapterName());
        }

        private string AdapterName()
        {
            try
            {
                using (var dxgi = _device.QueryInterface<SharpDX.DXGI.Device>())
                using (var adapter = dxgi.Adapter)
                    return adapter.Description.Description;
            }
            catch
            {
                return "?";
            }
        }

        private Texture2D Open(IntPtr handle, out bool freshlyOpened)
        {
            Texture2D texture;
            freshlyOpened = false;
            if (_opened.TryGetValue(handle, out texture)) return texture;
            texture = _device1.OpenSharedResource1<Texture2D>(handle);
            _opened[handle] = texture;
            freshlyOpened = true;
            var d = texture.Description;
            Program.Log("opened shared texture 0x" + handle.ToInt64().ToString("X") + ": " + d.Width + "x" + d.Height + " " + d.Format + ", usage " + d.Usage + ", bind " + d.BindFlags + ", misc " + d.OptionFlags);
            return texture;
        }

        /// <summary>The host announced a new ring: drop the textures of the old one (and close their handles, as the game does).</summary>
        public void Retain(ICollection<IntPtr> ring)
        {
            if (ring == null) return;
            var gone = new List<IntPtr>();
            foreach (var h in _opened.Keys) if (!ring.Contains(h)) gone.Add(h);
            foreach (var h in gone)
            {
                _opened[h].Dispose();
                _opened.Remove(h);
                CloseHandle(h);
                Program.Log("closed shared texture 0x" + h.ToInt64().ToString("X") + " (no longer in the host's ring)");
            }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        public int OpenedCount => _opened.Count;

        /// <summary>GPU copy of the shared texture into our own texture (the overlay's per-frame work).</summary>
        public bool CopyFrom(IntPtr handle, out bool freshlyOpened)
        {
            var source = Open(handle, out freshlyOpened);
            var d = source.Description;
            if (_copy == null || _copy.Description.Width != d.Width || _copy.Description.Height != d.Height)
            {
                _copy?.Dispose();
                _copy = new Texture2D(_device, new Texture2DDescription
                {
                    Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
                });
            }
            using (var mutex = TryGetMutex(source))
            {
                mutex?.Acquire(1, 1000);
                try { _device.ImmediateContext.CopyResource(source, _copy); }
                finally { mutex?.Release(0); }
            }
            _device.ImmediateContext.Flush();
            return true;
        }

        private static KeyedMutex TryGetMutex(Texture2D texture)
        {
            try { return (texture.Description.OptionFlags & ResourceOptionFlags.SharedKeyedmutex) != 0 ? texture.QueryInterface<KeyedMutex>() : null; }
            catch { return null; }
        }

        /// <summary>Copy, then read the pixels back on the CPU and count the opaque ones; null on success, else the error.</summary>
        public string ReadBack(IntPtr handle, out int opaque, out int width, out int height)
        {
            return ReadBackCount(handle, (b, g, r, a) => a != 0, out opaque, out width, out height);
        }

        /// <summary>Copy, then read the pixels back on the CPU and count those matching (BGRA); null on success, else the error.</summary>
        public string ReadBackCount(IntPtr handle, Func<byte, byte, byte, byte, bool> match, out int count, out int width, out int height)
        {
            count = width = height = 0;
            try
            {
                bool fresh;
                CopyFrom(handle, out fresh);
                var d = _copy.Description;
                width = d.Width;
                height = d.Height;
                if (_staging == null || _staging.Description.Width != d.Width || _staging.Description.Height != d.Height)
                {
                    _staging?.Dispose();
                    _staging = new Texture2D(_device, new Texture2DDescription
                    {
                        Width = d.Width, Height = d.Height, MipLevels = 1, ArraySize = 1, Format = d.Format,
                        SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging, CpuAccessFlags = CpuAccessFlags.Read,
                    });
                }
                _device.ImmediateContext.CopyResource(_copy, _staging);
                var box = _device.ImmediateContext.MapSubresource(_staging, 0, MapMode.Read, MapFlags.None);
                try
                {
                    unsafe
                    {
                        var p = (byte*)box.DataPointer;
                        for (var y = 0; y < height; y++)
                        {
                            var row = p + (long)y * box.RowPitch;
                            for (var x = 0; x < width; x++)
                            {
                                var px = row + x * 4;
                                if (match(px[0], px[1], px[2], px[3])) count++;
                            }
                        }
                    }
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_staging, 0);
                }
                return null;
            }
            catch (SharpDXException ex)
            {
                return ex.Descriptor + " (" + ex.Message.Trim() + ")";
            }
        }

        public void Dispose()
        {
            foreach (var t in _opened.Values) t.Dispose();
            _opened.Clear();
            _staging?.Dispose();
            _copy?.Dispose();
            _device1?.Dispose();
            _device?.Dispose();
        }
    }
}
