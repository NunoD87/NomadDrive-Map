using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>
    /// Top-down ground pictures of the world, split into 128 m tiles and saved per world seed in
    /// BepInEx/NomadDriveMap/tiles/&lt;seed&gt;/t_&lt;x&gt;_&lt;z&gt;.png. Tiles are loaded from disk only when the map needs them.
    /// </summary>
    internal class TileStore
    {
        internal const float Size = 128f;

        private readonly string _dir;
        private readonly Dictionary<long, Texture2D> _tex = new Dictionary<long, Texture2D>();
        private readonly HashSet<long> _known = new HashSet<long>();
        private readonly HashSet<long> _bad = new HashSet<long>();
        private readonly Dictionary<long, float> _use = new Dictionary<long, float>();
        private long _bytes;
        private int _loadFrame, _loads;

        internal TileStore(string seed)
        {
            if (!Persist.ValidSeed(seed)) return;
            _dir = Path.Combine(Path.Combine(Persist.Dir, "tiles"), seed);
            try
            {
                if (!Directory.Exists(_dir)) return;
                foreach (var f in Directory.GetFiles(_dir, "t_*_*.jpg"))
                {
                    var parts = Path.GetFileNameWithoutExtension(f).Split('_');
                    int tx, tz;
                    if (parts.Length == 3 && int.TryParse(parts[1], out tx) && int.TryParse(parts[2], out tz))
                    {
                        _known.Add(Key(tx, tz));
                        _bytes += new FileInfo(f).Length;
                    }
                }
                Plugin.Log.LogInfo("Map tiles on disk for seed " + seed + ": " + _known.Count + " (" + (_bytes / 1024) + " KB).");
            }
            catch (Exception e) { Plugin.Log.LogError("Scanning tiles failed: " + e.Message); }
        }

        internal static long Key(int tx, int tz) { return ((long)tx << 32) | (uint)tz; }
        internal static int Tx(long k) { return (int)(k >> 32); }
        internal static int Tz(long k) { return (int)(uint)(k & 0xFFFFFFFFL); }

        internal IEnumerable<long> Keys { get { return _known; } }
        internal int Count { get { return _known.Count; } }
        internal bool Has(int tx, int tz) { return _known.Contains(Key(tx, tz)); }

        /// <summary>The tile's texture, or null if there is none or the per-frame load budget is used up.</summary>
        internal Texture2D Get(long key)
        {
            Texture2D t;
            if (_tex.TryGetValue(key, out t)) { _use[key] = Time.unscaledTime; return t; }
            if (!_known.Contains(key) || _dir == null || _bad.Contains(key)) return null;
            if (_loadFrame != Time.frameCount) { _loadFrame = Time.frameCount; _loads = 0; }
            if (_loads >= 1) return null; // one tile per frame: decoding and compressing a tile is a few ms
            _loads++;
            try
            {
                long l0 = Prof.Now();
                var f = Path.Combine(_dir, "t_" + Tx(key) + "_" + Tz(key) + ".jpg");
                t = new Texture2D(2, 2, TextureFormat.RGB24, false);
                t.LoadImage(File.ReadAllBytes(f), false);
                long l1 = Prof.Now();
                Finish(t);
                Prof.Add("tile-load-decode", l0 > 0 ? l0 : l1);
                Prof.Add("tile-load-total", l0);
                _tex[key] = t;
                _use[key] = Time.unscaledTime;
                TrimMemory();
                return t;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Loading a tile failed: " + e.Message);
                _bad.Add(key);
                return null;
            }
        }

        private static void Finish(Texture2D t)
        {
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            try { t.Compress(false); } catch { }
            t.Apply(false, true);
        }

        // Keep at most ~150 tiles in memory (about 45 MB compressed); unload the ones not used for longest.
        private void TrimMemory()
        {
            if (_tex.Count <= 150) return;
            var keys = new List<long>(_tex.Keys);
            keys.Sort((a, b) => GetUse(a).CompareTo(GetUse(b)));
            for (int i = 0; i < 30 && i < keys.Count; i++)
            {
                if (_tex[keys[i]] != null) UnityEngine.Object.Destroy(_tex[keys[i]]);
                _tex.Remove(keys[i]);
            }
        }
        private float GetUse(long k) { float u; return _use.TryGetValue(k, out u) ? u : 0f; }

        internal void Put(int tx, int tz, Texture2D rgb, byte[] jpg)
        {
            long k = Key(tx, tz);
            if (_dir != null)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    var f = Path.Combine(_dir, "t_" + tx + "_" + tz + ".jpg");
                    if (File.Exists(f)) _bytes -= new FileInfo(f).Length;
                    File.WriteAllBytes(f, jpg);
                    _bytes += jpg.Length;
                }
                catch (Exception e) { Plugin.Log.LogError("Saving a tile failed: " + e.Message); }
            }
            Texture2D old;
            if (_tex.TryGetValue(k, out old) && old != null) UnityEngine.Object.Destroy(old);
            Finish(rgb);
            _tex[k] = rgb;
            _use[k] = Time.unscaledTime;
            _known.Add(k);
            EnforceCap();
        }

        // Disk cap: delete the oldest tiles first.
        private void EnforceCap()
        {
            long cap = (long)Plugin.MaxTilesMB.Value * 1024L * 1024L;
            if (_dir == null || _bytes <= cap) return;
            try
            {
                var files = new List<FileInfo>(new DirectoryInfo(_dir).GetFiles("t_*_*.jpg"));
                files.Sort((a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                int removed = 0;
                foreach (var fi in files)
                {
                    if (_bytes <= cap * 9 / 10) break;
                    var parts = Path.GetFileNameWithoutExtension(fi.Name).Split('_');
                    int tx, tz;
                    if (parts.Length == 3 && int.TryParse(parts[1], out tx) && int.TryParse(parts[2], out tz))
                    {
                        long key = Key(tx, tz);
                        _known.Remove(key);
                        Texture2D tex;
                        if (_tex.TryGetValue(key, out tex)) { if (tex != null) UnityEngine.Object.Destroy(tex); _tex.Remove(key); }
                    }
                    _bytes -= fi.Length;
                    fi.Delete();
                    removed++;
                }
                Plugin.Log.LogInfo("Tile cap reached: removed " + removed + " oldest tiles (" + (_bytes / 1024) + " KB left).");
            }
            catch (Exception e) { Plugin.Log.LogError("Enforcing the tile cap failed: " + e.Message); }
        }
    }

    /// <summary>Takes tile pictures around the player, in daylight only, one at a time.</summary>
    internal static class TileCapture
    {
        private static float _nextSkipLog, _nextRetry, _nextSlow;
        private static bool _warmed;

        // Does all the one-time work (effect volumes, camera, render texture, shader variants) shortly after the world
        // is ready, so the first real tile does not freeze the game for ~2 seconds.
        private static void Warm(Plugin p)
        {
            _warmed = true;
            var started = DateTime.Now;
            try
            {
                long w0 = Prof.Now();
                Look.Prewarm();
                double buildMs = Prof.Ms(w0);
                if (p.SunLight != null && p.SunDayMax > 0f)
                {
                    int res = Mathf.Clamp(Mathf.RoundToInt(TileStore.Size * Plugin.PixelsPerMetre.Value), 128, 2048);
                    Look.Bucket = p.Sun >= Plugin.MinSun.Value ? 0 : 1;
                    int hid;
                    Prof.BeginPhases();
                    var tex = TopDown.Render(p, p.Local.position, TileStore.Size * 0.5f, res, false, out hid, 1.1f, p.Local.position.y);
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                    Plugin.Log.LogInfo("Warm-up render: " + Prof.PhaseText());
                }
                Plugin.Log.LogInfo("Warm-up done in " + (DateTime.Now - started).TotalMilliseconds.ToString("F0") + " ms (effect volumes " + buildMs.ToString("F0") + " ms).");
                Prof.WatchHitch("warm-up");
            }
            catch (Exception e) { Plugin.Log.LogError("Warm-up failed: " + e.Message); }
        }
        private static readonly Dictionary<long, float> Blocked = new Dictionary<long, float>();

        internal static void Tick(Plugin p, MapUiInfo view)
        {
            var store = p.Mem != null ? p.Mem.Tiles : null;
            if (store == null || Time.unscaledTime < _nextRetry) return;
            if (!_warmed && p.WorldReadyFor(3f)) { Warm(p); return; }
            if (Time.unscaledDeltaTime > 0.035f) return; // do not add a render to a frame that is already slow

            // Only once the world is fully loaded and has been for a while
            if (!p.WorldReadyFor(6f)) return;
            if (p.SunLight == null) { Skip(p, "no directional light found"); return; }
            if (p.SunDayMax <= 0f) { Skip(p, "sun strength not known yet (needs one daytime moment)"); return; }

            var t = Plugin.ToTrue(p.Local.position);
            int cx = Mathf.FloorToInt(t.x / TileStore.Size), cz = Mathf.FloorToInt(t.z / TileStore.Size);
            float half = TileStore.Size * 0.5f;
            int bestX = 0, bestZ = 0;
            float best = float.MaxValue;
            bool urgent = false;
            for (int dx = -2; dx <= 2; dx++)
                for (int dz = -2; dz <= 2; dz++)
                {
                    int tx = cx + dx, tz = cz + dz;
                    long key = TileStore.Key(tx, tz);
                    float until;
                    if (store.Has(tx, tz) || (Blocked.TryGetValue(key, out until) && Time.unscaledTime < until)) continue;
                    float mx = (tx + 0.5f) * TileStore.Size - t.x, mz = (tz + 0.5f) * TileStore.Size - t.z;
                    // tiles that show on the minimap come first
                    bool inView = Mathf.Abs(mx) < view.HalfW + half && Mathf.Abs(mz) < view.HalfH + half;
                    float d = Mathf.Sqrt(mx * mx + mz * mz) + (inView ? 0f : 1000f);
                    if (d >= best) continue;
                    best = d; bestX = tx; bestZ = tz; urgent = inView;
                }
            if (best == float.MaxValue) return;
            if (!urgent && Time.unscaledTime < _nextSlow) return;

            var center = new Vector3((bestX + 0.5f) * TileStore.Size, t.y, (bestZ + 0.5f) * TileStore.Size);
            var rc = Plugin.ToRender(center);
            long bkey = TileStore.Key(bestX, bestZ);

            // The ground under the whole tile must be loaded: check nine points. The highest hit (ground, tree,
            // building) decides how high the camera has to be.
            float h = TileStore.Size * 0.45f;
            float topY = float.MinValue, groundY = 0f;
            for (int i = 0; i < 9; i++)
            {
                float ox = (i % 3 - 1) * h, oz = (i / 3 - 1) * h;
                var o = rc + new Vector3(ox, 0f, oz);
                o.y = p.Local.position.y + 600f;
                RaycastHit hit;
                if (!Physics.Raycast(o, Vector3.down, out hit, 1400f, ~0, QueryTriggerInteraction.Ignore))
                {
                    Blocked[bkey] = Time.unscaledTime + 15f; // try again later, and meanwhile do other tiles
                    return;
                }
                if (hit.point.y > topY) topY = hit.point.y;
                if (i == 4) groundY = hit.point.y;
            }

            var started = DateTime.Now;
            Prof.BeginPhases();
            long pt = Prof.Now();
            Look.Bucket = p.Sun >= Plugin.MinSun.Value ? 0 : 1;
            int res = Mathf.Clamp(Mathf.RoundToInt(TileStore.Size * Plugin.PixelsPerMetre.Value), 128, 2048);
            int hid;
            var tex = TopDown.Render(p, rc, TileStore.Size * 0.5f, res, Plugin.HideUnseenInTiles.Value, out hid, 1.1f, groundY, topY);
            if (tex == null) { Blocked[bkey] = Time.unscaledTime + 30f; return; }

            // The exposure is fixed (derived from the simulated sun), so every tile is lit the same. Content changes
            // the average brightness legitimately (dirt vs grass), so it is NOT used to adjust anything: only a
            // render that is plainly broken (all black / all white) is thrown away.
            pt = Prof.Now();
            float luma = Luma(tex);
            pt = Prof.Phase("brightness", pt);
            if (luma < 0.06f || luma > 0.92f)
            {
                UnityEngine.Object.Destroy(tex);
                Plugin.Log.LogInfo("Tile " + bestX + "," + bestZ + " rejected: broken render (brightness " + luma.ToString("F2") + ", sun " + p.Sun.ToString("F0") + " deg).");
                Blocked[bkey] = Time.unscaledTime + 60f;
                return;
            }

            var jpg = tex.EncodeToJPG(Plugin.JpegQuality.Value);
            pt = Prof.Phase("jpeg-encode", pt);
            store.Put(bestX, bestZ, tex, jpg);
            Prof.Phase("store(write+compress)", pt);
            _nextSlow = Time.unscaledTime + Plugin.TileInterval.Value;
            Plugin.Log.LogDebug("Tile " + bestX + "," + bestZ + " captured: " + (jpg.Length / 1024) + " KB, " + res + " px, brightness " + luma.ToString("F2") + ", sun " + p.Sun.ToString("F0") +
                               " deg, " + (DateTime.Now - started).TotalMilliseconds.ToString("F0") + " ms" + (urgent ? ", on minimap" : "") + ", at " + DateTime.Now.ToString("HH:mm:ss"));
            if (Plugin.ProfileLog.Value) Plugin.Log.LogInfo("  tile timing: " + Prof.PhaseText());
            Prof.WatchHitch("tile " + bestX + "," + bestZ);
        }

        private static void Skip(Plugin p, string why)
        {
            if (Time.unscaledTime < _nextSkipLog) return;
            _nextSkipLog = Time.unscaledTime + 60f;
            Plugin.Log.LogInfo("Ground tiles paused: " + why + ".");
        }

        // Mean brightness from the raw RGB24 data (no big temporary array).
        private static float Luma(Texture2D t)
        {
            var raw = t.GetRawTextureData<byte>();
            double sum = 0; int n = 0;
            for (int i = 0; i + 2 < raw.Length; i += 3 * 17) { sum += (0.2126 * raw[i] + 0.7152 * raw[i + 1] + 0.0722 * raw[i + 2]) / 255.0; n++; }
            return n > 0 ? (float)(sum / n) : 0f;
        }
    }

    /// <summary>How much ground the minimap shows around the player (metres from the player, north-up).</summary>
    internal struct MapUiInfo { public float HalfW, HalfH; }

    /// <summary>
    /// Top-down pictures of the game's own vehicle models, used as map icons. Each vehicle is rendered once
    /// against a black and a white background and the transparency is worked out from the difference.
    /// </summary>
    internal static class VehicleIcons
    {
        internal class Icon { public Texture2D Tex; public float Half; }

        private static readonly Dictionary<string, Icon> Icons = new Dictionary<string, Icon>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private const int IconLayer = 31;

        internal static string KeyOf(Transform v) { return v.name.Replace("(Clone)", "").Trim(); }

        internal static Icon Get(Transform v)
        {
            Icon i;
            return Icons.TryGetValue(KeyOf(v), out i) ? i : null;
        }

        /// <summary>Makes at most one missing icon per call.</summary>
        internal static void Tick(Plugin p)
        {
            if (!Plugin.VehicleIconsOn.Value || p.Mem == null) return;
            foreach (var v in p.Vehicles)
            {
                if (v == null || !p.Mem.VehiclesSeen.Contains(v.GetInstanceID())) continue;
                string key = KeyOf(v);
                if (Icons.ContainsKey(key) || Failed.Contains(key)) continue;
                try
                {
                    var icon = Make(p, v);
                    if (icon != null) { Icons[key] = icon; Plugin.Log.LogInfo("Vehicle icon made for '" + key + "' (" + (icon.Half * 2f).ToString("F1") + " m)."); }
                    else { Failed.Add(key); }
                }
                catch (Exception e) { Failed.Add(key); Plugin.Log.LogError("Vehicle icon for '" + key + "' failed: " + e.Message); }
                return;
            }
        }

        private static Icon Make(Plugin p, Transform v)
        {
            var rends = new List<Renderer>();
            foreach (var r in v.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
                rends.Add(r);
            }
            if (rends.Count == 0) return null;

            // size in the vehicle's own axes (forward = +Z)
            Vector3 mn = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue), mx = -mn;
            foreach (var r in rends)
            {
                var b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var l = v.InverseTransformPoint(c);
                    mn = Vector3.Min(mn, l); mx = Vector3.Max(mx, l);
                }
            }
            var centerLocal = (mn + mx) * 0.5f;
            float half = Mathf.Max(mx.x - mn.x, mx.z - mn.z) * 0.5f * 1.08f;
            if (half < 0.5f || half > 30f) return null;

            const int res = 128;
            var saved = new List<int>();
            GameObject camGo = null;
            RenderTexture rt = null;
            try
            {
                foreach (var r in rends) { saved.Add(r.gameObject.layer); r.gameObject.layer = IconLayer; }

                camGo = new GameObject("NomadMapIconCamera");
                var cam = camGo.AddComponent<Camera>();
                cam.enabled = false;
                cam.orthographic = true;
                cam.orthographicSize = half;
                cam.nearClipPlane = 0.1f;
                cam.farClipPlane = 200f;
                cam.allowHDR = false;
                cam.cullingMask = 1 << IconLayer;
                cam.clearFlags = CameraClearFlags.SolidColor;
                var worldCenter = v.TransformPoint(centerLocal);
                camGo.transform.position = worldCenter + v.up * 60f;
                camGo.transform.rotation = Quaternion.LookRotation(-v.up, v.forward); // image up = vehicle front
                TopDown.ConfigureHdrp(camGo);

                rt = new RenderTexture(res, res, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                cam.targetTexture = rt;

                Color32[] onBlack = null, onWhite;
                for (int attempt = 0; attempt < 4; attempt++)
                {
                    Look.Begin(p, Plugin.NormalizeSun.Value, true);
                    try { onBlack = Shoot(cam, camGo, rt, res, Color.black); }
                    finally { Look.End(); }
                    float l = MeanLuma(onBlack);
                    if ((l >= 0.12f && l <= 0.6f) || attempt == 3) break;
                    Look.Adjust(true, l, 0.3f);
                }
                Look.Begin(p, Plugin.NormalizeSun.Value, true);
                try { onWhite = Shoot(cam, camGo, rt, res, Color.white); }
                finally { Look.End(); }
                int opaque = 0;
                for (int i = 0; i < onBlack.Length; i++) if (Mathf.Abs(onBlack[i].r - onWhite[i].r) < 12 && onBlack[i].r > 8) opaque++;
                if (opaque > onBlack.Length * 0.9f) { Plugin.Log.LogWarning("Vehicle icon: the background did not clear (the picture is a full square). Using the plain marker."); return null; }
                return new Icon { Tex = Matte(onBlack, onWhite, res), Half = half };
            }
            finally
            {
                for (int i = 0; i < rends.Count && i < saved.Count; i++) if (rends[i] != null) rends[i].gameObject.layer = saved[i];
                if (camGo != null) UnityEngine.Object.Destroy(camGo);
                if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); }
            }
        }

        // Mean brightness of the pixels that are not the (black) background.
        private static float MeanLuma(Color32[] px)
        {
            double sum = 0; int n = 0;
            for (int i = 0; i < px.Length; i++)
            {
                if (px[i].r + px[i].g + px[i].b <= 9) continue;
                sum += (0.2126 * px[i].r + 0.7152 * px[i].g + 0.0722 * px[i].b) / 255.0; n++;
            }
            return n > 20 ? (float)(sum / n) : 0.3f;
        }

        private static Color32[] Shoot(Camera cam, GameObject go, RenderTexture rt, int res, Color bg)
        {
            cam.backgroundColor = bg;
            TopDown.SetBackground(go, bg);
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var t = new Texture2D(res, res, TextureFormat.RGBA32, false);
            t.ReadPixels(new Rect(0, 0, res, res), 0, 0);
            RenderTexture.active = prev;
            var px = t.GetPixels32();
            UnityEngine.Object.Destroy(t);
            return px;
        }

        // pixel = c*a + bg*(1-a), so black and white renders give a and c.
        private static Texture2D Matte(Color32[] ab, Color32[] aw, int res)
        {
            var bref = ab[0]; var wref = aw[0];
            float denom = ((wref.r - bref.r) + (wref.g - bref.g) + (wref.b - bref.b)) / 3f;
            if (denom < 20f) denom = 255f; // background did not take: assume ideal
            var outp = new Color32[res * res];
            for (int i = 0; i < outp.Length; i++)
            {
                float diff = ((aw[i].r - ab[i].r) + (aw[i].g - ab[i].g) + (aw[i].b - ab[i].b)) / 3f;
                float a = Mathf.Clamp01(1f - diff / denom);
                if (a < 0.03f) { outp[i] = new Color32(0, 0, 0, 0); continue; }
                float k = 1f - a;
                byte r = (byte)Mathf.Clamp(((ab[i].r - bref.r * k) / a), 0f, 255f);
                byte g = (byte)Mathf.Clamp(((ab[i].g - bref.g * k) / a), 0f, 255f);
                byte b = (byte)Mathf.Clamp(((ab[i].b - bref.b * k) / a), 0f, 255f);
                outp[i] = new Color32(r, g, b, (byte)(a * 255f));
            }
            var t = new Texture2D(res, res, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            t.SetPixels32(outp);
            t.Apply(false, true);
            return t;
        }
    }
}
