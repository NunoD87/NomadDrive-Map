using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace NomadDriveMap
{
    /// <summary>
    /// Small profiler for finding where the lag comes from. Per-tile phase times are logged with every tile,
    /// recurring work (scan, discovery, UI) is summed up in one line every 30 seconds, and the frame time right
    /// after each tile is logged so the felt hitch can be compared with the work done.
    /// </summary>
    internal static class Prof
    {
        private class Acc { public double Sum, Max; public int N; }

        private static readonly Dictionary<string, Acc> Recurring = new Dictionary<string, Acc>();
        private static readonly List<KeyValuePair<string, double>> Phases = new List<KeyValuePair<string, double>>();
        private static float _nextSummary = 30f;

        internal static long Now() { return Stopwatch.GetTimestamp(); }
        internal static double Ms(long since) { return (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency; }

        /// <summary>Time one recurring job (shows up in the 30 s summary).</summary>
        internal static void Add(string name, long since)
        {
            if (!Plugin.ProfileLog.Value) return;
            double ms = Ms(since);
            Acc a;
            if (!Recurring.TryGetValue(name, out a)) { a = new Acc(); Recurring[name] = a; }
            a.Sum += ms; a.N++; if (ms > a.Max) a.Max = ms;
        }

        /// <summary>Start timing the phases of one tile.</summary>
        internal static void BeginPhases() { Phases.Clear(); }

        internal static long Phase(string name, long since)
        {
            if (Plugin.ProfileLog.Value) Phases.Add(new KeyValuePair<string, double>(name, Ms(since)));
            return Now();
        }

        internal static string PhaseText()
        {
            var sb = new StringBuilder();
            double total = 0;
            foreach (var kv in Phases) { sb.Append(kv.Key).Append(' ').Append(kv.Value.ToString("F0")).Append(" | "); total += kv.Value; }
            sb.Append("sum ").Append(total.ToString("F0")).Append(" ms");
            return sb.ToString();
        }

        // ---- Frame time after a tile ------------------------------------------------------------

        private static int _watchFrames;
        private static float _watchWorst, _watchStartDelta;
        private static string _watchLabel;

        internal static void WatchHitch(string label)
        {
            if (!Plugin.ProfileLog.Value) return;
            _watchFrames = 6; _watchWorst = 0f; _watchLabel = label;
        }

        internal static void Frame(float unscaledDelta)
        {
            if (_watchFrames > 0)
            {
                _watchFrames--;
                if (unscaledDelta > _watchWorst) _watchWorst = unscaledDelta;
                if (_watchFrames == 0)
                    Plugin.Log.LogInfo("Hitch after " + _watchLabel + ": worst frame " + (_watchWorst * 1000f).ToString("F0") + " ms (a normal frame is about " + (_avg * 1000f).ToString("F0") + " ms).");
            }
            else _avg = _avg <= 0f ? unscaledDelta : _avg * 0.98f + unscaledDelta * 0.02f;

            if (Plugin.ProfileLog.Value && Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 30f;
                if (Recurring.Count == 0) return;
                var sb = new StringBuilder("Profile (last 30 s, avg/max ms per call, calls): ");
                foreach (var kv in Recurring)
                    sb.Append(kv.Key).Append(' ').Append((kv.Value.Sum / Math.Max(1, kv.Value.N)).ToString("F2")).Append('/').Append(kv.Value.Max.ToString("F1")).Append(" x").Append(kv.Value.N).Append(" | ");
                Plugin.Log.LogInfo(sb.ToString());
                Recurring.Clear();
            }
        }

        private static float _avg;
    }
}
