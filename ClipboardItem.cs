using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.UI;

namespace ReCall;

public enum ClipboardItemType { Text, Image }

public class ClipboardItem : INotifyPropertyChanged
{
    /// <summary>Stable identity used to name this item's persisted image
    /// file on disk (see HistoryStore) and to match it back up across
    /// app restarts. Assigned once, never reused.
    /// (Plain get/set, not init -- WinUI's generated XamlTypeInfo.g.cs
    /// constructs and populates ClipboardItem via reflection-style property
    /// setters for x:Bind, which can't target init-only accessors.)</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public ClipboardItemType Type { get; set; }
    public string? Text { get; set; }
    public BitmapImage? Image { get; set; }
    public byte[]? ImageBytes { get; set; }

    /// <summary>Filename (under HistoryStore's Images folder) this item's
    /// bytes are saved as, once persisted. Null until the first save;
    /// tracked here so re-saving doesn't rewrite unchanged image bytes.</summary>
    public string? ImageFile { get; set; }

    public DateTime Timestamp { get; set; } = DateTime.Now;

    private bool _isPinned;

    /// <summary>Pinned items are kept contiguous at the very top of the
    /// list (above every unpinned item), skip auto-trim, and are exempt
    /// from Clear All -- see ClipboardPanelWindow.TogglePin/Trim/
    /// OnClearAllClicked. Raises PropertyChanged so the pin button's glyph
    /// and resting opacity (both x:Bind Mode=OneWay in the item template)
    /// update live without the ListView needing to re-template the row.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinGlyph)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinOpacity)));
        }
    }

    /// <summary>Segoe Fluent icon for the pin button: outline "Pin" glyph
    /// when not yet pinned (click to pin), filled "UnPin" glyph once
    /// pinned (click to unpin) -- same convention Windows itself uses for
    /// pin/unpin toggle buttons.</summary>
    public string PinGlyph => IsPinned ? "\uE77A" : "\uE718";

    /// <summary>Resting (non-hover) opacity for the pin button: fully
    /// visible once pinned, invisible otherwise. The button itself stays
    /// laid out (Visibility="Visible") at all times -- only its opacity
    /// and hit-testability change -- so the row height never depends on
    /// whether this button happens to be showing. Hover state overrides
    /// this transiently in code-behind (see OnItemPointerEntered/Exited).</summary>
    public double PinOpacity => IsPinned ? 1.0 : 0.0;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Produces a fully independent copy of this item: a fresh Id
    /// (so it gets its own persisted image file rather than sharing the
    /// original's -- see ClipboardPanelWindow.AddCopyToFolder) and its own
    /// IsPinned state (starts unpinned, regardless of the original's pin
    /// state). Used by the right-click "Add to folder" menu -- unlike
    /// "Move to folder", the original stays exactly where it was, and
    /// editing/pinning/deleting the copy or the original from then on
    /// never affects the other.</summary>
    public ClipboardItem Clone() => new()
    {
        Type = Type,
        Text = Text,
        Image = Image,
        ImageBytes = ImageBytes,
        ImageFile = null,
        Timestamp = Timestamp,
        IsPinned = false,
    };

    public Visibility IsText => Type == ClipboardItemType.Text ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsImage => Type == ClipboardItemType.Image ? Visibility.Visible : Visibility.Collapsed;
    public string TimestampDisplay => Timestamp.ToString("t");

    /// <summary>Visible only once the image is actually decoded (Image is
    /// non-null) and its bytes are on hand -- both are set together in
    /// AddImage/LoadHistoryAsync, but this guards the badge in case a
    /// future caller ever constructs an Image item before either is
    /// ready.</summary>
    public Visibility ImageInfoVisibility =>
        Type == ClipboardItemType.Image && Image is not null && ImageBytes is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>"1920x1080 · 340 KB" badge text for image items. Pixel
    /// dimensions come straight off the decoded BitmapImage (populated by
    /// the time SetSourceAsync's await completes -- see
    /// BytesToBitmapImageAsync/OnClipboardChanged, both of which await it
    /// before handing the bitmap off); file size comes from the same raw
    /// bytes used to re-copy the image to the clipboard on click.</summary>
    public string ImageInfoDisplay => Type == ClipboardItemType.Image && Image is not null && ImageBytes is not null
        ? $"{Image.PixelWidth}\u00d7{Image.PixelHeight} \u00b7 {FormatFileSize(ImageBytes.Length)}"
        : string.Empty;

    /// <summary>Formats a byte count the way Windows Explorer does:
    /// whole bytes under 1 KB, whole KB under 1 MB, one decimal place of
    /// MB from there up.</summary>
    private static string FormatFileSize(int bytes)
    {
        const double Kb = 1024;
        const double Mb = Kb * 1024;

        if (bytes < Kb) return $"{bytes} B";
        if (bytes < Mb) return $"{Math.Round(bytes / Kb)} KB";
        return $"{(bytes / Mb).ToString("0.#", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>Matches a hex color code, with or without the leading
    /// '#': 3/4-digit shorthand (RGB, RGBA) or full 6/8-digit form
    /// (RRGGBB, RRGGBBAA -- the CSS convention for including alpha).
    /// The '#' is optional so bare hex (as some color pickers/tools copy
    /// it) is still recognized; this does mean an unrelated 6-or-8-digit
    /// hex string (a short git hash, an ID) copied on its own will also
    /// show a swatch.</summary>
    private static readonly Regex HexColorPattern =
        new(@"^#?(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    /// <summary>Matches rgb()/rgba(), comma- or space-separated (the
    /// modern CSS Color Module Level 4 syntax allows either), with an
    /// optional alpha channel as a 0-1 number or a percentage.</summary>
    private static readonly Regex RgbColorPattern = new(
        @"^rgba?\(\s*(?<r>\d{1,3})\s*[, ]\s*(?<g>\d{1,3})\s*[, ]\s*(?<b>\d{1,3})\s*(?:[,/]\s*(?<a>[\d.]+%?)\s*)?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches hsl()/hsla(), comma- or space-separated, with an
    /// optional alpha channel as a 0-1 number or a percentage.</summary>
    private static readonly Regex HslColorPattern = new(
        @"^hsla?\(\s*(?<h>[\d.]+)(?:deg)?\s*[, ]\s*(?<s>[\d.]+)%\s*[, ]\s*(?<l>[\d.]+)%\s*(?:[,/]\s*(?<a>[\d.]+%?)\s*)?\)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Matches linear-gradient(...)/radial-gradient(...) (and
    /// their repeating- variants), capturing which kind it is and
    /// everything between the outer parens. The inner content is parsed
    /// separately (see TryParseCssGradient) since it can itself contain
    /// commas nested inside rgb()/rgba()/hsl()/hsla() color stops.</summary>
    private static readonly Regex GradientPattern = new(
        @"^(?:repeating-)?(?<kind>linear|radial)-gradient\(\s*(?<inner>.*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>Trims <paramref name="text"/> and strips a single
    /// trailing ';' (plus any whitespace around it), so a value copied
    /// straight out of a CSS declaration -- "red;", "#fff ;",
    /// "linear-gradient(...);" -- previews the same as the bare value.
    /// Only one trailing semicolon is stripped: "red;;" or "red; ;"
    /// still won't match, since that's no longer a single clean CSS
    /// value copy.</summary>
    private static string NormalizeCssValueText(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.EndsWith(';'))
            trimmed = trimmed[..^1].TrimEnd();
        return trimmed;
    }

    /// <summary>True when this is a text item whose entire (trimmed)
    /// content is a recognized color value -- hex, rgb()/rgba(),
    /// hsl()/hsla(), or a CSS named color. Drives the swatch preview in
    /// the history list -- see ClipboardPanelWindow.xaml.</summary>
    public bool IsColorValue => Type == ClipboardItemType.Text
        && !string.IsNullOrWhiteSpace(Text)
        && TryParseCssColor(NormalizeCssValueText(Text), out _);

    /// <summary>True when this is a text item whose entire (trimmed)
    /// content is a recognized linear-gradient()/radial-gradient() (or
    /// their repeating- variants) CSS string. Drives the swatch preview
    /// alongside IsColorValue -- see ColorSwatchVisibility/ColorBrush --
    /// but doesn't participate in the "copy as" menu (ConvertColorTo),
    /// since a gradient has no single hex/rgb/hsl equivalent.</summary>
    public bool IsGradientValue => Type == ClipboardItemType.Text
        && !string.IsNullOrWhiteSpace(Text)
        && TryParseCssGradient(NormalizeCssValueText(Text), out _);

    public Visibility ColorSwatchVisibility => (IsColorValue || IsGradientValue) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Brush parsed from Text for the swatch preview -- a flat
    /// SolidColorBrush for a recognized solid color, or a
    /// LinearGradientBrush/RadialGradientBrush for a recognized
    /// linear-gradient()/radial-gradient() string. Only evaluated (and
    /// only bound) when ColorSwatchVisibility is Visible.</summary>
    public Brush? ColorBrush
    {
        get
        {
            // Belt-and-suspenders: this getter is called directly by
            // x:Bind (see ClipboardPanelWindow.xaml), so any unhandled
            // exception here crashes the whole app rather than merely
            // failing to render, the way a classic {Binding} failure
            // would. TryParseCssGradient already guards its own body,
            // but wrapping the getter too means a mistake anywhere in
            // this chain -- present or future -- degrades to "no
            // swatch" instead of taking the app down.
            try
            {
                var text = NormalizeCssValueText(Text);
                if (TryParseCssColor(text, out var color)) return new SolidColorBrush(color);
                if (TryParseCssGradient(text, out var brush)) return brush;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Re-renders this item's color (in whatever format it was
    /// originally copied as -- hex, rgb(), hsl(), or a named color) into
    /// a different format, for the swatch's right-click "copy as" menu.
    /// <paramref name="format"/> is one of "hex", "rgb", "rgba", "hsl",
    /// "hsla". Returns null if this item isn't a recognized color or the
    /// format isn't one of those.</summary>
    public string? ConvertColorTo(string format)
    {
        if (!TryParseCssColor(NormalizeCssValueText(Text), out var color)) return null;

        return format switch
        {
            "hex" => FormatHex(color),
            "rgb" => FormatRgb(color, includeAlpha: false),
            "rgba" => FormatRgb(color, includeAlpha: true),
            "hsl" => FormatHsl(color, includeAlpha: false),
            "hsla" => FormatHsl(color, includeAlpha: true),
            _ => null,
        };
    }

    /// <summary>Attempts to parse <paramref name="text"/> as a CSS color
    /// value in any of the supported formats: hex (#RGB/#RGBA/#RRGGBB/
    /// #RRGGBBAA, with or without the leading '#'), rgb()/rgba(),
    /// hsl()/hsla(), or a CSS named color (e.g. "rebeccapurple"). Matching
    /// requires the entire (trimmed) string to be the color value -- a
    /// color mentioned mid-sentence won't trigger the swatch.</summary>
    private static bool TryParseCssColor(string text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        if (HexColorPattern.IsMatch(text))
        {
            color = ParseHexColor(text);
            return true;
        }

        var rgbMatch = RgbColorPattern.Match(text);
        if (rgbMatch.Success)
        {
            return TryBuildRgbColor(rgbMatch, out color);
        }

        var hslMatch = HslColorPattern.Match(text);
        if (hslMatch.Success)
        {
            return TryBuildHslColor(hslMatch, out color);
        }

        if (NamedColors.TryGetValue(text.ToLowerInvariant(), out var namedHex))
        {
            color = ParseHexColor(namedHex);
            return true;
        }

        return false;
    }

    /// <summary>Attempts to parse <paramref name="text"/> as a CSS
    /// linear-gradient()/radial-gradient() string and build the
    /// equivalent WinUI brush. Handles an optional leading direction
    /// (angle or "to &lt;side/corner&gt;" for linear; shape/size/position,
    /// which is otherwise ignored, for radial -- a small swatch can't
    /// usefully distinguish "circle" from "ellipse" anyway) followed by
    /// two or more comma-separated color stops, each an optional
    /// percentage position after the color. Returns false for anything
    /// that isn't a well-formed gradient of at least two stops.</summary>
    private static bool TryParseCssGradient(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Brush? brush)
    {
        brush = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Safety net: this parser is reached from ColorBrush, which is
        // evaluated by x:Bind. Unlike classic {Binding}, an unhandled
        // exception thrown out of an x:Bind-evaluated property getter
        // crashes the whole app rather than just failing to render.
        // Gradient text comes from arbitrary clipboard content, so no
        // matter how well-tested the parsing logic is, an unexpected
        // input here must never propagate -- worst case we just show
        // no swatch.
        try
        {
            return TryParseCssGradientCore(text, out brush);
        }
        catch
        {
            brush = null;
            return false;
        }
    }

    private static bool TryParseCssGradientCore(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Brush? brush)
    {
        brush = null;

        var match = GradientPattern.Match(text);
        if (!match.Success) return false;

        bool isRadial = string.Equals(match.Groups["kind"].Value, "radial", StringComparison.OrdinalIgnoreCase);
        var parts = SplitTopLevel(match.Groups["inner"].Value, ',');
        if (parts.Count < 2) return false;

        // The first part may be a direction (linear) or a shape/size/
        // position (radial) rather than a color stop -- peel it off
        // when it doesn't parse as one.
        int stopStartIndex = 0;
        double angleDeg = 180; // CSS default direction for linear-gradient() is "to bottom".
        var firstPart = parts[0].Trim();

        if (!isRadial && TryParseLinearDirection(firstPart, out var parsedAngle))
        {
            angleDeg = parsedAngle;
            stopStartIndex = 1;
        }
        else if (isRadial && !TryParseColorStop(firstPart, out _, out _))
        {
            stopStartIndex = 1;
        }

        var rawStops = new List<(Color Color, double? Position)>();
        for (int i = stopStartIndex; i < parts.Count; i++)
        {
            if (!TryParseColorStop(parts[i].Trim(), out var stopColor, out var stopPosition)) return false;
            rawStops.Add((stopColor, stopPosition));
        }
        if (rawStops.Count < 2) return false;

        var resolvedStops = ResolveGradientPositions(rawStops);

        // NOTE: unlike WPF, WinUI's GradientBrush.GradientStops has no
        // setter -- it's a get-only ContentProperty, populated only by
        // adding to the collection the getter already returns. So we
        // can't sidestep the mutability question by assigning a
        // pre-built collection the way WPF allows; the .Add() loop
        // below is the only API WinUI offers. The real safety net here
        // is the try/catch wrapping this whole method (see
        // TryParseCssGradient above): if this brush's own GradientStops
        // ever turned out to be null or non-mutable for some input,
        // that catch is what stops it from taking the app down instead
        // of a defensive rewrite of this loop.
        if (isRadial)
        {
            var radial = new RadialGradientBrush();
            foreach (var (color, offset) in resolvedStops)
                radial.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
            brush = radial;
        }
        else
        {
            var (start, end) = LinearGradientEndpoints(angleDeg);
            var linear = new LinearGradientBrush { StartPoint = start, EndPoint = end };
            foreach (var (color, offset) in resolvedStops)
                linear.GradientStops.Add(new GradientStop { Color = color, Offset = offset });
            brush = linear;
        }

        return true;
    }

    /// <summary>Parses a linear-gradient() direction: either an angle
    /// ("45deg", "0.5turn", "100grad", "1.2rad" -- all converted to
    /// degrees) or a "to &lt;side&gt;"/"to &lt;corner&gt;" keyword phrase
    /// ("to right", "to top left", side order doesn't matter). Returns
    /// false for anything else, which the caller then tries to parse as
    /// a color stop instead (a gradient with no direction specified).</summary>
    private static bool TryParseLinearDirection(string text, out double angleDeg)
    {
        angleDeg = 180;

        var angleMatch = Regex.Match(text, @"^(?<num>-?[\d.]+)\s*(?<unit>deg|grad|rad|turn)$", RegexOptions.IgnoreCase);
        if (angleMatch.Success)
        {
            if (!double.TryParse(angleMatch.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)) return false;
            angleDeg = angleMatch.Groups["unit"].Value.ToLowerInvariant() switch
            {
                "rad" => num * 180.0 / Math.PI,
                "grad" => num * 0.9,
                "turn" => num * 360.0,
                _ => num, // "deg"
            };
            return true;
        }

        if (!text.StartsWith("to ", StringComparison.OrdinalIgnoreCase)) return false;

        var side = text[3..].Trim().ToLowerInvariant();
        bool top = side.Contains("top"), bottom = side.Contains("bottom");
        bool left = side.Contains("left"), right = side.Contains("right");

        angleDeg = (top, right, bottom, left) switch
        {
            (true, false, false, false) => 0,
            (false, true, false, false) => 90,
            (false, false, true, false) => 180,
            (false, false, false, true) => 270,
            (true, true, false, false) => 45,
            (false, true, true, false) => 135,
            (false, false, true, true) => 225,
            (true, false, false, true) => 315,
            _ => double.NaN,
        };

        return !double.IsNaN(angleDeg);
    }

    /// <summary>Parses a single color stop -- a color, optionally
    /// followed by a percentage position ("red", "#fff 20%", "rgba(0,
    /// 0, 0, .5) 100%"). A trailing length in other units (px/em/rem/pt)
    /// is recognized and stripped too, but since the swatch has no page
    /// layout to resolve it against, it's treated as "position
    /// unspecified" the same as a stop with no position at all -- see
    /// ResolveGradientPositions.</summary>
    private static bool TryParseColorStop(string text, out Color color, out double? position)
    {
        color = default;
        position = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var tokens = SplitTopLevel(text, ' ').Where(t => t.Length > 0).ToList();
        if (tokens.Count == 0) return false;

        var lastToken = tokens[^1];
        var lengthMatch = Regex.Match(lastToken, @"^(?<num>-?[\d.]+)(?<unit>%|px|em|rem|pt)$", RegexOptions.IgnoreCase);

        string colorPart = text;
        if (lengthMatch.Success && tokens.Count > 1)
        {
            colorPart = string.Join(' ', tokens.Take(tokens.Count - 1));
            if (string.Equals(lengthMatch.Groups["unit"].Value, "%", StringComparison.Ordinal)
                && double.TryParse(lengthMatch.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                position = Math.Clamp(pct / 100.0, 0.0, 1.0);
            }
        }

        return TryParseCssColor(colorPart.Trim(), out color);
    }

    /// <summary>Fills in offsets for gradient stops that didn't specify
    /// one, using the same algorithm CSS itself uses: the first and last
    /// stops default to 0/1 if unspecified, and any run of unspecified
    /// stops between two resolved ones is spaced evenly across that
    /// span.</summary>
    private static List<(Color Color, double Offset)> ResolveGradientPositions(List<(Color Color, double? Position)> stops)
    {
        var resolved = new (Color Color, double? Position)[stops.Count];
        for (int i = 0; i < stops.Count; i++) resolved[i] = stops[i];

        if (resolved[0].Position is null) resolved[0].Position = 0.0;
        if (resolved[^1].Position is null) resolved[^1].Position = 1.0;

        int cursor = 0;
        while (cursor < resolved.Length)
        {
            if (resolved[cursor].Position is not null) { cursor++; continue; }

            int gapEnd = cursor;
            while (resolved[gapEnd].Position is null) gapEnd++;

            double startOffset = resolved[cursor - 1].Position!.Value;
            double endOffset = resolved[gapEnd].Position!.Value;
            int span = gapEnd - cursor + 1;

            for (int i = cursor; i < gapEnd; i++)
            {
                double t = (i - cursor + 1) / (double)span;
                resolved[i].Position = startOffset + (endOffset - startOffset) * t;
            }

            cursor = gapEnd;
        }

        return resolved.Select(s => (s.Color, s.Position!.Value)).ToList();
    }

    /// <summary>Converts a CSS gradient angle (0deg = "to top", clockwise
    /// from there -- 90deg = "to right", 180deg = "to bottom", etc.) to
    /// a WinUI LinearGradientBrush StartPoint/EndPoint pair. WinUI's
    /// LinearGradientBrush always uses coordinates relative to the
    /// brush's own bounding box (0,0 top-left to 1,1 bottom-right), so
    /// these don't need to account for the swatch's actual pixel size.
    /// The line is extended past the unit square's edges (by the L1
    /// norm of the direction vector) so it fully spans a square box at
    /// any angle, matching the visual effect of CSS's exact
    /// corner-to-corner sizing closely enough for a small preview.</summary>
    private static (Point Start, Point End) LinearGradientEndpoints(double angleDeg)
    {
        double radians = angleDeg * Math.PI / 180.0;
        double dx = Math.Sin(radians);
        double dy = -Math.Cos(radians);

        double half = (Math.Abs(dx) + Math.Abs(dy)) / 2.0;
        if (half == 0) half = 0.5;

        return (
            new Point(0.5 - dx * half, 0.5 - dy * half),
            new Point(0.5 + dx * half, 0.5 + dy * half));
    }

    /// <summary>Splits <paramref name="text"/> on <paramref name="separator"/>,
    /// ignoring any separator that falls inside parentheses -- e.g.
    /// splitting "rgba(0, 0, 0, .5) 10%, red" on ',' yields ["rgba(0, 0,
    /// 0, .5) 10%", " red"], not a spurious split inside the rgba().</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            else if (c == separator && depth == 0)
            {
                parts.Add(text[start..i]);
                start = i + 1;
            }
        }
        parts.Add(text[start..]);

        return parts;
    }

    private static Color ParseHexColor(string hex)
    {
        hex = hex.TrimStart('#');

        // Expand shorthand #RGB / #RGBA to the full 6/8-digit form by
        // doubling each digit (the standard CSS shorthand expansion).
        if (hex.Length is 3 or 4)
        {
            var expanded = new char[hex.Length * 2];
            for (int i = 0; i < hex.Length; i++)
            {
                expanded[i * 2] = hex[i];
                expanded[i * 2 + 1] = hex[i];
            }
            hex = new string(expanded);
        }

        byte r = Convert.ToByte(hex.Substring(0, 2), 16);
        byte g = Convert.ToByte(hex.Substring(2, 2), 16);
        byte b = Convert.ToByte(hex.Substring(4, 2), 16);
        byte a = hex.Length == 8 ? Convert.ToByte(hex.Substring(6, 2), 16) : (byte)255;

        return Color.FromArgb(a, r, g, b);
    }

    /// <summary>Builds a Color from an rgb()/rgba() regex match. Returns
    /// false (rather than clamping) if any channel is out of the valid
    /// 0-255 range, since that means the text wasn't really a color.</summary>
    private static bool TryBuildRgbColor(Match match, out Color color)
    {
        color = default;

        if (!int.TryParse(match.Groups["r"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) || r > 255) return false;
        if (!int.TryParse(match.Groups["g"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) || g > 255) return false;
        if (!int.TryParse(match.Groups["b"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) || b > 255) return false;
        if (!TryParseAlpha(match.Groups["a"], out var a)) return false;

        color = Color.FromArgb(a, (byte)r, (byte)g, (byte)b);
        return true;
    }

    /// <summary>Builds a Color from an hsl()/hsla() regex match by
    /// converting the hue/saturation/lightness triple to RGB.</summary>
    private static bool TryBuildHslColor(Match match, out Color color)
    {
        color = default;

        if (!double.TryParse(match.Groups["h"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var h)) return false;
        if (!double.TryParse(match.Groups["s"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) || s < 0 || s > 100) return false;
        if (!double.TryParse(match.Groups["l"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var l) || l < 0 || l > 100) return false;
        if (!TryParseAlpha(match.Groups["a"], out var a)) return false;

        var (r, g, b) = HslToRgb(((h % 360) + 360) % 360, s / 100.0, l / 100.0);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }

    /// <summary>Parses an optional alpha capture group, which may be a
    /// 0-1 fraction (rgba(0,0,0,0.5)) or a percentage (rgb(0 0 0 / 50%)).
    /// An absent group means fully opaque.</summary>
    private static bool TryParseAlpha(Group group, out byte alpha)
    {
        alpha = 255;
        if (!group.Success || string.IsNullOrEmpty(group.Value)) return true;

        var value = group.Value;
        double fraction;
        if (value.EndsWith('%'))
        {
            if (!double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)) return false;
            fraction = pct / 100.0;
        }
        else
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out fraction)) return false;
        }

        if (fraction < 0 || fraction > 1) return false;
        alpha = (byte)Math.Round(fraction * 255);
        return true;
    }

    private static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
    {
        if (s == 0)
        {
            var gray = (byte)Math.Round(l * 255);
            return (gray, gray, gray);
        }

        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double hk = h / 360.0;

        double r = HueToRgb(p, q, hk + 1.0 / 3.0);
        double g = HueToRgb(p, q, hk);
        double b = HueToRgb(p, q, hk - 1.0 / 3.0);

        return ((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    /// <summary>Formats as "#RRGGBB", or "#RRGGBBAA" if the color isn't
    /// fully opaque (matches the CSS convention this app already reads
    /// on the way in -- see HexColorPattern/ParseHexColor).</summary>
    private static string FormatHex(Color c) => c.A == 255
        ? $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        : $"#{c.R:X2}{c.G:X2}{c.B:X2}{c.A:X2}";

    /// <summary>Formats as "rgb(r, g, b)" or, with alpha, "rgba(r, g, b,
    /// a)" where a is a 0-1 fraction rounded to 2 decimal places.</summary>
    private static string FormatRgb(Color c, bool includeAlpha) => includeAlpha
        ? $"rgba({c.R}, {c.G}, {c.B}, {AlphaFraction(c.A)})"
        : $"rgb({c.R}, {c.G}, {c.B})";

    /// <summary>Formats as "hsl(h, s%, l%)" or, with alpha, "hsla(h, s%,
    /// l%, a)", converting the stored RGB to HSL first.</summary>
    private static string FormatHsl(Color c, bool includeAlpha)
    {
        var (h, s, l) = RgbToHsl(c.R, c.G, c.B);
        var hsl = $"{Math.Round(h)}, {Math.Round(s)}%, {Math.Round(l)}%";

        return includeAlpha
            ? $"hsla({hsl}, {AlphaFraction(c.A)})"
            : $"hsl({hsl})";
    }

    /// <summary>Alpha byte (0-255) as a 0-1 fraction string, e.g. 204 ->
    /// "0.8". Always formatted with an invariant '.' decimal separator
    /// (see the culture note on TryParseAlpha) regardless of the user's
    /// locale, since that's what CSS/rgba()/hsla() require.</summary>
    private static string AlphaFraction(byte alpha) =>
        Math.Round(alpha / 255.0, 2, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    /// <summary>Converts an RGB triple to HSL (hue in degrees 0-360,
    /// saturation/lightness as 0-100 percentages) -- the inverse of
    /// HslToRgb, used to render "hsl()"/"hsla()" for colors that weren't
    /// originally copied in that format.</summary>
    private static (double h, double s, double l) RgbToHsl(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0;

        if (max == min)
            return (0, 0, l * 100); // achromatic (gray)

        double d = max - min;
        double s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);

        double h;
        if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;

        return (h, s * 100, l * 100);
    }

    /// <summary>The 148 CSS Color Module Level 4 named colors (the
    /// standard "extended" X11 keyword set recognized by every modern
    /// browser), mapped to their hex equivalents so they can reuse
    /// ParseHexColor. Keys are lowercase for case-insensitive lookup.</summary>
    private static readonly Dictionary<string, string> NamedColors = new()
    {
        ["aliceblue"] = "#F0F8FF", ["antiquewhite"] = "#FAEBD7", ["aqua"] = "#00FFFF",
        ["aquamarine"] = "#7FFFD4", ["azure"] = "#F0FFFF", ["beige"] = "#F5F5DC",
        ["bisque"] = "#FFE4C4", ["black"] = "#000000", ["blanchedalmond"] = "#FFEBCD",
        ["blue"] = "#0000FF", ["blueviolet"] = "#8A2BE2", ["brown"] = "#A52A2A",
        ["burlywood"] = "#DEB887", ["cadetblue"] = "#5F9EA0", ["chartreuse"] = "#7FFF00",
        ["chocolate"] = "#D2691E", ["coral"] = "#FF7F50", ["cornflowerblue"] = "#6495ED",
        ["cornsilk"] = "#FFF8DC", ["crimson"] = "#DC143C", ["cyan"] = "#00FFFF",
        ["darkblue"] = "#00008B", ["darkcyan"] = "#008B8B", ["darkgoldenrod"] = "#B8860B",
        ["darkgray"] = "#A9A9A9", ["darkgreen"] = "#006400", ["darkgrey"] = "#A9A9A9",
        ["darkkhaki"] = "#BDB76B", ["darkmagenta"] = "#8B008B", ["darkolivegreen"] = "#556B2F",
        ["darkorange"] = "#FF8C00", ["darkorchid"] = "#9932CC", ["darkred"] = "#8B0000",
        ["darksalmon"] = "#E9967A", ["darkseagreen"] = "#8FBC8F", ["darkslateblue"] = "#483D8B",
        ["darkslategray"] = "#2F4F4F", ["darkslategrey"] = "#2F4F4F", ["darkturquoise"] = "#00CED1",
        ["darkviolet"] = "#9400D3", ["deeppink"] = "#FF1493", ["deepskyblue"] = "#00BFFF",
        ["dimgray"] = "#696969", ["dimgrey"] = "#696969", ["dodgerblue"] = "#1E90FF",
        ["firebrick"] = "#B22222", ["floralwhite"] = "#FFFAF0", ["forestgreen"] = "#228B22",
        ["fuchsia"] = "#FF00FF", ["gainsboro"] = "#DCDCDC", ["ghostwhite"] = "#F8F8FF",
        ["gold"] = "#FFD700", ["goldenrod"] = "#DAA520", ["gray"] = "#808080",
        ["green"] = "#008000", ["greenyellow"] = "#ADFF2F", ["grey"] = "#808080",
        ["honeydew"] = "#F0FFF0", ["hotpink"] = "#FF69B4", ["indianred"] = "#CD5C5C",
        ["indigo"] = "#4B0082", ["ivory"] = "#FFFFF0", ["khaki"] = "#F0E68C",
        ["lavender"] = "#E6E6FA", ["lavenderblush"] = "#FFF0F5", ["lawngreen"] = "#7CFC00",
        ["lemonchiffon"] = "#FFFACD", ["lightblue"] = "#ADD8E6", ["lightcoral"] = "#F08080",
        ["lightcyan"] = "#E0FFFF", ["lightgoldenrodyellow"] = "#FAFAD2", ["lightgray"] = "#D3D3D3",
        ["lightgreen"] = "#90EE90", ["lightgrey"] = "#D3D3D3", ["lightpink"] = "#FFB6C1",
        ["lightsalmon"] = "#FFA07A", ["lightseagreen"] = "#20B2AA", ["lightskyblue"] = "#87CEFA",
        ["lightslategray"] = "#778899", ["lightslategrey"] = "#778899", ["lightsteelblue"] = "#B0C4DE",
        ["lightyellow"] = "#FFFFE0", ["lime"] = "#00FF00", ["limegreen"] = "#32CD32",
        ["linen"] = "#FAF0E6", ["magenta"] = "#FF00FF", ["maroon"] = "#800000",
        ["mediumaquamarine"] = "#66CDAA", ["mediumblue"] = "#0000CD", ["mediumorchid"] = "#BA55D3",
        ["mediumpurple"] = "#9370DB", ["mediumseagreen"] = "#3CB371", ["mediumslateblue"] = "#7B68EE",
        ["mediumspringgreen"] = "#00FA9A", ["mediumturquoise"] = "#48D1CC", ["mediumvioletred"] = "#C71585",
        ["midnightblue"] = "#191970", ["mintcream"] = "#F5FFFA", ["mistyrose"] = "#FFE4E1",
        ["moccasin"] = "#FFE4B5", ["navajowhite"] = "#FFDEAD", ["navy"] = "#000080",
        ["oldlace"] = "#FDF5E6", ["olive"] = "#808000", ["olivedrab"] = "#6B8E23",
        ["orange"] = "#FFA500", ["orangered"] = "#FF4500", ["orchid"] = "#DA70D6",
        ["palegoldenrod"] = "#EEE8AA", ["palegreen"] = "#98FB98", ["paleturquoise"] = "#AFEEEE",
        ["palevioletred"] = "#DB7093", ["papayawhip"] = "#FFEFD5", ["peachpuff"] = "#FFDAB9",
        ["peru"] = "#CD853F", ["pink"] = "#FFC0CB", ["plum"] = "#DDA0DD",
        ["powderblue"] = "#B0E0E6", ["purple"] = "#800080", ["rebeccapurple"] = "#663399",
        ["red"] = "#FF0000", ["rosybrown"] = "#BC8F8F", ["royalblue"] = "#4169E1",
        ["saddlebrown"] = "#8B4513", ["salmon"] = "#FA8072", ["sandybrown"] = "#F4A460",
        ["seagreen"] = "#2E8B57", ["seashell"] = "#FFF5EE", ["sienna"] = "#A0522D",
        ["silver"] = "#C0C0C0", ["skyblue"] = "#87CEEB", ["slateblue"] = "#6A5ACD",
        ["slategray"] = "#708090", ["slategrey"] = "#708090", ["snow"] = "#FFFAFA",
        ["springgreen"] = "#00FF7F", ["steelblue"] = "#4682B4", ["tan"] = "#D2B48C",
        ["teal"] = "#008080", ["thistle"] = "#D8BFD8", ["tomato"] = "#FF6347",
        ["turquoise"] = "#40E0D0", ["violet"] = "#EE82EE", ["wheat"] = "#F5DEB3",
        ["white"] = "#FFFFFF", ["whitesmoke"] = "#F5F5F5", ["yellow"] = "#FFFF00",
        ["yellowgreen"] = "#9ACD32",
    };
}