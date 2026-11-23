using CesiumLoader.SDK.Manifests;

[assembly: ModManifest(
    "怪物寻路预测",
    "1.0.0",
    "sunyuheng365",
    "点击 PVE 怪物以虚线预览接下来 X 格的精确寻路规划，在岔路口展示走向与多分支概率百分比。",
    Permissions = ModPermission.ReadGameState,
    SdkVersion = "3.8.3",
    Platforms = ModPlatform.Windows | ModPlatform.Android)]
