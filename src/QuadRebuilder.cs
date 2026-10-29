using System;
using System.Collections.Generic;
using UnityEngine;

namespace TerraformTech
{
    /// <summary>
    /// Rebuilds PQS leaf quads that overlap an edited area and re-cooks their colliders.
    /// Hidden parent quads over the area are only marked unbuilt: PQ.SetVisible() calls Build(),
    /// so PQS rebuilds them itself when they become visible again (when children collapse).
    /// </summary>
    public static class QuadRebuilder
    {
        // Extra slack around the brush: quad mesh bounds don't include the new heights yet,
        // and neighbouring quads need their edge normals refreshed.
        const float Margin = 100f;

        public struct Stats
        {
            public int visited, leaves, inRange, notBuilt, inactive, cached, refused, rebuilt, parentsMarked;

            public override string ToString()
            {
                return string.Format("visited={0} leaves={1} inRange={2} notBuilt={3} inactive={4} cached={5} refused={6} rebuilt={7} parentsMarked={8}",
                    visited, leaves, inRange, notBuilt, inactive, cached, refused, rebuilt, parentsMarked);
            }
        }

        public static Stats RebuildArea(PQS pqs, Vector3d worldCenter, double radius)
        {
            return Rebuild(pqs, worldCenter, (float)radius + Margin);
        }

        /// <summary>
        /// Rebuilds every built leaf quad overlapping any of the regions (PQS-local direction,
        /// angular radius), e.g. the tiles that existed before and after loading different edits.
        /// </summary>
        public static Stats RebuildRegions(BodyTerrain bt, List<KeyValuePair<Vector3d, double>> regions)
        {
            var stats = new Stats();
            if (bt.pqs == null || bt.pqs.quads == null || regions.Count == 0) return stats;
            var filter = new RegionFilter { bt = bt, regions = regions };
            foreach (PQ q in bt.pqs.quads) Visit(q, Vector3.zero, 0f, filter, ref stats);
            return stats;
        }

        sealed class RegionFilter
        {
            public BodyTerrain bt;
            public List<KeyValuePair<Vector3d, double>> regions;

            public bool Overlaps(PQ q)
            {
                Vector3d dir = bt.WorldToLocalDir(q.transform.position);
                double quadAngle = q.mesh.bounds.extents.magnitude / bt.pqs.radius;
                foreach (var r in regions)
                {
                    double angle = Math.Acos(Math.Min(1.0, Vector3d.Dot(dir, r.Key)));
                    if (angle <= quadAngle + r.Value + MarginAngle(bt)) return true;
                }
                return false;
            }

            static double MarginAngle(BodyTerrain bt) { return Margin / bt.pqs.radius; }
        }

        static Stats Rebuild(PQS pqs, Vector3 worldCenter, float reach)
        {
            var stats = new Stats();
            if (pqs == null || pqs.quads == null) return stats;
            foreach (PQ q in pqs.quads) Visit(q, worldCenter, reach, null, ref stats);
            return stats;
        }

        /// <summary>Returns true if anything under this quad was rebuilt.</summary>
        static bool Visit(PQ q, Vector3 worldCenter, float reach, RegionFilter filter, ref Stats stats)
        {
            if (q == null) return false;
            stats.visited++;

            // Parent quads are not culled by their mesh: once subdivided their mesh is not kept
            // meaningful, so the tree is walked fully (a few thousand nodes at most).
            if (q.isSubdivided)
            {
                if (q.subNodes == null) return false;
                bool touched = false;
                foreach (PQ child in q.subNodes)
                    touched |= Visit(child, worldCenter, reach, filter, ref stats);
                if (touched && q.isBuilt)
                {
                    q.isBuilt = false; // rebuilt by PQS on SetVisible()
                    stats.parentsMarked++;
                }
                return touched;
            }

            stats.leaves++;
            if (q.mesh == null) return false;
            if (filter != null)
            {
                if (!filter.Overlaps(q)) return false;
            }
            else
            {
                Vector3 local = q.transform.InverseTransformPoint(worldCenter);
                Bounds b = q.mesh.bounds;
                b.Expand(reach * 2f);
                if (!b.Contains(local)) return false;
            }
            stats.inRange++;

            if (!q.isBuilt) { stats.notBuilt++; return false; }
            if (!q.isActive) { stats.inactive++; return false; }
            if (q.isCached) { stats.cached++; return false; }

            q.isBuilt = false;
            q.Build(); // PQS.BuildQuad -> mods (incl. ours) -> OnQuadBuilt; then queues normals
            if (!q.isBuilt)
            {
                // PQS refused right now (e.g. builds disabled); it will build it later.
                stats.refused++;
                return true;
            }

            MeshCollider col = q.meshCollider;
            if (col != null && col.enabled)
            {
                // Same Mesh instance -> PhysX won't re-cook unless the reference changes.
                col.sharedMesh = null;
                col.sharedMesh = q.mesh;
            }
            stats.rebuilt++;
            return true;
        }
    }
}
