using System.Globalization;

namespace BurnSmith.Core;

/// <summary>
/// Puts two releases of one film on the same clock: scene cuts give the picture mapping new = A + K × original (K = 25 / 23.976 for a PAL speed-up),
/// cross-correlated sound gives each audio track's offset, subtitle starts give a subtitle track's drift. Measured with ffmpeg, in seconds of each file's own clock (-copyts).
/// </summary>
public static class FilmSync
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static string F(double s) => s.ToString("0.###", Inv);

    /// <summary>The scene cuts (scene score over 0.3) of the first video of <paramref name="file"/> between <paramref name="from"/> and +<paramref name="length"/> s of its clock; <paramref name="start"/> = the file's own start_time.</summary>
    public static List<double> Cuts(string file, double start, double from, double length)
    {
        var found = new List<double>();
        Tools.Run(Tools.Find("ffmpeg.exe"), ["-hide_banner", "-nostats", "-copyts", "-ss", F(Math.Max(0, from - start)), "-t", F(length), "-i", file, "-map", "0:v:0", "-an", "-sn", "-dn",
                  "-vf", "scale=480:-2,select='gt(scene,0.3)',showinfo", "-f", "null", "-"], line =>
        {
            var at = line.IndexOf("pts_time:", StringComparison.Ordinal);
            if (at < 0 || !line.Contains("Parsed_showinfo")) return;
            var text = line[(at + 9)..].Split(' ', 2)[0];
            if (double.TryParse(text, NumberStyles.Float, Inv, out var t)) lock (found) found.Add(t);
        });
        found.Sort();
        return found;
    }

    /// <summary>
    /// The offset A that puts the most original cuts (<paramref name="original"/> × <paramref name="k"/> + A) on a new one, within <paramref name="tolerance"/> s:
    /// every pair proposes its A, the one most cuts agree with wins, then A is their median. Null when fewer than 3 cuts (or under half of them) agree.
    /// </summary>
    public static (double A, int Matched, int Of, List<double> Residuals)? Match(IReadOnlyList<double> original, IReadOnlyList<double> fresh, double k, double tolerance = 0.03)
    {
        if (original.Count < 3 || fresh.Count < 3) return null;
        (double A, int Count) best = (0, 0);
        foreach (var o in original)
            foreach (var n in fresh)
            {
                var a = n - k * o;
                var count = original.Count(x => Nearest(fresh, a + k * x) is var d && Math.Abs(d) <= tolerance);
                if (count > best.Count) best = (a, count);
            }
        if (best.Count < 3) return null;
        var lined = original.Select(x => Nearest(fresh, best.A + k * x)).Where(d => Math.Abs(d) <= tolerance).ToList();
        var a0 = best.A + Median(lined);
        // the cuts of the original that fall in the span the new window covers: the share matched is of those
        var inside = original.Count(x => a0 + k * x >= fresh[0] - tolerance && a0 + k * x <= fresh[^1] + tolerance);
        if (best.Count * 2 < inside) return null;
        return (a0, best.Count, inside, [.. original.Select(x => Nearest(fresh, a0 + k * x)).Where(d => Math.Abs(d) <= tolerance)]);
    }

    /// <summary>Signed distance from <paramref name="t"/> to the nearest of the sorted <paramref name="list"/> (nearest − t); +inf when empty.</summary>
    public static double Nearest(IReadOnlyList<double> list, double t)
    {
        if (list.Count == 0) return double.PositiveInfinity;
        int lo = 0, hi = list.Count - 1;
        while (lo < hi) { var mid = (lo + hi) / 2; if (list[mid] < t) lo = mid + 1; else hi = mid; }
        var d = list[lo] - t;
        return lo > 0 && Math.Abs(list[lo - 1] - t) < Math.Abs(d) ? list[lo - 1] - t : d;
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? double.NaN : v.Count % 2 == 1 ? v[v.Count / 2] : (v[v.Count / 2 - 1] + v[v.Count / 2]) / 2;
    }

    /// <summary>
    /// Mono 8 kHz samples of an audio stream (<paramref name="map"/>, an ffmpeg -map such as "0:2" or "0:i:0x1100") from <paramref name="from"/> s of the file's clock.
    /// <paramref name="slow"/> &gt; 1 plays the sound that much slower (a PAL release brought back to film speed). Empty when nothing decodes there.
    /// </summary>
    public static float[] Samples(string file, double start, string map, double from, double length, double slow, string work, Action<string>? failed = null)
    {
        var raw = Path.Combine(work, $"pcm_{Guid.NewGuid():N}.raw");
        var filter = slow != 1 ? $"aresample=48000,asetrate={F(48000 / slow)},aresample=8000" : "aresample=8000";
        for (var attempt = 1; ; attempt++)
            try
            {
                Tools.Run(Tools.Find("ffmpeg.exe"), ["-hide_banner", "-v", "error", "-ss", F(Math.Max(0, from - start)), "-i", file, "-map", map, "-t", F(length), "-ac", "1", "-af", filter, "-f", "s16le", "-y", raw]);
                var bytes = File.ReadAllBytes(raw);
                var samples = new float[bytes.Length / 2];
                for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, 2 * i);
                if (samples.Length > 0 || attempt == 2) return samples;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException) // a damaged part of a disc decodes to nothing
            {
                if (attempt == 2) { failed?.Invoke($"{Path.GetFileName(file)} à {F(from)} s : {e.Message.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim()}"); return []; }
            }
            finally { try { File.Delete(raw); } catch (IOException) { } }
    }

    /// <summary>
    /// Where <paramref name="a"/> sits in <paramref name="b"/> (8 kHz), as seconds from the start of <paramref name="b"/>, and how alike they are there
    /// (normalized correlation, 1 = the same sound). FFT cross-correlation, the means taken off.
    /// </summary>
    public static (double Lag, double Peak) Locate(float[] a, float[] b, int rate = 8000)
    {
        if (a.Length == 0 || b.Length < a.Length) return (double.NaN, 0);
        var n = 1;
        while (n < a.Length + b.Length) n <<= 1;
        double ma = a.Average(x => (double)x), mb = b.Average(x => (double)x);
        double[] ar = new double[n], ai = new double[n], br = new double[n], bi = new double[n];
        for (var i = 0; i < a.Length; i++) ar[i] = a[i] - ma;
        for (var i = 0; i < b.Length; i++) br[i] = b[i] - mb;
        Fft(ar, ai, false); Fft(br, bi, false);
        for (var i = 0; i < n; i++) (br[i], bi[i]) = (br[i] * ar[i] + bi[i] * ai[i], bi[i] * ar[i] - br[i] * ai[i]); // B × conj(A)
        Fft(br, bi, true);
        // energy of b under a at each lag, for the normalized peak
        var prefix = new double[b.Length + 1];
        for (var i = 0; i < b.Length; i++) prefix[i + 1] = prefix[i] + (b[i] - mb) * (b[i] - mb);
        var ea = 0.0;
        for (var i = 0; i < a.Length; i++) ea += (a[i] - ma) * (a[i] - ma);
        var (lag, peak) = (0, double.MinValue);
        for (var m = 0; m <= b.Length - a.Length; m++) if (br[m] > peak) (lag, peak) = (m, br[m]);
        var eb = prefix[lag + a.Length] - prefix[lag];
        return ((double)lag / rate, ea > 0 && eb > 0 ? peak / Math.Sqrt(ea * eb) : 0);
    }

    /// <summary>In-place radix-2 FFT (length a power of two); the inverse is scaled by 1/n.</summary>
    public static void Fft(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var j = 0; j < len / 2; j++)
                {
                    int u = i + j, v = i + j + len / 2;
                    double xr = re[v] * cr - im[v] * ci, xi = re[v] * ci + im[v] * cr;
                    (re[v], im[v]) = (re[u] - xr, im[u] - xi);
                    re[u] += xr; im[u] += xi;
                    (cr, ci) = (cr * wr - ci * wi, cr * wi + ci * wr);
                }
            }
        }
        if (inverse) for (var i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    /// <summary>Least squares y = A + B × x, with the rms and the largest of what is left.</summary>
    public static (double A, double B, double Rms, double Max) Line(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        double mx = x.Average(), my = y.Average(), sxx = 0, sxy = 0;
        for (var i = 0; i < x.Count; i++) { sxx += (x[i] - mx) * (x[i] - mx); sxy += (x[i] - mx) * (y[i] - my); }
        var b = sxx > 0 ? sxy / sxx : 0;
        var a = my - b * mx;
        var left = x.Select((xi, i) => y[i] - (a + b * xi)).ToList();
        return (a, b, Math.Sqrt(left.Average(r => r * r)), left.Max(Math.Abs));
    }

    /// <summary>
    /// Subtitle starts of a new track against the original's mapped onto the new clock (<paramref name="map"/>): (new time, new − mapped) for each original
    /// start whose nearest new start is within <paramref name="window"/> s.
    /// </summary>
    public static List<(double At, double Diff)> Pair(IEnumerable<double> original, IReadOnlyList<double> fresh, Func<double, double> map, double window = 1.0) =>
        [.. original.Select(map).Select(t => (t, d: Nearest(fresh, t))).Where(p => Math.Abs(p.d) < window).Select(p => (p.t + p.d, p.d))];

    /// <summary>The drift of paired starts: a line through them, outliers (over 150 ms from a first line) left out; a constant when there are fewer than 8.</summary>
    public static (double A, double B, double Rms, double Max, int Count) Drift(List<(double At, double Diff)> pairs)
    {
        if (pairs.Count == 0) return (0, 0, 0, 0, 0);
        if (pairs.Count < 8)
        {
            var c = Median(pairs.Select(p => p.Diff));
            return (c, 0, Math.Sqrt(pairs.Average(p => (p.Diff - c) * (p.Diff - c))), pairs.Max(p => Math.Abs(p.Diff - c)), pairs.Count);
        }
        var first = Line([.. pairs.Select(p => p.At)], [.. pairs.Select(p => p.Diff)]);
        var kept = pairs.Where(p => Math.Abs(p.Diff - (first.A + first.B * p.At)) < 0.15).ToList();
        if (kept.Count < 8) kept = pairs;
        var (a, b, rms, max) = Line([.. kept.Select(p => p.At)], [.. kept.Select(p => p.Diff)]);
        return (a, b, rms, max, kept.Count);
    }

    /// <summary>check-film-sync: the pieces above on made-up data (cut lists, a delayed noise, a drifting subtitle track, a PGS file retimed). Returns the failures.</summary>
    public static List<string> SelfCheck()
    {
        var bad = new List<string>();
        void Check(string what, bool ok) { if (!ok) bad.Add(what); }
        var random = new Random(7);
        // cuts: a PAL original (25 fps) and its 23.976 release 12.3 s later, plus stray cuts on both sides
        const double k = 25 / (24000 / 1001.0);
        var original = Enumerable.Range(0, 30).Select(_ => 3600 + Math.Round(random.NextDouble() * 60 * 25) / 25).Distinct().OrderBy(x => x).ToList();
        var fresh = original.Select(x => 12.3 + k * x).Concat([12.3 + k * 3610.5, 12.3 + k * 3633.3]).OrderBy(x => x).ToList();
        var m = Match([.. original, 3620.123], fresh, k);
        Check($"cut match: offset {m?.A.ToString("0.000", Inv)}, {m?.Matched}/{original.Count} matched, instead of 12.300", m is { } r && Math.Abs(r.A - 12.3) < 0.001 && r.Matched >= original.Count);
        Check("cut match: no match from unrelated cuts", Match(original, [.. original.Select(x => 5 + x * 1.3)], k) == null);
        // sound: noise, the same noise 0.203 s later in a longer window
        var noise = Enumerable.Range(0, 8000 * 6).Select(_ => (float)(random.NextDouble() * 2000 - 1000)).ToArray();
        var later = new float[8000 * 8];
        Array.Copy(noise, 0, later, 2000 + 1624, noise.Length); // 2000 samples of silence before, then 0.203 s
        var (lag, peak) = Locate(noise, later);
        Check($"sound located at {lag:0.0000} s (peak {peak:0.00}) instead of 0.4530", Math.Abs(lag - 0.453) < 0.0002 && peak > 0.99);
        // drift: −0.5145 s + 3.925e-5 × t, with one outlier
        var pairs = Enumerable.Range(0, 200).Select(i => (At: i * 28.0, Diff: -0.5145 + 3.925e-5 * i * 28 + (random.NextDouble() - 0.5) * 0.02)).ToList();
        pairs.Add((1000, 0.6));
        var d = Drift(pairs);
        Check($"drift {d.A:0.0000} + {d.B:0.000e0} t", Math.Abs(d.A + 0.5145) < 0.005 && Math.Abs(d.B - 3.925e-5) < 2e-6 && d.Count == 200);
        // PGS: written, retimed by a line, starts read back; a forced flag cleared
        var writer = new PgsSubtitles.Writer(1920, 1080);
        foreach (var t in new[] { 10.0, 20.0, 30.0 }) writer.Add(t, t + 1, 100, 900, 8, 8, new byte[64], [(0, 0, 0, 0), (255, 255, 255, 255)]);
        var sup = writer.Finish();
        PgsSubtitles.Retime(sup, t => t - (-0.5 + 1e-3 * t));
        var starts = PgsSubtitles.Starts(sup);
        Check($"PGS retimed: {string.Join(' ', starts.Select(s => s.ToString("0.000", Inv)))}", starts.Count == 3 && starts.Zip(new[] { 10.49, 20.48, 30.47 }).All(p => Math.Abs(p.First - p.Second) < 0.0001));
        sup[FlagsAt] |= 0x40;
        Check("PGS forced flag counted", PgsSubtitles.ForcedObjects(sup) == (3, 1));
        Check("PGS forced flag cleared", PgsSubtitles.Retime(sup, t => t, clearForced: true) == 1 && PgsSubtitles.ForcedObjects(sup) == (3, 0));
        // pop-up IG: an ICS in two fragments over two TS packets, two pages with wipes of 1134 ticks, a zero step and a 4000 one; raised to 3754, the rest untouched
        var (clip, durations) = MadeUpIg();
        var before = (byte[])clip.Clone();
        Check("IG frame rate code", ReplaceFilm.SetIgRate(clip, 0x1400, 1) == 1);
        var raised = ReplaceFilm.RaiseIgEffects(clip, 0x1400, 3754);
        int Ticks(int[] at) => clip[at[0]] << 16 | clip[at[1]] << 8 | clip[at[2]];
        var expected = new[] { 3754, 0, 3754, 3754, 4000 };
        var others = Enumerable.Range(0, clip.Length).Where(i => clip[i] != before[i]).Except(durations.SelectMany(at => at)).ToList();
        Check($"IG effects raised {raised}, durations {string.Join(' ', durations.Select(Ticks))}, {others.Count} other byte(s) changed",
              raised == 3 && durations.Select(Ticks).SequenceEqual(expected) && others.Count == 1 && (clip[others[0]] >> 4) == 1);
        // mux rate: 10000, 35000 (+5000 null) and 10000 packets in three seconds of the arrival clock, across its wrap
        var ts = new MemoryStream();
        void Packets(long second, int count, int pid)
        {
            for (var i = 0; i < count; i++)
            {
                var ats = (second * 27_000_000 + (long)i * 27_000_000 / count) % (1L << 30);
                ts.Write([(byte)(ats >> 24), (byte)(ats >> 16), (byte)(ats >> 8), (byte)ats, 0x47, (byte)(pid >> 8), (byte)pid, 0x10]);
                ts.Write(new byte[184]);
            }
        }
        Packets(39, 10000, 0x1011); Packets(40, 35000, 0x1011); Packets(40, 5000, 0x1FFF); Packets(41, 10000, 0x1100);
        var (peakRate, over) = ReplaceFilm.PeakRate(new MemoryStream(ts.ToArray()), 48e6);
        Check($"mux peak {peakRate / 1e6:0.00} Mb/s, {over} s over, instead of 53.76 and 1", Math.Abs(peakRate - 35000 * 1536.0) < 1 && over == 1);
        return bad;
    }

    /// <summary>A .m2ts holding one IG PES (PID 0x1400) whose ICS is cut in two fragments, spread over two TS packets; and the positions of its effect durations.</summary>
    static (byte[] Clip, List<int[]> Durations) MadeUpIg()
    {
        var data = new List<byte> { 0, 0, 0, 0x00 }; // interactive_composition_length (not read), stream model 0
        data.AddRange(new byte[10 + 3]); // time-outs, user time-out
        data.Add(2); // pages
        var at = new List<int>();
        void Effect(int ticks, bool cropped)
        {
            at.Add(data.Count);
            data.AddRange([(byte)(ticks >> 16), (byte)(ticks >> 8), (byte)ticks, 0, 1]); // duration, palette, one object
            data.AddRange([0, 1, 0, (byte)(cropped ? 0x80 : 0), 0, 10, 0, 20]);
            if (cropped) data.AddRange([0, 0, 0, 0, 0, 100, 0, (byte)ticks]);
        }
        foreach (var page in new[] { 0, 1 })
        {
            data.AddRange([(byte)page, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            data.AddRange([1, 0, 0, 0, 0, 0, 0x07, 0x80, 0, 100]); // in: one window (id, x, y, width, height)
            data.Add((byte)(page == 0 ? 2 : 1));
            if (page == 0) { Effect(1134, true); Effect(0, false); } else Effect(1134, true);
            data.Add(0); // out: no window
            data.Add(1);
            Effect(page == 0 ? 1134 : 4000, page == 1);
            data.AddRange([0, 0, 1, 0, 1, 0, 1]); // animation, default buttons, palette, one group
            data.AddRange([0, 1, 1]); // default valid button, one button
            var button = new byte[35]; button[34] = 1; // one navigation command
            data.AddRange(button); data.AddRange(new byte[12]);
        }
        var half = data.Count / 2;
        var pes = new List<byte> { 0, 0, 1, 0xBF, 0, 0, 0x81, 0x80, 5, 0x21, 0, 1, 0, 1 };
        var header = pes.Count;
        foreach (var (part, seq) in new[] { (data.Take(half).ToList(), (byte)0x80), (data.Skip(half).ToList(), (byte)0x40) })
        {
            var length = 9 + part.Count;
            pes.AddRange([0x18, (byte)(length >> 8), (byte)length, 0x07, 0x80, 0x04, 0x38, 0x30, 0, 0, 0x80, seq]);
            pes.AddRange(part);
        }
        // durations: data offset → offset in the PES (the second fragment after its own 12-byte header) → offset in the file
        int InFile(int o) => (header + 12 + o + (o >= half ? 12 : 0)) is var q ? q / 184 * 192 + 8 + q % 184 : 0;
        var clip = new List<byte>();
        for (var o = 0; o < pes.Count; o += 184)
        {
            clip.AddRange([0, 0, 0, 0, 0x47, (byte)(o == 0 ? 0x54 : 0x14), 0x00, 0x10]);
            clip.AddRange(pes.Skip(o).Take(184).Concat(Enumerable.Repeat((byte)0xFF, Math.Max(0, o + 184 - pes.Count))));
        }
        return ([.. clip], [.. at.Select(o => new[] { InFile(o), InFile(o + 1), InFile(o + 2) })]);
    }

    /// <summary>The flags byte of the first object the first composition of a .sup places (header 13 + 11 bytes of composition + object id and window id).</summary>
    const int FlagsAt = 13 + 11 + 3;
}
