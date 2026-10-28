using System.Text.Json;
using System.Text.RegularExpressions;

namespace PCVitals;

/// <summary>Text tiles: placeholders such as <c>{cputemp}</c> or <c>{sensor:/amdcpu/0/temperature/2}</c> become readings.</summary>
static class PanelText
{
    static readonly Regex Placeholder = new(@"\{([A-Za-z0-9:/._#-]+)\}", RegexOptions.Compiled);

    /// <summary>The placeholders a text tile understands, for the editor's "Insert reading" menu.</summary>
    public static List<(string Placeholder, string Name)> Available(Summary? s) =>
        PanelSources.All(s).Select(x => ("{" + x.Key + "}", x.Name)).ToList();

    public static string Expand(string text, Snapshot? snap, AppSettings? settings)
    {
        if (snap == null || text.IndexOf('{') < 0) return text;
        return Placeholder.Replace(text, m =>
        {
            var key = m.Groups[1].Value;
            var tile = key.StartsWith("sensor:", StringComparison.OrdinalIgnoreCase)
                ? snap.Catalog[key[7..]] is { } entry ? new PanelTile { Sensor = entry.Key } : null
                : new PanelTile { Source = key };
            if (tile == null || PanelSources.Read(tile, snap, settings) is not { } r) return m.Value;
            if (r.Text != null) return r.Value == null ? "--" : r.Text;
            var (number, unit) = PanelSources.Format(r.Value, r.Unit, null);
            return unit.Length == 0 ? number : unit is "%" || unit.StartsWith('°') ? number + unit : number + " " + unit;
        });
    }

    /// <summary>Whether text has placeholders (and so changes with readings).</summary>
    public static bool IsLive(string? text) => text != null && Placeholder.IsMatch(text);
}

/// <summary>
/// Pictures used by panels (backgrounds and image tiles), copied into PC Vitals' own folder so a panel keeps
/// working if the original file is moved. Loaded once and kept while in use.
/// </summary>
static class PanelImages
{
    public static string Dir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PCVitals", "panels");

    static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    static readonly Dictionary<string, Image?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public const string FileFilter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";

    /// <summary>Copies a picture into the panels folder and returns its new file name; null if it isn't a picture we can show.</summary>
    public static string? Import(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Extensions.Contains(ext)) return null;
        try
        {
            using (var probe = Image.FromFile(path)) { } // refuse files that don't decode
            Directory.CreateDirectory(Dir);
            var name = Guid.NewGuid().ToString("N") + ext;
            File.Copy(path, Path.Combine(Dir, name));
            return name;
        }
        catch (Exception ex)
        {
            Program.Log($"Couldn't add picture {path}: {ex.Message}");
            return null;
        }
    }

    /// <summary>The picture, or null when the file is missing or broken.</summary>
    public static Image? Get(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            Image? img = null;
            try
            {
                // Copied into memory, so the file isn't kept locked.
                var file = Path.Combine(Dir, name);
                if (File.Exists(file))
                    using (var stream = new MemoryStream(File.ReadAllBytes(file)))
                    using (var loaded = Image.FromStream(stream))
                        img = new Bitmap(loaded);
            }
            catch (Exception ex)
            {
                Program.Log($"Couldn't load panel picture {name}: {ex.Message}");
            }
            return Cache[name] = img;
        }
    }

    /// <summary>Forgets loaded pictures no panel uses any more (and deletes their files).</summary>
    public static void Prune(IEnumerable<PanelLayout> panels)
    {
        var used = panels.SelectMany(p => p.Tiles.Select(t => t.Image).Append(p.Style.BackgroundImage))
                         .Where(n => n != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (Cache)
            foreach (var name in Cache.Keys.Where(k => !used.Contains(k)).ToList())
            {
                Cache[name]?.Dispose();
                Cache.Remove(name);
            }
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var file in Directory.EnumerateFiles(Dir))
                if (!used.Contains(Path.GetFileName(file))) File.Delete(file);
        }
        catch (Exception ex)
        {
            Program.Log("Couldn't tidy the panel pictures: " + ex.Message);
        }
    }

    /// <summary>Draws a picture into an area the way <paramref name="fit"/> says, clipped to it.</summary>
    public static void Draw(Graphics g, Image img, Rectangle area, PanelImageFit fit)
    {
        if (area.Width <= 0 || area.Height <= 0) return;
        var state = g.Save();
        g.SetClip(area, System.Drawing.Drawing2D.CombineMode.Intersect);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        float kx = area.Width / (float)img.Width, ky = area.Height / (float)img.Height;
        switch (fit)
        {
            case PanelImageFit.Stretch:
                g.DrawImage(img, area);
                break;
            case PanelImageFit.Tile:
                using (var brush = new TextureBrush(img) { WrapMode = System.Drawing.Drawing2D.WrapMode.Tile })
                {
                    brush.TranslateTransform(area.Left, area.Top);
                    g.FillRectangle(brush, area);
                }
                break;
            case PanelImageFit.Center:
                g.DrawImage(img, area.Left + (area.Width - img.Width) / 2, area.Top + (area.Height - img.Height) / 2, img.Width, img.Height);
                break;
            default:
            {
                float k = fit == PanelImageFit.Fill ? Math.Max(kx, ky) : Math.Min(kx, ky);
                float w = img.Width * k, h = img.Height * k;
                g.DrawImage(img, area.Left + (area.Width - w) / 2, area.Top + (area.Height - h) / 2, w, h);
                break;
            }
        }
        g.Restore(state);
    }
}

/// <summary>Layouts saved to and read from a file, pictures included, to move them between PCs.</summary>
static class PanelExchange
{
    public const string FileFilter = "PC Vitals panel (*.pcvpanel.json)|*.pcvpanel.json|JSON files (*.json)|*.json";

    sealed class FileFormat
    {
        public int PcVitalsPanel { get; set; } = 1;
        public PanelLayout? Layout { get; set; }
        /// <summary>Pictures by file name, base64.</summary>
        public Dictionary<string, string> Images { get; set; } = new();
    }

    public static void Export(PanelLayout layout, string path)
    {
        var file = new FileFormat { Layout = layout.Clone() };
        // Where it sits on this PC's monitors means nothing on another one.
        file.Layout.Monitor = null;
        file.Layout.MonitorSize = Size.Empty;
        file.Layout.Position = null;
        foreach (var name in layout.Tiles.Select(t => t.Image).Append(layout.Style.BackgroundImage).Where(n => n != null).Distinct())
        {
            var source = Path.Combine(PanelImages.Dir, name!);
            if (File.Exists(source)) file.Images[name!] = Convert.ToBase64String(File.ReadAllBytes(source));
        }
        File.WriteAllText(path, JsonSerializer.Serialize(file, AppSettings.Json));
    }

    /// <summary>
    /// A layout from a file, with a new id and its pictures written to the panels folder. Sensors are kept by
    /// key; on another PC they are found by name (or show "--"). Throws <see cref="InvalidDataException"/> with a
    /// reason to show when the file isn't a panel.
    /// </summary>
    public static PanelLayout Import(string path)
    {
        FileFormat? file;
        try { file = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(path), AppSettings.Json); }
        catch (JsonException ex) { throw new InvalidDataException("The file isn't a PC Vitals panel: " + ex.Message, ex); }
        if (file?.Layout is not { } layout || file.PcVitalsPanel < 1) throw new InvalidDataException("The file isn't a PC Vitals panel.");
        layout.Id = Guid.NewGuid().ToString("N");
        layout.Style ??= new PanelStyle();
        layout.Tiles ??= new();
        layout.Tiles.RemoveAll(t => t == null!);
        layout.Enabled = true;

        // Pictures get new names, so two imports of the same file don't share (and later delete) each other's.
        var renamed = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, data) in file.Images ?? new())
        {
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")) continue;
            try
            {
                Directory.CreateDirectory(PanelImages.Dir);
                var fresh = Guid.NewGuid().ToString("N") + ext;
                File.WriteAllBytes(Path.Combine(PanelImages.Dir, fresh), Convert.FromBase64String(data));
                renamed[name] = fresh;
            }
            catch (FormatException) { }
        }
        string? Rename(string? n) => n != null && renamed.TryGetValue(n, out var r) ? r : null;
        layout.Style.BackgroundImage = Rename(layout.Style.BackgroundImage);
        foreach (var t in layout.Tiles) t.Image = Rename(t.Image);
        return layout;
    }
}
