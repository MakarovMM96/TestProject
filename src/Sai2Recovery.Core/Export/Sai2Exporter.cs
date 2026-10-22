using Sai2Recovery.Core.Codecs;
using Sai2Recovery.Core.Format;
using Sai2Recovery.Core.Imaging;
using Sai2Recovery.Core.Model;

namespace Sai2Recovery.Core.Export;

public sealed class ExportOptions
{
    public bool Composite = true;
    public bool Thumbnail = true;
    public bool Layers = true;
    public bool Ora = true;
}

public sealed class ExportedLayer
{
    public required uint Id;
    public required string Name;
    public required BgraImage Image;
    public double Opacity = 1.0;
    public bool Visible = true;
    public int DamagedTiles;
}

/// <summary>
/// Saves everything readable as ordinary images: the merged picture, the
/// thumbnail and every layer (PNG + a layered OpenRaster file).
/// This works even when a valid .sai2 cannot be produced.
/// </summary>
public static class Sai2Exporter
{
    public static BgraImage? RenderComposite(Sai2Analysis a)
    {
        var c = a.Composite;
        if (c?.Pixels == null || c.Pixels.LengthCount == 0) return null;
        var img = c.Pixels.Render(c.Bytes(a.Data), 0u, 0u);
        if (c.Pixels.Channels == 4) img.UnpremultiplyIfNeeded();
        return img;
    }

    public static byte[]? ThumbnailJpeg(Sai2Analysis a)
    {
        var t = a.Thumbnail;
        if (t == null || t.Health == ChunkHealth.Lost) return null;
        return JssfCodec.ToJpeg(t.Bytes(a.Data), out _, out _);
    }

    public static List<ExportedLayer> RenderLayers(Sai2Analysis a)
    {
        var result = new List<ExportedLayer>();
        var pixelChunks = a.Chunks.Where(c => (c.Type == Sai2Format.Lpix || c.Type == Sai2Format.Fpix) && c.Pixels != null && c.Pixels.LengthCount > 0);
        foreach (var px in pixelChunks)
        {
            var layr = a.Chunks.FirstOrDefault(c => c.Type == Sai2Format.Layr && c.Id == px.Id);
            string name = layr?.Layer?.Name is { Length: > 0 } n ? n : $"Слой {px.Id}";
            var img = px.Pixels!.Render(px.Bytes(a.Data), 0u, 0u);
            if (px.Pixels.Channels == 4) img.UnpremultiplyIfNeeded();
            double opacity = layr?.Layer is { Recognized: true } l && l.Opacity <= 100 ? l.Opacity / 100.0 : 1.0;
            result.Add(new ExportedLayer
            {
                Id = px.Id,
                Name = name,
                Image = img,
                Opacity = opacity,
                DamagedTiles = px.Pixels.CorruptTiles + px.Pixels.MissingTiles,
            });
        }
        return result;
    }

    /// <returns>Paths of written files.</returns>
    public static List<string> Export(Sai2Analysis a, string folder, ExportOptions? options = null)
    {
        options ??= new ExportOptions();
        Directory.CreateDirectory(folder);
        var written = new List<string>();

        BgraImage? composite = null;
        if (options.Composite || options.Ora)
            composite = RenderComposite(a);
        if (options.Composite && composite != null)
        {
            string p = Path.Combine(folder, "сведённое_изображение.png");
            PngWriter.Save(p, composite);
            written.Add(p);
        }

        if (options.Thumbnail)
        {
            var jpg = ThumbnailJpeg(a);
            if (jpg != null)
            {
                string p = Path.Combine(folder, "миниатюра.jpg");
                File.WriteAllBytes(p, jpg);
                written.Add(p);
            }
        }

        if (options.Layers || options.Ora)
        {
            var layers = RenderLayers(a);
            if (options.Layers && layers.Count > 0)
            {
                string dir = Path.Combine(folder, "слои");
                Directory.CreateDirectory(dir);
                for (int i = 0; i < layers.Count; i++)
                {
                    string p = Path.Combine(dir, $"{i + 1:D2}_{SafeName(layers[i].Name)}.png");
                    PngWriter.Save(p, layers[i].Image);
                    written.Add(p);
                }
            }
            if (options.Ora && layers.Count > 0)
            {
                var merged = composite ?? layers[^1].Image;
                string p = Path.Combine(folder, "слои.ora");
                OraWriter.Save(p, (int)a.Header.Width, (int)a.Header.Height,
                    layers.Select(l => new OraLayer(l.Name, l.Image, l.Opacity, l.Visible)).ToList(), merged);
                written.Add(p);
            }
        }
        return written;
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var chars = name.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch).ToArray();
        var s = new string(chars).Trim().TrimEnd('.');
        if (s.Length > 60) s = s[..60];
        return s.Length == 0 ? "слой" : s;
    }
}
