using System;
using GTANetworkServer;
using GTANetworkShared;

// Compiled at runtime by the server (Roslyn). Every public class deriving from Script is instantiated.
public class ExampleGamemode : Script
{
    public ExampleGamemode()
    {
        API.onResourceStart += OnResourceStart;
        API.onPlayerConnected += OnPlayerConnected;
    }

    private void OnResourceStart()
    {
        API.consoleOutput("Example gamemode started on " + Environment.OSVersion + " (" + Environment.Version + ")");
    }

    private void OnPlayerConnected(Client player)
    {
        API.sendChatMessageToPlayer(player, "~g~Welcome to GTA Network, " + player.name + "!");
        API.sendNotificationToPlayer(player, "Type ~b~/hello");
    }

    [Command("hello")]
    public void HelloCommand(Client sender)
    {
        API.sendChatMessageToPlayer(sender, "Hello, " + sender.name + "!");
    }
}
