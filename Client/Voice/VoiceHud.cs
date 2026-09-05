using System.Drawing;
using NativeUI;

namespace GTANetwork.Voice
{
    /// <summary>A HUD line while the push-to-talk key is held (T-027): "TALKING" when frames go out, the capture error when the microphone did not open.</summary>
    internal static class VoiceHud
    {
        private static int _lastFrames;
        private static long _lastFrameTick;

        public static void Draw()
        {
            if (!VoiceCapture.Talking) return;
            string text; Color color;
            var error = VoiceCapture.LastError;
            if (error != null && !VoiceCapture.IsOpen)
            {
                text = "MIC: " + error;
                color = Color.FromArgb(235, 230, 90, 90);
            }
            else
            {
                var frames = VoiceCapture.FramesSent;
                if (frames != _lastFrames) { _lastFrames = frames; _lastFrameTick = System.Environment.TickCount; }
                var sending = System.Environment.TickCount - _lastFrameTick < 500;
                text = sending ? "\u25CF TALKING" : (VoiceCapture.IsOpen ? "\u25CB MIC OPEN, no frames yet" : "\u25CB MIC opening...");
                color = sending ? Color.FromArgb(235, 90, 220, 120) : Color.FromArgb(235, 230, 200, 90);
            }
            new UIResText(text, new Point(640, 655), 0.45f, color, GTA.UI.Font.ChaletLondon, UIResText.Alignment.Centered) { Outline = true }.Draw(new Size());
        }
    }
}
