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
                ApplyDefaultsAndDocs(config);
            }

            settings.Enabled = config.GetBool("enabled", settings.Enabled);
            settings.PreviewSteps = config.GetInt("previewSteps", settings.PreviewSteps);
            settings.DashLength = config.GetFloat("dashLength", settings.DashLength);
            settings.GapLength = config.GetFloat("gapLength", settings.GapLength);
            settings.LineWidth = config.GetFloat("lineWidth", settings.LineWidth);
            settings.HeightOffset = config.GetFloat("heightOffset", settings.HeightOffset);
            settings.FontSize = config.GetInt("fontSize", settings.FontSize);
            settings.ClickRadius = config.GetFloat("clickRadius", settings.ClickRadius);
            settings.ClearOnEmptyClick = config.GetBool("clearOnEmptyClick", settings.ClearOnEmptyClick);
            settings.ShowReasonTag = config.GetBool("showReasonTag", settings.ShowReasonTag);
            settings.ShowDeterministicPercent = config.GetBool("showDeterministicPercent", settings.ShowDeterministicPercent);
            settings.SimulateCombatOnFork = config.GetBool("simulateCombatOnFork", settings.SimulateCombatOnFork);
            settings.AllowNonAggressiveMonsters = config.GetBool("allowNonAggressiveMonsters", settings.AllowNonAggressiveMonsters);
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

            cfg.SetDefault("clickRadius", 48.0f, ConfigDoc.Of(
                "点击选中怪物时的屏幕命中半径（像素，20-120）",
                "點擊選中怪物時的螢幕命中半徑（像素，20-120）",
                "Screen-space hit radius for clicking monsters (20-120)"));

            cfg.SetDefault("clearOnEmptyClick", true, ConfigDoc.Of(
                "点击棋盘空白处时自动取消当前选中的怪物",
                "點擊棋盤空白處時自動取消目前選中的怪物",
                "Clear selected monster when clicking empty board space"));

            cfg.SetDefault("showReasonTag", true, ConfigDoc.Of(
                "在岔路口旁显示简短判定依据（如 起步·红箭 / 追P1 / 掉头平分）",
                "在岔路口旁顯示簡短判定依據（如 起步·紅箭 / 追P1 / 掉頭平分）",
                "Show concise decision tags at forks"));

            cfg.SetDefault("showDeterministicPercent", false, ConfigDoc.Of(
                "岔路口只有唯一确定方向时是否也显示 100%（多分支时始终显示百分比）",
                "岔路口只有唯一確定方向時是否也顯示 100%（多分支時始終顯示百分比）",
                "Also show 100% at deterministic forks (multi-branch forks always show percentages)"));

            cfg.SetDefault("simulateCombatOnFork", true, ConfigDoc.Of(
                "路径经过站有存活玩家的岔路口时，是否按战后续走（红蓝箭头）推演后续路线",
                "路徑經過站有存活玩家的岔路口時，是否按戰後續走（紅藍箭頭）推演後續路線",
                "Apply post-combat Red>Blue priority when path crosses a fork occupied by a living player"));

            cfg.SetDefault("allowNonAggressiveMonsters", true, ConfigDoc.Of(
                "允许点击小偷等非普通主动攻击怪（会在顶部提示非主动怪）",
                "允許點擊小偷等非普通主動攻擊怪（會在頂部提示非主動怪）",
                "Allow selecting non-aggressive monsters (with an informational banner note)"));

            cfg.SetDefault("clearHotkey", "Escape", ConfigDoc.Of(
                "快速取消当前预览的快捷键（Unity KeyCode 名称，默认 Escape）",
                "快速取消目前預覽的快捷鍵（Unity KeyCode 名稱，預設 Escape）",
                "Hotkey to clear active preview (Unity KeyCode name, default Escape)"));

            cfg.SetDefault("excludedPlayerBuffIds", "", ConfigDoc.Of(
                "额外排除出怪物追踪名单的玩家 Buff ID（逗号分隔，例如隐身类特殊 Buff）",
                "額外排除出怪物追蹤名單的玩家 Buff ID（逗號分隔，例如隱身類特殊 Buff）",
                "Extra comma-separated player Buff IDs excluded from monster tracking"));
        }
    }
}
