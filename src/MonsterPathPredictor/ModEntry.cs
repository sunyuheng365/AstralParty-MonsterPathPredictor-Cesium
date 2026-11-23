using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;
using MonsterPathPredictor.Runtime;

namespace MonsterPathPredictor
{
    public static class ModEntry
    {
        public static void Main()
        {
            SdkManifest.ExportSidecar();
            ModBase.Run(new MonsterPathController(), tag: "怪物寻路预测");
        }
    }
}
