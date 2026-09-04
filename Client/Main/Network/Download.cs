using GTANetwork.GUI;
using System;
using System.IO;
using System.Net;
using System.Threading;
using GTA;
using GTA.Native;
using GTANetwork.Streamer;
using GTANetwork.Util;
using GTANetworkShared;
using Lidgren.Network;

namespace GTANetwork
{
    internal partial class Main
    {
        // Resource files (<file src="..."/> in meta.xml) when the server runs its HTTP file server: the map and the
        // client scripts still arrive over UDP, the files come from GET /manifest.json + GET /<resource>/<path> and
        // land in <install dir>\resources\<resource>\<path>, where CEF pages (https://<resource>/<path>) look for them.
        private Thread _httpDownloadThread;
        private static volatile bool _cancelDownload;
        private static int _httpDownloadGeneration;

        /// <summary>True while resource files are being fetched over HTTP. The end-of-transfer handling (script start)
        /// waits for it, otherwise a script could open a page that is not on disk yet.</summary>
        internal static volatile bool HttpDownloadPending;

        private void StartFileDownload(string address)
        {
            _cancelDownload = false;
            HttpDownloadPending = true;

            var generation = Interlocked.Increment(ref _httpDownloadGeneration);
            var previous = _httpDownloadThread;

            _httpDownloadThread = new Thread(() => DownloadResourceFiles(address, generation, previous))
            {
                IsBackground = true,
                Name = "GTAN resource files",
            };
            _httpDownloadThread.Start();
        }

        /// <summary>Stops the running download (checked between files); the thread ends on its own.</summary>
        internal static void CancelFileDownload()
        {
            _cancelDownload = true;
        }

        private static void DownloadResourceFiles(string address, int generation, Thread previous)
        {
            try
            {
                // A second manifest (a resource started while we play) waits for the first pass instead of racing it.
                if (previous != null && previous.IsAlive) previous.Join(TimeSpan.FromMinutes(2));

                if (_cancelDownload || generation != _httpDownloadGeneration) return;

                using (var wc = new WebClient())
                {
                    var downloader = new ResourceFileDownloader(address, FileTransferId._DOWNLOADFOLDER_, url => wc.DownloadData(url))
                    {
                        Accept = DownloadManager.IsAllowedFile,
                        Cancelled = () => _cancelDownload || generation != _httpDownloadGeneration,
                        Progress = (label, index, total) =>
                        {
                            _threadsafeSubtitle = "Downloading " + label + " (" + index + "/" + total + ")";
                            ConnectLoader.Progress(label, index, total);
                        },
                        Log = text => LogManager.RuntimeLog("Resource files: " + text),
                    };

                    var result = downloader.Run();
                    LogManager.RuntimeLog("Resource files from " + address + ": " + result);

                    if (result.Rejected > 0 || result.Failed.Count > 0)
                        DownloadManager.NotifyOnMainThread("~r~Resource files: " + result.Rejected + " rejected, " + result.Failed.Count + " failed. See Runtime.log.");
                }
            }
            catch (Exception ex)
            {
                LogManager.LogException(ex, "HTTP FILE DOWNLOAD");
                DownloadManager.NotifyOnMainThread("~r~Could not download the resource files from " + address + ".");
            }
            finally
            {
                if (generation == _httpDownloadGeneration)
                {
                    _threadsafeSubtitle = null;
                    HttpDownloadPending = false;
                }
            }
        }

        public static void InvokeFinishedDownload(System.Collections.Generic.List<string> resources)
        {
            var confirmObj = Client.CreateMessage();
            confirmObj.Write((byte)PacketType.ConnectionConfirmed);
            confirmObj.Write(true);
            confirmObj.Write(resources.Count);

            for (int i = 0; i < resources.Count; i++)
            {
                confirmObj.Write(resources[i]);
            }

            Client.SendMessage(confirmObj, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.SyncEvent);

            HasFinishedDownloading = true;
            ConnectLoader.Hide("resources ready, " + resources.Count + " resource(s)");
            Function.Call((Hash)0x10D373323E5B9C0D); //_REMOVE_LOADING_PROMPT
            Function.Call(Hash.DISPLAY_RADAR, true);
        }
    }
}
