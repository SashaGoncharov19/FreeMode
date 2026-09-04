using System;
using System.Collections.Generic;
using System.Linq;
using GTA;
using GTANetwork.Util;
using WeaponHash = GTANetworkShared.WeaponHash;

namespace GTANetwork.Streamer
{
    public class WeaponManager : Script
    {

        public WeaponManager()
        {
            Tick += OnTick;
        }

        private static void OnTick(object sender, EventArgs e)
        {
            if (Main.IsConnected())
            {
                Update();
            }
        }

        private static List<WeaponHash> _playerInventory = new List<WeaponHash>
        {
            WeaponHash.Unarmed
        };

        public void Clear()
        {
            _playerInventory.Clear();
            _playerInventory.Add(WeaponHash.Unarmed);
        }

        private static readonly WeaponHash[] AllWeapons =
            Enum.GetValues(typeof(WeaponHash)).Cast<WeaponHash>().Where(h => h != WeaponHash.Unarmed).ToArray();

        // Removing every weapon hash in one go costs ~100 native calls (25-30 ms of game thread every 500 ms, a
        // visible stutter). Sweep a slice per frame instead: the whole list is still covered several times a second.
        private const int HashesPerUpdate = 8;
        private static int _sweepIndex;

        internal static void Update()
        {
            if (AllWeapons.Length == 0) return;

            var player = Game.Player.Character;
            var end = Math.Min(_sweepIndex + HashesPerUpdate, AllWeapons.Length);

            for (var i = _sweepIndex; i < end; i++)
            {
                var hash = AllWeapons[i];
                if (!_playerInventory.Contains(hash))
                {
                    player.Weapons.Remove((GTA.WeaponHash)(int)hash);
                }
            }

            _sweepIndex = end >= AllWeapons.Length ? 0 : end;
        }

        public void Allow(WeaponHash hash)
        {
            if (!_playerInventory.Contains(hash)) _playerInventory.Add(hash);
        }

        public void Deny(WeaponHash hash)
        {
            _playerInventory.Remove(hash);
        }
    }
}