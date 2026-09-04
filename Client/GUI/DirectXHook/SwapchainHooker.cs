using System;
using GTA;
using GTANetwork.Util;

namespace GTANetwork.GUI.DirectXHook
{
    public class SwapchainHooker : Script
    {
        public SwapchainHooker()
        {
            if (CefUtil.DISABLE_CEF) return;

            var hooked = false;

            var failures = 0;

            // Runs on the game's render thread inside the Present of the game: nothing may escape from here.
            Present += (sender, args) =>
            {
                try
                {
                    if (!CEFManager.Draw) return;

                    var menu = Main.MainMenu;
                    var warning = Main._mainWarning;
                    if ((menu != null && menu.Visible) || (warning != null && warning.Visible)) return;

                    CEFManager.DirectXHook?.ManualPresentHook((IntPtr)sender);
                }
                catch (Exception ex)
                {
                    if (failures++ < 5) LogManager.LogException(ex, "PRESENT (CEF overlay)");
                }
            };

            Tick += (sender, args) =>
            {
                if (!hooked)
                {
                    base.AttachD3DHook();
                    hooked = true;
                }
            };
        }
    }
}