using System;
using System.Windows.Forms;

namespace GTANetwork.Voice
{
    /// <summary>The push-to-talk key from settings.xml (VoiceKey), parsed once per change.</summary>
    internal static class VoiceKeys
    {
        private static string _parsedFrom;
        private static Keys _key = Keys.N;

        public static Keys PushToTalk
        {
            get
            {
                var text = Main.PlayerSettings?.VoiceKey;
                if (text != _parsedFrom)
                {
                    _parsedFrom = text;
                    Keys parsed;
                    _key = !string.IsNullOrWhiteSpace(text) && Enum.TryParse(text.Trim(), true, out parsed) ? parsed : Keys.N;
                }
                return _key;
            }
        }
    }
}
