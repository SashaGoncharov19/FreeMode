using System;
using System.Collections.Generic;

namespace GTANetwork.GUI.DirectXHook.Hook.Common
{
    /// <summary>
    /// An image that arrives as D3D11 shared textures from the browser host. The host owns a small ring of textures
    /// per browser and announces their handles ("textures": <see cref="Handles"/>, ours to open once and to close when
    /// the next ring replaces them); it copies each of Chromium's paints into a slot and, once that copy has executed
    /// on the GPU, names the slot holding the newest frame ("texture": <see cref="Pending"/>). The overlay, on the
    /// render thread, copies that slot GPU-side into the element's own texture and draws it.
    /// </summary>
    public sealed class SharedTextureSurface
    {
        public readonly object SyncRoot = new object();
        /// <summary>The ring slot with the newest frame, IntPtr.Zero when nothing new arrived since the last copy.</summary>
        public IntPtr Pending;
        /// <summary>The current ring: every handle the host announced last; ours to close when replaced or dropped.</summary>
        public readonly List<IntPtr> Handles = new List<IntPtr>();
        /// <summary>Set by the overlay when a handle could not be opened on the game's device.</summary>
        public bool Failed;
        public string LastError;
    }
}
