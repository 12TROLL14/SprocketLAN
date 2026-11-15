using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SprocketMP
{
    /// <summary>
    /// Custom decals and paint textures travel with a lobby design. A blueprint points to its images by URL:
    /// "imageURL": "Sprocket/Decals/x.png" (decals), "colourMapUrl": "Sprocket/Paint/y.png" (paint), files in the
    /// player's Documents/My Games/Sprocket. Built-in decals (a GUID) and web images (https://...) need nothing.
    /// The images go at the end of the design data, after the shell settings (older versions stop reading before
    /// them, so the protocol is unchanged). On arrival each image is written to the same place in our My Games
    /// folder. A file we already have with that name is never replaced: if it differs, the puppet shows ours.
    /// </summary>
    public static class DesignImages
    {
        const int MaxFile = 8 << 20, MaxTotal = 12 << 20, MaxCount = 200;
        const long MaxPerPlayer = 64L << 20;   // images written for one player per game run (no filling the disk)
        static readonly Dictionary<string, long> _written = new Dictionary<string, long>();
        static readonly Regex Ref = new Regex("\"(?:imageURL|colourMapUrl)\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
        static readonly string[] Folders = { "Decals", "Paint" };
        static readonly string[] Exts = { ".png", ".jpg", ".jpeg" };

        public static string MyGames => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games");

        /// <summary>The local images a blueprint uses, as the URLs written in it ("Sprocket/Decals/x.png").</summary>
        public static List<string> Referenced(byte[] bp)
        {
            var list = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string text; try { text = Encoding.UTF8.GetString(bp); } catch { return list; }
            foreach (Match m in Ref.Matches(text))
            {
                string url; try { url = System.Text.Json.JsonSerializer.Deserialize<string>("\"" + m.Groups[1].Value + "\""); } catch { continue; }
                if (Local(url) != null && seen.Add(url)) list.Add(url);
            }
            return list;
        }

        /// <summary>The file a URL means on this machine, or null unless it's a plain image file directly in
        /// Sprocket/Decals or Sprocket/Paint (a URL from the network can't point anywhere else).</summary>
        public static string Local(string url)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 260) return null;
            var parts = url.Replace('\\', '/').Split('/');
            if (parts.Length != 3 || parts[0] != "Sprocket" || !Folders.Contains(parts[1])) return null;
            var name = parts[2];
            if (name.Length == 0 || name.Trim('.', ' ').Length == 0 || name.IndexOf(':') >= 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            if (!Exts.Contains(Path.GetExtension(name).ToLowerInvariant())) return null;
            return Path.Combine(MyGames, "Sprocket", parts[1], name);
        }

        /// <summary>URLs the blueprint uses whose file we don't have.</summary>
        public static List<string> Missing(string blueprintPath)
        {
            try { return Referenced(File.ReadAllBytes(blueprintPath)).Where(u => !File.Exists(Local(u))).ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>Appends the design's images (those we have) to the design data.</summary>
        public static void Write(BinaryWriter w, byte[] bp, string design)
        {
            var files = new List<(string url, byte[] data)>(); int total = 0; var skipped = new List<string>();
            foreach (var url in Referenced(bp))
            {
                var path = Local(url);
                try
                {
                    if (!File.Exists(path)) { skipped.Add(url + " (not on this PC)"); continue; }
                    var len = new FileInfo(path).Length;
                    if (len > MaxFile || total + len > MaxTotal || files.Count >= MaxCount) { skipped.Add($"{url} (too big: {len / 1024} KB)"); continue; }
                    var data = File.ReadAllBytes(path); files.Add((url, data)); total += data.Length;
                }
                catch (Exception e) { skipped.Add($"{url} ({e.Message})"); }
            }
            w.Write(files.Count);
            foreach (var f in files) { w.Write(f.url); w.Write(f.data.Length); w.Write(f.data); }
            if (files.Count > 0 || skipped.Count > 0)
                MPLog.Info($"Lobby: '{design}' sends {files.Count} decal/paint images ({total / 1024} KB){(skipped.Count > 0 ? "; not sent: " + string.Join(", ", skipped) : "")}");
        }

        /// <summary>Reads the images at the end of a received design (if the sender's version sends them) and saves
        /// the ones we don't have. Never throws.</summary>
        public static void Read(BinaryReader r, string design, string who, byte[] bp)
        {
            int saved = 0, same = 0, kept = 0, bad = 0;
            var wanted = new HashSet<string>(Referenced(bp), StringComparer.OrdinalIgnoreCase);   // only what this design uses
            try
            {
                if (r.BaseStream.Position >= r.BaseStream.Length) return;   // sender's version doesn't send images
                int n = r.ReadInt32(); if (n < 0 || n > MaxCount) return;
                int total = 0;
                for (int i = 0; i < n; i++)
                {
                    string url = r.ReadString(); int len = r.ReadInt32();
                    if (len < 0 || len > MaxFile || (total += len) > MaxTotal) return;
                    var data = r.ReadBytes(len);
                    var path = Local(url);
                    if (path == null || !wanted.Contains(url) || !LooksLikeImage(data, Path.GetExtension(path))) { bad++; continue; }
                    if (File.Exists(path))
                    {
                        if (File.ReadAllBytes(path).AsSpan().SequenceEqual(data)) same++;
                        else { kept++; MPLog.Info($"Lobby: {url} from {who}: you have a different image with that name, yours is kept"); }
                        continue;
                    }
                    lock (_written)
                    {
                        _written.TryGetValue(who, out long w0);
                        if (w0 + data.Length > MaxPerPlayer) { bad++; if (w0 <= MaxPerPlayer) { _written[who] = MaxPerPlayer + 1; MPLog.Warn($"Lobby: {who} sent more than {MaxPerPlayer >> 20} MB of images since the game started: no more saved from them"); } continue; }
                        _written[who] = w0 + data.Length;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path + ".part", data);
                    File.Move(path + ".part", path);
                    saved++;
                }
            }
            catch (Exception e) { MPLog.Warn($"Lobby: images of '{design}': {e.Message}"); }
            finally
            {
                if (saved + same + kept + bad > 0)
                    MPLog.Info($"Lobby: '{design}' images from {who}: {saved} saved, {same} already here, {kept} name taken (yours kept){(bad > 0 ? $", {bad} refused" : "")}");
            }
        }

        static bool LooksLikeImage(byte[] d, string ext)
        {
            ext = ext.ToLowerInvariant();
            if (ext == ".png") return d.Length > 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47;
            return d.Length > 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF;   // .jpg / .jpeg
        }
    }
}
