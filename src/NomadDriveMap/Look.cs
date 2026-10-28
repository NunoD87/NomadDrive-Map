using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>
    /// A controlled look for the map's own renders (ground tiles, vehicle icons): a global HDRP volume with a
    /// very high priority that switches off the post-processing effects the game plays with (damage vignette,
    /// colour grading, bloom, fog, auto exposure...), optional fixed sun angle/strength, and the game's custom
    /// passes off. It is switched on only around one synchronous Camera.Render() and restored right after,
    /// so the player's own camera never sees it.
    /// </summary>
    internal static class Look
    {
        private const string Hd = "UnityEngine.Rendering.HighDefinition.";
        private static readonly GameObject[] Gos = new GameObject[2];
        private static readonly bool[] Tried = new bool[2];
        private static GameObject _go;
        private static bool _logged;
        private static Type _tCustomPass, _tHdLight;
        private static readonly List<Behaviour> DisabledPasses = new List<Behaviour>();

        // sun override state
        private static Light _sun;
        private static Quaternion _sunRot;
        private static float _sunIntensity;
        private static bool _sunWasEnabled, _sunOverridden, _sunWasTemp;
        private static Texture _sunCookie;
        private static Plugin _ovP;
        private static float _dimmerOrig = 1f;
        private static int _renderLogs;
        private static Color _sunColor;
        private static float _sunTemp;

        /// <summary>Builds both effect volumes ahead of time (it takes up to ~2 s, so it must not happen on the first tile).</summary>
        internal static void Prewarm()
        {
            Build(false);
            Build(true);
        }

        private static bool Build(bool linear)
        {
            int idx = linear ? 1 : 0;
            if (Tried[idx]) { _go = Gos[idx]; return _go != null; }
            Tried[idx] = true;
            _go = null;
            try
            {
                long b0 = Prof.Now();
                var tVolume = Util.Find("UnityEngine.Rendering.Volume");
                var tProfile = Util.Find("UnityEngine.Rendering.VolumeProfile");
                _tCustomPass = Util.Find(Hd + "CustomPassVolume");
                _tHdLight = Util.Find(Hd + "HDAdditionalLightData");
                if (tVolume == null || tProfile == null) { Plugin.Log.LogWarning("Look: Volume types not found, renders keep the game's post-processing."); return false; }

                _go = new GameObject(linear ? "NomadMapLookLinear" : "NomadMapLook");
                UnityEngine.Object.DontDestroyOnLoad(_go);
                _go.SetActive(false);
                var vol = _go.AddComponent(tVolume);
                Set(vol, "isGlobal", true);
                Set(vol, "priority", 1000000f);
                Set(vol, "weight", 1f);
                var profile = ScriptableObject.CreateInstance(tProfile);
                var add = tProfile.GetMethod("Add", new[] { typeof(Type), typeof(bool) });
                var report = new System.Text.StringBuilder("Look: volume overrides:");

                Func<string, Func<object, bool>, bool> comp = (name, setup) =>
                {
                    long c0 = Prof.Now();
                    var t = Util.Find(Hd + name);
                    if (t == null) { report.Append(' ').Append(name).Append("=missing"); return false; }
                    var c = add.Invoke(profile, new object[] { t, true });
                    bool ok = setup(c);
                    report.Append(' ').Append(name).Append(ok ? "=ok" : "=partial").Append('(').Append(Prof.Ms(c0).ToString("F0")).Append("ms)");
                    return ok;
                };

                comp("Exposure", c => { ExpComp[idx] = c; return P(c, "mode", "Fixed") & P(c, "fixedExposure", 13f); });
                comp("Vignette", c => P(c, "intensity", 0f));
                comp("ChromaticAberration", c => P(c, "intensity", 0f));
                comp("FilmGrain", c => P(c, "intensity", 0f));
                comp("MotionBlur", c => P(c, "intensity", 0f));
                comp("Bloom", c => P(c, "intensity", 0f));
                comp("DepthOfField", c => P(c, "focusMode", "Off"));
                comp("ColorAdjustments", c => P(c, "postExposure", 0f) & P(c, "contrast", 0f) & P(c, "saturation", 0f) & P(c, "hueShift", 0f) & P(c, "colorFilter", Color.white));
                comp("WhiteBalance", c => P(c, "temperature", 0f) & P(c, "tint", 0f));
                comp("Fog", c => P(c, "enabled", false));
                comp("Tonemapping", c => P(c, "mode", linear ? "None" : "ACES")); // same curve every time (the game's own may vary)

                // Effects centred on the camera that would give every tile its own pattern: cloud shadows, ambient occlusion,
                // contact shadows, screen-space reflections and global illumination.
                comp("VolumetricClouds", c => P(c, "enable", false));
                comp("CloudLayer", c => P(c, "opacity", 0f));
                comp("ScreenSpaceAmbientOcclusion", c => P(c, "intensity", 0f));
                comp("ContactShadows", c => P(c, "enable", false));
                comp("ScreenSpaceReflection", c => P(c, "enabled", false));
                comp("GlobalIllumination", c => P(c, "enable", false));

                // Ambient light: the same fixed gradient sky every time (day or night), at a fixed strength next to the sun.
                int gradId = GradientSkyId();
                comp("VisualEnvironment", c => P(c, "skyType", gradId) & P(c, "skyAmbientMode", "Dynamic"));
                comp("GradientSky", c =>
                {
                    SkyComp[idx] = c;
                    return P(c, "top", new Color(0.42f, 0.58f, 0.92f, 1f)) & P(c, "middle", new Color(0.72f, 0.78f, 0.85f, 1f)) &
                           P(c, "bottom", new Color(0.50f, 0.48f, 0.42f, 1f)) & P(c, "gradientDiffusion", 1f) &
                           P(c, "skyIntensityMode", "Lux") & P(c, "desiredLuxValue", 80f);
                });

                var setProfile = tVolume.GetProperty("sharedProfile");
                if (setProfile != null) setProfile.SetValue(vol, profile, null);
                else Set(vol, "profile", profile);
                Plugin.Log.LogInfo(report.ToString() + " | total " + Prof.Ms(b0).ToString("F0") + " ms");
                Gos[idx] = _go;
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Look: setup failed, renders keep the game's post-processing: " + e.Message);
                if (_go != null) UnityEngine.Object.Destroy(_go);
                _go = null; Gos[idx] = null;
                return false;
            }
        }

        private static int GradientSkyId()
        {
            try
            {
                var tSky = Util.Find(Hd + "SkySettings");
                var tGrad = Util.Find(Hd + "GradientSky");
                if (tSky == null || tGrad == null) return 0;
                foreach (var m in tSky.GetMethods(BindingFlags.Static | BindingFlags.Public))
                    if (m.Name == "GetUniqueID" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0)
                        return (int)m.MakeGenericMethod(tGrad).Invoke(null, null);
            }
            catch (Exception e) { Plugin.Log.LogInfo("Look: gradient sky id not found: " + e.Message); }
            return 0;
        }

        private static void Set(object target, string name, object value)
        {
            var t = target.GetType();
            const BindingFlags bf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var p = t.GetProperty(name, bf);
            if (p != null && p.CanWrite) { p.SetValue(target, value, null); return; }
            var f = t.GetField(name, bf);
            if (f != null) f.SetValue(target, value);
        }

        // Sets a VolumeParameter<T> field: value.Override(x) switches the override on and sets x.
        private static bool P(object comp, string name, object value)
        {
            try
            {
                var f = AccessTools.Field(comp.GetType(), name);
                object param = f != null ? f.GetValue(comp) : AccessTools.Property(comp.GetType(), name)?.GetValue(comp, null);
                if (param == null) return false;
                var m = param.GetType().GetMethod("Override", GetOverrideArg(param));
                if (m == null) return false;
                var pt = m.GetParameters()[0].ParameterType;
                object arg = value;
                if (pt.IsEnum && value is string) arg = Enum.Parse(pt, (string)value);
                else if (pt != value.GetType() && !(value is Color)) arg = Convert.ChangeType(value, pt);
                m.Invoke(param, new[] { arg });
                return true;
            }
            catch { return false; }
        }

        private static Type[] GetOverrideArg(object param)
        {
            // VolumeParameter<T>.Override(T): find T from the generic base type
            for (var t = param.GetType(); t != null; t = t.BaseType)
                if (t.IsGenericType && t.Name.StartsWith("VolumeParameter")) return new[] { t.GetGenericArguments()[0] };
            return Type.EmptyTypes;
        }

        /// <param name="normalizeSun">Fix the sun angle and strength so every picture is lit the same.</param>
        internal static int Bucket;
        private static readonly float[] LearnedBy = new float[2];
        internal static float LearnedLinear;
        // Puts the light into the fixed daytime state. Called before Render() and again from inside the render
        // (beginCameraRendering), in case something in the game changes the light between those two points.
        private static void ApplySun(bool log)
        {
            if (!_sunOverridden || _sun == null || _ovP == null) return;
            var p = _ovP;
            if (log) Plugin.Log.LogInfo("Look: sun as the renderer sees it BEFORE our override: " + SunState());
            // neutral white sun, the same every time (the game's own sun colour changes through the day)
            _sun.useColorTemperature = true;
            _sun.colorTemperature = Plugin.SunTemperature.Value;
            _sun.color = Color.white;
            _sun.transform.rotation = Quaternion.Euler(55f, _sunRot.eulerAngles.y, 0f);
            SetIntensity(_sun, p.SunDayMax);
            SetDimmer(_sun, 1f);
            _sun.cookie = null; // cloud shadows are often a cookie on the sun
            _sun.enabled = true;
            _sun.gameObject.SetActive(true);
            if (log) Plugin.Log.LogInfo("Look: sun AFTER our override: " + SunState() + ", cookie before: " + (_sunCookie != null ? _sunCookie.name : "none"));
        }

        /// <summary>Called from beginCameraRendering for our camera.</summary>
        internal static void ApplySunInRender()
        {
            ApplySun(_renderLogs < 3);
            _renderLogs++;
        }

        private static string SunState()
        {
            try
            {
                float elev = (float)(Math.Asin(Mathf.Clamp(-_sun.transform.forward.y, -1f, 1f)) * 180.0 / Math.PI);
                return "enabled " + _sun.enabled + ", elevation " + elev.ToString("F0") + ", strength " + Intensity(_sun).ToString("F1") + ", Light.intensity " + _sun.intensity.ToString("F1") +
                       ", dimmer " + Dimmer(_sun).ToString("F2") + ", colour " + _sun.color + ", temperature " + (_sun.useColorTemperature ? _sun.colorTemperature.ToString("F0") + " K" : "off") + ", shadows " + _sun.shadows;
            }
            catch (Exception e) { return "unreadable (" + e.Message + ")"; }
        }

        private static float Dimmer(Light l)
        {
            try
            {
                var hd = _tHdLight != null ? l.GetComponent(_tHdLight) : null;
                var pr = hd != null ? AccessTools.Property(_tHdLight, "lightDimmer") : null;
                if (pr != null) return Convert.ToSingle(pr.GetValue(hd, null));
            }
            catch { }
            return 1f;
        }

        private static void SetDimmer(Light l, float v)
        {
            try
            {
                var hd = _tHdLight != null ? l.GetComponent(_tHdLight) : null;
                var pr = hd != null ? AccessTools.Property(_tHdLight, "lightDimmer") : null;
                if (pr != null) pr.SetValue(hd, v, null);
            }
            catch { }
        }

        // ---- Clean weather: rain, snow and wet ground are switched off for the render only ----------------------

        private static readonly string[] FloatGlobals = { "_Global_SnowLevel", "_Global_PuddleParams", "_Global_RainIntensity", "_Global_StreamMax" };
        private static readonly string[] VectorGlobals = { "_Global_WetnessParams" };
        private static readonly float[] SavedFloats = new float[4];
        private static Vector4 _savedWet;
        private static bool _weatherOn;
        private static readonly List<Renderer> WeatherFx = new List<Renderer>();
        private static ParticleSystemRenderer[] _psr = new ParticleSystemRenderer[0];
        private static float _psrScan = -100f;
        private static bool _loggedWeatherFx;
        private static float _nextWeatherLog;

        // The Visual Engine (vegetation / ground tone): wetness, raindrops and snow overlay come from here.
        private static Type _tTve;
        private static object _tveData;
        private static object _tveMgr;
        private static MethodInfo _tvePush;
        private static readonly List<KeyValuePair<MemberInfo, object>> TveSaved = new List<KeyValuePair<MemberInfo, object>>();
        private static bool _tveTried, _tveChanged;
        private static readonly System.Text.RegularExpressions.Regex TveClear =
            new System.Text.RegularExpressions.Regex("^(wetness|raindrops|puddles|snow|overlay|rain)[A-Za-z]*(Intensity|Amount|Value)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static Type _tRoads;
        private static Component _roads;
        private static FieldInfo _roadWetField;
        private static MethodInfo _roadWetSet;
        private static float _savedRoadWet;
        private static bool _roadWetChanged;
        private static readonly System.Text.RegularExpressions.Regex WeatherName =
            new System.Text.RegularExpressions.Regex("rain|snow|blizzard|sandstorm|hail|drizzle|splash|mist|fog|storm|lightning|windstreak|raindrop", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private static void WeatherClean()
        {
            if (_weatherOn) return;
            _weatherOn = true;
            // terrain wetness / puddles / snow come from MicroSplat shader globals that the game updates every frame
            for (int i = 0; i < FloatGlobals.Length; i++) { SavedFloats[i] = Shader.GetGlobalFloat(FloatGlobals[i]); Shader.SetGlobalFloat(FloatGlobals[i], 0f); }
            _savedWet = Shader.GetGlobalVector(VectorGlobals[0]);
            Shader.SetGlobalVector(VectorGlobals[0], Vector4.zero);

            // road surfaces have their own wetness, pushed by the weather system
            _roadWetChanged = false;
            try
            {
                if (_tRoads == null)
                {
                    _tRoads = Util.Find("NomadDrive.Features.EvilRoads.EvilRoadsManager");
                    _roadWetField = _tRoads != null ? AccessTools.Field(_tRoads, "_roadWetness") : null;
                    _roadWetSet = _tRoads != null ? AccessTools.Method(_tRoads, "SetRoadWetness") : null;
                }
                if (_tRoads != null && _roadWetSet != null && _roadWetField != null)
                {
                    if (_roads == null) _roads = UnityEngine.Object.FindObjectOfType(_tRoads) as Component;
                    if (_roads != null)
                    {
                        _savedRoadWet = Convert.ToSingle(_roadWetField.GetValue(_roads));
                        if (_savedRoadWet > 0.001f) { _roadWetSet.Invoke(_roads, new object[] { 0f }); _roadWetChanged = true; }
                    }
                }
            }
            catch { _roads = null; }

            TveClean();

            // weather particles (rain, snow, splashes, mist) off
            WeatherFx.Clear();
            if (Time.unscaledTime - _psrScan > 5f)
            {
                _psrScan = Time.unscaledTime;
                _psr = UnityEngine.Object.FindObjectsOfType<ParticleSystemRenderer>();
            }
            var names = new System.Text.StringBuilder();
            foreach (var r in _psr)
            {
                if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                bool match = false;
                var tr = r.transform;
                for (int d = 0; d < 4 && tr != null && !match; d++, tr = tr.parent) match = WeatherName.IsMatch(tr.name);
                if (!match) continue;
                r.enabled = false;
                WeatherFx.Add(r);
                if (names.Length < 300) names.Append(r.name).Append(", ");
            }
            bool tveWet = false; foreach (var kv in TveSaved) { if (kv.Value is float && (float)kv.Value > 0.01f) tveWet = true; }
            bool wet = tveWet || SavedFloats[0] > 0.01f || SavedFloats[1] > 0.01f || SavedFloats[2] > 0.01f || _savedWet.sqrMagnitude > 0.0001f || _savedRoadWet > 0.01f;
            if (wet && Time.unscaledTime >= _nextWeatherLog)
            {
                _nextWeatherLog = Time.unscaledTime + 30f;
                Plugin.Log.LogInfo("Look: weather active at capture and switched off: snow " + SavedFloats[0].ToString("F2") + ", puddles " + SavedFloats[1].ToString("F2") + ", rain " + SavedFloats[2].ToString("F2") +
                                   ", wetness " + _savedWet + ", road wetness " + _savedRoadWet.ToString("F2") + ", particle systems off " + WeatherFx.Count + ", vegetation values zeroed " + TveSaved.Count);
            }
            if (!_loggedWeatherFx)
            {
                _loggedWeatherFx = true;
                Plugin.Log.LogInfo("Look: clean weather. Ground globals before override: snow " + SavedFloats[0].ToString("F2") + ", puddles " + SavedFloats[1].ToString("F2") +
                                   ", rain " + SavedFloats[2].ToString("F2") + ", wetness params " + _savedWet + "; weather particle systems switched off: " + WeatherFx.Count + " (" + names + ")");
            }
        }

        private static void TveClean()
        {
            _tveChanged = false;
            try
            {
                if (!_tveTried)
                {
                    _tveTried = true;
                    _tTve = Util.Find("TheVisualEngine.TVEManager");
                    if (_tTve == null) { Plugin.Log.LogInfo("Look: TVEManager type not found (vegetation tone not controlled)."); return; }
                }
                if (_tTve == null) return;
                if (_tveMgr == null || (_tveMgr as UnityEngine.Object) == null)
                {
                    var inst = AccessTools.Property(_tTve, "Instance")?.GetValue(null, null) ?? AccessTools.Field(_tTve, "Instance")?.GetValue(null);
                    if (inst == null) inst = UnityEngine.Object.FindObjectOfType(_tTve);
                    _tveMgr = inst;
                    _tveData = null;
                    if (inst != null)
                    {
                        _tveData = AccessTools.Field(_tTve, "globalAtmoData")?.GetValue(inst) ?? AccessTools.Property(_tTve, "globalAtmoData")?.GetValue(inst, null);
                        // find the method that pushes the values to the shaders
                        _tvePush = null;
                        foreach (var m in _tTve.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (m.GetParameters().Length != 0) continue;
                            if (System.Text.RegularExpressions.Regex.IsMatch(m.Name, "Global.*(Shader|Value|Propert)|Update.*Global|Set.*Global|Push|Apply", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) { _tvePush = m; break; }
                        }
                        var mn = new System.Text.StringBuilder();
                        foreach (var m in _tTve.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)) mn.Append(m.Name).Append(m.GetParameters().Length == 0 ? "() " : "(..) ");
                        var fn = new System.Text.StringBuilder();
                        if (_tveData != null)
                            foreach (var f in _tveData.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                                if (f.FieldType == typeof(float) || f.FieldType == typeof(int) || f.FieldType == typeof(bool)) fn.Append(f.Name).Append('=').Append(f.GetValue(_tveData)).Append(' ');
                        Plugin.Log.LogInfo("Look: TVE found. Push method: " + (_tvePush != null ? _tvePush.Name : "none") + ". Atmosphere values: " + fn + ". Manager methods: " + mn);
                    }
                }
                if (_tveData == null) return;
                TveSaved.Clear();
                foreach (var f in _tveData.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(float) || !TveClear.IsMatch(f.Name)) continue;
                    TveSaved.Add(new KeyValuePair<MemberInfo, object>(f, f.GetValue(_tveData)));
                    f.SetValue(_tveData, 0f);
                    _tveChanged = true;
                }
                if (_tveChanged && _tvePush != null) _tvePush.Invoke(_tveMgr, null);
            }
            catch (Exception e) { _tveMgr = null; if (!_loggedWeatherFx) Plugin.Log.LogInfo("Look: TVE override failed: " + e.Message); }
        }

        private static void TveRestore()
        {
            if (!_tveChanged || _tveData == null) return;
            try
            {
                foreach (var kv in TveSaved) ((FieldInfo)kv.Key).SetValue(_tveData, kv.Value);
                if (_tvePush != null) _tvePush.Invoke(_tveMgr, null);
            }
            catch { }
            _tveChanged = false;
        }

        private static void WeatherRestore()
        {
            if (!_weatherOn) return;
            _weatherOn = false;
            TveRestore();
            for (int i = 0; i < FloatGlobals.Length; i++) Shader.SetGlobalFloat(FloatGlobals[i], SavedFloats[i]);
            Shader.SetGlobalVector(VectorGlobals[0], _savedWet);
            foreach (var r in WeatherFx) if (r != null) r.enabled = true;
            WeatherFx.Clear();
            if (_roadWetChanged && _roads != null) { try { _roadWetSet.Invoke(_roads, new object[] { _savedRoadWet }); } catch { } }
            _roadWetChanged = false;
        }

        private static UnityEngine.Object[] _passes = new UnityEngine.Object[0];
        private static float _passScan = -100f;
        private static GameObject _active;
        private static readonly object[] ExpComp = new object[2];
        private static readonly object[] SkyComp = new object[2];
        private static int _activeIdx;
        private static bool _loggedEv, _loggedGame;

        internal static void Begin(Plugin p, bool normalizeSun, bool linear = false)
        {
            _active = null;
            if (!Build(linear)) return;
            _active = _go;
            _activeIdx = linear ? 1 : 0;
            _active.SetActive(true);
            WeatherClean();

            // Exposure: matched to the sun's strength (EV100 = log2(0.458 * lux) for a mid-grey surface lit by it),
            // plus a correction learned from earlier renders (see Adjust), plus the user's offset.
            float lux = p.SunDayMax > 0f ? p.SunDayMax : p.SunStrength;
            float ev = (lux > 0.01f ? Mathf.Log(0.458f * lux, 2f) : 10f) + Plugin.ExposureOffset.Value + (linear ? LearnedLinear : LearnedBy[Bucket]);
            if (!_loggedGame)
            {
                _loggedGame = true;
                float ge; string gi;
                ReadGameExposure(out ge, out gi);
                Plugin.Log.LogInfo("Look: volume stack default exposure (not used): " + gi);
            }
            if (ExpComp[_activeIdx] != null) P(ExpComp[_activeIdx], "fixedExposure", ev);
            if (SkyComp[_activeIdx] != null && lux > 0.01f) P(SkyComp[_activeIdx], "desiredLuxValue", lux * Plugin.AmbientFraction.Value);
            if (!_loggedEv) { _loggedEv = true; Plugin.Log.LogInfo("Look: fixed exposure EV " + ev.ToString("F1") + " (sun strength " + lux.ToString("F1") + ")."); }

            // game custom passes (screen effects) off
            DisabledPasses.Clear();
            if (_tCustomPass != null)
            {
                if (Time.unscaledTime - _passScan > 10f) { _passScan = Time.unscaledTime; _passes = UnityEngine.Object.FindObjectsOfType(_tCustomPass); }
                foreach (var o in _passes)
                {
                    var b = o as Behaviour;
                    if (b != null && b.enabled) { b.enabled = false; DisabledPasses.Add(b); }
                }
            }

            _sunOverridden = false;
            if (normalizeSun && p.SunLight != null && p.SunDayMax > 0f)
            {
                _sun = p.SunLight;
                _sunRot = _sun.transform.rotation;
                _sunIntensity = Intensity(_sun);
                _sunWasEnabled = _sun.enabled;
                _sunColor = _sun.color; _sunTemp = _sun.colorTemperature; _sunWasTemp = _sun.useColorTemperature;
                _dimmerOrig = Dimmer(_sun);
                _sunCookie = _sun.cookie;
                _ovP = p;
                _sunOverridden = true;
                ApplySun(false);
            }
            if (!_logged) { _logged = true; Plugin.Log.LogInfo("Look: active for renders (" + DisabledPasses.Count + " custom pass volumes off, sun override " + _sunOverridden + ")."); }
        }

        // Reads the exposure the game currently uses from HDRP's volume stack; true if it is a fixed one.
        private static bool ReadGameExposure(out float ev, out string info)
        {
            ev = 0f; info = "";
            try
            {
                var tvm = Util.Find("UnityEngine.Rendering.VolumeManager");
                var inst = tvm != null ? AccessTools.Property(tvm, "instance")?.GetValue(null, null) : null;
                var stack = inst != null ? AccessTools.Property(tvm, "stack")?.GetValue(inst, null) : null;
                var tExp = Util.Find(Hd + "Exposure");
                var get = stack != null ? stack.GetType().GetMethod("GetComponent", new[] { typeof(Type) }) : null;
                var exp = get != null && tExp != null ? get.Invoke(stack, new object[] { tExp }) : null;
                if (exp == null) { info = "no exposure component in the volume stack"; return false; }
                string mode = Val(exp, "mode");
                string fx = Val(exp, "fixedExposure");
                string comp = Val(exp, "compensation");
                info = "mode " + mode + ", fixedExposure " + fx + ", compensation " + comp;
                float f, c = 0f;
                if (mode == "Fixed" && float.TryParse(fx, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f))
                {
                    float.TryParse(comp, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out c);
                    ev = f - c; // compensation is subtracted from the metered EV
                    return true;
                }
            }
            catch (Exception e) { info = "error " + e.Message; }
            return false;
        }

        private static string Val(object comp, string field)
        {
            var f = AccessTools.Field(comp.GetType(), field);
            var param = f != null ? f.GetValue(comp) : null;
            if (param == null) return "?";
            var v = AccessTools.Property(param.GetType(), "value")?.GetValue(param, null);
            return v == null ? "?" : Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>A render came out too bright/dark: shift the exposure for the next one (kept for the session).</summary>
        internal static void Adjust(bool linear, float luma, float target)
        {
            float d = Mathf.Clamp((luma - target) * 8f, -4f, 4f);
            if (linear) LearnedLinear += d; else LearnedBy[Bucket] += d;
            Plugin.Log.LogInfo("Look: brightness " + luma.ToString("F2") + " (target " + target.ToString("F2") + "), exposure correction now " + (linear ? LearnedLinear : LearnedBy[Bucket]).ToString("F1") + " EV" + (linear ? " (icons)" : Bucket == 0 ? " (tiles, day)" : " (tiles, night)"));
        }

        internal static void End()
        {
            if (_active != null) _active.SetActive(false);
            _active = null;
            WeatherRestore();
            foreach (var b in DisabledPasses) if (b != null) b.enabled = true;
            DisabledPasses.Clear();
            if (_sunOverridden && _sun != null)
            {
                _sun.transform.rotation = _sunRot;
                SetIntensity(_sun, _sunIntensity);
                _sun.enabled = _sunWasEnabled;
                _sun.color = _sunColor; _sun.colorTemperature = _sunTemp; _sun.useColorTemperature = _sunWasTemp;
                SetDimmer(_sun, _dimmerOrig);
                _sun.cookie = _sunCookie;
            }
            _sunOverridden = false;
        }

        // HDRP lights keep their real strength in HDAdditionalLightData; Light.intensity can be stale.
        internal static float Intensity(Light l)
        {
            try
            {
                if (_tHdLight == null) _tHdLight = Util.Find(Hd + "HDAdditionalLightData");
                var hd = _tHdLight != null ? l.GetComponent(_tHdLight) : null;
                if (hd != null)
                {
                    var p = AccessTools.Property(_tHdLight, "intensity");
                    if (p != null) return Convert.ToSingle(p.GetValue(hd, null));
                }
            }
            catch { }
            return l.intensity;
        }

        private static void SetIntensity(Light l, float v)
        {
            try
            {
                var hd = _tHdLight != null ? l.GetComponent(_tHdLight) : null;
                var p = hd != null ? AccessTools.Property(_tHdLight, "intensity") : null;
                if (p != null) { p.SetValue(hd, v, null); return; }
            }
            catch { }
            l.intensity = v;
        }
    }
}
