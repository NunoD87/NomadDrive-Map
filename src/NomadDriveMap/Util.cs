using System;
using System.Collections.Generic;

namespace NomadDriveMap
{
    internal static class Util
    {
        private static readonly Dictionary<string, Type> Cache = new Dictionary<string, Type>();

        /// <summary>
        /// Finds a type by its full name. Harmony's AccessTools.TypeByName walks every type of every loaded assembly,
        /// and one of the game's assemblies (Unity.InputSystem.ForUI) throws while it is listed, so each call cost about
        /// 90 ms. Asking each assembly for the one name is instant, and the result is cached.
        /// </summary>
        internal static Type Find(string fullName)
        {
            Type t;
            if (Cache.TryGetValue(fullName, out t)) return t;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    t = a.GetType(fullName, false);
                    if (t != null) { Cache[fullName] = t; return t; }
                }
                catch { }
            }
            return null; // not cached: an assembly that is loaded later may have it
        }
    }
}
