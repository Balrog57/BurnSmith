namespace BurnSmith.Core;

/// <summary>Primary Blu-ray Dolby Digital Plus: a 48 kHz AC-3 core paired with a six-block dependent E-AC-3 extension.</summary>
internal static class DiscDolbyDigitalPlus
{
    static ReadOnlySpan<int> CoreRates => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    internal static bool Probe(string source, int track)
    {
        var raw = Path.GetTempFileName();
        try
        {
            Extract(source, $"0:a:{track}", raw, sample: true);
            return HasCoreAndExtension(raw);
        }
        catch (InvalidOperationException) { return false; } // An unverified stream follows the conversion path.
        finally { File.Delete(raw); }
    }

    internal static void Extract(string source, string map, string raw, bool sample = false)
    {
        List<string> args = ["-y", "-v", "error", "-probesize", "350000000", "-analyzeduration", "60000000",
            "-i", source, "-map", map, "-c:a", "copy"];
        if (sample) args.AddRange(["-frames:a", "8"]);
        args.AddRange(["-f", "eac3", raw]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args);
    }

    internal static bool IsStandalone(string raw, int channels)
    {
        using var probe = System.Text.Json.JsonDocument.Parse(Tools.Run(Tools.Find("ffprobe.exe"),
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_name,sample_rate,channels", "-of", "json", raw]));
        return probe.RootElement.GetProperty("streams").EnumerateArray().FirstOrDefault() is
            { ValueKind: System.Text.Json.JsonValueKind.Object } stream
            && stream.GetProperty("codec_name").GetString() == "eac3"
            && stream.GetProperty("sample_rate").GetString() == "48000"
            && stream.GetProperty("channels").GetInt32() == channels;
    }

    internal static bool HasCoreAndExtension(string raw)
    {
        using var file = File.OpenRead(raw);
        Span<byte> header = stackalloc byte[6];
        Span<byte> payload = stackalloc byte[4096];
        var hasPair = false;
        while (file.Position < file.Length)
        {
            if (!ReadHeader(file, header) || header[5] >> 3 > 8 || (header[4] >> 6) != 0 || (header[4] & 63) >= 38)
                return false;
            var core = CoreRates[(header[4] & 63) >> 1] * 4;
            if (!ReadPayload(file, payload, core)) return false;

            // strmtyp=1, substreamid=0, fscod=0, numblkscod=3, bsid=16.
            if (!ReadHeader(file, header) || header[2] >> 3 != 8 || header[4] >> 4 != 3 || header[5] >> 3 != 16)
                return false;
            var extension = 2 * (((header[2] & 7) << 8 | header[3]) + 1);
            if (!ReadPayload(file, payload, extension)) return false;
            hasPair = true;
        }
        return hasPair;
    }

    static bool ReadHeader(Stream file, Span<byte> header) =>
        file.ReadAtLeast(header, header.Length, false) == header.Length && header[0] == 0x0B && header[1] == 0x77;

    static bool ReadPayload(Stream file, Span<byte> buffer, int frameSize)
    {
        var bytes = frameSize - 6;
        return bytes > 0 && file.ReadAtLeast(buffer[..bytes], bytes, false) == bytes;
    }
}
