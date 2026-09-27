using System.Globalization;
using System.Net;
using System.Text;
using System.Xml;
using JoePro.Documents.Reports;
using SkiaSharp;

namespace JoePro.Reports;

/// <summary>Draws pages with SkiaSharp: the same code produces PDF files (fonts embedded) and page images.</summary>
public static class SkiaPainter
{
    private static SKColor Color(int vfp) => new((byte)(vfp & 0xFF), (byte)((vfp >> 8) & 0xFF), (byte)((vfp >> 16) & 0xFF));

    private static SKPathEffect? Dash(PenStyle style, float width) => style switch
    {
        PenStyle.Dot => SKPathEffect.CreateDash([width, width * 2], 0),
        PenStyle.Dash => SKPathEffect.CreateDash([width * 4, width * 2], 0),
        PenStyle.DashDot => SKPathEffect.CreateDash([width * 4, width * 2, width, width * 2], 0),
        PenStyle.DashDotDot => SKPathEffect.CreateDash([width * 4, width * 2, width, width * 2, width, width * 2], 0),
        _ => null,
    };

    public static void Paint(SKCanvas canvas, ReportPage page)
    {
        canvas.Clear(SKColors.White);
        foreach (var item in page.Items)
        {
            switch (item)
            {
                case RectItem r:
                {
                    var rect = new SKRect((float)r.X, (float)r.Y, (float)(r.X + r.W), (float)(r.Y + r.H));
                    var radius = r.Curvature >= 99 ? Math.Min(rect.Width, rect.Height) / 2 : r.Curvature / 99f * Math.Min(rect.Width, rect.Height) / 2;
                    if (r.FillColor is { } fill)
                    {
                        using var p = new SKPaint { Color = Color(fill), Style = SKPaintStyle.Fill, IsAntialias = true };
                        if (r.Curvature >= 99) canvas.DrawOval(rect, p); else canvas.DrawRoundRect(rect, radius, radius, p);
                    }
                    if (r.LineColor is { } line && r.Style != PenStyle.None)
                    {
                        using var p = new SKPaint { Color = Color(line), Style = SKPaintStyle.Stroke, StrokeWidth = (float)r.PenWidth, IsAntialias = true, PathEffect = Dash(r.Style, (float)r.PenWidth) };
                        if (r.Curvature >= 99) canvas.DrawOval(rect, p); else canvas.DrawRoundRect(rect, radius, radius, p);
                    }
                    break;
                }
                case LineItem l when l.Style != PenStyle.None:
                {
                    using var p = new SKPaint { Color = Color(l.Color), StrokeWidth = (float)l.PenWidth, IsAntialias = true, PathEffect = Dash(l.Style, (float)l.PenWidth) };
                    if (l.Vertical) canvas.DrawLine((float)l.X, (float)l.Y, (float)l.X, (float)(l.Y + l.H), p);
                    else canvas.DrawLine((float)l.X, (float)l.Y, (float)(l.X + l.W), (float)l.Y, p);
                    break;
                }
                case ImageItem img:
                    DrawImage(canvas, img);
                    break;
                case TextItem t:
                    DrawText(canvas, t);
                    break;
            }
        }
    }

    private static void DrawText(SKCanvas canvas, TextItem t)
    {
        if (t.Back is { } back)
        {
            using var bg = new SKPaint { Color = Color(back), Style = SKPaintStyle.Fill };
            canvas.DrawRect((float)t.X, (float)t.Y, (float)t.W, (float)t.H, bg);
        }
        using var font = Fonts.Font(t.Font);
        using var paint = new SKPaint { Color = Color(t.Color), IsAntialias = true };
        canvas.Save();
        canvas.ClipRect(new SKRect((float)t.X - 1, (float)t.Y - 1, (float)(t.X + t.W + 1), (float)(t.Y + t.H + 1)));
        var y = t.Y + t.Ascent;
        foreach (var line in t.Lines)
        {
            var width = font.MeasureText(line);
            var x = t.Align switch
            {
                TextAlign.Right => t.X + t.W - width,
                TextAlign.Center => t.X + (t.W - width) / 2,
                _ => t.X,
            };
            canvas.DrawText(line, (float)x, (float)y, font, paint);
            if (t.Font.Underline || t.Font.Strikeout)
            {
                using var deco = new SKPaint { Color = paint.Color, StrokeWidth = Math.Max(0.5f, (float)t.Font.Size / 16), IsAntialias = true };
                if (t.Font.Underline) canvas.DrawLine((float)x, (float)y + 1.5f, (float)(x + width), (float)y + 1.5f, deco);
                if (t.Font.Strikeout) canvas.DrawLine((float)x, (float)(y - t.Ascent * 0.3), (float)(x + width), (float)(y - t.Ascent * 0.3), deco);
            }
            y += t.LineHeight;
        }
        canvas.Restore();
    }

    private static void DrawImage(SKCanvas canvas, ImageItem img)
    {
        using var bitmap = SKBitmap.Decode(img.Data);
        if (bitmap == null) return;
        var box = new SKRect((float)img.X, (float)img.Y, (float)(img.X + img.W), (float)(img.Y + img.H));
        SKRect dest;
        switch (img.Scale)
        {
            case PictureScale.Stretch: dest = box; break;
            case PictureScale.Clip:
                // Actual size (at 96 dpi) from the top left, cut to the frame.
                dest = new SKRect(box.Left, box.Top, box.Left + bitmap.Width * 0.75f, box.Top + bitmap.Height * 0.75f);
                break;
            default:
            {
                var scale = Math.Min(box.Width / bitmap.Width, box.Height / bitmap.Height);
                var w = bitmap.Width * scale;
                var h = bitmap.Height * scale;
                dest = new SKRect(box.Left, box.Top, box.Left + w, box.Top + h);
                break;
            }
        }
        canvas.Save();
        canvas.ClipRect(box);
        using var image = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(image, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        canvas.Restore();
    }
}

public static class PdfRenderer
{
    public static void Write(RenderedReport report, Stream output)
    {
        var meta = new SKDocumentPdfMetadata
        {
            Title = report.Title, Creator = "Joe Pro", Producer = "Joe Pro report engine",
            Creation = DateTime.Now, Modified = DateTime.Now, RasterDpi = 300, EncodingQuality = 95,
        };
        using var doc = SKDocument.CreatePdf(output, meta);
        foreach (var page in report.Pages)
        {
            var canvas = doc.BeginPage((float)page.Width, (float)page.Height);
            SkiaPainter.Paint(canvas, page);
            doc.EndPage();
        }
        doc.Close();
    }

    public static void Write(RenderedReport report, string path)
    {
        using var fs = File.Create(path);
        Write(report, fs);
    }
}

public static class ImageRenderer
{
    /// <summary>A page as a PNG at the given resolution (96 = screen size).</summary>
    public static byte[] Png(ReportPage page, double dpi = 96)
    {
        var scale = (float)(dpi / 72.0);
        var info = new SKImageInfo((int)Math.Ceiling(page.Width * scale), (int)Math.Ceiling(page.Height * scale));
        using var surface = SKSurface.Create(info);
        surface.Canvas.Scale(scale);
        SkiaPainter.Paint(surface.Canvas, page);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Writes name.png for one page, name_1.png … for several.</summary>
    public static List<string> Write(RenderedReport report, string path, double dpi = 96)
    {
        var files = new List<string>();
        for (int i = 0; i < report.Pages.Count; i++)
        {
            var file = report.Pages.Count == 1 ? path : Path.Combine(Path.GetDirectoryName(path) ?? "", $"{Path.GetFileNameWithoutExtension(path)}_{i + 1}{Path.GetExtension(path)}");
            File.WriteAllBytes(file, Png(report.Pages[i], dpi));
            files.Add(file);
        }
        return files;
    }
}

/// <summary>HTML with each page as a positioned block (images embedded), for browsers and e-mail.</summary>
public static class HtmlRenderer
{
    private static string Css(int color) => $"#{color & 0xFF:x2}{(color >> 8) & 0xFF:x2}{(color >> 16) & 0xFF:x2}";
    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Render(RenderedReport report)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode(report.Title)).Append("</title>\n<style>\n")
          .Append("body{background:#e8e8e8;margin:0;padding:16px;font-family:sans-serif}\n")
          .Append(".page{position:relative;background:#fff;margin:0 auto 16px;box-shadow:0 1px 4px rgba(0,0,0,.3);overflow:hidden}\n")
          .Append(".t{position:absolute;white-space:pre;overflow:hidden;line-height:normal}\n.i{position:absolute}\n")
          .Append("@media print{body{background:none;padding:0}.page{box-shadow:none;margin:0;page-break-after:always}}\n</style></head><body>\n");
        foreach (var page in report.Pages)
        {
            sb.Append($"<div class=\"page\" style=\"width:{N(page.Width)}pt;height:{N(page.Height)}pt\">\n");
            foreach (var item in page.Items)
            {
                switch (item)
                {
                    case TextItem t:
                        sb.Append($"<div class=\"t\" style=\"left:{N(t.X)}pt;top:{N(t.Y)}pt;width:{N(t.W)}pt;height:{N(t.H)}pt;")
                          .Append($"font-family:'{WebUtility.HtmlEncode(t.Font.Family)}';font-size:{N(t.Font.Size)}pt;line-height:{N(t.LineHeight)}pt;color:{Css(t.Color)};")
                          .Append(t.Font.Bold ? "font-weight:bold;" : "").Append(t.Font.Italic ? "font-style:italic;" : "")
                          .Append(t.Font.Underline || t.Font.Strikeout ? $"text-decoration:{(t.Font.Underline ? "underline " : "")}{(t.Font.Strikeout ? "line-through" : "")};" : "")
                          .Append(t.Back is { } b ? $"background:{Css(b)};" : "")
                          .Append(t.Align switch { TextAlign.Right => "text-align:right;", TextAlign.Center => "text-align:center;", _ => "" })
                          .Append("\">").Append(WebUtility.HtmlEncode(t.Text)).Append("</div>\n");
                        break;
                    case LineItem l when l.Style != PenStyle.None:
                        var border = $"{N(l.PenWidth)}pt {(l.Style == PenStyle.Dot ? "dotted" : l.Style == PenStyle.Solid ? "solid" : "dashed")} {Css(l.Color)}";
                        sb.Append($"<div class=\"i\" style=\"left:{N(l.X)}pt;top:{N(l.Y)}pt;")
                          .Append(l.Vertical ? $"height:{N(l.H)}pt;border-left:{border}" : $"width:{N(l.W)}pt;border-top:{border}").Append("\"></div>\n");
                        break;
                    case RectItem r:
                        sb.Append($"<div class=\"i\" style=\"left:{N(r.X)}pt;top:{N(r.Y)}pt;width:{N(r.W)}pt;height:{N(r.H)}pt;box-sizing:border-box;")
                          .Append(r.LineColor is { } lc ? $"border:{N(r.PenWidth)}pt solid {Css(lc)};" : "")
                          .Append(r.FillColor is { } fc ? $"background:{Css(fc)};" : "")
                          .Append(r.Curvature >= 99 ? "border-radius:50%;" : r.Curvature > 0 ? $"border-radius:{N(r.Curvature / 99.0 * Math.Min(r.W, r.H) / 2)}pt;" : "")
                          .Append("\"></div>\n");
                        break;
                    case ImageItem img:
                        sb.Append($"<img class=\"i\" style=\"left:{N(img.X)}pt;top:{N(img.Y)}pt;width:{N(img.W)}pt;height:{N(img.H)}pt;")
                          .Append(img.Scale == PictureScale.Stretch ? "object-fit:fill" : img.Scale == PictureScale.Clip ? "object-fit:none;object-position:0 0" : "object-fit:contain;object-position:0 0")
                          .Append("\" src=\"data:image;base64,").Append(Convert.ToBase64String(img.Data)).Append("\">\n");
                        break;
                }
            }
            sb.Append("</div>\n");
        }
        sb.Append("</body></html>\n");
        return sb.ToString();
    }
}

/// <summary>Plain text on a character grid (10 characters per inch, 6 lines per inch); pages are separated by form feeds.</summary>
public static class TextRenderer
{
    public const double CharWidth = 7.2, LineHeight = 12;

    public static string Render(RenderedReport report, bool formFeeds = true)
    {
        var sb = new StringBuilder();
        for (int p = 0; p < report.Pages.Count; p++)
        {
            if (p > 0) sb.Append(formFeeds ? "\f" : "\n");
            var page = report.Pages[p];
            var rows = new SortedDictionary<int, char[]>();
            char[] Row(int r)
            {
                if (!rows.TryGetValue(r, out var row)) rows[r] = row = Enumerable.Repeat(' ', (int)Math.Ceiling(page.Width / CharWidth)).ToArray();
                return row;
            }
            foreach (var t in page.Items.OfType<TextItem>())
            {
                var lineStep = Math.Max(1, (int)Math.Round(t.LineHeight / LineHeight));
                var width = Math.Max(1, (int)Math.Round(t.W / CharWidth));
                for (int i = 0; i < t.Lines.Count; i++)
                {
                    var text = t.Lines[i];
                    if (text.Length > width) text = text[..width];
                    var col = (int)Math.Round(t.X / CharWidth) + t.Align switch
                    {
                        TextAlign.Right => width - text.Length,
                        TextAlign.Center => (width - text.Length) / 2,
                        _ => 0,
                    };
                    var row = Row((int)Math.Round(t.Y / LineHeight) + i * lineStep);
                    for (int c = 0; c < text.Length && col + c < row.Length; c++)
                        if (col + c >= 0) row[col + c] = text[c];
                }
            }
            if (rows.Count == 0) continue;
            var first = rows.Keys.First();
            var last = rows.Keys.Last();
            for (int r = first; r <= last; r++)
                sb.Append(rows.TryGetValue(r, out var row) ? new string(row).TrimEnd() : "").Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>The page model as XML (positions in points): for other tools and tests.</summary>
public static class XmlRenderer
{
    public static string Render(RenderedReport report)
    {
        var sb = new StringBuilder();
        using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false }))
        {
            string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
            w.WriteStartElement("report");
            w.WriteAttributeString("title", report.Title);
            w.WriteAttributeString("pages", report.Pages.Count.ToString(CultureInfo.InvariantCulture));
            foreach (var page in report.Pages)
            {
                w.WriteStartElement("page");
                w.WriteAttributeString("number", page.Number.ToString(CultureInfo.InvariantCulture));
                w.WriteAttributeString("width", N(page.Width));
                w.WriteAttributeString("height", N(page.Height));
                foreach (var item in page.Items)
                {
                    w.WriteStartElement(item switch { TextItem => "text", LineItem => "line", RectItem => "rect", _ => "image" });
                    w.WriteAttributeString("x", N(item.X));
                    w.WriteAttributeString("y", N(item.Y));
                    w.WriteAttributeString("w", N(item.W));
                    w.WriteAttributeString("h", N(item.H));
                    if (item is TextItem t) w.WriteString(t.Text);
                    w.WriteEndElement();
                }
                w.WriteEndElement();
            }
            w.WriteEndElement();
        }
        return sb.ToString();
    }
}
