using System;
using GTA;
using GTA.Math;
using GTA.Native;
using GTANetwork.Misc;
using GTANetwork.Util;
using GTANetworkShared;
using Lidgren.Network;
using Vector3 = GTA.Math.Vector3;

namespace GTANetwork.Streamer
{
    public partial class SyncCollector
    {
        private static bool _lastShooting;
        private static bool _lastBullet;
        private static DateTime _lastShot;
        private static bool _sent = true;

        private static void PedData(Ped player)
        {
            bool aiming = player.IsSubtaskActive(ESubtask.AIMED_SHOOTING_ON_FOOT) || player.IsSubtaskActive(ESubtask.AIMING_THROWABLE); // Game.IsControlPressed(GTA.Control.Aim);
            bool shooting = Function.Call<bool>(Hash.IS_PED_SHOOTING, player.Handle);

            // Each of these is a native call (a round trip to the game thread); read them once per frame.
            var parachuteState = Function.Call<int>(Hash.GET_PED_PARACHUTE_STATE, player.Handle);
            var usingLadder = player.IsSubtaskActive(ESubtask.USING_LADDER);
            var isRagdoll = player.IsRagdoll;
            var isReloading = player.IsReloading;
            var inMeleeCombat = player.IsInMeleeCombat;
            var meleeSubtask = player.IsSubtaskActive(ESubtask.MELEE_COMBAT);
            var aimingPrevented = player.IsSubtaskActive(ESubtask.AIMING_PREVENTED_BY_OBSTACLE);

            GTA.Math.Vector3 aimCoord = new Vector3();
            if (aiming || shooting)
            {
                aimCoord = Main.RaycastEverything(new Vector2(0, 0));
            }

            Weapon currentWeapon = player.Weapons.Current;

            var obj = new PedData
            {
                AimCoords = aimCoord.ToLVector(),
                Position = player.Position.ToLVector(),
                Quaternion = player.Rotation.ToLVector(),
                PedArmor = (byte) player.Armor,
                PedModelHash = player.Model.Hash,
                WeaponHash = (int)currentWeapon.Hash,
                WeaponAmmo = currentWeapon.Ammo,
                PlayerHealth = (byte) Util.Util.Clamp(0, player.Health, 255),
                Velocity = player.Velocity.ToLVector(),
                Flag = 0
            };


            if (isRagdoll)
                obj.Flag |= (int)PedDataFlags.Ragdoll;
            if (parachuteState == 0 &&
                player.IsInAir)
                obj.Flag |= (int)PedDataFlags.InFreefall;
            if (inMeleeCombat)
                obj.Flag |= (int)PedDataFlags.InMeleeCombat;
            if (aiming || shooting)
                obj.Flag |= (int)PedDataFlags.Aiming;
            if ((inMeleeCombat && Game.IsControlJustPressed(Control.Attack)))
                obj.Flag |= (int)PedDataFlags.Shooting;
            if (Function.Call<bool>(Hash.IS_PED_JUMPING, player.Handle))
                obj.Flag |= (int)PedDataFlags.Jumping;
            if (parachuteState == 2)
                obj.Flag |= (int)PedDataFlags.ParachuteOpen;
            if (player.IsInCover())
                obj.Flag |= (int)PedDataFlags.IsInCover;
            if (!Function.Call<bool>((Hash)0x6A03BF943D767C93, player))
                obj.Flag |= (int)PedDataFlags.IsInLowerCover;
            if (player.IsInCoverFacingLeft)
                obj.Flag |= (int)PedDataFlags.IsInCoverFacingLeft;
            if (isReloading)
                obj.Flag |= (int)PedDataFlags.IsReloading;
            if (ForceAimData)
                obj.Flag |= (int)PedDataFlags.HasAimData;
            if (usingLadder)
                obj.Flag |= (int)PedDataFlags.IsOnLadder;
            if (Function.Call<bool>(Hash.IS_PED_CLIMBING, player) && !usingLadder)
                obj.Flag |= (int)PedDataFlags.IsVaulting;
            if (Function.Call<bool>(Hash.IS_ENTITY_ON_FIRE, player))
                obj.Flag |= (int)PedDataFlags.OnFire;
            if (player.IsDead)
                obj.Flag |= (int)PedDataFlags.PlayerDead;

            if (player.IsSubtaskActive(168))
            {
                obj.Flag |= (int)PedDataFlags.ClosingVehicleDoor;
            }

            if (player.IsSubtaskActive(161) || player.IsSubtaskActive(162) || player.IsSubtaskActive(163) ||
                player.IsSubtaskActive(164))
            {
                obj.Flag |= (int)PedDataFlags.EnteringVehicle;

                obj.VehicleTryingToEnter =
                    Main.NetEntityHandler.EntityToNet(Function.Call<int>(Hash.GET_VEHICLE_PED_IS_TRYING_TO_ENTER,
                        player));

                obj.SeatTryingToEnter = (sbyte)
                    Function.Call<int>(Hash.GET_SEAT_PED_IS_TRYING_TO_ENTER,
                        player);
            }

            obj.Speed = Main.GetPedWalkingSpeed(player);

            lock (Lock)
            {
                LastSyncPacket = obj;
            }

            bool sendShootingPacket;

            if (obj.WeaponHash != null && !WeaponDataProvider.IsWeaponAutomatic(unchecked((GTANetworkShared.WeaponHash)obj.WeaponHash.Value)))
            {
                sendShootingPacket = (shooting && !aimingPrevented && !meleeSubtask);
            }
            else
            {
                if (!_lastShooting && !meleeSubtask)
                {
                    sendShootingPacket = (shooting && !aimingPrevented &&
                                          !meleeSubtask) ||
                                         ((inMeleeCombat || meleeSubtask) &&
                                          Game.IsEnabledControlPressed(Control.Attack));
                }
                else
                {
                    sendShootingPacket = (!aimingPrevented &&
                                          !meleeSubtask &&
                                          !isReloading &&
                                          currentWeapon.AmmoInClip > 0 &&
                                          Game.IsEnabledControlPressed(Control.Attack)) ||
                                         ((inMeleeCombat || meleeSubtask) &&
                                          Game.IsEnabledControlPressed(Control.Attack));
                }

                if (!sendShootingPacket && _lastShooting && !_lastBullet)
                {
                    _lastBullet = true;
                    _lastShooting = false;
                    return;
                }
            }

            _lastBullet = false;

            if (isRagdoll) sendShootingPacket = false;

            if (!meleeSubtask && currentWeapon.Ammo == 0) sendShootingPacket = false;

            if (sendShootingPacket && !_lastShooting && DateTime.Now.Subtract(_lastShot).TotalMilliseconds > 50)
            {
                //Util.Util.SafeNotify("Sending BPacket " + DateTime.Now.Millisecond);
                _sent = false;
                _lastShooting = true;
                _lastShot = DateTime.Now;

                var msg = Main.Client.CreateMessage();
                byte[] bin;

                var syncPlayer = Main.GetPedWeHaveDamaged();

                if (syncPlayer != null)
                {
                    bin = PacketOptimization.WriteBulletSync(0, true, syncPlayer.RemoteHandle);
                    msg.Write((byte)PacketType.BulletPlayerSync);
                }
                else
                {
                    bin = PacketOptimization.WriteBulletSync(0, true, aimCoord.ToLVector());
                    msg.Write((byte)PacketType.BulletSync);
                }

                msg.Write(bin.Length);
                msg.Write(bin);
                Main.Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.BulletSync);
                Main.BytesSent += bin.Length;
                Main.MessagesSent++;
            }
            else if (!sendShootingPacket && !_sent && DateTime.Now.Subtract(_lastShot).TotalMilliseconds > 50)
            {
                //Util.Util.SafeNotify("Sending NPacket " + DateTime.Now.Millisecond);
                _sent = true;
                _lastShooting = false;
                _lastShot = DateTime.Now;

                var msg = Main.Client.CreateMessage();

                byte[] bin = PacketOptimization.WriteBulletSync(0, false, aimCoord.ToLVector());
                msg.Write((byte)PacketType.BulletSync);

                msg.Write(bin.Length);
                msg.Write(bin);
                Main.Send(msg, NetDeliveryMethod.ReliableOrdered, (int)ConnectionChannel.BulletSync);
                Main.BytesSent += bin.Length;
                Main.MessagesSent++;
            }
        }
    }
}
