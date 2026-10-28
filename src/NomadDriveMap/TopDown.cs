using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>
    /// Top-down pictures of the ground. Characters and vehicles are hidden, and the buildings of places the
    /// player has not seen yet, so a picture does not spoil them. Everything is restored right after the render.
    /// </summary>
    internal static class TopDown
    {
        // One camera and one render texture, created once and reused: a new camera makes HDRP build (and later free)
        // a full set of per-camera buffers, which is expensive.
        private static bool _loggedHeight;
        private static GameObject _camGo;
        private static Camera _camCache;
        private static RenderTexture _rtCache;

        private static Camera GetCamera(int fullRes)
        {
            if (_camGo == null)
            {
                _camGo = new GameObject("NomadMapCaptureCamera");
                UnityEngine.Object.DontDestroyOnLoad(_camGo);
                _camCache = _camGo.AddComponent<Camera>();
                _camCache.enabled = false;
                _camCache.orthographic = true;
                _camCache.nearClipPlane = 0.1f;
                _camCache.farClipPlane = 900f;
                _camCache.allowHDR = false;
                _camCache.allowDynamicResolution = false;
                _camCache.clearFlags = CameraClearFlags.SolidColor;
                _camCache.backgroundColor = new Color(0.2f, 0.21f, 0.27f, 1f);
                _camGo.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // image up = +Z, right = +X
                ConfigureHdrp(_camGo);
            }
            if (_rtCache == null || _rtCache.width != fullRes)
            {
                if (_rtCache != null) { _rtCache.Release(); UnityEngine.Object.Destroy(_rtCache); }
                _rtCache = new RenderTexture(fullRes, fullRes, 24, RenderTextureFormat.ARGB32);
                _rtCache.Create();
            }
            _camCache.targetTexture = _rtCache;
            return _camCache;
        }

        /// <summary>One top-down render of a square area. Returns a readable RGB24 texture (caller owns it) or null.</summary>
        internal static Texture2D Render(Plugin p, Vector3 renderCenter, float halfSize, int res, bool hideUnseen, out int hiddenCount, float overscan = 1f, float baseY = float.NaN, float topY = float.NaN)
        {
            int fullRes = Mathf.RoundToInt(res * overscan);
            if (((fullRes - res) & 1) != 0) fullRes++;
            float fullHalf = halfSize * fullRes / (float)res;
            hiddenCount = 0;
            var hidden = new List<Renderer>();
            var forced = new List<LODGroup>();
            Action<UnityEngine.Rendering.ScriptableRenderContext, Camera> hook = null;
            long t0 = Prof.Now();

            // Terrain: use full-quality ground textures and meshes for this one render (restored below).
            var terrains = Terrain.activeTerrains;
            var tBase = new float[terrains.Length];
            var tErr = new float[terrains.Length];
            var tLod = new int[terrains.Length];
            float lodBias = QualitySettings.lodBias;
            try
            {
                for (int i = 0; i < terrains.Length; i++)
                {
                    tBase[i] = terrains[i].basemapDistance; tErr[i] = terrains[i].heightmapPixelError; tLod[i] = terrains[i].heightmapMaximumLOD;
                    terrains[i].basemapDistance = 20000f; terrains[i].heightmapPixelError = 1f; terrains[i].heightmapMaximumLOD = 0;
                }
                QualitySettings.lodBias = Mathf.Max(lodBias, 3f);
                t0 = Prof.Phase("terrain-quality", t0);

                var cam = GetCamera(fullRes);
                cam.orthographicSize = fullHalf;
                float camY = (float.IsNaN(baseY) ? p.Local.position.y : baseY) + Plugin.CaptureHeight.Value;
                if (!float.IsNaN(topY)) camY = Mathf.Max(camY, topY + 15f);
                cam.transform.position = new Vector3(renderCenter.x, camY, renderCenter.z);
                if (!_loggedHeight) { _loggedHeight = true; Plugin.Log.LogInfo("Capture camera: " + Plugin.CaptureHeight.Value + " m above the ground at the tile centre (first tile: camera y " + camY.ToString("F0") + ", ground y " + (float.IsNaN(baseY) ? p.Local.position.y : baseY).ToString("F0") + ")."); }
                t0 = Prof.Phase("camera", t0);

                HideAll(p.Local, hidden);
                foreach (var o in p.Others) HideAll(o, hidden);
                foreach (var v in p.Vehicles) HideAll(v, hidden);
                t0 = Prof.Phase("hide-players-vehicles", t0);

                if (hideUnseen && p.Mem != null)
                {
                    foreach (var poi in p.Pois)
                    {
                        if (poi.T == null || p.Mem.Pois.ContainsKey(Plugin.PoiKey(poi))) continue;
                        var root = poi.T;
                        if (root.GetComponentsInChildren<Renderer>(true).Length < 3 && root.parent != null) root = root.parent;
                        HideAll(root, hidden);
                    }
                }
                t0 = Prof.Phase("hide-unseen-places", t0);

                // Force the best level of detail on everything in view: LOD cross-fades draw as dotted edges from above.
                foreach (var lg in LodGroups())
                {
                    if (lg == null || !lg.enabled) continue;
                    var c = lg.transform.TransformPoint(lg.localReferencePoint);
                    var ls = lg.transform.lossyScale;
                    float rad = lg.size * Mathf.Max(Mathf.Abs(ls.x), Mathf.Max(Mathf.Abs(ls.y), Mathf.Abs(ls.z))) * 0.5f;
                    if (Mathf.Abs(c.x - renderCenter.x) > fullHalf + rad || Mathf.Abs(c.z - renderCenter.z) > fullHalf + rad) continue;
                    lg.ForceLOD(0);
                    forced.Add(lg);
                }
                t0 = Prof.Phase("force-lod(" + forced.Count + ")", t0);

                Look.Begin(p, Plugin.NormalizeSun.Value);
                t0 = Prof.Phase("look-begin", t0);
                var renderCam = cam;
                hook = (ctx, c) => { if (c == renderCam) Look.ApplySunInRender(); };
                UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += hook;
                cam.Render();
                Look.End();
                t0 = Prof.Phase("camera-render", t0);

                var prev = RenderTexture.active;
                RenderTexture.active = _rtCache;
                // Keep only the middle (the edges of an orthographic render have lighting artefacts); reading just
                // that part avoids a big temporary copy.
                int off = (fullRes - res) / 2;
                var tex = new Texture2D(res, res, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(off, off, res, res), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                hiddenCount = hidden.Count;
                t0 = Prof.Phase("read-back", t0);
                return tex;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Top-down render failed: " + e);
                return null;
            }
            finally
            {
                long t1 = Prof.Now();
                if (hook != null) UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= hook;
                Look.End();
                foreach (var lg in forced) if (lg != null) lg.ForceLOD(-1);
                foreach (var r in hidden) if (r != null) r.enabled = true;
                for (int i = 0; i < terrains.Length; i++)
                    if (terrains[i] != null) { terrains[i].basemapDistance = tBase[i]; terrains[i].heightmapPixelError = tErr[i]; terrains[i].heightmapMaximumLOD = tLod[i]; }
                QualitySettings.lodBias = lodBias;
                Prof.Phase("restore", t1);
            }
        }

        // Scanning every LODGroup in the scene for every tile is expensive: refresh the list every 10 s.
        private static LODGroup[] _lods = new LODGroup[0];
        private static float _lodScan = -100f;
        private static LODGroup[] LodGroups()
        {
            if (Time.unscaledTime - _lodScan > 10f)
            {
                _lodScan = Time.unscaledTime;
                _lods = UnityEngine.Object.FindObjectsOfType<LODGroup>();
            }
            return _lods;
        }

        private static void HideAll(Transform t, List<Renderer> hidden)
        {
            if (t == null) return;
            foreach (var r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                hidden.Add(r);
            }
        }

        // ---- Sun (day/night) -----------------------------------------------------------------

        private static float _nextLightScan = -100f;

        /// <summary>
        /// Finds the sun: the strongest enabled directional light (this game uses one "Sun and Moon" light for both). Strength is read from
        /// the HDRP light data (Light.intensity can be stale). Returns a description of all directional lights.
        /// </summary>
        internal static void UpdateSun(Plugin p, bool log)
        {
            if (Time.unscaledTime >= _nextLightScan || p.SunLight == null || !p.SunLight.gameObject.activeInHierarchy)
            {
                _nextLightScan = Time.unscaledTime + 10f;
                Light best = null;
                float bestI = -1f;
                var sb = new System.Text.StringBuilder();
                foreach (var l in UnityEngine.Object.FindObjectsOfType<Light>())
                {
                    if (l == null || l.type != LightType.Directional || !l.enabled || !l.gameObject.activeInHierarchy) continue;
                    float inten = Look.Intensity(l);
                    sb.Append(l.name).Append(" (strength ").Append(inten.ToString("F1")).Append(", ").Append(Elevation(l).ToString("F0")).Append(" deg) ");
                    if (inten > bestI) { best = l; bestI = inten; }
                }
                p.SunLight = best;
                p.LightsText = sb.ToString();
                if (log || Plugin.VerboseLog.Value) Plugin.Log.LogInfo("Directional lights: " + p.LightsText);
            }
            if (p.SunLight != null)
            {
                p.Sun = Elevation(p.SunLight);
                p.SunStrength = Look.Intensity(p.SunLight);
                if (p.Sun >= 25f && p.SunStrength > p.SunDayMax)
                {
                    p.SunDayMax = p.SunStrength;
                    p.SunDayColor = p.SunLight.color; p.SunDayTemp = p.SunLight.colorTemperature; p.SunDayUseTemp = p.SunLight.useColorTemperature;
                    p.SunDayKnown = true;
                    p.SaveSun();
                }
            }
            else { p.Sun = -90f; p.SunStrength = 0f; }
        }

        private static float Elevation(Light l)
        {
            return (float)(Math.Asin(Mathf.Clamp(-l.transform.forward.y, -1f, 1f)) * 180.0 / Math.PI);
        }

        // ---- HDRP camera setup -------------------------------------------------------------

        private static void SetMember(Type t, object target, string name, object value)
        {
            var f = t.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var p = t.GetProperty(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            var type = f != null ? f.FieldType : p != null ? p.PropertyType : null;
            if (type == null) return;
            if (value == null && type.IsEnum) value = Enum.Parse(type, "Color");
            else if (type.IsEnum && value is string) value = Enum.Parse(type, (string)value);
            if (f != null) f.SetValue(target, value); else p.SetValue(target, value, null);
        }

        internal static void ConfigureHdrp(GameObject camGo)
        {
            try
            {
                var t = Util.Find("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                if (t == null) return;
                var data = camGo.GetComponent(t) ?? camGo.AddComponent(t);
                SetMember(t, data, "clearColorMode", null);
                SetMember(t, data, "backgroundColorHDR", new Color(0.2f, 0.21f, 0.27f, 1f));
                SetMember(t, data, "antialiasing", "SubpixelMorphologicalAntiAliasing"); // thin lines (cables) need it
            }
            catch (Exception e) { Plugin.Log.LogInfo("Capture: could not set the HDRP background (" + e.Message + ")."); }
        }

        internal static void SetBackground(GameObject camGo, Color c)
        {
            try
            {
                var t = Util.Find("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData");
                var data = t != null ? camGo.GetComponent(t) : null;
                if (data != null) SetMember(t, data, "backgroundColorHDR", c);
            }
            catch { }
        }
    }
}
