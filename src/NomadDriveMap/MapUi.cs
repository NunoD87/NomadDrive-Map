using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>Reflection helpers to build uGUI objects with the game's own UI types, fonts and theme.</summary>
    internal static class Kit
    {
        internal static Type TRaw, TImage, TTmp, TMask, TScaler, TRaycaster, TSurface, TSurfaceKind, TStatusBar;
        private static PropertyInfo _imgColor, _imgSprite, _imgRaycast, _tmpText, _tmpSize, _tmpColor, _tmpAlign, _tmpFont, _tmpRaycast;
        private static bool _ready, _tried;
        internal static object Font;

        internal static readonly Color Gold = new Color(1f, 0.78f, 0.2f, 1f);
        internal static readonly Color Pale = new Color(0.82f, 0.88f, 0.95f, 0.95f);
        internal static readonly Color Green = new Color(0.35f, 0.9f, 0.4f, 1f);
        internal static readonly Color Cyan = new Color(0.3f, 0.85f, 1f, 1f);
        internal static readonly Color Trail = new Color(1f, 0.85f, 0.35f, 0.55f);
        internal static readonly Color Black = new Color(0f, 0f, 0f, 0.7f);
        internal static readonly Color Text = new Color(0.93f, 0.93f, 0.93f, 1f);

        private static Sprite _circle, _arrow;

        internal static bool Init()
        {
            if (_tried) return _ready;
            _tried = true;
            TImage = Util.Find("UnityEngine.UI.Image");
            TTmp = Util.Find("TMPro.TextMeshProUGUI");
            TRaw = Util.Find("UnityEngine.UI.RawImage");
            TMask = Util.Find("UnityEngine.UI.RectMask2D");
            TScaler = Util.Find("UnityEngine.UI.CanvasScaler");
            TRaycaster = Util.Find("UnityEngine.UI.GraphicRaycaster");
            TSurface = Util.Find("EvilCore.UI.Theme.UIStyledSurface");
            TSurfaceKind = Util.Find("EvilCore.UI.Theme.UISurfaceKind");
            TStatusBar = Util.Find("NomadDrive.Features.Player.UI.PeakStatusBarDisplayer");
            if (TImage == null || TTmp == null || TMask == null || TScaler == null)
            {
                Plugin.Log.LogError("UI types missing (Image=" + (TImage != null) + " TMP=" + (TTmp != null) + " RectMask2D=" + (TMask != null) +
                                    " CanvasScaler=" + (TScaler != null) + "). The map cannot be drawn.");
                return false;
            }
            _imgColor = AccessTools.Property(TImage, "color");
            _imgSprite = AccessTools.Property(TImage, "sprite");
            _imgRaycast = AccessTools.Property(TImage, "raycastTarget");
            _tmpText = AccessTools.Property(TTmp, "text");
            _tmpSize = AccessTools.Property(TTmp, "fontSize");
            _tmpColor = AccessTools.Property(TTmp, "color");
            _tmpAlign = AccessTools.Property(TTmp, "alignment");
            _tmpFont = AccessTools.Property(TTmp, "font");
            _tmpRaycast = AccessTools.Property(TTmp, "raycastTarget");
            _ready = true;
            return true;
        }

        /// <summary>Takes the font from a text the game already uses, so our labels match.</summary>
        internal static void FindFont(Component hint)
        {
            if (Font != null) return;
            try
            {
                Component src = hint != null ? hint.GetComponentInChildren(Util.Find("TMPro.TMP_Text"), true) : null;
                if (src == null)
                {
                    foreach (var o in UnityEngine.Object.FindObjectsOfType(Util.Find("TMPro.TMP_Text")))
                    {
                        src = o as Component;
                        if (src != null) break;
                    }
                }
                if (src != null) Font = AccessTools.Property(src.GetType(), "font")?.GetValue(src, null);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Font lookup failed: " + e.Message); }
        }

        internal static Sprite Circle
        {
            get
            {
                if (_circle != null) return _circle;
                const int n = 48;
                var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        float dx = x + 0.5f - n * 0.5f, dy = y + 0.5f - n * 0.5f;
                        float a = Mathf.Clamp01((n * 0.5f - 1f - Mathf.Sqrt(dx * dx + dy * dy)) * 0.9f + 0.5f);
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                    }
                t.Apply();
                _circle = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
                _circle.hideFlags = HideFlags.HideAndDontSave;
                return _circle;
            }
        }

        internal static Sprite Arrow
        {
            get
            {
                if (_arrow != null) return _arrow;
                const int n = 64;
                var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                var a = new Vector2(0.5f, 0.95f); var b = new Vector2(0.9f, 0.08f);
                var c = new Vector2(0.5f, 0.28f); var d = new Vector2(0.1f, 0.08f);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                    {
                        int hit = 0;
                        for (int sy = 0; sy < 2; sy++)
                            for (int sx = 0; sx < 2; sx++)
                            {
                                var pt = new Vector2((x + 0.25f + 0.5f * sx) / n, (y + 0.25f + 0.5f * sy) / n);
                                if (InTri(pt, a, b, c) || InTri(pt, a, c, d)) hit++;
                            }
                        t.SetPixel(x, y, new Color(1f, 1f, 1f, hit / 4f));
                    }
                t.Apply();
                _arrow = Sprite.Create(t, new Rect(0, 0, n, n), new Vector2(0.5f, 0.5f), 100f);
                _arrow.hideFlags = HideFlags.HideAndDontSave;
                return _arrow;
            }
        }

        private static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Sg(p, a, b), d2 = Sg(p, b, c), d3 = Sg(p, c, a);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }
        private static float Sg(Vector2 p1, Vector2 p2, Vector2 p3) { return (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y); }

        // ---- builders ---------------------------------------------------------------------

        internal static RectTransform Rect(string name, Transform parent)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.layer = parent.gameObject.layer;
            var rt = (RectTransform)go.transform;
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            return rt;
        }

        internal static void Stretch(RectTransform r, float l = 0, float t = 0, float rr = 0, float b = 0)
        {
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = new Vector2(l, b); r.offsetMax = new Vector2(-rr, -t);
        }

        /// <summary>Anchor to the top-left corner of the parent, position measured from there (y goes down).</summary>
        internal static void PlaceTL(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 1f);
            r.anchoredPosition = new Vector2(x, -y);
            r.sizeDelta = new Vector2(w, h);
        }

        internal static void PlaceBL(RectTransform r, float x, float y, float w, float h)
        {
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0f, 0f);
            r.anchoredPosition = new Vector2(x, y);
            r.sizeDelta = new Vector2(w, h);
        }

        internal static Component Img(string name, Transform parent, Color color, Sprite sprite = null, bool raycast = false)
        {
            var rt = Rect(name, parent);
            var c = rt.gameObject.AddComponent(TImage);
            _imgColor.SetValue(c, color, null);
            if (sprite != null) _imgSprite.SetValue(c, sprite, null);
            _imgRaycast.SetValue(c, raycast, null);
            return c;
        }

        private static PropertyInfo _rawTex, _rawColor, _rawRay;
        internal static Component Raw(string name, Transform parent)
        {
            if (_rawTex == null)
            {
                _rawTex = AccessTools.Property(TRaw, "texture");
                _rawColor = AccessTools.Property(TRaw, "color");
                _rawRay = AccessTools.Property(TRaw, "raycastTarget");
            }
            var rt = Rect(name, parent);
            var c = rt.gameObject.AddComponent(TRaw);
            _rawRay.SetValue(c, false, null);
            return c;
        }
        internal static void SetRaw(Component raw, Texture tex, Color col)
        {
            _rawTex.SetValue(raw, tex, null);
            _rawColor.SetValue(raw, col, null);
        }

        internal static void SetColor(Component img, Color c) { _imgColor.SetValue(img, c, null); }

        internal static Component Txt(string name, Transform parent, float size, string align, Color color)
        {
            var rt = Rect(name, parent);
            var c = rt.gameObject.AddComponent(TTmp);
            _tmpSize.SetValue(c, size, null);
            _tmpColor.SetValue(c, color, null);
            _tmpRaycast.SetValue(c, false, null);
            try { _tmpAlign.SetValue(c, Enum.Parse(_tmpAlign.PropertyType, align), null); } catch { }
            if (Font != null) _tmpFont.SetValue(c, Font, null);
            try
            {
                var w = AccessTools.Property(TTmp, "enableWordWrapping");
                if (w != null) w.SetValue(c, false, null);
                else
                {
                    var m = AccessTools.Property(TTmp, "textWrappingMode");
                    if (m != null) m.SetValue(c, Enum.Parse(m.PropertyType, "NoWrap"), null);
                }
            }
            catch { }
            return c;
        }

        internal static void SetText(Component t, string s) { _tmpText.SetValue(t, s, null); }

        /// <summary>Lets the game's theme paint the surface (colour, border, depth).</summary>
        internal static void Theme(Component target, string kind)
        {
            try
            {
                if (TSurface == null || TSurfaceKind == null) return;
                var comp = target.gameObject.AddComponent(TSurface);
                AccessTools.Field(TSurface, "kind")?.SetValue(comp, Enum.Parse(TSurfaceKind, kind));
            }
            catch (Exception e) { Plugin.Log.LogInfo("Theme surface not applied: " + e.Message); }
        }
    }

    /// <summary>A text whose content is only pushed to the game's text component when it changes.</summary>
    internal class TextRef
    {
        public Component C;
        public RectTransform Rt;
        private string _last;
        public TextRef(Component c) { C = c; Rt = (RectTransform)c.transform; }
        public void Set(string s) { if (s == _last) return; _last = s; Kit.SetText(C, s); }
    }

    internal class Pool
    {
        private readonly RectTransform _parent;
        private readonly Func<Transform, RectTransform> _make;
        private readonly List<RectTransform> _items = new List<RectTransform>();
        private int _used;

        private readonly CanvasGroup _group;
        public Pool(RectTransform area, string name, Func<Transform, RectTransform> make)
        {
            _parent = Kit.Rect(name, area);
            Kit.Stretch(_parent);
            _group = _parent.gameObject.AddComponent<CanvasGroup>();
            _group.blocksRaycasts = false;
            _group.interactable = false;
            _make = make;
        }
        public void Alpha(float a) { if (Mathf.Abs(_group.alpha - a) > 0.004f) _group.alpha = a; }
        public void Begin() { _used = 0; }
        public RectTransform Next()
        {
            if (_used == _items.Count) _items.Add(_make(_parent));
            var rt = _items[_used++];
            if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
            return rt;
        }
        public void End()
        {
            for (int i = _used; i < _items.Count; i++)
                if (_items[i].gameObject.activeSelf) _items[i].gameObject.SetActive(false);
        }
        public int Count { get { return _used; } }
    }

    internal class TextPool
    {
        private readonly RectTransform _parent;
        private readonly float _size;
        private readonly List<TextRef> _items = new List<TextRef>();
        private int _used;

        public TextPool(RectTransform area, string name, float size)
        {
            _parent = Kit.Rect(name, area);
            Kit.Stretch(_parent);
            _size = size;
        }
        public void Begin() { _used = 0; }
        public TextRef Next(string align)
        {
            if (_used == _items.Count)
            {
                var c = Kit.Txt("t", _parent, _size, align, Kit.Text);
                var tr = new TextRef(c);
                tr.Rt.sizeDelta = new Vector2(300f, 28f);
                _items.Add(tr);
            }
            var it = _items[_used++];
            if (!it.Rt.gameObject.activeSelf) it.Rt.gameObject.SetActive(true);
            return it;
        }
        public void End()
        {
            for (int i = _used; i < _items.Count; i++)
                if (_items[i].Rt.gameObject.activeSelf) _items[i].Rt.gameObject.SetActive(false);
        }
    }

    internal class RawPool
    {
        private class Slot { public RectTransform Rt; public Component C; public Texture Tex; public Color Col; }
        private readonly RectTransform _parent;
        private readonly List<Slot> _items = new List<Slot>();
        private int _used;

        public RawPool(RectTransform area, string name)
        {
            _parent = Kit.Rect(name, area);
            Kit.Stretch(_parent);
        }
        public void Begin() { _used = 0; }
        public RectTransform Next(Texture tex, Color col)
        {
            if (_used == _items.Count)
            {
                var c = Kit.Raw("r", _parent);
                _items.Add(new Slot { Rt = (RectTransform)c.transform, C = c });
            }
            var s = _items[_used++];
            if (!s.Rt.gameObject.activeSelf) s.Rt.gameObject.SetActive(true);
            if (s.Tex != tex || s.Col != col) { s.Tex = tex; s.Col = col; Kit.SetRaw(s.C, tex, col); }
            return s.Rt;
        }
        public void End()
        {
            for (int i = _used; i < _items.Count; i++)
                if (_items[i].Rt.gameObject.activeSelf) _items[i].Rt.gameObject.SetActive(false);
        }
    }

    /// <summary>Draws the map into a masked area using pooled uGUI images and texts.</summary>
    internal class MapSurface
    {
        private readonly Pool _gridLines, _trail, _backs, _poiBacks, _poiMinor, _poiMajor, _vehicles, _others, _arrowBack, _arrow;
        private readonly TextPool _gridLabels, _labels;
        private readonly RawPool _tiles, _icons;
        private readonly float _fontSize;

        public MapSurface(RectTransform area, float labelFont)
        {
            _fontSize = labelFont;
            _tiles = new RawPool(area, "Tiles");
            _gridLines = new Pool(area, "Grid", p => (RectTransform)Kit.Img("l", p, new Color(1f, 1f, 1f, 0.07f)).transform);
            _gridLabels = new TextPool(area, "GridLabels", labelFont * 0.8f);
            _trail = new Pool(area, "Trail", p => (RectTransform)Kit.Img("t", p, Kit.Trail, Kit.Circle).transform);
            _poiBacks = new Pool(area, "PoiBacks", p => (RectTransform)Kit.Img("b", p, Kit.Black, Kit.Circle).transform);
            _backs = new Pool(area, "Backs", p => (RectTransform)Kit.Img("b", p, Kit.Black, Kit.Circle).transform);
            _poiMinor = new Pool(area, "PoiMinor", p => (RectTransform)Kit.Img("p", p, Kit.Pale, Kit.Circle).transform);
            _poiMajor = new Pool(area, "PoiMajor", p => (RectTransform)Kit.Img("p", p, Kit.Gold, Kit.Circle).transform);
            _vehicles = new Pool(area, "Vehicles", p => (RectTransform)Kit.Img("v", p, Kit.Green).transform);
            _icons = new RawPool(area, "VehicleIcons");
            _others = new Pool(area, "Others", p => (RectTransform)Kit.Img("o", p, Kit.Cyan, Kit.Circle).transform);
            _arrowBack = new Pool(area, "ArrowBack", p => (RectTransform)Kit.Img("a", p, new Color(0f, 0f, 0f, 0.85f), Kit.Arrow).transform);
            _arrow = new Pool(area, "Arrow", p => (RectTransform)Kit.Img("a", p, Color.white, Kit.Arrow).transform);
            _labels = new TextPool(area, "Labels", labelFont);
        }

        private static void Put(RectTransform rt, Vector2 pos, float size)
        {
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(size, size);
        }

        /// <returns>Grid step in metres (full map only), for the scale bar.</returns>
        public float Render(Plugin p, Vector3 center, float s, float rot, Vector2 size, bool full, float viewYaw, float mapYaw)
        {
            float cos = Mathf.Cos(rot), sin = Mathf.Sin(rot);
            float hw = size.x * 0.5f + 40f, hh = size.y * 0.5f + 40f;
            float d = full ? Mathf.Clamp(0.9f + s * 0.6f, 1f, 1.5f) : 1f;
            var shift = p.Local.position - Plugin.ToTrue(p.Local.position);
            var mem = p.Mem;

            Vector2 P(Vector3 w)
            {
                float dx = w.x - center.x, dz = w.z - center.z;
                return new Vector2((dx * cos - dz * sin) * s, (dx * sin + dz * cos) * s);
            }
            bool In(Vector2 q) { return Mathf.Abs(q.x) <= hw && Mathf.Abs(q.y) <= hh; }

            _tiles.Begin(); _icons.Begin();
            _gridLines.Begin(); _gridLabels.Begin(); _trail.Begin(); _backs.Begin(); _poiBacks.Begin(); _poiMinor.Begin(); _poiMajor.Begin();
            _vehicles.Begin(); _others.Begin(); _arrowBack.Begin(); _arrow.Begin(); _labels.Begin();

            // Markers and trail turn see-through when zoomed in far, so the ground shows through
            float fz = Plugin.FadeZoomed.Value ? Mathf.InverseLerp(1.2f, 3.5f, s) : 0f;
            float poiA = Mathf.Lerp(1f, 0.3f, fz), trailA = Mathf.Lerp(1f, 0.12f, fz);
            _trail.Alpha(trailA); _poiBacks.Alpha(poiA); _poiMinor.Alpha(poiA); _poiMajor.Alpha(poiA);

            // Ground pictures
            if (mem != null && mem.Tiles != null)
            {
                float tsz = TileStore.Size * s;
                if (tsz >= 2f)
                {
                    var tint = Color.Lerp(Color.white, new Color(0.38f, 0.42f, 0.62f, 1f), p.Night);
                    float rotDeg = rot * Mathf.Rad2Deg;
                    foreach (var key in mem.Tiles.Keys)
                    {
                        var cpos = new Vector3((TileStore.Tx(key) + 0.5f) * TileStore.Size + shift.x, 0f, (TileStore.Tz(key) + 0.5f) * TileStore.Size + shift.z);
                        var q = P(cpos);
                        if (Mathf.Abs(q.x) > hw + tsz || Mathf.Abs(q.y) > hh + tsz) continue;
                        var tex = mem.Tiles.Get(key);
                        if (tex == null) continue;
                        var rt = _tiles.Next(tex, tint);
                        rt.anchoredPosition = q;
                        rt.sizeDelta = new Vector2(tsz * 1.012f, tsz * 1.012f);
                        rt.localEulerAngles = new Vector3(0f, 0f, rotDeg);
                    }
                }
            }

            float step = 0f;
            if (full) step = Grid(center, s, size, P);

            // Trail, thinned so zoomed-out maps do not need thousands of images.
            if (Plugin.ShowTrail.Value && mem != null)
            {
                Vector2 last = new Vector2(float.MaxValue, 0f);
                float minGap = 5f * d;
                var list = mem.Trail;
                int placed = 0;
                for (int i = 0; i < list.Count && placed < 800; i++)
                {
                    var q = P(list[i] + shift);
                    if (!In(q)) continue;
                    if ((q - last).sqrMagnitude < minGap * minGap) continue;
                    last = q;
                    Put(_trail.Next(), q, 5f * d);
                    placed++;
                }
            }

            // Places the player has seen
            if (mem != null)
            {
                foreach (var kv in mem.Pois)
                {
                    var k = kv.Value;
                    var q = P(k.TruePos + shift);
                    if (!In(q)) continue;
                    float r = (k.Major ? 16f : 11f) * d;
                    Put(_poiBacks.Next(), q, r + 5f * d);
                    Put((k.Major ? _poiMajor : _poiMinor).Next(), q, r);
                    bool name = Plugin.ShowNames.Value && (k.Major || (full && s >= 0.2f));
                    if (name)
                    {
                        var t = _labels.Next("MidlineLeft");
                        t.Set(k.Name);
                        t.Rt.pivot = new Vector2(0f, 0.5f);
                        t.Rt.anchoredPosition = q + new Vector2(r * 0.5f + 5f * d, 0f);
                    }
                }
            }

            // Vehicles the player has seen (their own model from above when we have it)
            if (mem != null)
            {
                foreach (var v in p.Vehicles)
                {
                    if (v == null || !mem.VehiclesSeen.Contains(v.GetInstanceID())) continue;
                    var q = P(v.position);
                    if (!In(q)) continue;
                    var icon = Plugin.VehicleIconsOn.Value ? VehicleIcons.Get(v) : null;
                    if (icon != null && icon.Tex != null)
                    {
                        float px = Mathf.Max(30f * d, icon.Half * 2f * s * Plugin.VehicleIconScale.Value);
                        var f = v.forward;
                        float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                        var rt = _icons.Next(icon.Tex, Color.white);
                        rt.anchoredPosition = q;
                        rt.sizeDelta = new Vector2(px, px);
                        rt.localEulerAngles = new Vector3(0f, 0f, -yaw + rot * Mathf.Rad2Deg);
                    }
                    else
                    {
                        Put(_backs.Next(), q, 17f * d);
                        Put(_vehicles.Next(), q, 12f * d);
                    }
                }
            }

            // Other players are always visible (your friends)
            foreach (var o in p.Others)
            {
                if (o == null) continue;
                var q = P(o.position);
                if (!In(q)) continue;
                Put(_backs.Next(), q, 18f * d);
                Put(_others.Next(), q, 13f * d);
            }

            // You
            var me = P(p.Local.position);
            float ang = viewYaw - mapYaw;
            float ms = Plugin.PlayerMarkerScale.Value;
            if (!(Plugin.HidePlayerInVehicle.Value && p.LocalSeated))
            {
            var a1 = _arrowBack.Next(); Put(a1, me, 46f * d * ms); a1.localEulerAngles = new Vector3(0f, 0f, -ang);
            var a2 = _arrow.Next(); Put(a2, me, 34f * d * ms); a2.localEulerAngles = new Vector3(0f, 0f, -ang);
            }

            _tiles.End(); _icons.End();
            _gridLines.End(); _gridLabels.End(); _trail.End(); _backs.End(); _poiBacks.End(); _poiMinor.End(); _poiMajor.End();
            _vehicles.End(); _others.End(); _arrowBack.End(); _arrow.End(); _labels.End();
            return step;
        }

        private float Grid(Vector3 center, float s, Vector2 size, Func<Vector3, Vector2> P)
        {
            float[] steps = { 50, 100, 250, 500, 1000, 2500, 5000, 10000 };
            float step = steps[steps.Length - 1];
            foreach (var st in steps) { if (st * s >= 100f) { step = st; break; } }

            var tc = Plugin.ToTrue(center);
            float shiftX = center.x - tc.x, shiftZ = center.z - tc.z;
            float hw = size.x * 0.5f / s, hh = size.y * 0.5f / s;

            int k0 = Mathf.FloorToInt((tc.x - hw) / step), k1 = Mathf.CeilToInt((tc.x + hw) / step);
            for (int k = k0; k <= k1 && k - k0 < 100; k++)
            {
                float sx = P(new Vector3(k * step + shiftX, 0f, center.z)).x;
                var l = _gridLines.Next(); l.anchoredPosition = new Vector2(sx, 0f); l.sizeDelta = new Vector2(2f, size.y);
            }
            k0 = Mathf.FloorToInt((tc.z - hh) / step); k1 = Mathf.CeilToInt((tc.z + hh) / step);
            for (int k = k0; k <= k1 && k - k0 < 100; k++)
            {
                float sy = P(new Vector3(center.x, 0f, k * step + shiftZ)).y;
                var l = _gridLines.Next(); l.anchoredPosition = new Vector2(0f, sy); l.sizeDelta = new Vector2(size.x, 2f);
            }
            return step;
        }
    }

    internal enum MapMode { Closed, View, Interactive }

    /// <summary>Owns the canvas, the HUD minimap and the fullscreen map.</summary>
    internal class MapUi
    {
        private GameObject _canvasGo;
        private Canvas _canvas;
        private RectTransform _canvasTf;

        // Minimap
        private RectTransform _miniRoot;
        private MapSurface _miniSurf;
        private TextRef _miniN;
        private int _miniWidth;
        private string _miniShape, _miniLayout, _miniCorner;
        private float _miniW, _miniH, _nextLayout;
        private Component _healthBar;
        private bool _loggedBar, _barOk, _hudVisible = true;
        private float _barLeft, _barTop, _barW, _hudAlpha = 1f, _nextHud, _hudSince = -1f, _nextFind;
        private CanvasGroup _miniGroup;

        // Fullscreen map
        private RectTransform _fullRoot, _fullArea;
        private MapSurface _fullSurf;
        private TextRef _fullTitle, _scaleText;
        private RectTransform _scaleBar;
        private MapMode _fullMode = MapMode.Closed;
        private float _scale = 0.5f;
        private Vector2 _pan;
        private bool _dragging;

        private static Type _mouseType;

        internal bool Ok { get { return Kit.Init(); } }

        /// <summary>The ground the minimap shows (so tiles there are photographed first).</summary>
        internal MapUiInfo ViewInfo(Plugin p)
        {
            float r = Mathf.Max(30f, Plugin.MinimapRadius.Value);
            float aspect = Plugin.Shape.Value == "Wide" ? 1.6f : 1f;
            return new MapUiInfo { HalfH = r * 1.25f, HalfW = r * aspect * 1.25f };
        }

        private bool EnsureCanvas(Plugin p)
        {
            if (!Kit.Init()) return false;
            if (_canvasGo != null) return true;

            Kit.FindFont(p.PausePanel);
            _canvasGo = new GameObject("NomadMapCanvas");
            UnityEngine.Object.DontDestroyOnLoad(_canvasGo);
            _canvas = _canvasGo.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            var pauseCanvas = p.PausePanel != null ? p.PausePanel.GetComponentInParent<Canvas>() : null;
            _canvas.sortingOrder = pauseCanvas != null ? pauseCanvas.rootCanvas.sortingOrder + 5 : 200;

            var scaler = _canvasGo.AddComponent(Kit.TScaler);
            AccessTools.Property(Kit.TScaler, "uiScaleMode")?.SetValue(scaler, Enum.Parse(AccessTools.Property(Kit.TScaler, "uiScaleMode").PropertyType, "ScaleWithScreenSize"), null);
            AccessTools.Property(Kit.TScaler, "referenceResolution")?.SetValue(scaler, new Vector2(1920f, 1080f), null);
            var mm = AccessTools.Property(Kit.TScaler, "screenMatchMode");
            if (mm != null) mm.SetValue(scaler, Enum.Parse(mm.PropertyType, "MatchWidthOrHeight"), null);
            AccessTools.Property(Kit.TScaler, "matchWidthOrHeight")?.SetValue(scaler, 0.5f, null);
            if (Kit.TRaycaster != null) _canvasGo.AddComponent(Kit.TRaycaster);

            _canvasTf = (RectTransform)_canvasGo.transform;
            Plugin.Log.LogInfo("Map canvas created (sortingOrder " + _canvas.sortingOrder + ", font " + (Kit.Font != null) + ").");
            return true;
        }

        internal void Tick(Plugin p)
        {
            if (!Plugin.Enabled.Value) return;

            bool haveWorld = p.Local != null;
            if (_canvasGo != null && !haveWorld) { Destroy(); return; }
            if (!haveWorld) return;
            if (!EnsureCanvas(p)) return;

            // Canvas gets destroyed with scenes sometimes; rebuild children if their parents vanished.
            if (_miniRoot == null) { _miniSurf = null; }
            if (_fullRoot == null && _fullMode != MapMode.Closed) { _fullMode = MapMode.Closed; }

            bool wantMini = Plugin.ShowMinimap.Value && p.MinimapOn && p.Mode == MapMode.Closed && !p.PauseMenuOpen;
            TickMini(p, wantMini);
            SyncFull(p);
            if (_fullRoot != null) TickFull(p);
        }

        internal void Destroy()
        {
            if (_canvasGo != null) UnityEngine.Object.Destroy(_canvasGo);
            _canvasGo = null; _canvas = null; _canvasTf = null;
            _miniRoot = null; _miniSurf = null; _fullRoot = null; _fullSurf = null; _fullMode = MapMode.Closed;
        }

        // ---- Minimap -----------------------------------------------------------------------

        internal static Color Panel(float mul = 1f, float alpha = 1f)
        {
            Color c;
            if (!ColorUtility.TryParseHtmlString("#" + (Plugin.PanelColor.Value ?? "353644").TrimStart('#'), out c)) c = new Color(0.208f, 0.212f, 0.267f, 1f);
            return new Color(Mathf.Clamp01(c.r * mul), Mathf.Clamp01(c.g * mul), Mathf.Clamp01(c.b * mul), alpha);
        }

        // The health bar is the HUD reference: the minimap sits on it, is as wide as it (Wide shape) and
        // shows/fades together with it.
        private void RefreshHealthBar()
        {
            _barOk = false;
            if (Kit.TStatusBar == null) return;
            if (_healthBar == null) _healthBar = UnityEngine.Object.FindObjectOfType(Kit.TStatusBar) as Component;
            var rt = _healthBar != null ? _healthBar.transform as RectTransform : null;
            if (rt == null) return;
            var c = new Vector3[4];
            rt.GetWorldCorners(c);
            var cv = _healthBar.GetComponentInParent<Canvas>();
            var cam = cv != null && cv.rootCanvas.renderMode != RenderMode.ScreenSpaceOverlay ? cv.rootCanvas.worldCamera : null;
            Vector2 bl = RectTransformUtility.WorldToScreenPoint(cam, c[0]);
            Vector2 tr = RectTransformUtility.WorldToScreenPoint(cam, c[2]);
            float k = _canvas != null && _canvas.scaleFactor > 0.01f ? _canvas.scaleFactor : 1f;
            if (tr.x - bl.x < 20f || tr.y - bl.y < 5f) return;
            _barLeft = bl.x / k; _barTop = tr.y / k; _barW = (tr.x - bl.x) / k;
            _barOk = true;
            if (!_loggedBar)
            {
                _loggedBar = true;
                Plugin.Log.LogInfo("Health bar found: left " + Mathf.RoundToInt(bl.x) + ", top " + Mathf.RoundToInt(tr.y) + ", width " +
                                   Mathf.RoundToInt(tr.x - bl.x) + " (screen px, canvas scale " + k.ToString("F2") + ").");
            }
        }

        // Visible only while the health bar is: it is active, and its canvas groups are not faded out.
        private void UpdateHud()
        {
            if (_healthBar == null && Time.unscaledTime >= _nextFind) { _nextFind = Time.unscaledTime + 0.5f; RefreshHealthBar(); }
            if (_healthBar == null) { _hudVisible = _hudSince < 0f || Time.unscaledTime - _hudSince > 10f; _hudAlpha = 1f; if (_hudSince < 0f) _hudSince = Time.unscaledTime; return; }
            var go = _healthBar.gameObject;
            if (!go.activeInHierarchy) { _hudVisible = false; return; }
            var cv = _healthBar.GetComponentInParent<Canvas>();
            if (cv != null && !cv.rootCanvas.isActiveAndEnabled) { _hudVisible = false; return; }
            float a = 1f;
            foreach (var g in _healthBar.GetComponentsInParent<CanvasGroup>(false))
            {
                a *= g.alpha;
                if (g.ignoreParentGroups) break;
            }
            _hudAlpha = a;
            _hudVisible = a > 0.02f;
        }

        private void BuildMini(Plugin p)
        {
            if (_miniRoot != null) UnityEngine.Object.Destroy(_miniRoot.gameObject);
            RefreshHealthBar();
            _miniShape = Plugin.Shape.Value;
            _miniLayout = Plugin.Layout.Value;
            _miniCorner = Plugin.FixedCorner.Value ?? "BottomLeft";
            _miniWidth = Plugin.MinimapSize.Value;
            if (_miniShape == "Wide")
            {
                _miniW = _barOk ? _barW : 400f;
                _miniH = _miniW / 1.6f;
            }
            else { _miniW = _miniH = _miniWidth; }

            const float border = 6f;
            _miniRoot = Kit.Rect("Minimap", _canvasTf);
            _miniRoot.sizeDelta = new Vector2(_miniW + 2 * border, _miniH + 2 * border);
            _miniGroup = _miniRoot.gameObject.AddComponent<CanvasGroup>();
            _miniGroup.blocksRaycasts = false;
            _miniGroup.interactable = false;
            _nextLayout = 0f;
            PositionMini();

            var frame = Kit.Img("Frame", _miniRoot, Panel(1.35f, 0.95f));
            Kit.Stretch((RectTransform)frame.transform);

            var bg = Kit.Img("Area", _miniRoot, Panel(1f, Plugin.Opacity.Value));
            var area = (RectTransform)bg.transform;
            Kit.Stretch(area, border, border, border, border);
            bg.gameObject.AddComponent(Kit.TMask);

            _miniSurf = new MapSurface(area, 15f);
            _miniN = new TextRef(Kit.Txt("N", area, 17f, "Center", new Color(1f, 0.5f, 0.45f, 1f)));
            _miniN.Rt.sizeDelta = new Vector2(30f, 26f);
            _miniN.Set("N");
        }

        // Puts the minimap above the health bar (left edges aligned), or in a fixed corner.
        private void PositionMini()
        {
            if (_miniRoot == null) return;
            const float margin = 28f;
            RefreshHealthBar();
            if (_miniLayout == "AboveHealthBar" && _barOk)
            {
                _miniRoot.anchorMin = _miniRoot.anchorMax = _miniRoot.pivot = new Vector2(0f, 0f);
                _miniRoot.anchoredPosition = new Vector2(_barLeft - 6f, _barTop + 8f);
                return;
            }
            if (_miniLayout == "AboveHealthBar" && !_loggedBar) { _loggedBar = true; Plugin.Log.LogWarning("Health bar not found (yet): minimap uses the fixed corner for now."); }

            bool left = _miniCorner.EndsWith("Left"), top = _miniCorner.StartsWith("Top");
            var anchor = new Vector2(left ? 0f : 1f, top ? 1f : 0f);
            _miniRoot.anchorMin = _miniRoot.anchorMax = _miniRoot.pivot = anchor;
            _miniRoot.anchoredPosition = new Vector2(left ? margin : -margin, top ? -margin : margin);
        }

        private void TickMini(Plugin p, bool visible)
        {
            if (Time.unscaledTime >= _nextHud) { _nextHud = Time.unscaledTime + 0.1f; UpdateHud(); }
            visible = visible && _hudVisible;
            if (!visible)
            {
                if (_miniRoot != null && _miniRoot.gameObject.activeSelf) _miniRoot.gameObject.SetActive(false);
                return;
            }
            bool wideChanged = _miniShape == "Wide" && _barOk && Mathf.Abs(_barW - _miniW) > 2f;
            if (_miniRoot == null || _miniWidth != Plugin.MinimapSize.Value || _miniShape != Plugin.Shape.Value || wideChanged ||
                _miniLayout != Plugin.Layout.Value || _miniCorner != Plugin.FixedCorner.Value) BuildMini(p);
            if (!_miniRoot.gameObject.activeSelf) { _miniRoot.gameObject.SetActive(true); _nextLayout = 0f; }
            if (Time.unscaledTime >= _nextLayout) { _nextLayout = Time.unscaledTime + 1f; PositionMini(); }
            if (_miniGroup != null) _miniGroup.alpha = _hudAlpha;

            float s = (_miniH * 0.5f) / Mathf.Max(10f, Plugin.MinimapRadius.Value);
            float viewYaw = ViewYaw(p);
            float mapYaw = Plugin.RotateMinimap.Value ? viewYaw : 0f;
            float rot = mapYaw * Mathf.Deg2Rad;

            _miniSurf.Render(p, p.Local.position, s, rot, new Vector2(_miniW, _miniH), false, viewYaw, mapYaw);

            float nr = _miniH * 0.5f - 18f;
            _miniN.Rt.anchoredPosition = new Vector2(-Mathf.Sin(rot) * nr, Mathf.Cos(rot) * nr);
        }

        // ---- Fullscreen map ------------------------------------------------------------------

        private void SyncFull(Plugin p)
        {
            if (p.Mode == _fullMode && (_fullRoot != null || p.Mode == MapMode.Closed)) return;
            if (_fullRoot != null) { UnityEngine.Object.Destroy(_fullRoot.gameObject); _fullRoot = null; _fullSurf = null; }
            _fullMode = p.Mode;
            _dragging = false;
            if (p.Mode == MapMode.Closed) return;
            try { BuildFull(p, p.Mode == MapMode.Interactive); }
            catch (Exception e)
            {
                Plugin.Log.LogError("Building the map failed: " + e);
                if (_fullRoot != null) UnityEngine.Object.Destroy(_fullRoot.gameObject);
                _fullRoot = null; _fullMode = MapMode.Closed; p.CloseMap();
            }
        }

        private void BuildFull(Plugin p, bool interactive)
        {
            _pan = Vector2.zero;
            _scale = 0.9f;

            _fullRoot = Kit.Rect("FullMap", _canvasTf);
            Kit.Stretch(_fullRoot);
            _fullRoot.SetAsLastSibling();

            // In "view" mode (the M key) nothing may catch clicks or the cursor: the driver keeps driving.
            var cg = _fullRoot.gameObject.AddComponent<CanvasGroup>();
            cg.alpha = interactive ? 1f : Plugin.ViewOpacity.Value;
            cg.blocksRaycasts = interactive;
            cg.interactable = interactive;

            var dim = Kit.Img("Dim", _fullRoot, new Color(0f, 0f, 0f, interactive ? 0.72f : 0.25f), null, interactive);
            Kit.Stretch((RectTransform)dim.transform);

            var window = Kit.Img("Window", _fullRoot, Panel(1f, 0.97f), null, interactive);
            var winRt = (RectTransform)window.transform;
            Kit.Stretch(winRt, 80f, 60f, 80f, 60f);

            var title = Kit.Txt("Title", winRt, 40f, "MidlineLeft", Kit.Text);
            Kit.PlaceTL((RectTransform)title.transform, 32f, 18f, 900f, 56f);
            _fullTitle = new TextRef(title);

            var hint = Kit.Txt("Hint", winRt, 22f, "MidlineRight", new Color(1f, 1f, 1f, 0.55f));
            var hintRt = (RectTransform)hint.transform;
            hintRt.anchorMin = hintRt.anchorMax = hintRt.pivot = new Vector2(1f, 1f);
            hintRt.sizeDelta = new Vector2(700f, 40f);
            hintRt.anchoredPosition = new Vector2(interactive ? -480f : -32f, -28f);
            Kit.SetText(hint, interactive ? "Drag to pan   Scroll or +/- to zoom" : "+ / - to zoom    " + Plugin.FullMapKey.Value + " to close");

            var frame = Kit.Img("MapFrame", winRt, Panel(1.3f, 1f));
            var frameRt = (RectTransform)frame.transform;
            Kit.Stretch(frameRt, 28f, 96f, 28f, 84f);
            var areaGo = Kit.Img("Area", frameRt, Panel(0.8f, 1f));
            _fullArea = (RectTransform)areaGo.transform;
            Kit.Stretch(_fullArea, 4f, 4f, 4f, 4f);
            areaGo.gameObject.AddComponent(Kit.TMask);
            _fullSurf = new MapSurface(_fullArea, 22f);

            // Footer: scale bar and legend
            var bar = Kit.Img("ScaleBar", winRt, new Color(1f, 1f, 1f, 0.9f));
            _scaleBar = (RectTransform)bar.transform;
            Kit.PlaceBL(_scaleBar, 32f, 46f, 100f, 4f);
            _scaleText = new TextRef(Kit.Txt("ScaleText", winRt, 20f, "MidlineLeft", Kit.Text));
            Kit.PlaceBL(_scaleText.Rt, 32f, 16f, 200f, 28f);

            float lx = 760f;
            lx = Legend(winRt, lx, Kit.Gold, "Place", true);
            lx = Legend(winRt, lx, Kit.Pale, "Small place", true);
            lx = Legend(winRt, lx, Kit.Green, "Vehicle", false);
            lx = Legend(winRt, lx, Kit.Cyan, "Player", true);

            if (interactive)
            {
                var settingsBtn = p.PausePanel != null ? AccessTools.Field(p.PausePanel.GetType(), "settingsButton")?.GetValue(p.PausePanel) as Component : null;
                if (settingsBtn != null)
                {
                    MakeButton(settingsBtn, winRt, "Close", 0, () => p.CloseMap());
                    MakeButton(settingsBtn, winRt, "Recenter", 1, () => { _pan = Vector2.zero; });
                }
                else Plugin.Log.LogWarning("Settings button not found: the map has no Close button (use Esc).");
            }
            Plugin.Log.LogInfo("Fullscreen map opened (" + (interactive ? "from the pause menu" : "view mode, input passes through") + ").");
        }

        private float Legend(RectTransform parent, float x, Color col, string text, bool round)
        {
            var icon = Kit.Img("LegendIcon", parent, col, round ? Kit.Circle : null);
            Kit.PlaceBL((RectTransform)icon.transform, x, 28f, 18f, 18f);
            var t = Kit.Txt("LegendText", parent, 20f, "MidlineLeft", new Color(1f, 1f, 1f, 0.8f));
            Kit.PlaceBL((RectTransform)t.transform, x + 26f, 22f, 200f, 30f);
            Kit.SetText(t, text);
            return x + 26f + text.Length * 11f + 34f;
        }

        private static void MakeButton(Component template, Transform parent, string label, int index, Action onClick)
        {
            var btn = UnityEngine.Object.Instantiate(template.gameObject, parent);
            btn.name = "Map" + label;
            foreach (var c in btn.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                if (c.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) UnityEngine.Object.Destroy(c);
            }
            var rt = (RectTransform)btn.transform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(1f, 1f);
            rt.sizeDelta = new Vector2(220f, 58f);
            rt.anchoredPosition = new Vector2(-28f - index * 232f, -20f);
            Plugin.Instance.StartCoroutine(Plugin.ConfigureButton(btn, label, onClick));
        }

        private void TickFull(Plugin p)
        {
            if (_fullSurf == null || _fullArea == null) return;
            var size = _fullArea.rect.size;
            if (size.x < 20f || size.y < 20f) return;

            // zoom keys work in both modes, mouse only when the cursor is free
            if (Plugin.KeyDownAny(Plugin.ZoomInKeys.Value)) _scale = Mathf.Clamp(_scale * 1.25f, 0.03f, 4f);
            if (Plugin.KeyDownAny(Plugin.ZoomOutKeys.Value)) _scale = Mathf.Clamp(_scale / 1.25f, 0.03f, 4f);
            if (_fullMode == MapMode.Interactive) HandleMouse();

            var center = new Vector3(p.Local.position.x + _pan.x, 0f, p.Local.position.z + _pan.y);
            float step = _fullSurf.Render(p, center, _scale, 0f, size, true, ViewYaw(p), 0f);

            _fullTitle.Set("Map");
            _scaleBar.sizeDelta = new Vector2(Mathf.Max(4f, step * _scale), 4f);
            _scaleText.Set(step >= 1000f ? (step / 1000f) + " km" : step + " m");
        }

        // Mouse via the game's Input System (reflection), like the Wardrobe preview.
        private void HandleMouse()
        {
            if (_fullArea == null) return;
            if (_mouseType == null) _mouseType = Util.Find("UnityEngine.InputSystem.Mouse");
            var mouse = _mouseType != null ? AccessTools.Property(_mouseType, "current")?.GetValue(null, null) : null;
            if (mouse == null) return;

            Vector2 pos = ReadVec2(mouse, "position"), delta = ReadVec2(mouse, "delta"), scroll = ReadVec2(mouse, "scroll");
            var left = AccessTools.Property(_mouseType, "leftButton")?.GetValue(mouse, null);
            bool pressed = left != null && (bool)(AccessTools.Property(left.GetType(), "isPressed")?.GetValue(left, null) ?? false);

            var c = new Vector3[4];
            _fullArea.GetWorldCorners(c);
            bool inside = pos.x >= c[0].x && pos.x <= c[2].x && pos.y >= c[0].y && pos.y <= c[2].y;

            if (!pressed) _dragging = false;
            else if (inside) _dragging = true;

            float k = _canvas != null && _canvas.scaleFactor > 0.01f ? _canvas.scaleFactor : 1f;
            if (_dragging && pressed)
            {
                _pan.x -= delta.x / k / _scale;
                _pan.y -= delta.y / k / _scale;
            }
            if (inside && Mathf.Abs(scroll.y) > 0.01f)
                _scale = Mathf.Clamp(_scale * (scroll.y > 0 ? 1.15f : 1f / 1.15f), 0.03f, 4f);
        }

        private static Vector2 ReadVec2(object mouse, string control)
        {
            try
            {
                var ctl = AccessTools.Property(_mouseType, control)?.GetValue(mouse, null);
                if (ctl == null) return Vector2.zero;
                var read = ctl.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                return read != null ? (Vector2)read.Invoke(ctl, null) : Vector2.zero;
            }
            catch { return Vector2.zero; }
        }

        private static float ViewYaw(Plugin p)
        {
            var cam = Camera.main;
            return cam != null ? cam.transform.eulerAngles.y : p.Local.eulerAngles.y;
        }
    }
}
