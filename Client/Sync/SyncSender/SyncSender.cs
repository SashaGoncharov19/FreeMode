using System;
using System.Threading;
using GTA;
using GTANetwork.Util;
using GTANetworkShared;
using Lidgren.Network;

namespace GTANetwork.Streamer
{
    internal static class SyncSender
    {
        private const int LIGHT_SYNC_RATE = 1500;
        private const int PURE_SYNC_RATE = 100;

        internal static void MainLoop()
        {
            bool lastPedData = false;
            int lastLightSyncSent = 0;

            while (true)
            {
                if (!Main.IsConnected())
                {
                    Thread.Sleep(100);
                    continue;
                }

                object lastPacket;
                lock (SyncCollector.Lock)
                {
                    lastPacket = SyncCollector.LastSyncPacket;
                    SyncCollector.LastSyncPacket = null;
                }

                if (lastPacket == null)
                {
                    Thread.Sleep(PURE_SYNC_RATE);   // nothing collected yet (connecting, loading): do not spin
                    continue;
                }
                try
                {
                    var data = lastPacket as PedData;
                    if (data != null)
                    {
                        var bin = PacketOptimization.WritePureSync(data);

                        var msg = Main.Client.CreateMessage();
                        msg.Write((byte) PacketType.PedPureSync);
                        msg.Write(bin.Length);
                        msg.Write(bin);

                        //try
                        //{
                            Main.Send(msg, NetDeliveryMethod.UnreliableSequenced, (int) ConnectionChannel.PureSync);
                            if (GTANetwork.Sync.RouteRecorder.Enabled) GTANetwork.Sync.RouteRecorder.Write(data.Position, data.Quaternion?.Z ?? 0f);   // T-018
                        //}
                        //catch (Exception ex)
                        //{
                        //    Util.Util.SafeNotify("FAILED TO SEND DATA: " + ex.Message);
                        //    LogManager.LogException(ex, "SENDPLAYERDATA");
                        //}

                        if (!lastPedData || Environment.TickCount - lastLightSyncSent > LIGHT_SYNC_RATE)
                        {
                            lastLightSyncSent = Environment.TickCount;

                            LogManager.DebugLog("SENDING LIGHT VEHICLE SYNC");

                            var lightBin = PacketOptimization.WriteLightSync(data);

                            var lightMsg = Main.Client.CreateMessage();
                            lightMsg.Write((byte) PacketType.PedLightSync);
                            lightMsg.Write(lightBin.Length);
                            lightMsg.Write(lightBin);
                            //try
                            //{
                                Main.Send(lightMsg, NetDeliveryMethod.ReliableSequenced, (int) ConnectionChannel.LightSync);
                            //}
                            //catch (Exception ex)
                            //{
                            //    Util.Util.SafeNotify("FAILED TO SEND LIGHT DATA: " + ex.Message);
                            //    LogManager.LogException(ex, "SENDPLAYERDATA");
                            //}

                            Interlocked.Add(ref Main.BytesSent, lightBin.Length);
                            Interlocked.Increment(ref Main.MessagesSent);
                        }

                        lastPedData = true;

                        lock (Main.AveragePacketSize)
                        {
                            Main.AveragePacketSize.Add(bin.Length);
                            if (Main.AveragePacketSize.Count > 10)
                                Main.AveragePacketSize.RemoveAt(0);
                        }

                        Interlocked.Add(ref Main.BytesSent, bin.Length);
                        Interlocked.Increment(ref Main.MessagesSent);
                    }
                    else
                    {
                        var bin = PacketOptimization.WritePureSync((VehicleData) lastPacket);

                        var msg = Main.Client.CreateMessage();
                        msg.Write((byte) PacketType.VehiclePureSync);
                        msg.Write(bin.Length);
                        msg.Write(bin);
                        //try
                        //{
                            Main.Send(msg, NetDeliveryMethod.UnreliableSequenced, (int) ConnectionChannel.PureSync);
                        //}
                        //catch (Exception ex)
                        //{
                        //    Util.Util.SafeNotify("FAILED TO SEND DATA: " + ex.Message);
                        //    LogManager.LogException(ex, "SENDPLAYERDATA");
                        //}

                        if (lastPedData || Environment.TickCount - lastLightSyncSent > LIGHT_SYNC_RATE)
                        {
                            lastLightSyncSent = Environment.TickCount;

                            LogManager.DebugLog("SENDING LIGHT VEHICLE SYNC");

                            var lightBin = PacketOptimization.WriteLightSync((VehicleData)lastPacket);

                            var lightMsg = Main.Client.CreateMessage();
                            lightMsg.Write((byte) PacketType.VehicleLightSync);
                            lightMsg.Write(lightBin.Length);
                            lightMsg.Write(lightBin);
                            //try
                            //{
                                Main.Send(lightMsg, NetDeliveryMethod.ReliableSequenced, (int) ConnectionChannel.LightSync);
                            //}
                            //catch (Exception ex)
                            //{
                            //    Util.Util.SafeNotify("FAILED TO SEND LIGHT DATA: " + ex.Message);
                            //    LogManager.LogException(ex, "SENDPLAYERDATA");
                            //}

                            Interlocked.Add(ref Main.BytesSent, lightBin.Length);
                            Interlocked.Increment(ref Main.MessagesSent);
                        }

                        lastPedData = false;

                        lock (Main.AveragePacketSize)
                        {
                            Main.AveragePacketSize.Add(bin.Length);
                            if (Main.AveragePacketSize.Count > 10)
                                Main.AveragePacketSize.RemoveAt(0);
                        }

                        Interlocked.Add(ref Main.BytesSent, bin.Length);
                        Interlocked.Increment(ref Main.MessagesSent);
                    }
                }
                catch (Exception ex)
                {
                    LogManager.LogException(ex, "SYNCSENDER");
                }

                LogManager.DebugLog("END SYNC SEND");

                Thread.Sleep(PURE_SYNC_RATE);
            }
        }
    }

    public partial class SyncCollector : Script
    {
        internal static volatile bool ForceAimData;   // written by scripts, read by the collector
        internal static object LastSyncPacket;
        internal static object Lock = new object();

        public SyncCollector()
        {
            var t = new Thread(SyncSender.MainLoop) {IsBackground = true};
            t.Start();
            Tick += OnTick;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (!Main.IsOnServer()) return;
            var player = Game.Player.Character;

            if (player.IsInVehicle())
            {
                VehicleData(player);
            }
            else
            {
                PedData(player);
            }
        }


    }
}
