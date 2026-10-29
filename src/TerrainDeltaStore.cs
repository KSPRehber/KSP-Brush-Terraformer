using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace TerraformTech
{
    public enum BrushOp { Raise, Lower, Flatten, Smooth }

    /// <summary>
    /// Sparse height-offset field over an equiangular cube sphere.
    /// Each of the 6 faces is a (N+1)x(N+1) grid of points, stored in TileSize x TileSize tiles
    /// that only exist where something was edited. Offsets are in meters along the radial direction.
    /// Directions are in PQS-local space (same frame as VertexBuildData.directionFromCenter).
    /// </summary>
    public sealed class TerrainDeltaStore
    {
        public const int TileSize = 64;
        const int TileLen = TileSize * TileSize;
        const double FourOverPi = 4.0 / Math.PI;
        const double PiOverFour = Math.PI / 4.0;

        public readonly int CellsPerFace;

        readonly Dictionary<long, float[]> tiles = new Dictionary<long, float[]>();
        // Unedited terrain height (altitude above pqs.radius) per grid point, NaN = not sampled yet.
        // Only used by Flatten/Smooth, never persisted.
        readonly Dictionary<long, float[]> baseTiles = new Dictionary<long, float[]>();

        long lastKey = long.MinValue;
        float[] lastTile;

        /// <summary>Incremented on every mutation.</summary>
        public int Version { get; private set; }

        /// <summary>When true, the PQSMod ignores this store (used to sample the original terrain).</summary>
        public bool Bypass;

        public bool IsEmpty { get { return tiles.Count == 0; } }
        public int TileCount { get { return tiles.Count; } }

        public TerrainDeltaStore(int cellsPerFace)
        {
            if (cellsPerFace <= 0 || cellsPerFace % TileSize != 0)
                throw new ArgumentException("cellsPerFace must be a positive multiple of " + TileSize);
            CellsPerFace = cellsPerFace;
        }

        static long Key(int face, int tx, int ty)
        {
            return ((long)face << 48) | ((long)tx << 24) | (uint)ty;
        }

        #region Cube mapping

        /// <summary>Maps a direction to its cube face and equiangular face coordinates a,b in [-1,1].</summary>
        public static void DirToFace(Vector3d d, out int face, out double a, out double b)
        {
            double ax = Math.Abs(d.x), ay = Math.Abs(d.y), az = Math.Abs(d.z);
            int k;
            double major, s, t;
            if (ax >= ay && ax >= az) { k = 0; major = d.x; s = d.y; t = d.z; }
            else if (ay >= az) { k = 1; major = d.y; s = d.z; t = d.x; }
            else { k = 2; major = d.z; s = d.x; t = d.y; }
            face = k * 2 + (major < 0 ? 1 : 0);
            double inv = 1.0 / Math.Abs(major);
            a = Math.Atan(s * inv) * FourOverPi;
            b = Math.Atan(t * inv) * FourOverPi;
        }

        /// <summary>Projects a direction onto a given face's coordinates. False if it points away from the face.</summary>
        static bool ProjectToFace(int face, Vector3d d, out double a, out double b)
        {
            int k = face >> 1;
            double sign = (face & 1) == 1 ? -1.0 : 1.0;
            double major, s, t;
            switch (k)
            {
                case 0: major = d.x; s = d.y; t = d.z; break;
                case 1: major = d.y; s = d.z; t = d.x; break;
                default: major = d.z; s = d.x; t = d.y; break;
            }
            major *= sign;
            if (major <= 1e-6) { a = b = 0; return false; }
            a = Math.Atan(s / major) * FourOverPi;
            b = Math.Atan(t / major) * FourOverPi;
            return true;
        }

        public static Vector3d FaceToDir(int face, double a, double b)
        {
            int k = face >> 1;
            double sign = (face & 1) == 1 ? -1.0 : 1.0;
            double s = Math.Tan(a * PiOverFour), t = Math.Tan(b * PiOverFour);
            Vector3d d;
            switch (k)
            {
                case 0: d = new Vector3d(sign, s, t); break;
                case 1: d = new Vector3d(t, sign, s); break;
                default: d = new Vector3d(s, t, sign); break;
            }
            return d.normalized;
        }

        Vector3d GridToDir(int face, int gx, int gy)
        {
            return FaceToDir(face, gx * 2.0 / CellsPerFace - 1.0, gy * 2.0 / CellsPerFace - 1.0);
        }

        #endregion

        #region Point access

        float[] GetTile(int face, int gx, int gy, bool create)
        {
            long key = Key(face, gx / TileSize, gy / TileSize);
            if (key == lastKey) return lastTile;
            float[] tile;
            if (!tiles.TryGetValue(key, out tile))
            {
                if (!create) return null;
                tile = new float[TileLen];
                tiles.Add(key, tile);
            }
            lastKey = key;
            lastTile = tile;
            return tile;
        }

        public float GetPoint(int face, int gx, int gy)
        {
            float[] tile = GetTile(face, gx, gy, false);
            return tile == null ? 0f : tile[(gy % TileSize) * TileSize + gx % TileSize];
        }

        void SetPoint(int face, int gx, int gy, float value)
        {
            float[] tile = GetTile(face, gx, gy, true);
            tile[(gy % TileSize) * TileSize + gx % TileSize] = value;
        }

        /// <summary>Bilinearly interpolated offset in meters for a PQS-local direction.</summary>
        public double Sample(Vector3d dir)
        {
            if (tiles.Count == 0) return 0.0;

            int face;
            double a, b;
            DirToFace(dir, out face, out a, out b);
            double x = (a + 1.0) * 0.5 * CellsPerFace;
            double y = (b + 1.0) * 0.5 * CellsPerFace;
            int ix = Math.Min(Math.Max((int)x, 0), CellsPerFace - 1);
            int iy = Math.Min(Math.Max((int)y, 0), CellsPerFace - 1);
            double fx = x - ix, fy = y - iy;

            double v00 = GetPoint(face, ix, iy);
            double v10 = GetPoint(face, ix + 1, iy);
            double v01 = GetPoint(face, ix, iy + 1);
            double v11 = GetPoint(face, ix + 1, iy + 1);
            return (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
        }

        #endregion

        #region Brushes

        /// <summary>
        /// Applies a brush centered on a PQS-local direction.
        /// <paramref name="rate"/> is meters for Raise/Lower, a 0..1 blend fraction for Flatten/Smooth.
        /// <paramref name="sampleBaseAltitude"/> returns the unedited terrain altitude for a direction
        /// (only called for Flatten/Smooth; results are cached).
        /// </summary>
        public void ApplyBrush(BrushOp op, Vector3d center, double radiusMeters, double sphereRadius,
            double rate, double flattenAltitude, Func<Vector3d, double> sampleBaseAltitude)
        {
            center = center.normalized;
            double angRadius = radiusMeters / sphereRadius;
            double cosAng = Math.Cos(angRadius);
            // grid points per radian on a face is 2N/pi; pad for equiangular distortion off-axis
            int halfWindow = (int)Math.Ceiling(angRadius * 2.0 * CellsPerFace / Math.PI * 1.6) + 2;

            for (int face = 0; face < 6; face++)
            {
                double a, b;
                if (!ProjectToFace(face, center, out a, out b)) continue;
                int cx = (int)Math.Round((a + 1.0) * 0.5 * CellsPerFace);
                int cy = (int)Math.Round((b + 1.0) * 0.5 * CellsPerFace);
                int x0 = Math.Max(cx - halfWindow, 0), x1 = Math.Min(cx + halfWindow, CellsPerFace);
                int y0 = Math.Max(cy - halfWindow, 0), y1 = Math.Min(cy + halfWindow, CellsPerFace);
                if (x0 > x1 || y0 > y1) continue;

                if (op == BrushOp.Raise || op == BrushOp.Lower)
                {
                    double signed = op == BrushOp.Raise ? rate : -rate;
                    for (int gy = y0; gy <= y1; gy++)
                    for (int gx = x0; gx <= x1; gx++)
                    {
                        double w = Weight(GridToDir(face, gx, gy), center, cosAng, angRadius);
                        if (w <= 0) continue;
                        SetPoint(face, gx, gy, (float)(GetPoint(face, gx, gy) + signed * w));
                    }
                }
                else
                {
                    ApplyBlendBrush(op, face, x0, x1, y0, y1, center, cosAng, angRadius, rate,
                        flattenAltitude, sampleBaseAltitude);
                }
            }
            Version++;
        }

        void ApplyBlendBrush(BrushOp op, int face, int x0, int x1, int y0, int y1, Vector3d center,
            double cosAng, double angRadius, double rate, double flattenAltitude,
            Func<Vector3d, double> sampleBaseAltitude)
        {
            // Smooth needs a 1-point ring around the window to read neighbours.
            int pad = op == BrushOp.Smooth ? 1 : 0;
            int wx0 = Math.Max(x0 - pad, 0), wx1 = Math.Min(x1 + pad, CellsPerFace);
            int wy0 = Math.Max(y0 - pad, 0), wy1 = Math.Min(y1 + pad, CellsPerFace);
            int w = wx1 - wx0 + 1, h = wy1 - wy0 + 1;

            var weights = new double[w * h];
            var total = new double[w * h];
            for (int gy = wy0; gy <= wy1; gy++)
            for (int gx = wx0; gx <= wx1; gx++)
            {
                int i = (gy - wy0) * w + (gx - wx0);
                Vector3d dir = GridToDir(face, gx, gy);
                double wt = Weight(dir, center, cosAng, angRadius);
                weights[i] = wt;
                // Neighbours of weighted points are needed for Smooth; skip everything else.
                if (wt <= 0 && op != BrushOp.Smooth) continue;
                if (wt <= 0 && !NearWeighted(gx, gy, face, center, cosAng, angRadius)) continue;
                total[i] = GetBaseAltitude(face, gx, gy, dir, sampleBaseAltitude) + GetPoint(face, gx, gy);
            }

            for (int gy = y0; gy <= y1; gy++)
            for (int gx = x0; gx <= x1; gx++)
            {
                int i = (gy - wy0) * w + (gx - wx0);
                double wt = weights[i];
                if (wt <= 0) continue;

                double target;
                if (op == BrushOp.Flatten)
                {
                    target = flattenAltitude;
                }
                else
                {
                    double sum = 0;
                    int n = 0;
                    if (gx > wx0) { sum += total[i - 1]; n++; }
                    if (gx < wx1) { sum += total[i + 1]; n++; }
                    if (gy > wy0) { sum += total[i - w]; n++; }
                    if (gy < wy1) { sum += total[i + w]; n++; }
                    if (n == 0) continue;
                    target = sum / n;
                }

                double newTotal = total[i] + (target - total[i]) * Math.Min(1.0, rate * wt);
                SetPoint(face, gx, gy, (float)(newTotal - GetBaseAltitude(face, gx, gy, Vector3d.zero, null)));
            }
        }

        bool NearWeighted(int gx, int gy, int face, Vector3d center, double cosAng, double angRadius)
        {
            // Cheap check: is any 4-neighbour inside the brush?
            if (gx > 0 && Weight(GridToDir(face, gx - 1, gy), center, cosAng, angRadius) > 0) return true;
            if (gx < CellsPerFace && Weight(GridToDir(face, gx + 1, gy), center, cosAng, angRadius) > 0) return true;
            if (gy > 0 && Weight(GridToDir(face, gx, gy - 1), center, cosAng, angRadius) > 0) return true;
            if (gy < CellsPerFace && Weight(GridToDir(face, gx, gy + 1), center, cosAng, angRadius) > 0) return true;
            return false;
        }

        double GetBaseAltitude(int face, int gx, int gy, Vector3d dir, Func<Vector3d, double> sampleBaseAltitude)
        {
            long key = Key(face, gx / TileSize, gy / TileSize);
            float[] tile;
            if (!baseTiles.TryGetValue(key, out tile))
            {
                tile = new float[TileLen];
                for (int i = 0; i < TileLen; i++) tile[i] = float.NaN;
                baseTiles.Add(key, tile);
            }
            int idx = (gy % TileSize) * TileSize + gx % TileSize;
            if (float.IsNaN(tile[idx]))
            {
                if (sampleBaseAltitude == null) throw new InvalidOperationException("base altitude not sampled");
                tile[idx] = (float)sampleBaseAltitude(dir);
            }
            return tile[idx];
        }

        /// <summary>Smooth falloff: 1 at the center, 0 at the rim.</summary>
        static double Weight(Vector3d dir, Vector3d center, double cosAng, double angRadius)
        {
            double dot = Vector3d.Dot(dir, center);
            if (dot < cosAng) return 0;
            double t = Math.Acos(Math.Min(dot, 1.0)) / angRadius;
            double q = 1.0 - t * t;
            return q * q;
        }

        #endregion

        /// <summary>Center direction and angular radius (radians) of every stored tile.</summary>
        public List<KeyValuePair<Vector3d, double>> TileRegions()
        {
            var regions = new List<KeyValuePair<Vector3d, double>>(tiles.Count);
            foreach (long key in tiles.Keys)
            {
                int face = (int)(key >> 48);
                int gx0 = (int)((key >> 24) & 0xFFFFFF) * TileSize;
                int gy0 = (int)(key & 0xFFFFFF) * TileSize;
                Vector3d center = GridToDir(face, gx0 + TileSize / 2, gy0 + TileSize / 2);
                Vector3d corner = GridToDir(face, gx0, gy0);
                regions.Add(new KeyValuePair<Vector3d, double>(center, Math.Acos(Math.Min(1.0, Vector3d.Dot(center, corner)))));
            }
            return regions;
        }

        public void Clear()
        {
            tiles.Clear();
            lastKey = long.MinValue;
            lastTile = null;
            Version++;
        }

        #region Serialization

        const int Magic = 0x31444654; // "TFD1"

        public string Serialize()
        {
            using (var ms = new MemoryStream())
            {
                using (var deflate = new DeflateStream(ms, CompressionMode.Compress, true))
                using (var w = new BinaryWriter(deflate))
                {
                    w.Write(Magic);
                    w.Write(CellsPerFace);
                    w.Write(tiles.Count);
                    foreach (var kv in tiles)
                    {
                        w.Write(kv.Key);
                        float[] t = kv.Value;
                        for (int i = 0; i < TileLen; i++) w.Write(t[i]);
                    }
                }
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        /// <summary>Replaces the contents with serialized data. Returns false if the data is for a different grid.</summary>
        public bool Deserialize(string base64)
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using (var ms = new MemoryStream(bytes))
            using (var deflate = new DeflateStream(ms, CompressionMode.Decompress))
            using (var r = new BinaryReader(deflate))
            {
                if (r.ReadInt32() != Magic) throw new InvalidDataException("bad terraform data header");
                if (r.ReadInt32() != CellsPerFace) return false;
                int count = r.ReadInt32();
                Clear();
                for (int n = 0; n < count; n++)
                {
                    long key = r.ReadInt64();
                    var t = new float[TileLen];
                    for (int i = 0; i < TileLen; i++) t[i] = r.ReadSingle();
                    tiles[key] = t;
                }
            }
            Version++;
            return true;
        }

        #endregion
    }
}
