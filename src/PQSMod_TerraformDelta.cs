namespace TerraformTech
{
    /// <summary>
    /// Adds the stored terrain offsets on top of whatever the rest of the PQS chain produced
    /// (stock or Kopernicus). Runs last in the height pass; colour mods run in the later
    /// OnVertexBuild pass, so they already see the edited height.
    /// </summary>
    public class PQSMod_TerraformDelta : PQSMod
    {
        public const int ModOrder = 1000000000;

        public TerrainDeltaStore store;

        public override void OnVertexBuildHeight(PQS.VertexBuildData data)
        {
            TerrainDeltaStore s = store;
            if (s == null || s.Bypass || s.IsEmpty) return;
            data.vertHeight += s.Sample(data.directionFromCenter);
        }
    }
}
