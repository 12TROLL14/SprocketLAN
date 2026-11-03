using System;
using UnityEngine;

namespace SprocketMP
{
    public static class Terrain
    {
        /// <summary>World-space ground height under a point, from the active Unity terrains.</summary>
        public static bool GroundHeight(Vector3 p, out float y)
        {
            y = 0;
            try
            {
                var ts = UnityEngine.Terrain.activeTerrains;
                if (ts == null) return false;
                for (int i = 0; i < ts.Length; i++)
                {
                    var t = ts[i]; if (t == null) continue;
                    var o = t.GetPosition(); var size = t.terrainData.size;
                    if (p.x < o.x || p.z < o.z || p.x > o.x + size.x || p.z > o.z + size.z) continue;
                    y = t.SampleHeight(p) + o.y;
                    return true;
                }
            }
            catch (Exception e) { MPLog.Debug("GroundHeight: " + e.Message); }
            return false;
        }
    }
}
