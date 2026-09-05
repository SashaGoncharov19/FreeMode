using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTANetwork.GUI;
using GTANetwork.Javascript;
using GTANetwork.Misc;
using GTANetwork.Streamer;
using GTANetwork.Util;
using GTANetworkShared;
using GTANetworkShared.Crypto;
using Lidgren.Network;
using Microsoft.Win32;
using NativeUI;
using NativeUI.PauseMenu;
using Newtonsoft.Json;
using ProtoBuf;
using Control = GTA.Control;
using Vector3 = GTA.Math.Vector3;
using WeaponHash = GTA.WeaponHash;
using VehicleHash = GTA.VehicleHash;
using GTANetwork.Sync;

namespace GTANetwork
{
    internal partial class Main
    {
        public static void ChatOnComplete(object sender, EventArgs args)
        {
            var message = GUI.Chat.SanitizeString(Chat.CurrentInput);
            if (!string.IsNullOrWhiteSpace(message))
            {
                JavascriptHook.InvokeMessageEvent(message);

                var obj = new ChatData()
                {
                    Message = message,
                };
                var data = SerializeBinary(obj);

                var msg = Client.CreateMessage();
                msg.Write((byte)PacketType.ChatData);
                msg.Write(data.Length);
                msg.Write(data);
                Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Chat);
            }

            Chat.IsFocused = false;
        }

        // ---- the encrypted session (T-009) ----

        /// <summary>The cipher of the current connection; null before the handshake and on plaintext connections to old servers.</summary>
        internal static NetSessionEncryption Session;
        private static KeyPair _handshakeKey;
        private static byte[] _pinnedServerKey;
        private static int _authFailuresLogged;

        /// <summary>Every message to the server goes through here: encrypted once the session exists.</summary>
        internal static void Send(NetOutgoingMessage msg, NetDeliveryMethod method, int channel = 0)
        {
            var client = Client;
            if (client == null || msg == null) return;
            var session = Session;
            if (session != null) msg.Encrypt(session);
            client.SendMessage(msg, method, channel);
        }

        /// <summary>The message pump: a data message of an encrypted session is decrypted here; false = drop it.</summary>
        internal static bool DecryptIncoming(NetIncomingMessage msg)
        {
            var session = Session;
            if (session == null || msg.MessageType != NetIncomingMessageType.Data) return true;
            if (msg.Decrypt(session)) return true;
            if (_authFailuresLogged++ < 3) LogManager.RuntimeLog("session: dropped a message that failed authentication (replay or not from this session)");
            return false;
        }

        /// <summary>
        /// The approval hail arrived. With a server key: check the pin, derive the session key, encrypt from here on. Without
        /// one: a plaintext session with an old server, unless a key was pinned. False = do not stay connected.
        /// </summary>
        internal static bool CompleteHandshake(ConnectionResponse response)
        {
            var serverKey = response?.ServerPublicKey;
            if (serverKey == null || serverKey.Length != 32)
            {
                if (_pinnedServerKey != null)
                {
                    LogManager.RuntimeLog("session: the server offered no key but one was pinned; not connecting");
                    Util.Util.SafeNotify("~r~Server key mismatch:~w~ the server offered no key.");
                    return false;
                }
                LogManager.RuntimeLog("session: plaintext (the server offered no key: old server, or RequireEncryption off)");
                return true;
            }
            if (_handshakeKey == null)
            {
                LogManager.RuntimeLog("session: the server offered a key but this connection sent none; plaintext");
                return true;
            }
            var fingerprint = SessionHandshake.Fingerprint(serverKey);
            if (_pinnedServerKey != null && !SameKey(_pinnedServerKey, serverKey))
            {
                LogManager.RuntimeLog("session: SERVER KEY MISMATCH: pinned " + SessionHandshake.Fingerprint(_pinnedServerKey) + ", offered " + fingerprint + "; not connecting");
                Util.Util.SafeNotify("~r~Server key mismatch:~w~ expected " + SessionHandshake.Fingerprint(_pinnedServerKey) + ", got " + fingerprint + ".");
                return false;
            }
            try
            {
                var key = SessionHandshake.DeriveSessionKey(_handshakeKey.PrivateKey, serverKey, _handshakeKey.PublicKey, serverKey);
                Session = new NetSessionEncryption(Client, new SessionCipher(key, isServer: false), fingerprint);
            }
            catch (Exception ex)
            {
                LogManager.LogException(ex, "SESSION HANDSHAKE");
                Util.Util.SafeNotify("~r~The session handshake failed.");
                return false;
            }
            LogManager.RuntimeLog("session: encrypted (X25519 + AES-256-GCM), server key " + fingerprint + (_pinnedServerKey != null ? " (pinned)" : " (not pinned)"));
            return true;
        }

        private static bool SameKey(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            var diff = 0;
            for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        /// <summary>Connects; <paramref name="pinnedServerKey"/> is the server's public key (64 hex characters, "host:port#key" in the menu) that must match.</summary>
        public void ConnectToServer(string ip, int port = 0, bool passProtected = false, string myPass = "", string pinnedServerKey = null)
        {
            Session = null;
            _authFailuresLogged = 0;
            _pinnedServerKey = null;
            if (!string.IsNullOrWhiteSpace(pinnedServerKey))
            {
                _pinnedServerKey = SessionHandshake.FromHex(pinnedServerKey, 32);
                if (_pinnedServerKey == null)
                {
                    Util.Util.SafeNotify("~r~The pinned server key is not 64 hex characters.");
                    LogManager.RuntimeLog("session: pinned key rejected (not 32 bytes of hex): " + pinnedServerKey);
                    return;
                }
            }
            _handshakeKey = KeyPair.Generate();
            if (IsOnServer())
            {
                Client.Disconnect("Switching servers");
                Wait(1000);
            }
            ShowLoadingPrompt("Loading");
            Function.Call(Hash.DISPLAY_RADAR, false);

            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());

            if (!_minimapSet)
            {
                var scal = new Scaleform("minimap");
                scal.CallFunction("MULTIPLAYER_IS_ACTIVE", true, false);

                Function.Call(Hash._SET_RADAR_BIGMAP_ENABLED, true, false);
                Function.Call(Hash._SET_RADAR_BIGMAP_ENABLED, false, false);

                _minimapSet = true;
            }

            Chat.Init();

            Client.Shutdown("Shutdown");
            Wait(1000);
            var cport = GetOpenUdpPort();
            if (cport == 0)
            {
                Util.Util.SafeNotify("No available UDP port was found.");
                return;
            }
            _config.Port = cport;
            Client = new NetClient(_config);
            Client.Start();

            lock (Npcs) Npcs = new Dictionary<string, SyncPed>();
            lock (_tickNatives) _tickNatives = new Dictionary<string, NativeData>();

            var msg = Client.CreateMessage();

            var obj = new ConnectionRequest();
            obj.SocialClubName = string.IsNullOrWhiteSpace(Game.Player.Name) ? "Unknown" : Game.Player.Name; // To be used as identifiers in server files
            obj.DisplayName = string.IsNullOrWhiteSpace(PlayerSettings.DisplayName) ? obj.SocialClubName : PlayerSettings.DisplayName.Trim();
            obj.ScriptVersion = CurrentVersion.ToString();
            obj.CEF = !CefUtil.DISABLE_CEF;
            obj.CEFDevtool = EnableDevTool;
            obj.GameVersion = (byte)Game.Version;
            obj.MediaStream = EnableMediaStream;
            obj.ClientPublicKey = _handshakeKey.PublicKey;
            obj.DlcPacks = Util.DlcPacks.Mounted();   // T-014: what the launcher applied for this session
            obj.Integrity = Util.Integrity.Report;     // T-017: hashes of the client's binaries (null until computed)

            if (passProtected)
            {
                if (!string.IsNullOrWhiteSpace(myPass))
                {
                    obj.Password = myPass;
                }
                else
                {
                    MainMenu.TemporarilyHidden = true;
                    obj.Password = Game.GetUserInput();
                    MainMenu.TemporarilyHidden = false;
                }
            }

            var bin = SerializeBinary(obj);

            msg.Write((byte)PacketType.ConnectionRequest);
            msg.Write(bin.Length);
            msg.Write(bin);

            try
            {
                Client.Connect(ip, port == 0 ? Port : port, msg);
            }
            catch (NetException ex)
            {
                GTA.UI.Screen.ShowNotification("~b~~h~GTA Network~h~~w~~n~" + ex.Message);
                OnLocalDisconnect();
                return;
            }

            var pos = Game.Player.Character.Position;
            Function.Call(Hash.CLEAR_AREA_OF_PEDS, pos.X, pos.Y, pos.Z, 100f, 0);
            Function.Call(Hash.CLEAR_AREA_OF_VEHICLES, pos.X, pos.Y, pos.Z, 100f, 0);

            Function.Call(Hash.SET_GARBAGE_TRUCKS, 0);
            Function.Call(Hash.SET_RANDOM_BOATS, 0);
            Function.Call(Hash.SET_RANDOM_TRAINS, 0);

            Function.Call(Hash.CLEAR_ALL_BROKEN_GLASS);

            DisableSlowMo();

            Game.TimeScale = 1;

            ResetPlayer();

            _currentServerIp = ip;
            _currentServerPort = port == 0 ? Port : port;
        }

        public static bool IsOnServer()
        {
            return Client?.ConnectionStatus == NetConnectionStatus.Connected;
        }

        private void OnLocalDisconnect()
        {
            Session = null;
            StopLoadingPrompt();
            ConnectLoader.Hide("disconnect");
            DEBUG_STEP = 42;
            if (NetEntityHandler.ServerWorld?.LoadedIpl != null)
            {
                foreach (var ipl in NetEntityHandler.ServerWorld.LoadedIpl)
                    Function.Call(Hash.REMOVE_IPL, ipl);
            }

            DEBUG_STEP = 43;
            if (NetEntityHandler.ServerWorld?.RemovedIpl != null)
            {
                foreach (var ipl in NetEntityHandler.ServerWorld.RemovedIpl)
                {
                    Function.Call(Hash.REQUEST_IPL, ipl);
                }
            }

            DEBUG_STEP = 44;

            ClearLocalEntities();

            DEBUG_STEP = 45;

            ClearLocalBlips();

            DEBUG_STEP = 49;
            CameraManager.Reset();
            NetEntityHandler.ClearAll();
            DEBUG_STEP = 50;
            JavascriptHook.StopAllScripts();
            JavascriptHook.TextElements.Clear();
            SyncCollector.ForceAimData = false;
            StringCache.Dispose();
            StringCache = null;
            _threadsafeSubtitle = null;
            CancelFileDownload();
            CefController.ShowCursor = false;
            DEBUG_STEP = 51;
            DownloadManager.Cancel();
            DownloadManager.FileIntegrity.Clear();
            Chat = _backupChat;
            Chat.Clear();
            WeaponInventoryManager.Clear();
            VehicleSyncManager.StopAll();
            HasFinishedDownloading = false;
            ScriptChatVisible = true;
            CanOpenChatbox = true;
            DisplayWastedMessage = true;
            _password = string.Empty;
            CEFManager.Draw = false;


            UIColor = Color.White;

            DEBUG_STEP = 52;

            lock (CEFManager.Browsers)
            {
                foreach (var browser in CEFManager.Browsers)
                {
                    browser.Close();
                    browser.Dispose();
                }

                CEFManager.Browsers.Clear();
            }

            CEFManager.Dispose();
            ClearStats();

            RestoreMainMenu();

            DEBUG_STEP = 56;

            ResetWorld();

            DEBUG_STEP = 57;

            ResetPlayer();

            DEBUG_STEP = 58;

            if (_serverProcess != null)
            {
                GTA.UI.Screen.ShowNotification("~b~~h~GTA Network~h~~w~~n~Shutting down server...");
                _serverProcess.Kill();
                _serverProcess.Dispose();
                _serverProcess = null;
            }
        }

        public static void SendToServer(object newData, PacketType packetType, bool important, ConnectionChannel channel)
        {
            var data = SerializeBinary(newData);
            NetOutgoingMessage msg = Client.CreateMessage();
            msg.Write((byte)packetType);
            msg.Write(data.Length);
            msg.Write(data);
            Send(msg, important ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.ReliableSequenced, (int)channel);
        }

        public static void TriggerServerEvent(string eventName, string resource, params object[] args)
        {
            if (!IsOnServer()) return;
            var packet = new ScriptEventTrigger();
            packet.EventName = eventName;
            packet.Resource = resource;
            packet.Arguments = ParseNativeArguments(args);
            var bin = SerializeBinary(packet);

            var msg = Client.CreateMessage();
            msg.Write((byte)PacketType.ScriptEventTrigger);
            msg.Write(bin.Length);
            msg.Write(bin);

            Send(msg, NetDeliveryMethod.ReliableOrdered);
        }

        /// <summary>An RpcRequest or RpcResponse to the server (T-008), reliable and ordered on the Rpc channel.</summary>
        internal static void SendRpc(PacketType type, object packet)
        {
            if (!IsOnServer()) return;
            var bin = SerializeBinary(packet);
            var msg = Client.CreateMessage();
            msg.Write((byte)type);
            msg.Write(bin.Length);
            msg.Write(bin);
            Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.Rpc);
        }


        public int GetOpenUdpPort()
        {


            var startingAtPort = 49152;
            var maxNumberOfPortsToCheck = 65535;
            var range = Enumerable.Range(startingAtPort, maxNumberOfPortsToCheck);
            var enumerable = range as IList<int> ?? range.ToList();
            var portsInUse =
                from p in enumerable
                join used in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveUdpListeners()
            on p equals used.Port
                select p;

            var inUse = portsInUse as IList<int> ?? portsInUse.ToList();

            Random rand = new Random();
            int toTake = rand.Next(0, inUse.Count);


            return enumerable.Except(inUse).ElementAtOrDefault(toTake);
        }

        public static void HandleUnoccupiedVehicleSync(VehicleData data)
        {
            if (data.VehicleHandle != null)
            {
                var car = NetEntityHandler.NetToStreamedItem(data.VehicleHandle.Value) as RemoteVehicle;

                if (car != null)
                {
                    if (data.VehicleHealth != null) car.Health = data.VehicleHealth.Value;
                    car.IsDead = (data.Flag & (int)VehicleDataFlags.VehicleDead) != 0;

                    if (car.DamageModel == null) car.DamageModel = new VehicleDamageModel();
                    car.DamageModel.BrokenWindows = data.DamageModel.BrokenWindows;
                    car.DamageModel.BrokenDoors = data.DamageModel.BrokenDoors;

                    if (data.PlayerHealth != null)
                    {
                        car.Tires = data.PlayerHealth.Value;

                        if (car.StreamedIn)
                        {
                            var ent = NetEntityHandler.NetToEntity(data.VehicleHandle.Value);

                            if (ent != null)
                            {
                                if (data.Velocity != null)
                                {
                                    VehicleSyncManager.Interpolate(data.VehicleHandle.Value, ent.Handle, data.Position.ToVector(), data.Velocity, data.Quaternion.ToVector());
                                }
                                else
                                {
                                    car.Position = data.Position;
                                    car.Rotation = data.Quaternion;
                                }

                                var veh = new Vehicle(ent.Handle);

                                veh.SetVehicleDamageModel(car.DamageModel);

                                veh.EngineHealth = car.Health;
                                if (!ent.IsDead && car.IsDead)
                                {
                                    ent.IsInvincible = false;
                                    veh.Explode();
                                }

                                for (int i = 0; i < 8; i++)
                                {
                                    bool busted = (data.PlayerHealth.Value & (byte)(1 << i)) != 0;
                                    if (busted && !veh.IsTireBurst(i)) veh.Wheels[i].Burst();
                                    else if (!busted && veh.IsTireBurst(i)) veh.Wheels[i].Fix();
                                }
                            }
                        }
                        else
                        {
                            car.Position = data.Position;
                            car.Rotation = data.Quaternion;
                        }
                    }
                }
            }
        }

        private static bool isIPLocal(string ipaddress)
        {
            try
            {
                var straryIpAddress = ipaddress.Split(new[] { "." }, StringSplitOptions.RemoveEmptyEntries);
                var iaryIpAddress = new[]
                {
                    int.Parse(straryIpAddress[0], CultureInfo.InvariantCulture),
                    int.Parse(straryIpAddress[1], CultureInfo.InvariantCulture),
                    int.Parse(straryIpAddress[2], CultureInfo.InvariantCulture),
                    int.Parse(straryIpAddress[3], CultureInfo.InvariantCulture)
                };


                return iaryIpAddress[0] == 10 || iaryIpAddress[0] == 127 || iaryIpAddress[0] == 192 && iaryIpAddress[1] == 168 || iaryIpAddress[0] == 172 && iaryIpAddress[1] >= 16 && iaryIpAddress[1] <= 31;

                // IP Address is "probably" public. This doesn't catch some VPN ranges like OpenVPN and Hamachi.
            }
            catch
            {
                return false;
            }
        }

    }
}
