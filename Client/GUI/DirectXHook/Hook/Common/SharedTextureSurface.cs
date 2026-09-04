using System;
using System.Collections.Generic;

namespace GTANetwork.GUI.DirectXHook.Hook.Common
{
    /// <summary>
    /// An image that arrives as D3D11 shared textures from another process (the browser host): the producer sets the
    /// handle of the texture holding the newest frame; the overlay, on the render thread, opens each handle once,
    /// copies the texture GPU-side into the element's own texture and draws that. Handles Chromium stops using are
    /// released after a while (it cycles through a small pool and re-creates it now and then).
    /// </summary>
    public sealed class SharedTextureSurface
    {
        public readonly object SyncRoot = new object();
        /// <summary>The texture with the newest frame, IntPtr.Zero when nothing new arrived since the last copy.</summary>
        public IntPtr Pending;
        /// <summary>Every handle received so far and not yet released; ours to close.</summary>
        public readonly List<IntPtr> Handles = new List<IntPtr>();
        /// <summary>Set by the overlay when a handle could not be opened on the game's device.</summary>
        public bool Failed;
        public string LastError;
    }
}
