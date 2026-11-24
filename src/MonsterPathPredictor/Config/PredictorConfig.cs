using CesiumLoader.SDK.Configuration;
using MonsterPathPredictor.Core;

namespace MonsterPathPredictor.Config
{
    internal static class PredictorConfig
    {
        public static PredictorSettings Load(ModConfig config, bool isInitial)
        {
            var settings = new PredictorSettings();
            if (config == null)
            {
                settings.ClampInPlace();
                return settings;
            }

            if (isInitial)
            {
                // One-time migration from v1 configs where clearOnEmptyClick defaulted to true,
                // so upgrading users immediately get persistent click-to-track behavior while
                // still allowing explicit manual opt-in afterwards.
                bool isLegacyV1 = config.Has("enabled") && config.GetInt("configVersion", 1) < 2;
                if (isLegacyV1)
                {
                    config.Set("clearOnEmptyClick", false);
                    config.Set("configVersion", 2);
                }
                ApplyDefaultsAndDocs(config);
            }

            settings.Enabled = config.GetBool("enabled", settings.Enabled);
            settings.PreviewSteps = config.GetInt("previewSteps", settings.PreviewSteps);
            settings.DashLength = config.GetFloat("dashLength", settings.DashLength);
            settings.GapLength = config.GetFloat("gapLength", settings.GapLength);
            settings.DashSpeed = config.GetFloat("dashSpeed", settings.DashSpeed);
            settings.LineWidth = config.GetFloat("lineWidth", settings.LineWidth);
            settings.HeightOffset = config.GetFloat("heightOffset", settings.HeightOffset);
            settings.FontSize = config.GetInt("fontSize", settings.FontSize);
            settings.ClickRadius = config.GetFloat("clickRadius", settings.ClickRadius);
            settings.ClearOnEmptyClick = config.GetBool("clearOnEmptyClick", settings.ClearOnEmptyClick);
            settings.ShowReasonTag = config.GetBool("showReasonTag", settings.ShowReasonTag);
            settings.ShowDeterministicPercent = config.GetBool("showDeterministicPercent", settings.ShowDeterministicPercent);
            settings.SimulateCombatOnFork = config.GetBool("simulateCombatOnFork", settings.SimulateCombatOnFork);
            settings.PostCombatMode = PredictorSettings.ParsePostCombatMode(config.GetString("postCombatMode", "continue"));
            settings.AllowNonAggressiveMonsters = config.GetBool("allowNonAggressiveMonsters", settings.AllowNonAggressiveMonsters);
            settings.PickerHotkey = config.GetString("pickerHotkey", settings.PickerHotkey);
            settings.ClosePickerOnSelect = config.GetBool("closePickerOnSelect", settings.ClosePickerOnSelect);
            settings.ClearHotkey = config.GetString("clearHotkey", settings.ClearHotkey);
            settings.ExcludedPlayerBuffIds = PredictorSettings.ParseIdCsv(config.GetString("excludedPlayerBuffIds", ""));

            settings.ClampInPlace();

            if (isInitial)
            {
                config.SaveIfDirty();
            }

            return settings;
        }

        private static void ApplyDefaultsAndDocs(ModConfig cfg)
        {
            cfg.SetDefault("enabled", true, ConfigDoc.Of(
                "是否启用怪物寻路预测模组",
                "是否啟用怪物尋路預測模組",
                "Enable the Monster Path Predictor mod"));

            cfg.SetDefault("previewSteps", 12, ConfigDoc.Of(
                "点击怪物后向前延伸预测的格数 X（范围 1-30，默认 12）",
                "點擊怪物後向前延伸預測的格數 X（範圍 1-30，預設 12）",
                "Number of tiles (X) to forecast ahead from the selected monster (1-30, default 12)"));

            cfg.SetDefault("dashLength", 12.0f, ConfigDoc.Of(
                "虚线单段长度（像素，范围 4-40）",
                "虛線單段長度（像素，範圍 4-40）",
                "Length of each dash segment in UI pixels (4-40)"));

            cfg.SetDefault("gapLength", 8.0f, ConfigDoc.Of(
                "虚线间隔长度（像素，范围 2-30）",
                "虛線間隔長度（像素，範圍 2-30）",
                "Gap length between dash segments in UI pixels (2-30)"));

            cfg.SetDefault("dashSpeed", 26.0f, ConfigDoc.Of(
                "虚线缓慢向前流动的速度（像素/秒，范围 0-150，0 为静态虚线）",
                "虛線緩慢向前流動的速度（像素/秒，範圍 0-150，0 為靜態虛線）",
                "Forward marching animation speed of dashes in px/sec (0-150, 0 = static)"));

            cfg.SetDefault("lineWidth", 4.5f, ConfigDoc.Of(
                "虚线线条粗细（像素，范围 1.5-14）",
                "虛線線條粗細（像素，範圍 1.5-14）",
                "Stroke thickness of dashed preview lines (1.5-14)"));

            cfg.SetDefault("heightOffset", 0.45f, ConfigDoc.Of(
                "路线与标签相对地块的 3D 高度偏移（0-5）",
                "路線與標籤相對地塊的 3D 高度偏移（0-5）",
                "3D world Y height offset above tiles (0-5)"));

            cfg.SetDefault("fontSize", 18, ConfigDoc.Of(
                "岔路口概率百分比与说明标签字号（12-36）",
                "岔路口機率百分比與說明標籤字號（12-36）",
                "Font size for fork probability badges and tags (12-36)"));

            cfg.SetDefault("clickRadius", 28.0f, ConfigDoc.Of(
                "点击怪物模型外缘时的容差半径（像素，8-120；点在模型内部直接命中）",
                "點擊怪物模型外緣時的容差半徑（像素，8-120；點在模型內部直接命中）",
                "Near-miss tolerance radius outside monster sprite bounds in pixels (8-120)"));

            cfg.SetDefault("configVersion", 2, ConfigDoc.Of(
                "内部配置架构版本号（自动迁移用，请勿手动修改）",
                "內部設定架構版本號（自動遷移用，請勿手動修改）",
                "Internal config schema version for one-time migrations"));

            cfg.SetDefault("clearOnEmptyClick", false, ConfigDoc.Of(
                "点击棋盘空白处时是否取消当前选中的怪物（默认 false：路线常驻跟踪，直到再次点击该怪/点击其他怪/怪物死亡或按清除键）",
                "點擊棋盤空白處時是否取消目前選中的怪物（預設 false：路線常駐追蹤，直到再次點擊該怪/點擊其他怪/怪物死亡或按清除鍵）",
                "Clear selected monster when clicking empty board space (default false: stay locked until re-clicked, switched, or dead)"));

            cfg.SetDefault("showReasonTag", true, ConfigDoc.Of(
                "在岔路口旁显示简短判定依据（如 起步·红箭 / →P1(2格) / 掉头平分）",
                "在岔路口旁顯示簡短判定依據（如 起步·紅箭 / →P1(2格) / 掉頭平分）",
                "Show concise decision tags at forks"));

            cfg.SetDefault("showDeterministicPercent", false, ConfigDoc.Of(
                "岔路口只有唯一确定方向时是否也显示 100%（多分支时始终显示百分比）",
                "岔路口只有唯一確定方向時是否也顯示 100%（多分支時始終顯示百分比）",
                "Also show 100% at deterministic forks (multi-branch forks always show percentages)"));

            cfg.SetDefault("simulateCombatOnFork", true, ConfigDoc.Of(
                "路径经过站有存活玩家的岔路口时，离开该岔路口按战后续走（红蓝箭头）推演",
                "路徑經過站有存活玩家的岔路口時，離開該岔路口按戰後續走（紅藍箭頭）推演",
                "Apply post-combat Red>Blue priority when leaving a fork occupied by a living player"));

            cfg.SetDefault("postCombatMode", "continue", ConfigDoc.Of(
                "路径遭遇存活玩家后的续行策略：continue（按攻略实测：标出交战点并以半透明虚线继续按站位预测后续岔路）/ stop（在首次遇敌格截断）/ redBlue（遇敌后后续岔路一律改按红蓝箭头）",
                "路徑遭遇存活玩家後的續行策略：continue（按攻略實測：標出交戰點並以半透明虛線繼續按站位預測後續岔路）/ stop（在首次遇敵格截斷）/ redBlue（遇敵後後續岔路一律改按紅藍箭頭）",
                "Behavior after encountering a living player: continue | stop | redBlue"));

            cfg.SetDefault("allowNonAggressiveMonsters", true, ConfigDoc.Of(
                "允许点击小偷等非普通主动攻击怪（会在顶部提示非主动怪）",
                "允許點擊小偷等非普通主動攻擊怪（會在頂部提示非主動怪）",
                "Allow selecting non-aggressive monsters (with an informational banner note)"));

            cfg.SetDefault("pickerHotkey", "F8", ConfigDoc.Of(
                "打开/关闭怪物快速选择弹窗的热键（Unity KeyCode 名称，默认 F8）",
                "開啟/關閉怪物快速選擇彈窗的熱鍵（Unity KeyCode 名稱，預設 F8）",
                "Hotkey to toggle the monster picker popup (Unity KeyCode name, default F8)"));

            cfg.SetDefault("closePickerOnSelect", false, ConfigDoc.Of(
                "在选怪弹窗中点击某个怪物后是否自动关闭弹窗（默认 false，方便连续切换对比不同怪物）",
                "在選怪彈窗中點擊某個怪物後是否自動關閉彈窗（預設 false，方便連續切換對比不同怪物）",
                "Auto-close the monster picker popup after selecting a monster"));

            cfg.SetDefault("clearHotkey", "Escape", ConfigDoc.Of(
                "快速关闭弹窗或取消当前预览的快捷键（Unity KeyCode 名称，默认 Escape）",
                "快速關閉彈窗或取消目前預覽的快捷鍵（Unity KeyCode 名稱，預設 Escape）",
                "Hotkey to close popup or clear active preview (Unity KeyCode name, default Escape)"));

            cfg.SetDefault("excludedPlayerBuffIds", "", ConfigDoc.Of(
                "额外排除出怪物追踪名单的玩家 Buff ID（逗号分隔，例如隐身类特殊 Buff）",
                "額外排除出怪物追蹤名單的玩家 Buff ID（逗號分隔，例如隱身類特殊 Buff）",
                "Extra comma-separated player Buff IDs excluded from monster tracking"));
        }
    }
}
