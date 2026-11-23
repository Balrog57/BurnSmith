using System.Buffers.Binary;

namespace BurnSmith.Core.Menu;

/// <summary>
/// Synthesizes 48 kHz 16-bit stereo PCM navigation/activation sound effects and encodes standard Blu-ray <c>sound.bdmv</c> (<c>BCLK0200</c>) and WAV streams.
/// </summary>
public static class SoundBank
{
    public const int SampleRate = 48000;
    public static readonly (string Value, string Label)[] Packs =
    [
        ("none", "Aucun"),
        ("discret", "Discret"),
        ("cinema", "Cinéma"),
        ("retro", "Rétro 8-bit"),
        ("classic", "Lecteur classique"),
        ("custom", "WAV personnalisés…"),
    ];

    /// <summary>Returns interleaved stereo 16-bit signed PCM samples (little-endian int16 pairs).</summary>
    static short[] SynthesizeSamples(MenuSoundSettings settings, bool isSelect)
    {
        var pack = (settings.Pack ?? "none").Trim().ToLowerInvariant();
        if (pack == "none") return [];

        if (pack == "custom")
        {
            var wavFile = isSelect ? settings.SelectWav : settings.MoveWav;
            if (!string.IsNullOrWhiteSpace(wavFile) && File.Exists(wavFile))
                return LoadExternalPcm(wavFile, settings.Volume);
            pack = "discret";
        }

        var durSec = (pack, isSelect) switch
        {
            ("discret", false) => 0.035,
            ("discret", true) => 0.060,
            ("cinema", false) => 0.048,
            ("cinema", true) => 0.105,
            ("retro", false) => 0.032,
            ("retro", true) => 0.085,
            ("classic", false) => 0.028,
            ("classic", true) => 0.065,
            _ => 0.040,
        };

        var frames = (int)(SampleRate * durSec);
        var buf = new short[frames * 2];
        var vol = Math.Clamp(settings.Volume, 0, 100) / 100.0;

        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)SampleRate;
            var u = i / (double)frames;
            // Smooth 3 ms fade-in + exponential/cosine decay to prevent zero-crossing clicks
            var env = (i < 144 ? i / 144.0 : 1.0) * Math.Pow(1.0 - u, 1.4);
            double sample = (pack, isSelect) switch
            {
                ("discret", false) => 0.28 * Math.Sin(2 * Math.PI * 820 * t),
                ("discret", true) => 0.34 * (Math.Sin(2 * Math.PI * (u < 0.45 ? 1100 : 1480) * t) + 0.35 * Math.Sin(2 * Math.PI * 2200 * t)),
                ("cinema", false) => 0.36 * (2 * Math.Abs(2 * (t * 210 - Math.Floor(t * 210 + 0.5))) - 1) + 0.14 * Math.Sin(2 * Math.PI * 420 * t),
                ("cinema", true) => 0.32 * Math.Sin(2 * Math.PI * 440 * t) + 0.24 * Math.Sin(2 * Math.PI * 660 * t) + 0.15 * Math.Sin(2 * Math.PI * 880 * t),
                ("retro", false) => 0.22 * Math.Sign(Math.Sin(2 * Math.PI * 660 * t)),
                ("retro", true) => 0.24 * Math.Sign(Math.Sin(2 * Math.PI * (u < 0.48 ? 880 : 1320) * t)),
                ("classic", false) => 0.25 * Math.Sin(2 * Math.PI * 1200 * t),
                ("classic", true) => 0.30 * Math.Sin(2 * Math.PI * (1500 + 300 * u) * t),
                _ => 0,
            };
            var s = (short)Math.Clamp(Math.Round(sample * env * vol * 32767.0), short.MinValue, short.MaxValue);
            buf[i * 2] = s;
            buf[i * 2 + 1] = s;
        }
        return buf;
    }

    static short[] LoadExternalPcm(string path, int volume)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"bs_snd_{Guid.NewGuid():N}.raw");
        try
        {
            var vol = (Math.Clamp(volume, 0, 100) / 100.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-y", "-v", "error", "-i", path, "-t", "1.5", "-af", $"volume={vol}", "-f", "s16le", "-ac", "2", "-ar", SampleRate.ToString(), tmp]);
            var raw = File.ReadAllBytes(tmp);
            var samples = new short[raw.Length / 2];
            for (var i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(raw.AsSpan(i * 2, 2));
            return samples;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FileNotFoundException)
        {
            return [];
        }
        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } }
    }

    /// <summary>Builds a standard RIFF WAV (48 kHz, 16-bit, stereo) for UI sound preview and BD-J <c>HSound</c>.</summary>
    public static byte[] BuildWav(MenuSoundSettings settings, bool isSelect)
    {
        var samples = SynthesizeSamples(settings, isSelect);
        var dataBytes = samples.Length * 2;
        var wav = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), 36 + dataBytes);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 2); // Stereo
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), SampleRate * 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 4);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), dataBytes);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(44 + i * 2), samples[i]);
        return wav;
    }

    /// <summary>Encodes Blu-ray <c>BDMV/AUXDATA/sound.bdmv</c> (<c>BCLK0200</c>) containing sound 0 (move) and sound 1 (select).</summary>
    public static byte[] BuildSoundBdmv(MenuSoundSettings settings)
    {
        var move = SynthesizeSamples(settings, false);
        var select = SynthesizeSamples(settings, true);
        if (move.Length == 0) move = new short[960];
        if (select.Length == 0) select = new short[960];

        static byte[] ToBigEndianPcm(short[] src)
        {
            var bytes = new byte[src.Length * 2];
            for (var i = 0; i < src.Length; i++)
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), src[i]);
            return bytes;
        }

        var pcm0 = ToBigEndianPcm(move);
        var pcm1 = ToBigEndianPcm(select);

        // BCLK0200 header (40 bytes) + SoundIndex + 4-byte padded SoundData block
        const int HeaderLen = 40;
        const int EntryCount = 2;
        const int IndexPayloadLen = 2 + EntryCount * 12; // reserved + count + entries
        const int RawDataStart = HeaderLen + 4 + IndexPayloadLen; // 40 + 4 + 26 = 70
        const int SoundDataStart = (RawDataStart + 3) & ~3; // 72 (4-byte aligned)

        var sound0Offset = 4; // relative to SoundData start (after 4-byte length field)
        var sound1Offset = (sound0Offset + pcm0.Length + 3) & ~3;
        var soundDataTotalPayload = sound1Offset - 4 + pcm1.Length;
        var fileLen = SoundDataStart + 4 + soundDataTotalPayload;
        var buf = new byte[fileLen];

        "BCLK0200"u8.CopyTo(buf);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(8), (uint)SoundDataStart);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(12), 0); // extension_data_start_address

        // SoundIndex at offset 40
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(HeaderLen), (uint)IndexPayloadLen);
        buf[HeaderLen + 4] = 0;
        buf[HeaderLen + 5] = EntryCount;

        void WriteEntry(int idx, int dataRelOffset, int totalSamples)
        {
            var at = HeaderLen + 6 + idx * 12;
            buf[at] = 0x31; // channel_config=3 (stereo), sample_rate=1 (48 kHz)
            buf[at + 1] = 0x40; // bits_per_sample=1 (16-bit)
            buf[at + 2] = 0;
            buf[at + 3] = 0;
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 4), (uint)dataRelOffset);
            BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(at + 8), (uint)totalSamples);
        }

        WriteEntry(0, sound0Offset, move.Length / 2);
        WriteEntry(1, sound1Offset, select.Length / 2);

        // SoundData at SoundDataStart
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(SoundDataStart), (uint)soundDataTotalPayload);
        pcm0.CopyTo(buf, SoundDataStart + sound0Offset);
        pcm1.CopyTo(buf, SoundDataStart + sound1Offset);
        return buf;
    }
}
