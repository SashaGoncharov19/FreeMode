// Freeroam, the reference gamemode, in TypeScript on the Bun runtime (runtime/main.ts; the engine starts Bun, this module
// gets one `gtan` per resource). gtan.api is the server API (Server/API.cs, typed by runtime/gtan/api.generated.d.ts: players
// and entities are handles, every call returns a Promise); gtan.players is the 10 Hz state mirror; gtan.commands handles
// "/name args"; gtan.on the engine's events; gtan.rpc the request/response calls of client scripts and CEF pages (T-008).
// The client half is client/index.ts (bundled by the server, T-005). The same messages as the C# version it replaces.
import type { Gtan } from "../../../../runtime/gtan";

const SPAWNS = [
  { x: -1037.7, y: -2737.8, z: 20.2 }, // airport
  { x: 228.6, y: -992.1, z: 29.3 },    // downtown
  { x: -1607.9, y: -1032.4, z: 13.0 }, // Vespucci beach
  { x: -75.0, y: -818.9, z: 326.2 },   // Maze Bank roof
];

const STARTER_WEAPONS = ["Pistol", "MicroSMG", "CarbineRifle", "PumpShotgun", "SniperRifle"];

export default function main(gtan: Gtan) {
  const { api, enums } = gtan;
  const say = (player: number, text: string) => void api.sendChatMessageToPlayer(player, text);
  const random = (max: number) => Math.floor(Math.random() * max);
  const clamp = (value: number, min: number, max: number) => Math.max(min, Math.min(max, value));

  async function nameOf(player: number): Promise<string> {
    return gtan.player(player)?.name || ((await api.getPlayerName(player)) as string) || "player";
  }

  function spawn(player: number) {
    const at = SPAWNS[random(SPAWNS.length)];
    void api.setEntityPosition(player, at);
    void api.setPlayerHealth(player, 100);
    void api.setPlayerArmor(player, 0);
  }

  // ---- lifecycle ----

  // "resourceStart": this resource's module is loaded (raised by the runtime); "serverResourceStart" is the engine-wide event for any resource.
  gtan.on("resourceStart", () => {
    void api.setTime(12, 0);
    void api.setWeather(0);
    void api.consoleOutput("Freeroam gamemode started (TypeScript). Commands: /help");
  });

  gtan.on("playerConnected", async (player: number) => {
    const name = await nameOf(player);
    void api.sendChatMessageToAll("~g~" + name + " ~w~joined the server.");
    say(player, "~b~Welcome to GTA Network freeroam!~w~ Type ~y~/help~w~ for the commands.");
    void api.sendNotificationToPlayer(player, "~g~Welcome, " + name + "!");
    spawn(player);
  });

  gtan.on("playerDisconnected", (player: number, reason: string) => {
    const name = gtan.player(player)?.name || "A player";
    void api.sendChatMessageToAll("~y~" + name + " ~w~left the server (" + reason + ").");
  });

  gtan.on("playerRespawn", (player: number) => spawn(player));

  // Sent by client/index.ts once the client-side script runs on that player's game.
  gtan.on("clientEventTrigger", (player: number, eventName: string) => {
    if (eventName === "freeroam:clientReady") say(player, "~g~Client-side script is running.");
  });

  // ---- RPC (T-008): client scripts call these with API.rpc.call; the bot test exercises them ----

  gtan.rpc.register("freeroam:ping", async (player, args) => ({ t: Date.now(), echo: args ?? null, player: await nameOf(player) }));
  gtan.rpc.register("freeroam:secret", () => "the secret is 42", {
    allow: async (player) => (await api.hasEntityData(player, "auth:account")) === true, // logged in through the auth resource
  });

  // ---- commands ----

  gtan.commands.register("help", (player) => {
    say(player, "~y~/veh [model]~w~ spawn a vehicle, e.g. /veh adder");
    say(player, "~y~/weapon [name] [ammo]~w~ e.g. /weapon carbinerifle 250, ~y~/weapons~w~ for a starter pack");
    say(player, "~y~/tp [x] [y] [z]~w~ teleport, ~y~/spawn~w~ respawn, ~y~/pos~w~ show position");
    say(player, "~y~/skin [model]~w~ e.g. /skin Franklin, ~y~/heal~w~, ~y~/fix~w~ repair vehicle");
    say(player, "~y~/time [hour]~w~, ~y~/weather [0-13]~w~, ~y~/players~w~, ~y~/shard [text]~w~, ~y~/ping~w~, ~y~/kill");
  });

  gtan.commands.register("ping", async (player) => {
    say(player, "~b~pong~w~ (" + (await api.getPlayerPing(player)) + " ms)");
  });

  gtan.commands.register("veh", async (player, modelText) => {
    const model = gtan.parseEnum(enums.VehicleHash, modelText);
    if (model === undefined) { say(player, "~y~USAGE: ~w~/veh [model], e.g. /veh adder"); return; }
    const position = gtan.player(player)?.position ?? ((await api.getEntityPosition(player)) as { x: number; y: number; z: number });
    const rotation = (await api.getEntityRotation(player)) as { x: number; y: number; z: number };
    const dimension = (await api.getEntityDimension(player)) as number;
    const vehicle = (await api.createVehicle(model, { x: position.x + 2.5, y: position.y, z: position.z + 0.5 }, { x: 0, y: 0, z: rotation.z },
      random(160), random(160), dimension)) as number;
    void api.setPlayerIntoVehicle(player, vehicle, -1);
    say(player, "~g~Spawned ~w~" + gtan.enumName(enums.VehicleHash, model) + ".");
  }, { aliases: ["car", "vehicle"] });

  gtan.commands.register("weapon", (player, weaponText, ammoText) => {
    const weapon = gtan.parseEnum(enums.WeaponHash, weaponText);
    if (weapon === undefined) { say(player, "~y~USAGE: ~w~/weapon [name] [ammo], e.g. /weapon carbinerifle 250"); return; }
    const ammo = ammoText === undefined ? 500 : Number.parseInt(ammoText, 10);
    if (!Number.isFinite(ammo)) { say(player, "~y~USAGE: ~w~/weapon [name] [ammo]: the ammo must be a number"); return; }
    void api.givePlayerWeapon(player, weapon, ammo, true, true);
    say(player, "~g~Given ~w~" + gtan.enumName(enums.WeaponHash, weapon) + " with " + ammo + " rounds.");
  }, { aliases: ["gun"] });

  gtan.commands.register("weapons", (player) => {
    for (const weapon of STARTER_WEAPONS) void api.givePlayerWeapon(player, enums.WeaponHash[weapon], 250, false, true);
    say(player, "~g~Starter pack given.");
  });

  gtan.commands.register("tp", (player, xText, yText, zText) => {
    const [x, y, z] = [xText, yText, zText].map((v) => Number.parseFloat(v));
    if (![x, y, z].every(Number.isFinite)) { say(player, "~y~USAGE: ~w~/tp [x] [y] [z]"); return; }
    void api.setEntityPosition(player, { x, y, z });
  });

  gtan.commands.register("spawn", (player) => spawn(player));

  gtan.commands.register("pos", async (player) => {
    const p = gtan.player(player)?.position ?? ((await api.getEntityPosition(player)) as { x: number; y: number; z: number });
    say(player, "~b~Position:~w~ " + p.x.toFixed(2) + ", " + p.y.toFixed(2) + ", " + p.z.toFixed(2));
  });

  gtan.commands.register("skin", (player, modelText) => {
    const model = gtan.parseEnum(enums.PedHash, modelText);
    if (model === undefined) { say(player, "~y~USAGE: ~w~/skin [model], e.g. /skin Franklin"); return; }
    void api.setPlayerSkin(player, model);
    void api.setPlayerDefaultClothes(player);
  });

  gtan.commands.register("heal", (player) => {
    void api.setPlayerHealth(player, 100);
    void api.setPlayerArmor(player, 100);
  });

  gtan.commands.register("fix", async (player) => {
    if (!(await api.isPlayerInAnyVehicle(player))) { say(player, "~r~You are not in a vehicle."); return; }
    void api.repairVehicle((await api.getPlayerVehicle(player)) as number);
    say(player, "~g~Vehicle repaired.");
  });

  gtan.commands.register("time", (player, hourText) => {
    const hour = Number.parseInt(hourText, 10);
    if (!Number.isFinite(hour)) { say(player, "~y~USAGE: ~w~/time [hour]"); return; }
    void api.setTime(clamp(hour, 0, 23), 0);
  });

  gtan.commands.register("weather", (player, weatherText) => {
    const weather = Number.parseInt(weatherText, 10);
    if (!Number.isFinite(weather)) { say(player, "~y~USAGE: ~w~/weather [0-13]"); return; }
    void api.setWeather(clamp(weather, 0, 13));
  });

  gtan.commands.register("players", async (player) => {
    const handles = (await api.getAllPlayers()) as number[];
    const names = await Promise.all(handles.map((h) => nameOf(h)));
    say(player, "~b~Online (" + handles.length + "):~w~ " + names.join(", "));
  });

  gtan.commands.register("shard", (player, ...words) => {
    void api.triggerClientEvent(player, "freeroam:shard", words.join(" "));
  });

  gtan.commands.register("kill", (player) => {
    void api.setPlayerHealth(player, -1);
  });
}
