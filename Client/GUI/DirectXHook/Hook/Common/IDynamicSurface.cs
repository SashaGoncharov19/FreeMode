using System;

namespace GTANetwork.GUI.DirectXHook.Hook.Common
{
    /// <summary>
    /// A BGRA image that changes over time and is uploaded into a persistent texture by the overlay: only the part
    /// that changed since the last upload. The producer fills <see cref="Data"/> and widens the dirty rectangle; the
    /// overlay, on the render thread, uploads and calls <see cref="MarkUploaded"/>. Everything under <see cref="SyncRoot"/>.
    /// </summary>
    public interface IDynamicSurface
    {
        object SyncRoot { get; }
        int Width { get; }
        int Height { get; }
        /// <summary>BGRA pixels, top-down, <see cref="Stride"/> bytes per row; valid while <see cref="SyncRoot"/> is held.</summary>
        IntPtr Data { get; }
        int Stride { get; }
        /// <summary>Something changed since the last upload.</summary>
        bool HasUpdate { get; }
        /// <summary>The rectangle to upload (the union of all changes since the last upload).</summary>
        void GetDirty(out int x, out int y, out int width, out int height);
        void MarkUploaded();
        /// <summary>Ask for the whole image to be uploaded next time (a new texture was created).</summary>
        void InvalidateAll();
    }
}
