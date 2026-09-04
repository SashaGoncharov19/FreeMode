using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using CefSharp;
using CefSharp.Enums;
using CefSharp.Handler;
using CefSharp.OffScreen;
using CefSharp.Structs;
using GTANetwork.GUI.DirectXHook.Hook.Common;
using GTANetwork.Streamer;
using GTANetwork.Util;
using GTANetworkShared;
using Point = System.Drawing.Point;
using Range = CefSharp.Structs.Range;
using Rect = CefSharp.Structs.Rect;

namespace GTANetwork.GUI
{
    internal static class CefUtil
    {
        public static bool DISABLE_CEF = true;

        /// <summary>Our wrapper for a CefSharp browser control, or null.</summary>
        internal static Browser GetBrowser(IWebBrowser chromiumWebBrowser)
        {
            lock (CEFManager.Browsers)
            {
                for (var index = CEFManager.Browsers.Count - 1; index >= 0; index--)
                {
                    var b = CEFManager.Browsers[index];
                    if (b != null && ReferenceEquals(b._browser, chromiumWebBrowser)) return b;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Receives the frames of one off-screen browser and hands them to the DirectX overlay as an image element.
    /// CEF calls <see cref="OnPaint"/> on its UI thread; the buffer is only valid during the call, so it is copied.
    /// </summary>
    internal class OverlayRenderHandler : IRenderHandler
    {
        private int _width;
        private int _height;
        private ImageElement _imageElement;
        private int _paintsLogged;

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
            if (_imageElement != null) _imageElement.Hidden = hidden;
        }

        public void SetSize(int width, int height)
        {
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);
        }

        public void SetPosition(int x, int y)
        {
            Position = new Point(x, y);
            if (_imageElement != null) _imageElement.Location = Position;
        }

        public void Dispose()
        {
            var element = _imageElement;
            _imageElement = null;
            if (element == null) return;

            CEFManager.DirectXHook?.RemoveImage(element);
            element.Dispose();
        }

        public ScreenInfo? GetScreenInfo()
        {
            return null; // the view rectangle is the screen, scale factor 1
        }

        public Rect GetViewRect()
        {
            return new Rect(0, 0, _width, _height);
        }

        public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
        {
            screenX = viewX + Position.X;
            screenY = viewY + Position.Y;
            return true;
        }

        public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo acceleratedPaintInfo)
        {
            // Shared D3D11 textures (zero copy into the overlay) are the next step; software paints are used today.
        }

        public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
        {
            try
            {
                if (type != PaintElementType.View || _imageElement == null || width <= 0 || height <= 0 || buffer == IntPtr.Zero) return;

                var copy = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var data = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        var rowBytes = (long)width * 4;
                        var src = (byte*)buffer;
                        var dst = (byte*)data.Scan0;
                        for (var y = 0; y < height; y++)
                        {
                            System.Buffer.MemoryCopy(src + y * rowBytes, dst + (long)y * data.Stride, rowBytes, rowBytes);
                        }
                    }
                }
                finally
                {
                    copy.UnlockBits(data);
                }

                _imageElement.SetBitmap(copy);

                if (_paintsLogged < 3 && LogManager.Verbose)
                {
                    _paintsLogged++;
                    LogManager.CefLog("-> Paint " + width + "x" + height + " (dirty " + dirtyRect.Width + "x" + dirtyRect.Height + " at " + dirtyRect.X + "," + dirtyRect.Y + ")");
                }
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF PAINT");
            }
        }

        public void OnCursorChange(IntPtr cursor, CursorType type, CursorInfo customCursorInfo)
        {
        }

        public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
        {
            return false;
        }

        public void UpdateDragCursor(DragOperationsMask operation)
        {
        }

        public void OnPopupShow(bool show)
        {
        }

        public void OnPopupSize(Rect rect)
        {
        }

        public void OnImeCompositionRangeChanged(Range selectedRange, Rect[] characterBounds)
        {
        }

        public void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
        {
        }
    }

    /// <summary>
    /// Local-mode browsers (the ones resources create for their UI) only see the files of the resources:
    /// <c>https://&lt;resource&gt;/&lt;path&gt;</c> is served from the download folder, everything else is refused.
    /// Remote browsers keep the normal network stack.
    /// </summary>
    internal class LocalResourceRequestHandler : RequestHandler
    {
        private readonly bool _localMode;
        private readonly LocalResourceHandlerFactory _factory = new LocalResourceHandlerFactory();

        public LocalResourceRequestHandler(bool localMode)
        {
            _localMode = localMode;
        }

        protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request,
            bool isNavigation, bool isDownload, string requestInitiator, ref bool disableDefaultHandling)
        {
            return _localMode ? _factory : null;
        }

        protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser, CefTerminationStatus status, int errorCode, string errorMessage)
        {
            LogManager.CefLog("-> Render process terminated: " + status + " (" + errorCode + ") " + errorMessage);
        }
    }

    internal class LocalResourceHandlerFactory : ResourceRequestHandler
    {
        protected override IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
        {
            try
            {
                var url = request.Url ?? string.Empty;

                // data:/about: pages (loadHtmlCefBrowser, blank pages) do not touch the disk.
                if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return null;

                LogManager.VerboseCefLog("-> [Local mode] Uri: " + url);

                Uri uri;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
                {
                    LogManager.CefLog("-> Refused: only https://<resource>/<file> is allowed in a local browser");
                    return ResourceHandler.ForErrorMessage("Only https://<resource>/<file> is allowed here", HttpStatusCode.Forbidden);
                }

                string file;
                if (!ResourceFileDownloader.TryGetLocalPath(FileTransferId._DOWNLOADFOLDER_, uri.Host, Uri.UnescapeDataString(uri.AbsolutePath), out file))
                {
                    LogManager.CefLog("-> Refused: bad path");
                    return ResourceHandler.ForErrorMessage("Bad path", HttpStatusCode.Forbidden);
                }

                LogManager.VerboseCefLog("-> Loading: " + file);

                if (!File.Exists(file))
                {
                    LogManager.CefLog("-> Error: File does not exist!");
                    return ResourceHandler.ForErrorMessage("File not found: " + uri.Host + uri.AbsolutePath, HttpStatusCode.NotFound);
                }

                return ResourceHandler.FromFilePath(file, MimeType.GetMimeType(Path.GetExtension(file)), true);
            }
            catch (Exception ex)
            {
                LogManager.CefLog(ex, "CEF SCHEME HANDLING");
                return ResourceHandler.ForErrorMessage("error", HttpStatusCode.InternalServerError);
            }
        }
    }

    /// <summary>Pop-ups (target=_blank, window.open) navigate the browser itself instead of opening a window.</summary>
    internal class PopupToMainFrameLifeSpanHandler : LifeSpanHandler
    {
        protected override bool OnBeforePopup(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, string targetUrl, string targetFrameName,
            WindowOpenDisposition targetDisposition, bool userGesture, IPopupFeatures popupFeatures, IWindowInfo windowInfo, IBrowserSettings browserSettings,
            ref bool noJavascriptAccess, out IWebBrowser newBrowser)
        {
            newBrowser = null;
            if (!string.IsNullOrEmpty(targetUrl)) chromiumWebBrowser.Load(targetUrl);
            return true;
        }
    }

    internal class NoContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IContextMenuParams parameters, IMenuModel model)
        {
            model.Clear();
        }
    }

    /// <summary>
    /// Defines <c>resourceCall(name, ...args)</c> and <c>resourceEval(code)</c> in every page. CEF runs pages in its
    /// render process, so the functions post a message that the browser process (the game) forwards to the client
    /// script of the resource; they return nothing. <c>gtan.call/gtan.eval</c> are the same functions under a
    /// namespace for new pages.
    /// </summary>
    internal class ResourceBridgeInjector : IRenderProcessMessageHandler
    {
        internal const string Shim =
            "(function(){" +
            " if (window.resourceCall && window.gtan) return;" +
            " var post = function(m){ if (window.CefSharp && CefSharp.PostMessage) CefSharp.PostMessage(m); else if (window.cefSharp && cefSharp.postMessage) cefSharp.postMessage(m); };" +
            " window.resourceCall = function(name){ post({ type: 'resourceCall', name: String(name), args: Array.prototype.slice.call(arguments, 1) }); };" +
            " window.resourceEval = function(code){ post({ type: 'resourceEval', code: String(code) }); };" +
            " window.gtan = { call: window.resourceCall, eval: window.resourceEval };" +
            "})();";

        public void OnContextCreated(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
        {
            if (frame == null) return;
            if (frame.IsMain) LogManager.VerboseCefLog("-> Main context created: " + frame.Url);
            frame.ExecuteJavaScriptAsync(Shim, "gtan://bridge", 0);
        }

        public void OnContextReleased(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame)
        {
        }

        public void OnFocusedNodeChanged(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IDomNode node)
        {
        }

        public void OnUncaughtException(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, JavascriptException exception)
        {
            LogManager.CefLog("-> Page exception in " + (frame != null ? frame.Url : "?") + ": " + exception.Message);
        }
    }
}
