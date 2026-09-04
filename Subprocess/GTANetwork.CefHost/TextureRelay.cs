using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using GTANetworkShared.Cef;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using Resource1 = SharpDX.DXGI.Resource1;
using Rect = CefSharp.Structs.Rect;

namespace GTANetwork.CefHost
{
    /// <summary>
    /// Relays Chromium's accelerated paints to the game as D3D11 textures the host owns.
    ///
    /// CEF's OnAcceleratedPaint hands over a texture handle that is only valid inside the callback: the texture is one
    /// of a pool Chromium recycles, the handle is a fresh duplicate every paint, and the buffer is rewritten with a
    /// later frame once the callback returns. Forwarding those handles to the game (the first version did) meant stale
    /// frames whenever a handle value was reused for another pool texture, and an open failure (E_INVALIDARG) once the
    /// game had closed a handle the host still mapped. So every browser gets a small ring of shared textures the host
    /// creates itself: each paint is copied into the next slot on the host's own D3D11 device, a publisher thread waits
    /// until that copy has finished on the GPU and only then tells the game which slot holds the frame ("texture").
    /// The game's handles to the ring are stable ("textures" announces them): it opens each slot once and copies it
    /// GPU-side on its render thread. Four slots: the game copies the newest slot when it presents, the host writes the
    /// same slot again three paints later at the earliest, so the two never touch one texture at the same time.
    /// </summary>
    internal sealed class TextureRelay : IDisposable
    {
        public const int RingSize = 4;

        private static readonly object InstanceLock = new object();
        private static TextureRelay _instance;
        private static string _unavailable;

        private readonly Device _device;
        private readonly Device1 _device1;
        private readonly DeviceContext _context;
        private readonly IntPtr _parentProcess;
        private readonly Thread _publisher;
        private readonly Queue<Publish> _pending = new Queue<Publish>();
        private readonly AutoResetEvent _pendingSignal = new AutoResetEvent(false);
        private volatile bool _disposed;
        private int _openErrorsLogged;
        private int _waitTimeoutsLogged;

        private sealed class Publish
        {
            public TextureRing Ring;
            public TextureRing.Slot Slot;
            public Rect Dirty;
        }

        /// <summary>The process-wide relay, created on first use; null (with the reason) when the host has no D3D11 device.</summary>
        public static TextureRelay Get(out string error)
        {
            lock (InstanceLock)
            {
                error = _unavailable;
                if (_instance != null || _unavailable != null) return _instance;
                try
                {
                    _instance = new TextureRelay();
                }
                catch (Exception ex)
                {
                    _unavailable = error = ex.GetType().Name + ": " + ex.Message.Trim();
                    Program.Log("texture relay unavailable: " + _unavailable);
                }
                return _instance;
            }
        }

        public static void DisposeInstance()
        {
            TextureRelay relay;
            lock (InstanceLock)
            {
                relay = _instance;
                _instance = null;
            }
            relay?.Dispose();
        }

        private TextureRelay()
        {
            _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            _device1 = _device.QueryInterface<Device1>();
            _context = _device.ImmediateContext;
            // The CEF UI thread copies, the publisher thread polls the copies' queries: let D3D11 serialise the calls.
            using (var multithread = _context.QueryInterface<Multithread>()) multithread.SetMultithreadProtected(true);

            _parentProcess = Program.ParentPid > 0 ? NativeMethods.OpenProcess(NativeMethods.ProcessDupHandle, false, Program.ParentPid) : IntPtr.Zero;
            if (_parentProcess == IntPtr.Zero)
                throw new InvalidOperationException("cannot open the game process for handle duplication (error " + Marshal.GetLastWin32Error() + ")");

            _publisher = new Thread(PublishLoop) { IsBackground = true, Name = "texture publisher" };
            _publisher.Start();
            Program.Log("texture relay: D3D11 " + _device.FeatureLevel + " on " + AdapterName() + ", ring of " + RingSize + " shared textures per browser");
        }

        private string AdapterName()
        {
            try
            {
                using (var dxgi = _device.QueryInterface<SharpDX.DXGI.Device>())
                using (var adapter = dxgi.Adapter)
                    return adapter.Description.Description.TrimEnd('\0');
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// One paint (CEF UI thread, inside OnAcceleratedPaint): open Chromium's texture, copy it into the ring, queue
        /// the announcement. The ring is created, and announced, when the texture's size or format changes.
        /// </summary>
        public void Paint(TextureRing ring, IntPtr chromiumHandle, Rect dirty, bool log)
        {
            Texture2D source = null;
            try
            {
                lock (ring.Sync)
                {
                    if (ring.Disposed || ring.Broken) return;
                    try
                    {
                        source = _device1.OpenSharedResource1<Texture2D>(chromiumHandle);
                    }
                    catch (Exception ex)
                    {
                        if (_openErrorsLogged++ < 3)
                            Program.Log("browser " + ring.BrowserId + ": Chromium's texture 0x" + chromiumHandle.ToInt64().ToString("X") + " could not be opened: " + ex.Message.Trim());
                        return;
                    }

                    var d = source.Description;
                    if (!ring.Matches(d.Width, d.Height, d.Format)) Recreate(ring, d.Width, d.Height, d.Format);

                    var slot = ring.Next();
                    _context.CopyResource(source, slot.Texture);
                    _context.End(slot.Done);
                    _context.Flush();
                    slot.InFlight = true;
                    if (log)
                        Program.Log("browser " + ring.BrowserId + ": texture paint " + d.Width + "x" + d.Height + " (dirty " + dirty.Width + "x" + dirty.Height + " at " + dirty.X + "," + dirty.Y + ") -> slot " + slot.Index);
                    lock (_pending) _pending.Enqueue(new Publish { Ring = ring, Slot = slot, Dirty = dirty });
                }
                _pendingSignal.Set();
            }
            finally
            {
                // The copy is recorded and flushed; D3D11 keeps the source alive until the GPU is done with it.
                source?.Dispose();
            }
        }

        /// <summary>New ring for a new size/format (under the ring's lock). The game hears of it before any slot is announced.</summary>
        private void Recreate(TextureRing ring, int width, int height, Format format)
        {
            ring.ReleaseSlots();
            var slots = new TextureRing.Slot[RingSize];
            try
            {
                for (var i = 0; i < slots.Length; i++) slots[i] = CreateSlot(i, width, height, format);
            }
            catch (Exception ex)
            {
                foreach (var s in slots) s?.Release();
                ring.Broken = true;
                var error = ex.GetType().Name + ": " + ex.Message.Trim();
                Program.Log("browser " + ring.BrowserId + ": shared texture ring " + width + "x" + height + " failed: " + error);
                Program.TrySend(new CefHostMessage(CefHostProtocol.Textures, ring.BrowserId) { W = width, H = height, Text = error });
                throw;
            }
            ring.Install(slots, width, height, format);
            Program.Log("browser " + ring.BrowserId + ": shared texture ring " + width + "x" + height + " " + format + ", generation " + ring.Generation +
                        ", game handles " + string.Join(" ", slots.Select(s => s.GameHandle.ToInt64().ToString("X"))));
            Program.TrySend(new CefHostMessage(CefHostProtocol.Textures, ring.BrowserId)
            {
                W = width, H = height, Gen = ring.Generation, Handles = slots.Select(s => s.GameHandle.ToInt64()).ToArray(),
            });
        }

        private TextureRing.Slot CreateSlot(int index, int width, int height, Format format)
        {
            var texture = new Texture2D(_device, new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.Shared | ResourceOptionFlags.SharedNthandle,
            });
            var slot = new TextureRing.Slot { Index = index, Texture = texture };
            try
            {
                using (var resource = texture.QueryInterface<Resource1>())
                    slot.HostHandle = resource.CreateSharedHandle(null, SharedResourceFlags.Read | SharedResourceFlags.Write);
                IntPtr forGame;
                if (!NativeMethods.DuplicateHandle(NativeMethods.GetCurrentProcess(), slot.HostHandle, _parentProcess, out forGame, 0, false, NativeMethods.DuplicateSameAccess))
                    throw new InvalidOperationException("DuplicateHandle into the game failed (error " + Marshal.GetLastWin32Error() + ")");
                slot.GameHandle = forGame;
                slot.Done = new Query(_device, new QueryDescription { Type = QueryType.Event });
                return slot;
            }
            catch
            {
                slot.Release();
                throw;
            }
        }

        /// <summary>Announces each copied slot once its copy has executed on the GPU, so the game never copies a half-written texture.</summary>
        private void PublishLoop()
        {
            while (!_disposed)
            {
                Publish p;
                lock (_pending) p = _pending.Count > 0 ? _pending.Dequeue() : null;
                if (p == null)
                {
                    _pendingSignal.WaitOne();
                    continue;
                }

                try
                {
                    var clock = Stopwatch.StartNew();
                    var spin = new SpinWait();
                    CefHostMessage announce = null;
                    while (!_disposed)
                    {
                        bool ready;
                        lock (p.Ring.Sync)
                        {
                            if (p.Slot.Released || p.Ring.Disposed) break; // replaced or closed meanwhile: nothing to announce
                            int done;
                            ready = _context.GetData(p.Slot.Done, AsynchronousFlags.None, out done);
                            if (!ready && clock.ElapsedMilliseconds > 250)
                            {
                                if (_waitTimeoutsLogged++ < 3)
                                    Program.Log("browser " + p.Ring.BrowserId + ": the GPU copy into slot " + p.Slot.Index + " did not finish within 250 ms; announcing it anyway");
                                ready = true;
                            }
                            if (ready)
                            {
                                p.Slot.InFlight = false;
                                announce = new CefHostMessage(CefHostProtocol.Texture, p.Ring.BrowserId)
                                {
                                    Handle = p.Slot.GameHandle.ToInt64(), W = p.Ring.Width, H = p.Ring.Height,
                                    X = p.Dirty.X, Y = p.Dirty.Y, Dx = p.Dirty.Width, Dy = p.Dirty.Height, Gen = p.Slot.Index,
                                };
                            }
                        }
                        if (ready) break;
                        spin.SpinOnce();
                    }
                    if (announce != null) Program.TrySend(announce);
                }
                catch (Exception ex)
                {
                    Program.Log("texture publisher: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _pendingSignal.Set();
            try { _publisher.Join(500); } catch { }
            try { _device1?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            if (_parentProcess != IntPtr.Zero) NativeMethods.CloseHandle(_parentProcess);
        }
    }

    /// <summary>The host-owned shared textures of one browser (see <see cref="TextureRelay"/>).</summary>
    internal sealed class TextureRing : IDisposable
    {
        public readonly int BrowserId;
        public readonly object Sync = new object();
        private Slot[] _slots;
        private int _next;

        public int Width { get; private set; }
        public int Height { get; private set; }
        public Format Format { get; private set; }
        public int Generation { get; private set; }
        public bool Disposed { get; private set; }
        /// <summary>Set when the ring could not be created; the game was told to use CPU frames for this browser.</summary>
        public bool Broken;

        internal sealed class Slot
        {
            public int Index;
            public Texture2D Texture;
            /// <summary>Our NT handle to the texture (CreateSharedHandle); closed with the slot.</summary>
            public IntPtr HostHandle;
            /// <summary>The same handle duplicated into the game process; the game closes it when the ring is replaced.</summary>
            public IntPtr GameHandle;
            /// <summary>Completes when the last copy into the texture has executed on the GPU.</summary>
            public Query Done;
            /// <summary>Copied into, not yet announced to the game.</summary>
            public bool InFlight;
            public bool Released;

            public void Release()
            {
                Released = true;
                try { Done?.Dispose(); } catch { }
                Done = null;
                try { Texture?.Dispose(); } catch { }
                Texture = null;
                if (HostHandle != IntPtr.Zero) NativeMethods.CloseHandle(HostHandle);
                HostHandle = IntPtr.Zero;
            }
        }

        public TextureRing(int browserId)
        {
            BrowserId = browserId;
        }

        public bool Matches(int width, int height, Format format)
        {
            return _slots != null && Width == width && Height == height && Format == format;
        }

        internal void Install(Slot[] slots, int width, int height, Format format)
        {
            _slots = slots;
            Width = width;
            Height = height;
            Format = format;
            _next = 0;
            Generation++;
        }

        /// <summary>The slot for the next paint: the one after the last written, preferring slots already announced.</summary>
        internal Slot Next()
        {
            for (var i = 0; i < _slots.Length; i++)
            {
                var slot = _slots[(_next + i) % _slots.Length];
                if (slot.InFlight) continue;
                _next = (slot.Index + 1) % _slots.Length;
                return slot;
            }
            var oldest = _slots[_next];
            _next = (_next + 1) % _slots.Length;
            return oldest;
        }

        internal void ReleaseSlots()
        {
            var slots = _slots;
            _slots = null;
            if (slots == null) return;
            foreach (var s in slots) s.Release();
        }

        public void Dispose()
        {
            lock (Sync)
            {
                Disposed = true;
                ReleaseSlots();
            }
        }
    }
}
