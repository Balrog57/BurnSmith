using System.Globalization;

namespace BurnSmith.Core;

/// <summary>
/// The edit tabs for one video, drawn into the picture while it is re-encoded. Rogner: <see cref="Rotate"/> (degrees clockwise), flips, then the crop
/// margins in pixels of the turned picture. Effet: <see cref="Brightness"/> / <see cref="Contrast"/> / <see cref="Saturation"/> / <see cref="WhiteBalance"/>
/// from -100 to 100 and a <see cref="Look"/> (a key of <see cref="Looks"/>). Filigrane: <see cref="Watermark"/>, a PNG (the app draws a text watermark into one;
/// the Watermark* text fields only let the window show it again), <see cref="WatermarkWidth"/> its share of the picture's width (0 = its own size).
/// Sous-titres: <see cref="BurnSubtitle"/>, a text subtitle file drawn into the picture, <see cref="BurnPosition"/> 0 = bottom .. 100 = centre,
/// <see cref="BurnDelay"/> seconds later than the file says.
/// </summary>
public sealed record VideoEffects(
    int Rotate = 0, bool FlipH = false, bool FlipV = false,
    int CropTop = 0, int CropBottom = 0, int CropLeft = 0, int CropRight = 0,
    int Brightness = 0, int Contrast = 0, int Saturation = 0, int WhiteBalance = 0, string Look = "",
    string? Watermark = null, bool WatermarkIsText = true, string WatermarkText = "", string WatermarkFont = "Arial", int WatermarkSize = 72,
    bool WatermarkBold = false, bool WatermarkItalic = false, bool WatermarkUnderline = false, string WatermarkColor = "FFFFFF",
    double WatermarkWidth = 0, int WatermarkOpacity = 100, string WatermarkPlace = "br",
    string? BurnSubtitle = null, string BurnFont = "Arial", int BurnSize = 22, bool BurnBold = false, bool BurnItalic = false,
    string BurnColor = "FFFFFF", int BurnOpacity = 100, int BurnPosition = 0, double BurnDelay = 0)
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The "filters" of the Effet tab: name shown, ffmpeg filter.</summary>
    public static readonly IReadOnlyList<(string Name, string Filter)> Looks =
    [
        ("Aucun", ""),
        ("Niveaux de gris", "hue=s=0"),
        ("Sépia", "colorchannelmixer=.393:.769:.189:0:.349:.686:.168:0:.272:.534:.131"),
        ("Négatif", "negate"),
        ("Vintage", "curves=preset=vintage"),
        ("Chaud", "colorbalance=rm=0.15:gm=0.04:bm=-0.15"),
        ("Froid", "colorbalance=rm=-0.12:bm=0.15"),
        ("Contraste fort", "curves=preset=strong_contrast"),
        ("Couleurs vives", "eq=saturation=1.6"),
        ("Délavé", "curves=preset=lighter,eq=saturation=0.6"),
    ];

    public bool IsEmpty => Rotate % 360 == 0 && !FlipH && !FlipV && CropTop + CropBottom + CropLeft + CropRight == 0
                           && Brightness == 0 && Contrast == 0 && Saturation == 0 && WhiteBalance == 0 && Look is "" or "Aucun"
                           && Watermark == null && BurnSubtitle == null;

    /// <summary>The picture's size once turned and cropped, from the source's <paramref name="width"/> x <paramref name="height"/>.</summary>
    public (int Width, int Height) Size(int width, int height)
    {
        var (w, h) = Rotate % 180 == 90 ? (height, width) : (width, height);
        return (Math.Max(2, (w - CropLeft - CropRight) / 2 * 2), Math.Max(2, (h - CropTop - CropBottom) / 2 * 2));
    }

    /// <summary>
    /// The -vf graph: these effects on a source of <paramref name="width"/> x <paramref name="height"/>, then <paramref name="rest"/> (the disc's scaling).
    /// <paramref name="start"/>: the source second the first frame shows (the cut's start, or the frame a preview reads), so a burnt subtitle lines up.
    /// <paramref name="first"/>: filters on the source before the effects (an HDR picture made SDR, so a burnt subtitle or a watermark is not toned down with it).
    /// </summary>
    public string Vf(int width, int height, double start, string rest, string first = "")
    {
        var f = new List<string>();
        if (first.Length > 0) f.Add(first);
        if (Rotate % 360 == 90) f.Add("transpose=1");
        else if (Rotate % 360 == 180) f.Add("hflip,vflip");
        else if (Rotate % 360 == 270) f.Add("transpose=2");
        if (FlipH) f.Add("hflip");
        if (FlipV) f.Add("vflip");
        if (CropTop + CropBottom + CropLeft + CropRight > 0)
        {
            var (w, h) = Size(width, height);
            f.Add($"crop={w}:{h}:{CropLeft}:{CropTop}");
        }
        if (Brightness != 0 || Contrast != 0 || Saturation != 0)
            f.Add($"eq=brightness={N(Brightness / 250.0)}:contrast={N(1 + Contrast / 100.0)}:saturation={N(1 + Saturation / 100.0)}");
        if (WhiteBalance != 0)
        {
            var k = N(WhiteBalance / 300.0);
            var m = N(-WhiteBalance / 300.0);
            f.Add($"colorbalance=rm={k}:rh={k}:bm={m}:bh={m}");
        }
        if (Looks.FirstOrDefault(l => l.Name == Look).Filter is { Length: > 0 } look) f.Add(look);
        if (BurnSubtitle != null)
        {
            // the subtitles filter reads cue times off the frame's own time, which -ss reset to 0: shift it to the source's time for the filter, then back
            var shift = N(start - BurnDelay);
            var alpha = ((int)Math.Round(255 * (1 - Math.Clamp(BurnOpacity, 0, 100) / 100.0))).ToString("X2");
            var style = $"Fontname={BurnFont},Fontsize={BurnSize},PrimaryColour=&H{alpha}{Bgr(BurnColor)},Bold={(BurnBold ? 1 : 0)},Italic={(BurnItalic ? 1 : 0)}," +
                        $"Alignment=2,MarginV={10 + Math.Clamp(BurnPosition, 0, 100) * 125 / 100}"; // 288 lines tall: 135 is the middle
            f.Add($"setpts=PTS+{shift}/TB,subtitles=filename={Quote(BurnSubtitle)}:force_style='{style}',setpts=PTS-{shift}/TB");
        }
        var chain = f.Count > 0 ? string.Join(",", f) : "null";
        if (Watermark == null) return chain == "null" ? rest : $"{chain},{rest}";
        var (fw, _) = Size(width, height);
        var margin = Math.Max(8, fw / 50);
        var scale = WatermarkWidth > 0 ? $",scale={Math.Max(2, (int)(fw * WatermarkWidth))}:-1" : "";
        var at = WatermarkPlace switch
        {
            "tl" => $"{margin}:{margin}",
            "tr" => $"W-w-{margin}:{margin}",
            "bl" => $"{margin}:H-h-{margin}",
            "c" => "(W-w)/2:(H-h)/2",
            _ => $"W-w-{margin}:H-h-{margin}",
        };
        return $"movie=filename={Quote(Watermark)},format=rgba{scale},colorchannelmixer=aa={N(Math.Clamp(WatermarkOpacity, 0, 100) / 100.0)}[wm];" +
               $"[in]{chain}[base];[base][wm]overlay={at}:format=auto,{rest}";
    }

    /// <summary>
    /// One part of another video's effects copied onto these ("Appliquer à tous" of a tab): "crop", "effect", "watermark" or "subtitle".
    /// </summary>
    public VideoEffects Take(VideoEffects o, string part) => part switch
    {
        "crop" => this with { Rotate = o.Rotate, FlipH = o.FlipH, FlipV = o.FlipV, CropTop = o.CropTop, CropBottom = o.CropBottom, CropLeft = o.CropLeft, CropRight = o.CropRight },
        "effect" => this with { Brightness = o.Brightness, Contrast = o.Contrast, Saturation = o.Saturation, WhiteBalance = o.WhiteBalance, Look = o.Look },
        "watermark" => this with
        {
            Watermark = o.Watermark, WatermarkIsText = o.WatermarkIsText, WatermarkText = o.WatermarkText, WatermarkFont = o.WatermarkFont, WatermarkSize = o.WatermarkSize,
            WatermarkBold = o.WatermarkBold, WatermarkItalic = o.WatermarkItalic, WatermarkUnderline = o.WatermarkUnderline, WatermarkColor = o.WatermarkColor,
            WatermarkWidth = o.WatermarkWidth, WatermarkOpacity = o.WatermarkOpacity, WatermarkPlace = o.WatermarkPlace,
        },
        // the subtitle file belongs to its video: only its look is shared
        "subtitle" => this with { BurnFont = o.BurnFont, BurnSize = o.BurnSize, BurnBold = o.BurnBold, BurnItalic = o.BurnItalic, BurnColor = o.BurnColor, BurnOpacity = o.BurnOpacity, BurnPosition = o.BurnPosition, BurnDelay = o.BurnDelay },
        _ => this,
    };

    static string N(double v) => v.ToString("0.###", Inv);

    /// <summary>RRGGBB as ASS writes a colour: BBGGRR.</summary>
    static string Bgr(string rgb) => rgb.Length == 6 ? rgb[4..6] + rgb[2..4] + rgb[..2] : "FFFFFF";

    /// <summary>
    /// A file path as a filter option, escaped twice as ffmpeg reads it: once for the option value (\ ' :), then for the graph (\ ' [ ] , ;),
    /// so "D:\Vidéos d'été, [1]\film.srt" survives (an apostrophe is common in French names).
    /// </summary>
    static string Quote(string path) => Escape(Escape(path.Replace('\\', '/'), @"\':"), @"\'[],;");

    static string Escape(string s, string special) => string.Concat(s.Select(c => special.Contains(c) ? "\\" + c : c.ToString()));
}
