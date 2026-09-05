using System.Globalization;
using Concentus;
using Concentus.Enums;

namespace GTANetwork.Bot;

/// <summary>Test audio for the voice protocol (T-015): a tone or a 16-bit PCM WAV, encoded into 20 ms Opus frames (48 kHz mono, 24 kbit/s).</summary>
internal static class VoiceTest
{
    public const int SampleRate = 48000;
    public const int FrameSamples = 960;   // 20 ms

    /// <summary>"5" = five seconds of a 440 Hz tone; anything else is a WAV file path.</summary>
    public static List<byte[]> Frames(string source)
    {
        short[] pcm = double.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? Tone(seconds) : ReadWav(source);
        var encoder = OpusCodecFactory.CreateEncoder(SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
        encoder.Bitrate = 24000;
        var frames = new List<byte[]>();
        var output = new byte[400];
        for (var offset = 0; offset + FrameSamples <= pcm.Length; offset += FrameSamples)
        {
            var length = encoder.Encode(pcm.AsSpan(offset, FrameSamples), FrameSamples, output.AsSpan(), output.Length);
            frames.Add(output.AsSpan(0, length).ToArray());
        }
        return frames;
    }

    private static short[] Tone(double seconds)
    {
        var samples = new short[(int)(SampleRate * Math.Max(0.02, seconds))];
        for (var i = 0; i < samples.Length; i++) samples[i] = (short)(Math.Sin(2 * Math.PI * 440 * i / SampleRate) * 12000);
        return samples;
    }

    /// <summary>A RIFF WAV with 16-bit PCM; stereo is mixed down, other sample rates are resampled linearly to 48 kHz.</summary>
    private static short[] ReadWav(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException(path + ": not a RIFF file");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException(path + ": not a WAVE file");
        int channels = 1, rate = SampleRate, bits = 16; byte[] data = null;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = new string(reader.ReadChars(4)); var size = reader.ReadInt32();
            if (id == "fmt ")
            {
                var format = reader.ReadInt16(); channels = reader.ReadInt16(); rate = reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16();
                reader.BaseStream.Seek(size - 16, SeekOrigin.Current);
                if (format != 1 || bits != 16) throw new InvalidDataException(path + ": only 16-bit PCM WAV is supported");
            }
            else if (id == "data") { data = reader.ReadBytes(size); break; }
            else reader.BaseStream.Seek(size + (size & 1), SeekOrigin.Current);
        }
        if (data == null) throw new InvalidDataException(path + ": no data chunk");
        var frames = data.Length / 2 / channels;
        var mono = new short[frames];
        for (var i = 0; i < frames; i++)
        {
            var sum = 0;
            for (var c = 0; c < channels; c++) sum += BitConverter.ToInt16(data, (i * channels + c) * 2);
            mono[i] = (short)(sum / channels);
        }
        if (rate == SampleRate) return mono;
        var resampled = new short[(long)mono.Length * SampleRate / rate];
        for (var i = 0; i < resampled.Length; i++)
        {
            var position = (double)i * rate / SampleRate;
            var index = (int)position; var fraction = position - index;
            var a = mono[Math.Min(index, mono.Length - 1)]; var b = mono[Math.Min(index + 1, mono.Length - 1)];
            resampled[i] = (short)(a + (b - a) * fraction);
        }
        return resampled;
    }
}
