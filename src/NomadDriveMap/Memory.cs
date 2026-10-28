using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>A place the player has actually seen. Kept in true-world coordinates.</summary>
    internal class Known
    {
        public string Name;
        public Vector3 TruePos;
        public bool Major;
    }

    /// <summary>What the map knows about one world (keyed by seed): seen places, seen vehicles, trail.</summary>
    internal class WorldMemory
    {
        public readonly Dictionary<string, Known> Pois = new Dictionary<string, Known>();
        public readonly HashSet<int> VehiclesSeen = new HashSet<int>();
        public readonly List<Vector3> Trail = new List<Vector3>();
        public bool Dirty;
        public TileStore Tiles;
    }

    /// <summary>
    /// What is written to BepInEx/NomadDriveMap/world_&lt;seed&gt;.json. Plain lists only (JsonUtility drops lists of
    /// custom classes, which is how places were lost in testing). Place number i is placeNames[i], placeTypes[i],
    /// placePositions[i] and placeKeys[i].
    /// </summary>
    [Serializable] public class SaveData
    {
        public int version = 2;
        public string seed = "";
        public List<string> placeNames = new List<string>();
        public List<string> placeTypes = new List<string>();     // "Major" or "Minor"
        public List<Vector3> placePositions = new List<Vector3>(); // x, y, z in world coordinates
        public List<string> placeKeys = new List<string>();
        public List<Vector3> trail = new List<Vector3>();
    }

    /// <summary>Saves what the map knows per world seed to BepInEx/NomadDriveMap/world_&lt;seed&gt;.json.</summary>
    internal static class Persist
    {
        internal static string Dir { get { return Path.Combine(BepInEx.Paths.BepInExRootPath, "NomadDriveMap"); } }

        internal static bool ValidSeed(string seed) { return !string.IsNullOrEmpty(seed) && seed != "0" && seed != "unknown"; }

        private static string FileFor(string seed) { return Path.Combine(Dir, "world_" + seed + ".json"); }

        internal static void Load(string seed, WorldMemory m)
        {
            if (!ValidSeed(seed)) return;
            try
            {
                var f = FileFor(seed);
                if (!File.Exists(f)) return;
                var d = JsonUtility.FromJson<SaveData>(File.ReadAllText(f));
                if (d == null) return;
                int n = Math.Min(Math.Min(d.placeNames.Count, d.placePositions.Count), Math.Min(d.placeTypes.Count, d.placeKeys.Count));
                for (int i = 0; i < n; i++)
                    m.Pois[d.placeKeys[i]] = new Known { Name = d.placeNames[i], TruePos = d.placePositions[i], Major = d.placeTypes[i] == "Major" };
                m.Trail.AddRange(d.trail);
                Plugin.Log.LogInfo("Loaded saved map for seed " + seed + ": " + n + " places, " + d.trail.Count + " trail points.");
            }
            catch (Exception e) { Plugin.Log.LogError("Loading the saved map failed: " + e.Message); }
        }

        internal static void Save(string seed, WorldMemory m)
        {
            if (!ValidSeed(seed)) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var d = new SaveData { seed = seed };
                foreach (var kv in m.Pois)
                {
                    d.placeKeys.Add(kv.Key);
                    d.placeNames.Add(kv.Value.Name);
                    d.placeTypes.Add(kv.Value.Major ? "Major" : "Minor");
                    d.placePositions.Add(kv.Value.TruePos);
                }
                d.trail.AddRange(m.Trail);
                var f = FileFor(seed);
                var json = JsonUtility.ToJson(d, true);
                File.WriteAllText(f + ".tmp", json);
                if (File.Exists(f)) File.Delete(f);
                File.Move(f + ".tmp", f);
                m.Dirty = false;

                // Make sure the places really made it into the file (they did not in an early version).
                var check = JsonUtility.FromJson<SaveData>(json);
                if (check == null || check.placeNames.Count != m.Pois.Count)
                    Plugin.Log.LogError("Saved map check failed: " + m.Pois.Count + " places in memory, " + (check == null ? 0 : check.placeNames.Count) + " in the file.");
            }
            catch (Exception e) { Plugin.Log.LogError("Saving the map failed: " + e.Message); }
        }
    }

    /// <summary>
    /// Decides what the player has "seen", so the map does not spoil content: an object counts as seen
    /// only if it is within the reveal radius AND a line from the camera to it is not blocked by
    /// something else (walls, hills). A blocker that is right next to the object does not count as
    /// blocking, because then you are looking at the object's own building or hull.
    /// </summary>
    internal static class Sight
    {
        private static readonly RaycastHit[] Hits = new RaycastHit[48];
        private static readonly Comparer<RaycastHit> ByDistance = Comparer<RaycastHit>.Create((a, b) => a.distance.CompareTo(b.distance));
        private static readonly RaycastHit[] Sorted = new RaycastHit[48];

        internal static bool CanSee(Vector3 eye, Vector3 target, Transform targetRoot, Transform self, Transform ownVehicle, float tolerance)
        {
            var dir = target - eye;
            float dist = dir.magnitude;
            if (dist < 0.5f) return true;
            int n = Physics.RaycastNonAlloc(eye, dir / dist, Hits, dist, ~0, QueryTriggerInteraction.Ignore);
            if (n == 0) return true;

            Array.Copy(Hits, Sorted, n);
            Array.Sort(Sorted, 0, n, ByDistance);
            for (int i = 0; i < n; i++)
            {
                var col = Sorted[i].collider;
                if (col == null) continue;
                var tr = col.transform;
                if (self != null && tr.IsChildOf(self)) continue;              // our own body
                if (ownVehicle != null && tr.IsChildOf(ownVehicle)) continue;  // the vehicle we sit in
                if (targetRoot != null && tr.IsChildOf(targetRoot)) return true;
                return Sorted[i].distance >= dist - tolerance;                  // first real blocker
            }
            return true;
        }
    }
}
