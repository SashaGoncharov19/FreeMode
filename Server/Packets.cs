using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using GTANetworkServer.Constant;
using GTANetworkServer.Managers;
using GTANetworkShared;
using Lidgren.Network;
using Newtonsoft.Json;
using ProtoBuf;

namespace GTANetworkServer
{
    internal partial class GameServer
    {
        public static void UpdateEntityInfo(int netId, EntityType entity, Delta_EntityProperties newInfo, Client exclude = null)
        {
            var packet = new UpdateEntity
            {
                EntityType = (byte)entity,
                Properties = newInfo,
                NetHandle = netId
            };
            if (exclude == null)
                Program.ServerInstance.SendToAll(packet, PacketType.UpdateEntityProperties, true, ConnectionChannel.EntityBackend);
            else
                Program.ServerInstance.SendToAll(packet, PacketType.UpdateEntityProperties, true, exclude, ConnectionChannel.EntityBackend);
        }


        // Recipients of one player's sync packets. Near players (see Managers/Streamer) get the full packet,
        // far players get one position-only packet per second per sender.
        private static bool CanReceive(Client client, Client sender)
        {
            if (client == null || client.Fake || client.NetConnection == null) return false;
            if (client.NetConnection.Status == NetConnectionStatus.Disconnected) return false;
            if (!client.ConnectionConfirmed) return false;
            return client != sender;
        }

        private static List<NetConnection> CollectNear(Client sender, bool requirePosition)
        {
            var connections = new List<NetConnection>();

            foreach (var client in sender.Streamer.GetNearClients())
            {
                if (!CanReceive(client, sender)) continue;
                if (requirePosition && client.Position == null) continue;
                connections.Add(client.NetConnection);
            }

            return connections;
        }

        private static List<NetConnection> CollectFar(Client sender)
        {
            var connections = new List<NetConnection>();
            var now = Program.MonotonicMs();

            foreach (var client in sender.Streamer.GetFarClients())
            {
                if (!CanReceive(client, sender)) continue;

                lock (client.LastPacketReceived)
                {
                    long last;
                    if (client.LastPacketReceived.TryGetValue(sender.handle.Value, out last) && now - last <= 1000) continue;
                    client.LastPacketReceived[sender.handle.Value] = now;
                }

                connections.Add(client.NetConnection);
            }

            return connections;
        }

        private void SendBasicSync(int netHandle, Vector3 position, List<NetConnection> connections)
        {
            var basic = PacketOptimization.WriteBasicSync(netHandle, position);

            var msg = Server.CreateMessage();
            msg.Write((byte)PacketType.BasicSync);
            msg.Write(basic.Length);
            msg.Write(basic);

            Server.SendMessage(msg, connections, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.BasicSync);
        }

        //Ped Packet
        internal void ResendPacket(PedData fullPacket, Client exception, bool pure)
        {
            var full = pure ? PacketOptimization.WritePureSync(fullPacket) : PacketOptimization.WriteLightSync(fullPacket);

            var connectionsNear = CollectNear(exception, pure);
            if (connectionsNear.Count > 0)
            {
                var msg = Server.CreateMessage();
                msg.Write((byte)(pure ? PacketType.PedPureSync : PacketType.PedLightSync));
                msg.Write(full.Length);
                msg.Write(full);

                if (pure) Server.SendMessage(msg, connectionsNear, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.PureSync);
                else Server.SendMessage(msg, connectionsNear, NetDeliveryMethod.ReliableSequenced, (int)ConnectionChannel.LightSync);
            }

            if (!pure || fullPacket.NetHandle == null || fullPacket.Position == null) return;

            var connectionsFar = CollectFar(exception);
            if (connectionsFar.Count > 0) SendBasicSync(fullPacket.NetHandle.Value, fullPacket.Position, connectionsFar);
        }

        //Vehicle Packet
        internal void ResendPacket(VehicleData fullPacket, Client exception, bool pure)
        {
            var full = pure ? PacketOptimization.WritePureSync(fullPacket) : PacketOptimization.WriteLightSync(fullPacket);

            var connectionsNear = CollectNear(exception, pure);
            if (connectionsNear.Count > 0)
            {
                var msg = Server.CreateMessage();
                msg.Write((byte)(pure ? PacketType.VehiclePureSync : PacketType.VehicleLightSync));
                msg.Write(full.Length);
                msg.Write(full);

                if (pure) Server.SendMessage(msg, connectionsNear, NetDeliveryMethod.UnreliableSequenced, (int)ConnectionChannel.PureSync);
                else Server.SendMessage(msg, connectionsNear, NetDeliveryMethod.ReliableSequenced, (int)ConnectionChannel.LightSync);
            }

            if (!pure || fullPacket.NetHandle == null) return;

            var connectionsFar = CollectFar(exception);
            if (connectionsFar.Count == 0) return;

            // Passengers carry no position of their own; use the vehicle's last known one.
            Vector3 position = null;
            if (fullPacket.Flag != null && PacketOptimization.CheckBit(fullPacket.Flag.Value, VehicleDataFlags.Driver))
            {
                position = fullPacket.Position;
            }
            else if (!exception.CurrentVehicle.IsNull)
            {
                EntityProperties vehicle;
                if (NetEntityHandler.ToDict().TryGetValue(exception.CurrentVehicle.Value, out vehicle)) position = vehicle.Position;
            }

            if (position != null) SendBasicSync(fullPacket.NetHandle.Value, position, connectionsFar);
        }

        internal bool CheckUnoccupiedTrailerDriver(Client player, NetHandle vehicle)
        {
            if (vehicle.IsNull)
            {
                Program.Output("NULL CATCH");
                return false;
            }


            NetHandle traileredBy = Program.ServerInstance.PublicAPI.getVehicleTraileredBy(vehicle);

            if (traileredBy != null)
            {
                Program.Output("TRAILERED");
                return Program.ServerInstance.PublicAPI.getVehicleDriver(traileredBy) == player;
            }

            Program.Output("NOT TRAILERED");
            return false;
        }

        internal void ResendUnoccupiedPacket(VehicleData fullPacket, Client exception)
        {
            if (fullPacket.NetHandle == null) return;

            var vehicleEntity = new NetHandle(fullPacket.NetHandle.Value);
            var full = PacketOptimization.WriteUnOccupiedVehicleSync(fullPacket);
            var basic = PacketOptimization.WriteBasicUnOccupiedVehicleSync(fullPacket);

            var msgNear = Server.CreateMessage();
            msgNear.Write((byte)PacketType.UnoccupiedVehSync);
            msgNear.Write(full.Length);
            msgNear.Write(full);

            var msgFar = Server.CreateMessage();
            msgFar.Write((byte)PacketType.BasicUnoccupiedVehSync);
            msgFar.Write(basic.Length);
            msgFar.Write(basic);

            List<NetConnection> connectionsNear = new List<NetConnection>();
            List<NetConnection> connectionsFar = new List<NetConnection>();

            foreach (var client in exception.Streamer.GetNearClients())
            {
                if (!CanReceive(client, exception)) continue;
                // skip sending a sync packet for a trailer to it's owner.
                if (CheckUnoccupiedTrailerDriver(client, vehicleEntity)) continue;

                if (client.Position == null) continue;
                if (client.Position.DistanceToSquared(fullPacket.Position) < 20000)
                {
                    connectionsNear.Add(client.NetConnection);
                }
                else
                {
                    connectionsFar.Add(client.NetConnection);
                }
            }

            if (connectionsNear.Count > 0) Server.SendMessage(msgNear, connectionsNear,
                NetDeliveryMethod.UnreliableSequenced,
                (int)ConnectionChannel.UnoccupiedVeh);

            foreach (var client in exception.Streamer.GetFarClients())
            {
                if (!CanReceive(client, exception)) continue;
                connectionsFar.Add(client.NetConnection);
            }

            if (connectionsFar.Count > 0) Server.SendMessage(msgFar, connectionsFar,
                NetDeliveryMethod.UnreliableSequenced,
                (int)ConnectionChannel.UnoccupiedVeh);
        }


        internal void ResendBulletPacket(int netHandle, Vector3 aim, bool shooting, Client exception)
        {
            var full = PacketOptimization.WriteBulletSync(netHandle, shooting, aim);

            var msg = Server.CreateMessage();
            msg.Write((byte)PacketType.BulletSync);
            msg.Write(full.Length);
            msg.Write(full);

            List<NetConnection> connections = new List<NetConnection>();

            foreach (var client in exception.Streamer.GetNearClients())
            {
                if (!CanReceive(client, exception)) continue;
                //if (range && client.Position.DistanceToSquared(exception.Position) > 80000) continue;

                connections.Add(client.NetConnection);
            }

            if (connections.Count > 0) Server.SendMessage(msg, connections,
                NetDeliveryMethod.ReliableSequenced,
                (int)ConnectionChannel.BulletSync);
        }

        internal void ResendBulletPacket(int netHandle, int netHandleTarget, bool shooting, Client exception)
        {
            var full = PacketOptimization.WriteBulletSync(netHandle, shooting, netHandleTarget);

            var msg = Server.CreateMessage();
            msg.Write((byte)PacketType.BulletPlayerSync);
            msg.Write(full.Length);
            msg.Write(full);

            List<NetConnection> connections = new List<NetConnection>();

            foreach (var client in exception.Streamer.GetNearClients())
            {
                if (!CanReceive(client, exception)) continue;
                //if (range && client.Position.DistanceToSquared(exception.Position) > 80000) continue; 

                connections.Add(client.NetConnection);
            }

            if (connections.Count > 0) Server.SendMessage(msg, connections, NetDeliveryMethod.ReliableSequenced, (int)ConnectionChannel.BulletSync);
        }



        public void SendToClient(Client c, object newData, PacketType packetType, bool important, ConnectionChannel channel)
        {
            var data = SerializeBinary(newData);
            var msg = Server.CreateMessage();
            msg.Write((byte)packetType);
            msg.Write(data.Length);
            msg.Write(data);
            Server.SendMessage(msg, c.NetConnection, important ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.UnreliableSequenced, (int)channel);
        }

        public void SendToAll(object newData, PacketType packetType, bool important, ConnectionChannel channel)
        {
            var data = SerializeBinary(newData);
            var msg = Server.CreateMessage();
            msg.Write((byte)packetType);
            msg.Write(data.Length);
            msg.Write(data);

            Server.SendToAll(msg, null, important ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.ReliableSequenced, (int)channel);
        }

        public void SendToAll(object newData, PacketType packetType, bool important, Client exclude, ConnectionChannel channel)
        {
            var data = SerializeBinary(newData);
            var msg = Server.CreateMessage();
            msg.Write((byte)packetType);
            msg.Write(data.Length);
            msg.Write(data);

            Server.SendToAll(msg, exclude.NetConnection, important ? NetDeliveryMethod.ReliableOrdered : NetDeliveryMethod.ReliableSequenced, (int)channel);
        }

        public void SendDeleteObject(Client player, Vector3 pos, float radius, int modelHash)
        {
            var obj = new ObjectData
            {
                Position = pos,
                Radius = radius,
                modelHash = modelHash
            };
            var bin = SerializeBinary(obj);

            var msg = Server.CreateMessage();
            msg.Write((byte)PacketType.DeleteObject);
            msg.Write(bin.Length);
            msg.Write(bin);
            player.NetConnection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.EntityBackend);
        }


        public void SendNativeCallToPlayer(Client player, bool safe, ulong hash, params object[] arguments)
        {
            var obj = new NativeData
            {
                Hash = hash,
                Internal = safe,
                Arguments = ParseNativeArguments(arguments)
            };
            var bin = SerializeBinary(obj);

            var msg = Server.CreateMessage();
            msg.Write((byte)PacketType.NativeCall);
            msg.Write(bin.Length);
            msg.Write(bin);
            player.NetConnection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.NativeCall);
        }

        public void SendNativeCallToAllPlayers(bool safe, ulong hash, params object[] arguments)
        {
            var obj = new NativeData
            {
                Hash = hash,
                Internal = safe,
                Arguments = ParseNativeArguments(arguments),
                ReturnType = null,
                Id = 0
            };

            var bin = SerializeBinary(obj);

            var msg = Server.CreateMessage();

            msg.Write((byte)PacketType.NativeCall);
            msg.Write(bin.Length);
            msg.Write(bin);

            // A NetOutgoingMessage may only be handed to Lidgren once; sending it per client in a loop threw
            // "This message has already been sent!" as soon as two players were online. Send to the list instead.
            List<NetConnection> recipients;
            lock (Clients)
            {
                recipients = Clients
                    .Where(c => !c.Fake && c.NetConnection != null && c.NetConnection.Status != NetConnectionStatus.Disconnected)
                    .Select(c => c.NetConnection)
                    .ToList();
            }

            if (recipients.Count > 0)
            {
                Server.SendMessage(msg, recipients, NetDeliveryMethod.ReliableOrdered, (int) ConnectionChannel.NativeCall);
            }
        }

        private Dictionary<uint, Action<object>> _callbacks = new Dictionary<uint, Action<object>>();
        public void GetNativeCallFromPlayer(Client player, bool safe, uint salt, ulong hash, NativeArgument returnType, Action<object> callback, params object[] arguments)
        {
            var obj = new NativeData
            {
                Hash = hash,
                ReturnType = returnType,
                Id = salt,
                Arguments = ParseNativeArguments(arguments),
                Internal = safe
            };

            var bin = SerializeBinary(obj);

            var msg = Server.CreateMessage();

            msg.Write((byte)PacketType.NativeCall);
            msg.Write(bin.Length);
            msg.Write(bin);

            _callbacks.Add(salt, callback);
            player.NetConnection.SendMessage(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.NativeCall);
        }


        public void ChangePlayerTeam(Client target, int newTeam)
        {
            if (NetEntityHandler.ToDict().ContainsKey(target.handle.Value))
            {
                ((PlayerProperties)NetEntityHandler.ToDict()[target.handle.Value]).Team = newTeam;
            }

            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerTeamChange,
                Arguments = ParseNativeArguments(target.handle.Value, newTeam)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipColor(Client target, int newColor)
        {
            if (NetEntityHandler.ToDict().ContainsKey(target.handle.Value))
            {
                ((PlayerProperties)NetEntityHandler.ToDict()[target.handle.Value]).BlipColor = newColor;
            }

            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipColorChange,
                Arguments = ParseNativeArguments(target.handle.Value, newColor)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipColorForPlayer(Client target, int newColor, Client forPlayer)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipColorChange,
                Arguments = ParseNativeArguments(target.handle.Value, newColor)
            };

            SendToClient(forPlayer, obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipSprite(Client target, int newSprite)
        {
            if (NetEntityHandler.ToDict().ContainsKey(target.handle.Value))
            {
                ((PlayerProperties)NetEntityHandler.ToDict()[target.handle.Value]).BlipSprite = newSprite;
            }

            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipSpriteChange,
                Arguments = ParseNativeArguments(target.handle.Value, newSprite)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipSpriteForPlayer(Client target, int newSprite, Client forPlayer)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipSpriteChange,
                Arguments = ParseNativeArguments(target.handle.Value, newSprite)
            };

            SendToClient(forPlayer, obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipAlpha(Client target, int newAlpha)
        {
            if (NetEntityHandler.ToDict().ContainsKey(target.handle.Value))
            {
                ((PlayerProperties)NetEntityHandler.ToDict()[target.handle.Value]).BlipAlpha = (byte)newAlpha;
            }

            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipAlphaChange,
                Arguments = ParseNativeArguments(target.handle.Value, newAlpha)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void ChangePlayerBlipAlphaForPlayer(Client target, int newAlpha, Client forPlayer)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerBlipAlphaChange,
                Arguments = ParseNativeArguments(target.handle.Value, newAlpha)
            };

            SendToClient(forPlayer, obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }


        public void SendServerEvent(ServerEventType type, params object[] arg)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)type,
                Arguments = ParseNativeArguments(arg)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void SendServerEventToPlayer(Client target, ServerEventType type, params object[] arg)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)type,
                Arguments = ParseNativeArguments(arg)
            };

            SendToClient(target, obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }


        public void DetachEntity(int nethandle, bool collision)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.EntityDetachment,
                Arguments = ParseNativeArguments(nethandle, collision)
            };
            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void SetPlayerOnSpectate(Client target, bool spectating)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerSpectatorChange,
                Arguments = ParseNativeArguments(target.handle.Value, spectating)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void SetPlayerOnSpectatePlayer(Client spectator, Client target)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerSpectatorChange,
                Arguments = ParseNativeArguments(spectator.handle.Value, true, target.handle.Value)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }


        public void PlayCustomPlayerAnimation(Client target, int flag, string animDict, string animName)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerAnimationStart,
                Arguments = ParseNativeArguments(target.handle.Value, flag, animDict, animName)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void PlayCustomPlayerAnimationStop(Client target)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerAnimationStop,
                Arguments = ParseNativeArguments(target.handle.Value)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }

        public void PlayCustomPlayerAnimationStop(Client target, string animDict, string animName)
        {
            var obj = new SyncEvent
            {
                EventType = (byte)ServerEventType.PlayerAnimationStop,
                Arguments = ParseNativeArguments(target.handle.Value, animDict, animName)
            };

            SendToAll(obj, PacketType.ServerEvent, true, ConnectionChannel.EntityBackend);
        }
    }
}
