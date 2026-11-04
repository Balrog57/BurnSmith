// Ported to C# from bdmvkit (module encode.py: picture, video_args, fit_filter, encode_menu): https://github.com/kajisho5/bdmvkit, commit 5789076 (v0.7.0).
// Copyright (c) kajisho5 (Shogo Kajiyama). GNU Lesser General Public License v3.0 or later (COPYING.LESSER upstream);
// redistributed here under the GNU GPL v3 or later, with the LGPL's additional permissions removed, as section 7 of the GPL v3 allows. See THIRD-PARTY-NOTICES.md.
using System.Globalization;

namespace BurnSmith.Core.Hdmv;

/// <summary>Reading pictures and encoding the menu clip (picture + IG stream) with the ffmpeg BurnSmith already carries.</summary>
public static class MenuClip
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>A picture file (PNG, JPEG ...) as straight-alpha RGBA, scaled to <paramref name="w"/> x <paramref name="h"/>; size = the file's own when both are 0.</summary>
    public static (int W, int H, byte[] Rgba) ReadRgba(string path, int w = 0, int h = 0)
    {
        if (w == 0 || h == 0)
        {
            var probe = Tools.Run(Tools.Find("ffprobe.exe"), ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path]);
            var parts = probe.Trim().Split(',');
            if (parts.Length < 2 || !int.TryParse(parts[0], out w) || !int.TryParse(parts[1], out h)) throw new InvalidDataException($"cannot read the picture {path}");
        }
        // rawvideo goes to a file: Tools.Run hands back stdout as text
        var raw = Path.Combine(Path.GetTempPath(), $"burnsmith-rgba-{Guid.NewGuid():N}.raw");
        try
        {
            Tools.Run(Tools.Find("ffmpeg.exe"), ["-hide_banner", "-nostdin", "-v", "error", "-y", "-i", path, "-frames:v", "1", "-vf", $"scale={w}:{h}", "-f", "rawvideo", "-pix_fmt", "rgba", raw]);
            var rgba = File.ReadAllBytes(raw);
            if (rgba.Length != w * h * 4) throw new InvalidDataException($"cannot read the picture {path}: {rgba.Length} bytes for {w} x {h}");
            return (w, h, rgba);
        }
        finally { try { File.Delete(raw); } catch (IOException) { } }
    }

    /// <summary>x264 options for a Blu-ray video stream at num/den fps: High@4.1, at most a second between key frames, 1080i flags for 25 and 29.97 fps.</summary>
    public static List<string> VideoArgs(int num, int den, bool fast = false, bool hd720 = false)
    {
        var codes = HdmvFormats.VideoCodes(num, den, hd720) ?? throw new ArgumentException($"{num}/{den} fps has no Blu-ray format");
        var gop = (int)Math.Round((double)num / den);          // at most one second between key frames
        var x264 = $"bluray-compat=1:slices=4:aud=1:nal-hrd=vbr:keyint={gop}:min-keyint=1:force-cfr=1";
        if (codes.Format == 4) x264 += ":fake-interlaced=1:pic-struct=1";
        return ["-c:v", "libx264", "-preset", fast ? "ultrafast" : "medium", "-crf", fast ? "24" : "19", "-profile:v", "high", "-level", "4.1",
                "-maxrate", HdmvFormats.MaxVideoBps.ToString(Inv), "-bufsize", HdmvFormats.VbvBuffer.ToString(Inv), "-x264-params", x264,
                "-r", $"{num}/{den}", "-pix_fmt", "yuv420p", "-aspect", "16:9", "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
                "-forced-idr", "1"];
    }

    /// <summary>
    /// A still menu clip <paramref name="seconds"/> long: the picture <paramref name="background"/> fitted into the HD frame with the IG stream
    /// <paramref name="sup"/> (<see cref="IgStream.Build"/>) as its buttons, muxed by ffmpeg like a subtitle stream, then marked Interactive
    /// Graphics (0x91) in every PMT. No sound track: a menu with music or a moving background is not ported (bdmvkit's `motion` and `music`).
    /// </summary>
    public static string Encode(string background, string sup, string output, int num, int den, double seconds = 1.0, bool fast = false, bool hd720 = false)
    {
        var (w, h) = hd720 ? (1280, 720) : (HdmvFormats.Width, HdmvFormats.Height);
        var fps = $"{num}/{den}";
        var graph = $"[0:v]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={fps},format=yuv420p[v]";
        var length = seconds.ToString("0.000", Inv);
        double lead;
        // ffmpeg starts the IG stream at its first PTS (the ICS) and shifts everything to keep timestamps positive, so a dated ICS (DTS before PTS)
        // would put the IG data before the first picture in the file. Delaying the IG by that lead (ICS PTS - DTS) puts the first picture first,
        // then the ICS, palette and objects, as on the reference disc. With undated segments (bdmvkit) the lead is 0 and nothing changes.
        using (var head = File.OpenRead(sup))
        {
            var h13 = new byte[13];
            if (head.Read(h13, 0, 13) != 13) throw new InvalidDataException($"{sup} is not a .sup");
            lead = (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h13.AsSpan(2)) - System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(h13.AsSpan(6))) / 90000.0;
        }
        var args = new List<string> { "-hide_banner", "-nostdin", "-v", "error", "-y", "-loop", "1", "-framerate", fps, "-t", length, "-i", background };
        if (lead > 0) args.AddRange(["-itsoffset", lead.ToString("0.000000", Inv)]);
        args.AddRange(["-f", "sup", "-i", sup, "-filter_complex", graph, "-map", "[v]", "-map", "1:0"]);
        args.AddRange(VideoArgs(num, den, fast, hd720));
        args.AddRange(["-c:s", "copy", "-streamid", $"0:{HdmvFormats.VideoPid}", "-streamid", $"1:{HdmvFormats.IgPid}", "-t", length,
                       "-f", "mpegts", "-mpegts_m2ts_mode", "1", "-mpegts_pmt_start_pid", HdmvFormats.PmtPid.ToString(Inv), output]);
        Tools.Run(Tools.Find("ffmpeg.exe"), args);
        TransportStream.SetStreamTypes(output, new Dictionary<int, byte> { [HdmvFormats.IgPid] = HdmvFormats.CodingIg });
        return output;
    }
}
