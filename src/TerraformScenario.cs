using System;
using System.Collections.Generic;
using System.Text;

namespace TerraformTech
{
    /// <summary>
    /// Stores terrain edits inside the save file, so quicksave/quickload and reverts also restore terrain.
    /// <code>
    /// SCENARIO { name = TerraformScenario
    ///   BODY { name = Kerbin  cellsPerFace = 131072  data = &lt;base64 chunk&gt; data = ... }
    /// }
    /// </code>
    /// </summary>
    [KSPScenario(ScenarioCreationOptions.AddToAllGames, GameScenes.FLIGHT, GameScenes.SPACECENTER, GameScenes.TRACKSTATION)]
    public class TerraformScenario : ScenarioModule
    {
        const int ChunkSize = 4096;

        public override void OnSave(ConfigNode node)
        {
            foreach (BodyTerrain bt in Terraform.All)
            {
                if (bt.store.IsEmpty) continue;
                string data = Serialized(bt);
                ConfigNode bn = node.AddNode("BODY");
                bn.AddValue("name", bt.body.bodyName);
                bn.AddValue("cellsPerFace", bt.store.CellsPerFace);
                for (int i = 0; i < data.Length; i += ChunkSize)
                    bn.AddValue("data", data.Substring(i, Math.Min(ChunkSize, data.Length - i)));
            }
        }

        public override void OnLoad(ConfigNode node)
        {
            Terraform.AttachAll();

            var loaded = new Dictionary<string, string>();
            foreach (ConfigNode bn in node.GetNodes("BODY"))
            {
                string name = bn.GetValue("name");
                if (string.IsNullOrEmpty(name)) continue;
                var sb = new StringBuilder();
                foreach (string chunk in bn.GetValues("data")) sb.Append(chunk);
                loaded[name] = sb.ToString();
            }

            foreach (BodyTerrain bt in Terraform.All)
            {
                string data;
                loaded.TryGetValue(bt.body.bodyName, out data);

                if (data == null)
                {
                    if (bt.store.IsEmpty) continue;
                    bt.QueueEditedAreaRebuild();
                    bt.store.Clear();
                    Remember(bt, null);
                    LogReload(bt);
                    continue;
                }

                // Same data we already have in memory: nothing to rebuild (common on scene changes).
                if (data == bt.lastSerialized && bt.store.Version == bt.lastSerializedVersion) continue;

                bt.QueueEditedAreaRebuild();
                try
                {
                    if (!bt.store.Deserialize(data))
                    {
                        Terraform.Log("discarding edits for " + bt.body.bodyName + ": saved with a different grid resolution");
                        bt.store.Clear();
                    }
                }
                catch (Exception e)
                {
                    Terraform.Log("failed to load edits for " + bt.body.bodyName + ": " + e.Message);
                    bt.store.Clear();
                }
                Remember(bt, data);
                bt.QueueEditedAreaRebuild();
                LogReload(bt);
            }
        }

        static void LogReload(BodyTerrain bt)
        {
            Terraform.Log("edits for " + bt.body.bodyName + " changed on load (" + bt.store.TileCount + " tiles), rebuild queued");
        }

        static string Serialized(BodyTerrain bt)
        {
            if (bt.lastSerialized == null || bt.lastSerializedVersion != bt.store.Version)
                Remember(bt, bt.store.Serialize());
            return bt.lastSerialized;
        }

        static void Remember(BodyTerrain bt, string data)
        {
            bt.lastSerialized = data;
            bt.lastSerializedVersion = bt.store.Version;
        }
    }
}
