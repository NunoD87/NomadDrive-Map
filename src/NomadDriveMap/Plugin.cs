using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace NomadDriveMap
{
    /// <summary>
    /// A minimap on the HUD and a fullscreen map opened from the pause menu.
    /// The game has no map, so everything is drawn from live scene data with generated
    /// textures: loaded POIs, vehicles, other players and a trail of where you have been.
    /// Client-side only. Game types are reached through reflection, so no game DLLs are needed to build.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.nunod87.nomaddrive.map";
        public const string Name = "Nomad Drive Map";
        public const string Version = "0.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // Settings the user can change (BepInEx/config/com.nunod87.nomaddrive.map.cfg)
        internal static ConfigEntry<bool> Enabled, ShowMinimap, RotateMinimap, ShowTrail, ShowNames, VehicleIconsOn, OnlySeen, AutoTiles;
        internal static ConfigEntry<string> Layout, Shape, FixedCorner, PanelColor, ToggleKey, FullMapKey;
        internal static ConfigEntry<int> MinimapSize, MinimapRadius, RevealRadius, MaxTilesMB;
        internal static ConfigEntry<float> PixelsPerMetre, TileInterval, Opacity, ViewOpacity, ExposureOffset;

        // Fixed values (tuned during development; not worth a setting)
        internal static readonly Const<bool> ProfileLog = new Const<bool>(false), VerboseLog = new Const<bool>(false),
            HidePlayerInVehicle = new Const<bool>(true), FadeZoomed = new Const<bool>(true), NormalizeSun = new Const<bool>(true),
            CaptureAtNight = new Const<bool>(true), HideUnseenInTiles = new Const<bool>(false);
        internal static readonly Const<int> JpegQuality = new Const<int>(88), CaptureHeight = new Const<int>(150);
        internal static readonly Const<float> MinSun = new Const<float>(15f), PlayerMarkerScale = new Const<float>(0.6f),
            VehicleIconScale = new Const<float>(1.6f), SunTemperature = new Const<float>(6500f), AmbientFraction = new Const<float>(0.25f);
        internal static readonly Const<string> ZoomInKeys = new Const<string>("Equals,NumpadPlus"), ZoomOutKeys = new Const<string>("Minus,NumpadMinus");

        private Harmony _harmony;
        private readonly HashSet<string> _patched = new HashSet<string>();

        // Pause menu
        private Component _pausePanel;
        internal Component PausePanel { get { return _pausePanel; } }

        // Map state
        internal MapMode Mode = MapMode.Closed;
        private readonly MapUi _ui = new MapUi();
        private bool _minimapOn = true;
        internal bool MinimapOn { get { return _minimapOn; } }

        // Scene data (refreshed on a timer, positions are read live every frame)
        private Type _tPlayer, _tPoi, _tVehicle, _tGen;
        private PropertyInfo _genReady;
        private PropertyInfo _isLocal, _poiName, _poiCategory, _genSeed;
        private UnityEngine.Object _gen;
        private float _nextScan, _nextDiscover, _nextTrail, _nextSeat;
        private bool _loggedFirstScan, _loggedSun;
        private string _worldKey;
        private static readonly Dictionary<string, WorldMemory> Worlds = new Dictionary<string, WorldMemory>();

        internal Transform Local;
        internal WorldMemory Mem;
        internal float Sun = 45f;                                   // sun elevation in degrees, negative = night
        internal Light SunLight;
        private object _weather;
        private Type _tWeather;
        private bool _loggedWeather;

        /// <summary>True when the weather is clean: no rain, snow, fog, storm, sand, and the ground has dried.</summary>
        internal bool WeatherClean(out string why)
        {
            why = "";
            try
            {
                if (_tWeather == null) _tWeather = Util.Find("NomadDrive.Features.Weather.DynamicWeatherDirector");
                if (_tWeather == null) return true;
                if (_weather == null || (_weather as UnityEngine.Object) == null) _weather = UnityEngine.Object.FindObjectOfType(_tWeather);
                if (_weather == null) return true;
                string kind = Convert.ToString(AccessTools.Property(_tWeather, "CurrentKind")?.GetValue(_weather, null));
                float wet = Convert.ToSingle(AccessTools.Property(_tWeather, "Wetness")?.GetValue(_weather, null) ?? 0f);
                float snow = Convert.ToSingle(AccessTools.Property(_tWeather, "Snow")?.GetValue(_weather, null) ?? 0f);
                float rain = Convert.ToSingle(AccessTools.Property(_tWeather, "RainEmission")?.GetValue(_weather, null) ?? 0f);
                if (!_loggedWeather) { _loggedWeather = true; Log.LogInfo("Weather read: kind " + kind + ", wetness " + wet.ToString("F2") + ", snow " + snow.ToString("F2") + ", rain " + rain.ToString("F2")); }
                bool badKind = kind == "Fog" || kind == "LightRain" || kind == "HeavyRain" || kind == "Storm" || kind == "Snow" || kind == "Blizzard" || kind == "Sandstorm";
                if (badKind || wet > 0.05f || snow > 0.05f || rain > 0.02f)
                {
                    why = "weather is not clean (" + kind + ", wetness " + wet.ToString("F2") + ", snow " + snow.ToString("F2") + ", rain " + rain.ToString("F2") + ")";
                    return false;
                }
            }
            catch (Exception e) { if (!_loggedWeather) { _loggedWeather = true; Log.LogInfo("Weather unreadable: " + e.Message); } }
            return true;
        }
        internal Color SunDayColor = Color.white;
        internal float SunDayTemp;
        internal bool SunDayUseTemp, SunDayKnown;
        private float _lastSunSaved;
        private object _roadMgr;
        private float _roadRadius = float.MaxValue, _nextRoadRead = -100f;
        private bool _loggedRoadRadius;

        /// <summary>How far from the player the game keeps roads fully detailed (dynamic upgrade / lazy loading distance).</summary>
        internal float RoadDetailRadius()
        {
            if (Time.unscaledTime < _nextRoadRead) return _roadRadius;
            _nextRoadRead = Time.unscaledTime + 30f;
            try
            {
                var t = Util.Find("NomadDrive.Features.EvilRoads.EvilRoadsManager");
                if (t == null) return _roadRadius;
                if (_roadMgr == null || (_roadMgr as UnityEngine.Object) == null) _roadMgr = UnityEngine.Object.FindObjectOfType(t);
                if (_roadMgr == null) return _roadRadius;
                Func<string, object> call = n => AccessTools.Method(t, n)?.Invoke(_roadMgr, null);
                float r = float.MaxValue;
                string info = "";
                var dyn = call("IsDynamicUpgradeEnabled");
                var up = call("GetUpgradeDistanceThreshold");
                var lazy = call("IsLazyLoadingEnabled");
                var lz = call("GetLazyLoadingDistance");
                if (dyn is bool && (bool)dyn && up != null) { r = Mathf.Min(r, Convert.ToSingle(up)); info += "dynamic upgrade " + up + " m; "; }
                if (lazy is bool && (bool)lazy && lz != null) { r = Mathf.Min(r, Convert.ToSingle(lz)); info += "lazy loading " + lz + " m; "; }
                _roadRadius = r < 20f ? float.MaxValue : r;
                if (!_loggedRoadRadius) { _loggedRoadRadius = true; Log.LogInfo("Road detail distance: " + (info.Length > 0 ? info : "no limit found") + "-> using " + (_roadRadius == float.MaxValue ? "no limit" : _roadRadius.ToString("F0") + " m")); }
            }
            catch (Exception e) { if (!_loggedRoadRadius) { _loggedRoadRadius = true; Log.LogInfo("Road detail distance unreadable: " + e.Message); } }
            return _roadRadius;
        }
        internal bool LocalSeated;
        private Component _localComp;
        private FieldInfo _seatField;
        internal float SunStrength;
        internal float SunDayMax;                                   // strongest sun seen while it was high
        internal string LightsText = "";
        private float _worldReadySince = -1f;
        internal bool WorldReadyFor(float seconds) { return _worldReadySince >= 0f && Time.unscaledTime - _worldReadySince >= seconds; }
        internal float Night { get { return Mathf.Clamp01((10f - Sun) / 15f); } }
        private float _nextTile;
        internal readonly List<Poi> Pois = new List<Poi>();             // everything loaded (NOT shown, only used for discovery)
        internal readonly List<Transform> Vehicles = new List<Transform>();
        internal readonly List<Transform> Others = new List<Transform>();

        internal struct Poi
        {
            public Transform T;
            public string Name;
            public bool Major;
        }

        private void Awake()
        {
            Log = Logger;
            Instance = this;

            Enabled = Config.Bind("General", "Enabled", true, "Turn the mod on or off without uninstalling it (needs a game restart).");

            ShowMinimap = Config.Bind("Minimap", "Show", true, "Show the minimap on the HUD.");
            Layout = Config.Bind("Minimap", "Layout", "FixedCorner",
                new ConfigDescription("FixedCorner = the corner chosen below. AboveHealthBar = sits right above the health bar, left edges aligned.",
                    new AcceptableValueList<string>("FixedCorner", "AboveHealthBar")));
            FixedCorner = Config.Bind("Minimap", "Corner", "TopLeft",
                new ConfigDescription("Screen corner for the minimap (also used when the health bar cannot be found).",
                    new AcceptableValueList<string>("TopLeft", "TopRight", "BottomLeft", "BottomRight")));
            Shape = Config.Bind("Minimap", "Shape", "Square",
                new ConfigDescription("Square (uses Size), or Wide (as wide as the health bar, 1.6 : 1, like the GTA V minimap).", new AcceptableValueList<string>("Square", "Wide")));
            MinimapSize = Config.Bind("Minimap", "Size", 300,
                new ConfigDescription("Size of the square minimap in pixels at 1080p (scales with the screen). The Wide shape ignores this.", new AcceptableValueRange<int>(120, 700)));
            MinimapRadius = Config.Bind("Minimap", "ZoomRadiusMetres", 70,
                new ConfigDescription("How many metres from you to the top/bottom edge of the minimap (smaller = more zoomed in).", new AcceptableValueRange<int>(30, 1000)));
            Opacity = Config.Bind("Minimap", "Opacity", 0.85f,
                new ConfigDescription("Minimap background opacity.", new AcceptableValueRange<float>(0.2f, 1f)));
            RotateMinimap = Config.Bind("Minimap", "RotateWithView", false, "Rotate the minimap so it always points where you look. Off = north (the way the road goes) is up.");
            ToggleKey = Config.Bind("Minimap", "ToggleKey", "F7", "Key that shows/hides the minimap (Unity Input System key name, e.g. F7). Empty = no key.");

            FullMapKey = Config.Bind("Map", "FullMapKey", "M", "Key that opens/closes the fullscreen map without pausing the game or taking the controls. Empty = no key. The map is also in the pause menu.");
            ViewOpacity = Config.Bind("Map", "FullMapOpacity", 0.92f,
                new ConfigDescription("How solid the fullscreen map is when opened with the key (lower = you can see the game through it).", new AcceptableValueRange<float>(0.3f, 1f)));
            PanelColor = Config.Bind("Map", "PanelColor", "353644", "Colour of the map panels, hex RRGGBB. The default matches the game's health bar.");
            ShowTrail = Config.Bind("Map", "ShowTrail", true, "Draw a trail of where you have been.");
            ShowNames = Config.Bind("Map", "ShowPlaceNames", true, "Show the names of places on the maps.");
            VehicleIconsOn = Config.Bind("Map", "ShowVehicleModels", true, "Draw the game's own vehicle models (seen from above) on the maps instead of a plain marker.");

            OnlySeen = Config.Bind("Discovery", "OnlyShowSeen", true,
                "Only show places and vehicles you have actually seen (in range and with a clear line of sight). Off = show everything that is loaded.");
            RevealRadius = Config.Bind("Discovery", "RevealRadiusMetres", 150,
                new ConfigDescription("How far you can see a place or vehicle to put it on the map.", new AcceptableValueRange<int>(20, 600)));

            AutoTiles = Config.Bind("GroundPictures", "Enabled", true, "Take top-down pictures of the ground around you and draw them on the maps. Saved per world.");
            PixelsPerMetre = Config.Bind("GroundPictures", "PixelsPerMetre", 6f, new ConfigDescription("Sharpness of the ground pictures (a tile is 128 m wide). Higher shows thinner things like power lines, but makes bigger files and a longer hitch per picture.", new AcceptableValueRange<float>(2f, 12f)));
            TileInterval = Config.Bind("GroundPictures", "SecondsBetweenPictures", 4f, new ConfigDescription("Minimum time between two ground pictures outside the minimap area (pictures inside it use 1 second). Higher = less lag, slower map filling.", new AcceptableValueRange<float>(0.5f, 30f)));
            MaxTilesMB = Config.Bind("GroundPictures", "MaxSizeMB", 150, new ConfigDescription("Disk space limit for the ground pictures of one world. The oldest are deleted above it.", new AcceptableValueRange<int>(20, 5000)));
            ExposureOffset = Config.Bind("GroundPictures", "BrightnessOffsetEV", 0.5f, new ConfigDescription("Make new ground pictures brighter (+) or darker (-). Pictures already taken keep their brightness.", new AcceptableValueRange<float>(-4f, 4f)));

            if (!Enabled.Value) { Log.LogInfo("Disabled in config."); return; }

            _harmony = new Harmony(Guid);
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) => TryPatch(args.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) TryPatch(asm);
            try { Assembly.Load("EvilCore.UI"); }
            catch (Exception e) { Log.LogInfo("Could not preload EvilCore.UI: " + e.Message); }

            LoadSun();
            Log.LogInfo(Name + " " + Version + " loaded.");
        }

        // ---- Pause menu entry (same technique as the Wardrobe mod) ------------------------

        private void TryPatch(Assembly asm)
        {
            string asmName;
            try { asmName = asm.GetName().Name; } catch { return; }
            if (asmName != "EvilCore.UI" || !_patched.Add(asmName)) return;
            try
            {
                var panel = asm.GetType("EvilCore.UI.GameMenu.Panels.GameMenuRootPanel");
                var start = panel?.GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (start == null) { Log.LogError("GameMenuRootPanel.Start not found. Game version changed?"); return; }
                _harmony.Patch(start, postfix: new HarmonyMethod(typeof(Plugin).GetMethod(
                    nameof(RootPanelStartPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                Log.LogInfo("Pause menu: Map button patch ready.");
            }
            catch (Exception e) { Log.LogError("Patching failed: " + e); }
        }

        private static void RootPanelStartPostfix(object __instance)
        {
            try
            {
                var panel = __instance as Component;
                var settings = AccessTools.Field(__instance.GetType(), "settingsButton")?.GetValue(__instance) as Component;
                if (panel == null || settings == null) { Log.LogWarning("settingsButton not found on the pause menu."); return; }
                Instance._pausePanel = panel;

                var btnType = Util.Find("UnityEngine.UI.Button");
                var layoutType = Util.Find("UnityEngine.UI.LayoutGroup");

                Transform item = settings.transform, list = null;
                var chain = new System.Text.StringBuilder("Pause menu structure:");
                for (int i = 0; i < 5 && item != null; i++)
                {
                    var par = item.parent;
                    bool isList = par != null && layoutType != null && par.GetComponent(layoutType) != null;
                    chain.Append(' ').Append(item.name).Append('[').Append(item.childCount).Append("] <");
                    if (isList) { list = par; chain.Append(' ').Append(par.name).Append('[').Append(par.childCount).Append(", layout]"); break; }
                    item = par;
                }
                Log.LogInfo(chain.ToString());

                int buttons = btnType != null && item != null ? item.GetComponentsInChildren(btnType, true).Length : 99;
                GameObject clone;
                if (list != null && buttons <= 1)
                {
                    if (list.Find("MapButton") != null) return;
                    clone = UnityEngine.Object.Instantiate(item.gameObject, list);
                    clone.transform.SetSiblingIndex(item.GetSiblingIndex() + 1);
                }
                else
                {
                    Log.LogWarning("Could not find the pause menu list (buttons in entry: " + buttons + "); using a plain copy next to Settings.");
                    var parent = settings.transform.parent;
                    if (parent.Find("MapButton") != null) return;
                    clone = UnityEngine.Object.Instantiate(settings.gameObject, parent);
                    Instance.StartCoroutine(PlaceSide(settings.transform, clone));
                }
                clone.name = "MapButton";

                // Localization components would overwrite our label; strip them on the copy only.
                foreach (var c in clone.GetComponentsInChildren<Component>(true))
                {
                    if (c == null) continue;
                    if (c.GetType().Name.IndexOf("Locali", StringComparison.OrdinalIgnoreCase) >= 0) UnityEngine.Object.Destroy(c);
                }
                Instance.StartCoroutine(ConfigureButton(clone, "Map", () => Instance.ToggleInteractive()));
                Log.LogInfo("Map entry added to the pause menu.");
            }
            catch (Exception e) { Log.LogError("RootPanelStartPostfix: " + e); }
        }

        private static IEnumerator PlaceSide(Transform reference, GameObject clone)
        {
            yield return new WaitForSecondsRealtime(0.8f);
            if (clone == null || reference == null) yield break;
            try
            {
                var le = Util.Find("UnityEngine.UI.LayoutElement");
                if (le != null)
                {
                    var comp = clone.GetComponent(le) ?? clone.AddComponent(le);
                    AccessTools.Property(le, "ignoreLayout")?.SetValue(comp, true, null);
                }
                var r = (RectTransform)reference;
                var c = (RectTransform)clone.transform;
                c.anchorMin = r.anchorMin; c.anchorMax = r.anchorMax; c.pivot = r.pivot; c.sizeDelta = r.sizeDelta;
                float w = r.rect.width > 20f ? r.rect.width : 360f;
                c.anchoredPosition = r.anchoredPosition + new Vector2(w + 24f, 0f);
            }
            catch (Exception e) { Log.LogError("PlaceSide: " + e); }
        }

        internal static IEnumerator ConfigureButton(GameObject clone, string label, Action onClick)
        {
            yield return null; // let the destroyed localization components go first
            if (clone == null) yield break;
            try
            {
                var tmp = Util.Find("TMPro.TMP_Text");
                var text = tmp != null ? clone.GetComponentInChildren(tmp, true) : null;
                if (text != null) AccessTools.Property(tmp, "text")?.SetValue(text, label, null);

                var btnType = Util.Find("UnityEngine.UI.Button");
                var btn = btnType != null ? clone.GetComponentInChildren(btnType, true) : null;
                var ev = btn != null ? AccessTools.Property(btnType, "onClick")?.GetValue(btn, null) as UnityEvent : null;
                if (ev == null) { Log.LogWarning("Could not wire the button '" + label + "' (Button.onClick not found)."); yield break; }
                ev.RemoveAllListeners();
                ev.AddListener(() => onClick());
            }
            catch (Exception e) { Log.LogError("ConfigureButton: " + e); }
        }

        // ---- Map modes ---------------------------------------------------------------------

        internal bool PauseMenuOpen
        {
            get { return _pausePanel != null && _pausePanel.gameObject.activeInHierarchy; }
        }

        private void ToggleInteractive()
        {
            Mode = Mode == MapMode.Interactive ? MapMode.Closed : MapMode.Interactive;
        }

        internal void CloseMap() { Mode = MapMode.Closed; }

        // ---- Update ------------------------------------------------------------------------

        private void Update()
        {
            if (!Enabled.Value) return;

            bool pause = PauseMenuOpen;
            if (Local == null) Mode = MapMode.Closed;
            if (Mode == MapMode.Interactive && !pause) Mode = MapMode.Closed;
            if (Mode == MapMode.View && pause) Mode = MapMode.Closed;

            if (Local != null)
            {
                if (KeyDownAny(FullMapKey.Value) && !TypingInField())
                {
                    if (Mode == MapMode.Closed && !pause) Mode = MapMode.View;
                    else if (Mode != MapMode.Closed) Mode = MapMode.Closed;
                }
                if (!pause && KeyDown(ToggleKey.Value)) { _minimapOn = !_minimapOn; Log.LogInfo("Minimap " + (_minimapOn ? "on" : "off")); }
            }

            if (Time.unscaledTime >= _nextScan)
            {
                long ps = Prof.Now();
                int stage = _scanStage;
                try { ScanStage(); } catch (Exception e) { Log.LogError("Scan: " + e); _scanStage = 0; _nextScan = Time.unscaledTime + 3f; }
                Prof.Add(stage == 0 ? "scan-players+world" : stage == 1 ? "scan-vehicles" : "scan-places", ps);
            }
            if (Local != null && Mem != null && Time.unscaledTime >= _nextDiscover)
            {
                _nextDiscover = Time.unscaledTime + 0.5f;
                long pd = Prof.Now(); try { Discover(); } catch (Exception e) { Log.LogError("Discover: " + e); } Prof.Add("discover", pd);
            }
            if (Local != null && Mem != null && Time.unscaledTime >= _nextTrail)
            {
                _nextTrail = Time.unscaledTime + 0.25f;
                var t = ToTrue(Local.position);
                var trail = Mem.Trail;
                if (trail.Count == 0 || (trail[trail.Count - 1] - t).sqrMagnitude >= 100f)
                {
                    trail.Add(t);
                    Mem.Dirty = true;
                    if (trail.Count > 6000) trail.RemoveRange(0, 500);
                }
            }

            if (Time.unscaledTime >= _nextSave) { _nextSave = Time.unscaledTime + 30f; SaveAll(); }

            if (AutoTiles.Value && Local != null && Mem != null && Mem.Tiles != null && !pause && Time.unscaledTime >= _nextTile)
            {
                _nextTile = Time.unscaledTime + 1f;
                long pt0 = Prof.Now(); try { TileCapture.Tick(this, _ui.ViewInfo(this)); } catch (Exception e) { Log.LogError("Tile capture: " + e.Message); }
                Prof.Add("tile-tick(all)", pt0);
            }


            if (Local != null && Time.unscaledTime >= _nextSeat)
            {
                _nextSeat = Time.unscaledTime + 0.2f;
                try
                {
                    if (_localComp == null || _localComp.transform != Local) _localComp = _tPlayer != null ? Local.GetComponent(_tPlayer) : null;
                    if (_seatField == null && _tPlayer != null) _seatField = AccessTools.Field(_tPlayer, "_currentSeat");
                    var seat = _localComp != null && _seatField != null ? _seatField.GetValue(_localComp) as UnityEngine.Object : null;
                    LocalSeated = seat != null;
                }
                catch { LocalSeated = false; }
            }
            else if (Local == null) LocalSeated = false;

            Prof.Frame(Time.unscaledDeltaTime);
            long pu = Prof.Now();
            try { _ui.Tick(this); }
            catch (Exception e) { Log.LogError("UI: " + e); _nextScan = Time.unscaledTime + 5f; }
            Prof.Add("ui-tick", pu);
        }

        [Serializable] private class SunSave { public float max; public Color color = Color.white; public float temp; public bool useTemp; }

        private static string SunFile { get { return System.IO.Path.Combine(Persist.Dir, "sun.json"); } }

        private void LoadSun()
        {
            try
            {
                if (!System.IO.File.Exists(SunFile)) return;
                var d = JsonUtility.FromJson<SunSave>(System.IO.File.ReadAllText(SunFile));
                if (d == null || d.max <= 0f) return;
                SunDayMax = d.max; SunDayColor = d.color; SunDayTemp = d.temp; SunDayUseTemp = d.useTemp; SunDayKnown = true; _lastSunSaved = d.max;
                Log.LogInfo("Daytime sun remembered: strength " + d.max.ToString("F1") + (d.useTemp ? ", " + d.temp.ToString("F0") + " K" : ", colour " + d.color));
            }
            catch (Exception e) { Log.LogInfo("Could not read sun.json: " + e.Message); }
        }

        internal void SaveSun()
        {
            if (Mathf.Abs(SunDayMax - _lastSunSaved) < SunDayMax * 0.01f) return;
            try
            {
                System.IO.Directory.CreateDirectory(Persist.Dir);
                System.IO.File.WriteAllText(SunFile, JsonUtility.ToJson(new SunSave { max = SunDayMax, color = SunDayColor, temp = SunDayTemp, useTemp = SunDayUseTemp }));
                _lastSunSaved = SunDayMax;
            }
            catch { }
        }

        private void OnDestroy() { SaveAll(); _ui.Destroy(); }
        private void OnApplicationQuit() { SaveAll(); }

        private float _nextSave;
        private void SaveAll()
        {
            foreach (var kv in Worlds) if (kv.Value.Dirty) Persist.Save(kv.Key, kv.Value);
        }

        internal static string PoiKey(Poi poi)
        {
            var tp = ToTrue(poi.T.position);
            return poi.Name + "@" + Mathf.RoundToInt(tp.x / 20f) + "," + Mathf.RoundToInt(tp.z / 20f);
        }

        // ---- Scene scan (what is loaded) -------------------------------------------------------

        // The scene scan is split in three steps, one per frame, and runs every 3 seconds (FindObjectsOfType is
        // expensive: doing all of it in one frame was a 13-17 ms stutter).
        private int _scanStage;
        private const float ScanInterval = 3f;

        private void ScanStage()
        {
            if (_scanStage == 0)
            {
                ScanPlayers();
                if (Local == null) { _nextScan = Time.unscaledTime + 1.5f; return; } // keep looking for the player
                _scanStage = 1; _nextScan = 0f;
            }
            else if (_scanStage == 1)
            {
                if (Local == null) { _scanStage = 0; return; }
                Vehicles.Clear();
                foreach (var o in UnityEngine.Object.FindObjectsOfType(_tVehicle))
                {
                    var c = o as Component;
                    if (c != null) Vehicles.Add(c.transform);
                }
                _scanStage = 2; _nextScan = 0f;
            }
            else
            {
                _scanStage = 0; _nextScan = Time.unscaledTime + ScanInterval;
                if (Local == null) return;
                ScanPlaces();
            }
        }

        private void ScanPlayers()
        {
            if (_tPlayer == null)
            {
                _tPlayer = Util.Find("NomadDrive.Features.Player.Player");
                _tPoi = Util.Find("NomadDrive.Features.WorldGeneration.POISpawning.PoiDefinition");
                _tVehicle = Util.Find("NomadDrive.Features.Vehicle.VehicleManager");
                _tGen = Util.Find("NomadDrive.Features.WorldGeneration.WorldGenerator");
                if (_tPlayer == null || _tPoi == null || _tVehicle == null)
                {
                    Log.LogError("Game types missing (Player=" + (_tPlayer != null) + " PoiDefinition=" + (_tPoi != null) +
                                 " VehicleManager=" + (_tVehicle != null) + "). Game version changed?");
                    _tPlayer = null; _nextScan = Time.unscaledTime + 30f; return;
                }
                _isLocal = AccessTools.Property(_tPlayer, "isLocalPlayer");
                _poiName = AccessTools.Property(_tPoi, "DisplayName");
                _poiCategory = AccessTools.Property(_tPoi, "Category");
                _genSeed = _tGen != null ? AccessTools.Property(_tGen, "Seed") : null;
                _genReady = _tGen != null ? AccessTools.Property(_tGen, "IsWorldFullyReady") : null;
                Log.LogInfo("Map types resolved (isLocalPlayer=" + (_isLocal != null) + ", DisplayName=" + (_poiName != null) +
                            ", Category=" + (_poiCategory != null) + ", world seed=" + (_genSeed != null) + ").");
            }

            var prevLocal = Local;
            Local = null;
            Others.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_tPlayer))
            {
                var c = o as Component;
                if (c == null) continue;
                bool local = false;
                try { local = _isLocal != null && (bool)_isLocal.GetValue(c, null); } catch { }
                if (local) Local = c.transform; else Others.Add(c.transform);
            }
            if (Local != prevLocal) { _gen = null; _worldKey = null; _worldReadySince = -1f; if (Local == null) SaveAll(); }
            if (Local == null) { Pois.Clear(); Vehicles.Clear(); return; }

            UpdateWorld();
            TopDown.UpdateSun(this, !_loggedSun);
            _loggedSun = true;
            bool ready = false;
            try { ready = _gen != null && _genReady != null && (bool)_genReady.GetValue(_gen, null); } catch { }
            if (ready) { if (_worldReadySince < 0f) { _worldReadySince = Time.unscaledTime; Log.LogInfo("World is fully ready; ground pictures start in a few seconds."); } }
            else _worldReadySince = -1f;
        }

        private void ScanPlaces()
        {
            Pois.Clear();
            foreach (var o in UnityEngine.Object.FindObjectsOfType(_tPoi))
            {
                var c = o as Component;
                if (c == null) continue;
                string cat = null;
                try { cat = _poiCategory != null ? _poiCategory.GetValue(c, null)?.ToString() : null; } catch { }
                Pois.Add(new Poi { T = c.transform, Name = PoiLabel(c), Major = cat == "Major" });
            }

            try { VehicleIcons.Tick(this); } catch (Exception e) { Log.LogError("Vehicle icons: " + e.Message); }

            if (VerboseLog.Value || !_loggedFirstScan)
            {
                _loggedFirstScan = true;
                Log.LogInfo("Scan: you at " + ToTrue(Local.position).ToString("F0") + ", loaded pois=" + Pois.Count + ", vehicles=" + Vehicles.Count +
                            ", other players=" + Others.Count + ", known pois=" + (Mem != null ? Mem.Pois.Count : 0));
                int n = 0;
                foreach (var p in Pois)
                {
                    if (p.T == null) continue;
                    Log.LogInfo("  loaded POI " + (p.Major ? "[major] " : "[minor] ") + p.Name + " " + Vector3.Distance(p.T.position, Local.position).ToString("F0") +
                                " m away, hierarchy: " + PathOf(p.T));
                    if (++n >= 8 && !VerboseLog.Value) { Log.LogInfo("  ..."); break; }
                }
                if (Vehicles.Count > 0) Log.LogInfo("  first vehicle hierarchy: " + PathOf(Vehicles[0]));
            }
        }

        private static string PathOf(Transform t)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 4 && t != null; i++)
            {
                sb.Append(t.name).Append('[').Append(t.childCount).Append("] < ");
                t = t.parent;
            }
            return sb.ToString();
        }

        private void UpdateWorld()
        {
            if (_gen == null && _tGen != null) _gen = UnityEngine.Object.FindObjectOfType(_tGen);
            string key = "unknown";
            if (_gen != null && _genSeed != null)
            {
                try { key = Convert.ToString(_genSeed.GetValue(_gen, null)); } catch { }
            }
            if (key == _worldKey && Mem != null) return;
            var previous = Mem;
            bool previousValid = _worldKey != null && Persist.ValidSeed(_worldKey);
            _worldKey = key;
            WorldMemory m;
            if (!Worlds.TryGetValue(key, out m)) { m = new WorldMemory(); Worlds[key] = m; Persist.Load(key, m); m.Tiles = new TileStore(key); }
            // Things found before the world's seed was known (it is read a moment after spawning) belong to this world.
            if (previous != null && !previousValid && Persist.ValidSeed(key))
            {
                foreach (var kv in previous.Pois) m.Pois[kv.Key] = kv.Value;
                m.Trail.AddRange(previous.Trail);
                foreach (var id in previous.VehiclesSeen) m.VehiclesSeen.Add(id);
                m.Dirty = true;
                previous.Pois.Clear(); previous.Trail.Clear();
            }
            Mem = m;
            Log.LogInfo("Map memory for world seed " + key + ": " + m.Pois.Count + " places, " + m.Trail.Count + " trail points.");
        }

        private string PoiLabel(Component c)
        {
            string s = null;
            try { s = _poiName != null ? _poiName.GetValue(c, null) as string : null; } catch { }
            if (string.IsNullOrEmpty(s) || (s.IndexOf(' ') < 0 && s.IndexOf('.') >= 0)) s = c.gameObject.name;
            return s.Replace("(Clone)", "").Replace('_', ' ').Trim();
        }

        // ---- Discovery: only what you have seen goes on the map ---------------------------------

        private void Discover()
        {
            var cam = Camera.main;
            Vector3 eye = cam != null ? cam.transform.position : Local.position + Vector3.up * 1.6f;
            bool all = !OnlySeen.Value;
            float radius = RevealRadius.Value;

            // the vehicle we are in (its own colliders must not block the view)
            Transform own = null;
            float best = 7f * 7f;
            foreach (var v in Vehicles)
            {
                if (v == null) continue;
                float sq = (v.position - eye).sqrMagnitude;
                if (sq < best) { best = sq; own = v; }
            }

            int budget = 60;
            foreach (var poi in Pois)
            {
                if (poi.T == null) continue;
                var tp = ToTrue(poi.T.position);
                string key = PoiKey(poi);
                if (Mem.Pois.ContainsKey(key)) continue;
                if (!all)
                {
                    float dist = Vector3.Distance(eye, poi.T.position);
                    if (dist > radius) continue;
                    if (budget-- <= 0) break;
                    if (!Sight.CanSee(eye, poi.T.position + Vector3.up * 2.5f, poi.T, Local, own, 12f)) continue;
                }
                Mem.Pois[key] = new Known { Name = poi.Name, TruePos = tp, Major = poi.Major };
                Mem.Dirty = true;
                Log.LogInfo("Discovered " + (poi.Major ? "place " : "small place ") + poi.Name + " at " + tp.ToString("F0") + " (" +
                            Vector3.Distance(eye, poi.T.position).ToString("F0") + " m away)");
            }

            foreach (var v in Vehicles)
            {
                if (v == null) continue;
                int id = v.GetInstanceID();
                if (Mem.VehiclesSeen.Contains(id)) continue;
                if (!all)
                {
                    float dist = Vector3.Distance(eye, v.position);
                    if (dist > radius) continue;
                    if (dist > 8f)
                    {
                        if (budget-- <= 0) break;
                        if (!Sight.CanSee(eye, v.position + Vector3.up, v, Local, own, 4f)) continue;
                    }
                }
                Mem.VehiclesSeen.Add(id);
                if (VerboseLog.Value) Log.LogInfo("Discovered vehicle " + v.name);
            }
        }

        // ---- Floating origin: keep the trail in true-world coordinates ---------------------

        private static MethodInfo _toTrue, _toRender;
        private static bool _foResolved;

        private static void ResolveFo()
        {
            if (_foResolved) return;
            _foResolved = true;
            var t = Util.Find("NomadDrive.Features.FloatingOrigin.FloatingOriginManager");
            _toTrue = t != null ? AccessTools.Method(t, "ToTrueWorld", new[] { typeof(Vector3) }) : null;
            _toRender = t != null ? AccessTools.Method(t, "ToRenderWorld", new[] { typeof(Vector3) }) : null;
            if (_toTrue == null || _toRender == null)
                Log.LogWarning("FloatingOriginManager.ToTrueWorld/ToRenderWorld not found: the trail will drift after an origin shift.");
        }

        internal static Vector3 ToTrue(Vector3 render)
        {
            ResolveFo();
            if (_toTrue == null) return render;
            try { return (Vector3)_toTrue.Invoke(null, new object[] { render }); } catch { return render; }
        }

        internal static Vector3 ToRender(Vector3 world)
        {
            ResolveFo();
            if (_toRender == null) return world;
            try { return (Vector3)_toRender.Invoke(null, new object[] { world }); } catch { return world; }
        }

        // ---- Hotkey (Unity 6 / new Input System, reflection so there is no hard reference) ---

        private static Type _kbType, _keyEnum;
        private static MethodInfo _kbItem;
        private static bool _kbResolved;
        private static readonly Dictionary<string, object> KeyValues = new Dictionary<string, object>();
        private static readonly Dictionary<string, PropertyInfo> KeyProps = new Dictionary<string, PropertyInfo>();

        private static bool KeyDown(string name) { return KeyState(name, "wasPressedThisFrame"); }
        internal static bool KeyHeld(string name) { return KeyState(name, "isPressed"); }

        private static bool KeyState(string name, string prop)
        {
            if (string.IsNullOrEmpty(name)) return false;
            try
            {
                if (!_kbResolved)
                {
                    _kbResolved = true;
                    _kbType = Util.Find("UnityEngine.InputSystem.Keyboard");
                    _keyEnum = Util.Find("UnityEngine.InputSystem.Key");
                    if (_kbType != null && _keyEnum != null) _kbItem = _kbType.GetMethod("get_Item", new[] { _keyEnum });
                }
                if (_kbItem != null)
                {
                    var cur = AccessTools.Property(_kbType, "current")?.GetValue(null, null);
                    if (cur == null) return false;
                    object kv;
                    if (!KeyValues.TryGetValue(name, out kv))
                    {
                        try { kv = Enum.Parse(_keyEnum, name, true); }
                        catch { kv = null; Log.LogWarning("Unknown key name '" + name + "' in the config."); }
                        KeyValues[name] = kv;
                    }
                    if (kv == null) return false;
                    var ctrl = _kbItem.Invoke(cur, new[] { kv });
                    if (ctrl == null) return false;
                    PropertyInfo pi;
                    if (!KeyProps.TryGetValue(prop, out pi)) { pi = ctrl.GetType().GetProperty(prop); KeyProps[prop] = pi; }
                    return pi != null && (bool)pi.GetValue(ctrl, null);
                }
                return prop == "isPressed" ? Input.GetKey((KeyCode)Enum.Parse(typeof(KeyCode), name, true))
                                           : Input.GetKeyDown((KeyCode)Enum.Parse(typeof(KeyCode), name, true));
            }
            catch { return false; }
        }

        internal static bool KeyDownAny(string csv)
        {
            if (string.IsNullOrEmpty(csv)) return false;
            foreach (var k in csv.Split(','))
                if (KeyDown(k.Trim())) return true;
            return false;
        }

        // Do not react to hotkeys while the player types (chat, password boxes...).
        private static Type _esType;
        private static bool TypingInField()
        {
            try
            {
                if (_esType == null) _esType = Util.Find("UnityEngine.EventSystems.EventSystem");
                var cur = _esType != null ? AccessTools.Property(_esType, "current")?.GetValue(null, null) : null;
                var go = cur != null ? AccessTools.Property(_esType, "currentSelectedGameObject")?.GetValue(cur, null) as GameObject : null;
                if (go == null) return false;
                foreach (var c in go.GetComponents<Component>())
                    if (c != null && c.GetType().Name.IndexOf("InputField", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
        }
    }

    /// <summary>A fixed value with the same .Value syntax as a config entry.</summary>
    internal class Const<T>
    {
        public readonly T Value;
        public Const(T value) { Value = value; }
    }
}
