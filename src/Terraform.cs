using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace TerraformTech
{
    /// <summary>Terraforming state for one celestial body.</summary>
    public sealed class BodyTerrain
    {
        public readonly CelestialBody body;
        public readonly PQS pqs;
        public readonly TerrainDeltaStore store;
        public readonly PQSMod_TerraformDelta mod;

        // Serialized form as of the last save/load, used to skip needless sphere rebuilds.
        public string lastSerialized;
        public int lastSerializedVersion = -1;

        public BodyTerrain(CelestialBody body, PQS pqs, TerrainDeltaStore store, PQSMod_TerraformDelta mod)
        {
            this.body = body;
            this.pqs = pqs;
            this.store = store;
            this.mod = mod;
        }

        /// <summary>Approximate vertex spacing of the finest PQS level right now, in meters.</summary>
        public double VertexSpacing() { return VertexSpacing(pqs); }

        public static double VertexSpacing(PQS pqs)
        {
            int sideVerts = PQS.cacheSideVertCount > 1 ? PQS.cacheSideVertCount : 15;
            return Math.PI * 0.5 * pqs.radius / (Math.Pow(2, pqs.maxLevel) * (sideVerts - 1));
        }

        public double CellSize
        {
            get { return Math.PI * 0.5 * pqs.radius / store.CellsPerFace; }
        }

        /// <summary>Terrain altitude (above pqs.radius) ignoring all terraform edits.</summary>
        public double BaseAltitude(Vector3d localDir)
        {
            bool old = store.Bypass;
            store.Bypass = true;
            try { return pqs.GetSurfaceHeight(localDir) - pqs.radius; }
            finally { store.Bypass = old; }
        }

        /// <summary>Current (edited) terrain altitude above pqs.radius.</summary>
        public double Altitude(Vector3d localDir)
        {
            return pqs.GetSurfaceHeight(localDir) - pqs.radius;
        }

        public Vector3d WorldToLocalDir(Vector3d worldPos)
        {
            return body.GetRelSurfaceNVector(body.GetLatitude(worldPos, false), body.GetLongitude(worldPos, false));
        }

        /// <summary>
        /// Areas (PQS-local direction, angular radius) whose terrain changed outside of a brush,
        /// e.g. on load. Rebuilt by <see cref="TerraformBootstrap"/> once the scene has settled:
        /// tearing the sphere down with PQS.RebuildSphere() during scene load crashed Unity natively.
        /// </summary>
        readonly List<KeyValuePair<Vector3d, double>> pendingRegions = new List<KeyValuePair<Vector3d, double>>();

        public bool RebuildPending { get { return pendingRegions.Count > 0; } }

        /// <summary>Queues the current edited area for rebuild; call before and after replacing the edits.</summary>
        public void QueueEditedAreaRebuild()
        {
            pendingRegions.AddRange(store.TileRegions());
        }

        internal void DoPendingRebuild()
        {
            var regions = new List<KeyValuePair<Vector3d, double>>(pendingRegions);
            pendingRegions.Clear();
            if (!pqs.isActive) return; // will build fresh, with current edits, when it starts
            var sw = System.Diagnostics.Stopwatch.StartNew();
            QuadRebuilder.Stats stats = QuadRebuilder.RebuildRegions(this, regions);
            Terraform.Log(string.Format("rebuild of {0} edited area ({1} regions): {2:F0} ms, {3}",
                body.bodyName, regions.Count, sw.Elapsed.TotalMilliseconds, stats));
        }
    }

    public static class Terraform
    {
        public const string LogPrefix = "[TerraformTech] ";
        const double MaxCellSize = 4.0;

        static readonly Dictionary<string, BodyTerrain> bodies = new Dictionary<string, BodyTerrain>();

        public static IEnumerable<BodyTerrain> All { get { return bodies.Values; } }

        public static BodyTerrain Get(CelestialBody body)
        {
            if (body == null) return null;
            BodyTerrain bt;
            bodies.TryGetValue(body.bodyName, out bt);
            return bt;
        }

        public static BodyTerrain Get(string bodyName)
        {
            BodyTerrain bt;
            bodies.TryGetValue(bodyName, out bt);
            return bt;
        }

        public static void Log(string msg) { Debug.Log(LogPrefix + msg); }

        #region Settings and terrain detail

        /// <summary>Extra PQS subdivision levels on top of the active terrain detail preset.</summary>
        public static int DetailBoost = 3;

        static string PluginDataDir
        {
            get { return Path.Combine(KSPUtil.ApplicationRootPath, "GameData/TerraformTech/PluginData"); }
        }

        /// <summary>Reads TERRAFORMTECH { detailBoost = N } from GameData/TerraformTech/TerraformTech.cfg.</summary>
        internal static void LoadSettings()
        {
            foreach (ConfigNode n in GameDatabase.Instance.GetConfigNodes("TERRAFORMTECH"))
            {
                int boost;
                if (int.TryParse(n.GetValue("detailBoost"), out boost)) DetailBoost = Mathf.Clamp(boost, 0, 6);
            }
        }

        /// <summary>
        /// Raises maxSubdivision of every terrain preset (PQS.StartSphere reads it on each sphere start).
        /// The unboosted values are kept in PluginData so the boost never compounds when KSP
        /// writes the boosted presets back to settings.cfg.
        /// </summary>
        internal static void ApplyDetailBoost()
        {
            PQSCache.PQSGlobalPresetList list = PQSCache.PresetList;
            if (list == null || list.presets == null) return;

            string path = Path.Combine(PluginDataDir, "TerrainOriginals.cfg");
            var originals = new Dictionary<string, int>();
            ConfigNode saved = File.Exists(path) ? ConfigNode.Load(path) : null;
            if (saved != null)
            {
                foreach (ConfigNode n in saved.GetNodes("ORIGINAL"))
                {
                    int max;
                    if (int.TryParse(n.GetValue("maxSubdivision"), out max))
                        originals[n.GetValue("preset") + "/" + n.GetValue("planet")] = max;
                }
            }

            bool added = false;
            foreach (PQSCache.PQSPreset preset in list.presets)
            {
                foreach (PQSCache.PQSSpherePreset sp in preset.spherePresets)
                {
                    string key = preset.name + "/" + sp.name;
                    int original;
                    if (!originals.TryGetValue(key, out original))
                    {
                        original = sp.maxSubdivision;
                        originals[key] = original;
                        added = true;
                    }
                    // Oceans are separate spheres we never edit.
                    if (sp.name.EndsWith("Ocean")) continue;
                    sp.maxSubdivision = original + DetailBoost;
                }
            }

            if (added)
            {
                var root = new ConfigNode();
                foreach (var kv in originals)
                {
                    int slash = kv.Key.IndexOf('/');
                    ConfigNode n = root.AddNode("ORIGINAL");
                    n.AddValue("preset", kv.Key.Substring(0, slash));
                    n.AddValue("planet", kv.Key.Substring(slash + 1));
                    n.AddValue("maxSubdivision", kv.Value);
                }
                Directory.CreateDirectory(PluginDataDir);
                root.Save(path);
            }

            Log(string.Format("terrain detail boost +{0} applied to preset list (active preset: {1})", DetailBoost, list.preset));
        }

        #endregion

        /// <summary>Injects the delta mod into every body's PQS. Safe to call more than once.</summary>
        internal static void AttachAll()
        {
            foreach (CelestialBody body in FlightGlobals.Bodies)
            {
                if (body == null || body.pqsController == null || bodies.ContainsKey(body.bodyName)) continue;
                try
                {
                    Attach(body);
                }
                catch (Exception e)
                {
                    Debug.LogError(LogPrefix + "failed to attach to " + body.bodyName + ": " + e);
                }
            }
        }

        static void Attach(CelestialBody body)
        {
            PQS pqs = body.pqsController;

            double faceArc = Math.PI * 0.5 * pqs.radius;
            double vertexSpacing = BodyTerrain.VertexSpacing(pqs);

            // Fixed fine grid: PQS maxLevel at the main menu is not necessarily what flight uses,
            // and the saved data must keep a stable resolution.
            double targetCell = MaxCellSize;
            int cells = TerrainDeltaStore.TileSize;
            while (faceArc / cells > targetCell && cells < (1 << 22)) cells <<= 1;

            var go = new GameObject("TerraformDelta");
            go.layer = pqs.gameObject.layer;
            go.transform.parent = pqs.transform;
            go.transform.localPosition = Vector3.zero;

            var mod = go.AddComponent<PQSMod_TerraformDelta>();
            mod.sphere = pqs;
            mod.order = PQSMod_TerraformDelta.ModOrder;
            mod.modEnabled = true;
            mod.requirements = PQS.ModiferRequirements.Default;
            mod.store = new TerrainDeltaStore(cells);

            var bt = new BodyTerrain(body, pqs, mod.store, mod);
            bodies.Add(body.bodyName, bt);

            Log(string.Format("attached to {0}: radius={1:F0} maxLevel={2} vertexSpacing~{3:F1}m cellsPerFace={4} cell~{5:F2}m",
                body.bodyName, pqs.radius, pqs.maxLevel, vertexSpacing, cells, bt.CellSize));
        }
    }

    /// <summary>
    /// Lives for the whole session: attaches to the bodies once and runs deferred full rebuilds
    /// only after a scene has finished loading.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.MainMenu, true)]
    public class TerraformBootstrap : MonoBehaviour
    {
        // Frames to wait after a scene's GUI is ready before touching the sphere.
        const int SettleFrames = 30;

        bool sceneLoading;
        int readyFrame;

        void Awake()
        {
            DontDestroyOnLoad(gameObject);
            Terraform.LoadSettings();
            Terraform.ApplyDetailBoost();
        }

        void Start()
        {
            Terraform.AttachAll();
            GameEvents.onGameSceneLoadRequested.Add(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Add(OnLevelReady);
        }

        void OnDestroy()
        {
            GameEvents.onGameSceneLoadRequested.Remove(OnSceneLoadRequested);
            GameEvents.onLevelWasLoadedGUIReady.Remove(OnLevelReady);
        }

        void OnSceneLoadRequested(GameScenes scene) { sceneLoading = true; }

        void OnLevelReady(GameScenes scene)
        {
            sceneLoading = false;
            readyFrame = Time.frameCount + SettleFrames;
        }

        void Update()
        {
            if (sceneLoading || Time.frameCount < readyFrame) return;
            foreach (BodyTerrain bt in Terraform.All)
                if (bt.RebuildPending) bt.DoPendingRebuild();
        }
    }
}
