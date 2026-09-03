using System;
using System.Linq;
using GTANetworkServer;
using GTANetworkShared;

// Freeroam demo gamemode. Compiled by the server at startup (Roslyn); every public class deriving
// from Script is instantiated. Commands are plain methods with [Command]; the first parameter is the
// caller, the others are parsed from the chat line (numbers, enums such as VehicleHash, player names).
public class Freeroam : Script
{
    private static readonly Vector3[] Spawns =
    {
        new Vector3(-1037.7f, -2737.8f, 20.2f), // airport
        new Vector3(228.6f, -992.1f, 29.3f),    // downtown
        new Vector3(-1607.9f, -1032.4f, 13.0f), // Vespucci beach
        new Vector3(-75.0f, -818.9f, 326.2f),   // Maze Bank roof
    };

    private readonly Random _random = new Random();

    public Freeroam()
    {
        API.onResourceStart += OnResourceStart;
        API.onPlayerConnected += OnPlayerConnected;
        API.onPlayerDisconnected += OnPlayerDisconnected;
        API.onPlayerRespawn += Spawn;
        API.onClientEventTrigger += OnClientEvent;
    }

    private void OnResourceStart()
    {
        API.setTime(12, 0);
        API.setWeather(0);
        API.consoleOutput("Freeroam gamemode started. Commands: /help");
    }

    private void OnPlayerConnected(Client player)
    {
        API.sendChatMessageToAll("~g~" + player.name + " ~w~joined the server.");
        API.sendChatMessageToPlayer(player, "~b~Welcome to GTA Network freeroam!~w~ Type ~y~/help~w~ for the commands.");
        API.sendNotificationToPlayer(player, "~g~Welcome, " + player.name + "!");
        Spawn(player);
    }

    private void OnPlayerDisconnected(Client player, string reason)
    {
        API.sendChatMessageToAll("~y~" + player.name + " ~w~left the server (" + reason + ").");
    }

    private void OnClientEvent(Client sender, string eventName, params object[] arguments)
    {
        if (eventName == "freeroam:ping")
        {
            API.sendChatMessageToPlayer(sender, "~b~pong~w~ (" + API.getPlayerPing(sender) + " ms)");
        }
    }

    private void Spawn(Client player)
    {
        var spawn = Spawns[_random.Next(Spawns.Length)];
        API.setEntityPosition(player.handle, spawn);
        API.setPlayerHealth(player, 100);
        API.setPlayerArmor(player, 0);
    }

    [Command("help", Description = "List the freeroam commands")]
    public void Help(Client sender)
    {
        API.sendChatMessageToPlayer(sender, "~y~/veh [model]~w~ spawn a vehicle, e.g. /veh adder");
        API.sendChatMessageToPlayer(sender, "~y~/weapon [name] [ammo]~w~ e.g. /weapon carbinerifle 250, ~y~/weapons~w~ for a starter pack");
        API.sendChatMessageToPlayer(sender, "~y~/tp [x] [y] [z]~w~ teleport, ~y~/spawn~w~ respawn, ~y~/pos~w~ show position");
        API.sendChatMessageToPlayer(sender, "~y~/skin [model]~w~ e.g. /skin Franklin, ~y~/heal~w~, ~y~/fix~w~ repair vehicle");
        API.sendChatMessageToPlayer(sender, "~y~/time [hour]~w~, ~y~/weather [0-13]~w~, ~y~/players~w~, ~y~/shard [text]~w~, ~y~/kill");
    }

    [Command("veh", Alias = "car,vehicle", Description = "Spawn a vehicle and get in")]
    public void Veh(Client sender, VehicleHash model)
    {
        var position = sender.position;
        var rotation = API.getEntityRotation(sender.handle);

        var vehicle = API.createVehicle(model, new Vector3(position.X + 2.5f, position.Y, position.Z + 0.5f), new Vector3(0f, 0f, rotation.Z),
            _random.Next(0, 160), _random.Next(0, 160), sender.dimension);

        API.setPlayerIntoVehicle(sender, vehicle, -1);
        API.sendChatMessageToPlayer(sender, "~g~Spawned ~w~" + model + ".");
    }

    [Command("weapon", Alias = "gun", Description = "Give yourself a weapon")]
    public void Weapon(Client sender, WeaponHash weapon, int ammo = 500)
    {
        API.givePlayerWeapon(sender, weapon, ammo, true, true);
        API.sendChatMessageToPlayer(sender, "~g~Given ~w~" + weapon + " with " + ammo + " rounds.");
    }

    [Command("weapons", Description = "Starter weapon pack")]
    public void Weapons(Client sender)
    {
        foreach (var weapon in new[] { WeaponHash.Pistol, WeaponHash.MicroSMG, WeaponHash.CarbineRifle, WeaponHash.PumpShotgun, WeaponHash.SniperRifle })
        {
            API.givePlayerWeapon(sender, weapon, 250, false, true);
        }
        API.sendChatMessageToPlayer(sender, "~g~Starter pack given.");
    }

    [Command("tp", Description = "Teleport to coordinates")]
    public void Tp(Client sender, float x, float y, float z)
    {
        API.setEntityPosition(sender.handle, new Vector3(x, y, z));
    }

    [Command("spawn", Description = "Respawn at a random spawn point")]
    public void SpawnCommand(Client sender)
    {
        Spawn(sender);
    }

    [Command("pos", Description = "Show your position")]
    public void Pos(Client sender)
    {
        var p = sender.position;
        API.sendChatMessageToPlayer(sender, string.Format("~b~Position:~w~ {0:0.00}, {1:0.00}, {2:0.00}", p.X, p.Y, p.Z));
    }

    [Command("skin", Description = "Change your ped model")]
    public void Skin(Client sender, PedHash model)
    {
        API.setPlayerSkin(sender, model);
        API.setPlayerDefaultClothes(sender);
    }

    [Command("heal", Description = "Full health and armor")]
    public void Heal(Client sender)
    {
        API.setPlayerHealth(sender, 100);
        API.setPlayerArmor(sender, 100);
    }

    [Command("fix", Description = "Repair your vehicle")]
    public void Fix(Client sender)
    {
        if (!sender.isInVehicle)
        {
            API.sendChatMessageToPlayer(sender, "~r~You are not in a vehicle.");
            return;
        }

        API.repairVehicle(API.getPlayerVehicle(sender));
        API.sendChatMessageToPlayer(sender, "~g~Vehicle repaired.");
    }

    [Command("time", Description = "Set the hour of the day")]
    public void Time(Client sender, int hour)
    {
        API.setTime(Math.Max(0, Math.Min(23, hour)), 0);
    }

    [Command("weather", Description = "Set the weather (0-13)")]
    public void Weather(Client sender, int weather)
    {
        API.setWeather(Math.Max(0, Math.Min(13, weather)));
    }

    [Command("players", Description = "List online players")]
    public void Players(Client sender)
    {
        var players = API.getAllPlayers();
        API.sendChatMessageToPlayer(sender, "~b~Online (" + players.Count + "):~w~ " + string.Join(", ", players.Select(p => p.name)));
    }

    [Command("shard", GreedyArg = true, Description = "Show a big message (client script)")]
    public void Shard(Client sender, string text)
    {
        API.triggerClientEvent(sender, "freeroam:shard", text);
    }

    [Command("kill", Description = "Suicide")]
    public void Kill(Client sender)
    {
        API.setPlayerHealth(sender, -1);
    }
}
