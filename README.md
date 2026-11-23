# MonsterPathPredictor（怪物寻路预测）

面向《吉星派对》（Astral Party）**CesiumLoader / AstralParty.Toys** 生态的纯只读 PVE 蹲怪辅助模组。

## 核心效果

- **点击怪物模型任意处选中（支持叠怪区分）**：直接读取每个怪物渲染精灵（`SpriteRenderer`）的真实 3D 包围盒投影，点击头、身体、翅膀或脚底任意位置均可命中；多只怪物堆叠在同一地块时，会按实际偏移后的模型中心与镜头前后遮挡关系准确识别点击目标。
- **热键弹窗快速选怪**：按 **`F8`**（可在配置中自定义 `pickerHotkey`）打开可拖拽的怪物列表弹窗，按真实行动顺序显示场上所有存活怪物的编号、地块与血量，点击即可快速锁定或切换对比，底部还支持直接 **`-` / `+`** 实时增减预测格数 `X`。
- **动态流动虚线延伸 X 格**：从怪物当前位置与朝向出发，以**缓慢向前流动的动态虚线**延伸 **`X` 格**（`previewSteps` 默认 `12` 格，范围 `1–30`，支持热重载与弹窗实时调节）。
- **岔路口方向与概率展示**：
  - 岔路口起步 / 战后续走：按攻略一致的 **红箭 / 蓝箭** 配色标注方向与依据；
  - 半路遇岔多分支可能：同时延伸所有分支的动态虚线，并在各岔路出口展示**精确概率百分比**（如 `62.5%` / `37.5%`、`50%` / `50%`）；
  - 遭遇玩家交战提示：在路径经过存活玩家的地块上标出 **`⚔遇P1`** 交战节点，并将交战后的续行虚线自动切换为更柔和的半透明样式（支持配置 `postCombatMode`：`continue` / `stop` / `redBlue`）。
- **纯只读安全**：仅声明 `ReadGameState` 权限，不发送任何操作指令，不篡改游戏逻辑。

## 实现的怪物寻路规则

以 B 站大数据回放实测（`BV1Kxsq67E9C`）及 UP 主置顶全图一图流攻略为准：

1. **全图自适应红蓝优先级**：直接读取 `LandManager` 原始 `neighborLandIds` 邻接表顺序（`红 ＞ 蓝 ＞ 无`），零地图硬编码，天然兼容全部 PVE 地图。
2. **岔路口起步 / 岔路口战后续走（一步索敌）**：不看玩家站位，排除来路不倒车，按「有红走红、无红走蓝」走。
3. **半路遇到岔路口（排队 + 最短路抽签）**：
   - 依据实测规则「**之后再遇岔，看当时站位**」：计算到所有存活且可追踪玩家的全图最短路距离（**回头方向也算、不限距离**）；
   - 按距离由近到远排队，**同距按座次 `P1 → P4` 优先**；
   - 对排到的玩家在所有等长最短路里等概率抽签（每条最短路 = 1 支签）；
   - 抽到掉头则跳过并将该部分概率顺延给下一个人；全员掉头兜底则两个前进方向各 `50%`。
4. **特殊状态处理**：
   - 刚出生在岔路口的怪优先沿出生箭头（`FrontNodeIds`）走；
   - 支持追踪**垃圾箱（Z3000）**、**雪莉**、**玲玲**技能挪动怪物后的下一步走向规则。

## 安装方式（AstralParty.Toys）

1. 在 `AstralParty.Toys` 中确保已安装 **CesiumLoader**（SDK `>= 3.8.3`）。
2. 将打包好的 `MonsterPathPredictor-<version>.zip` 直接拖入 Toys 模组页导入，或手动解压至：
   ```text
   <游戏目录>/AstralParty_ModLoader/mods/MonsterPathPredictor/
   ├── MonsterPathPredictor.dll
   ├── MonsterPathPredictor.json
   └── config.json
   ```
3. 重启游戏生效。

## 配置项（`config.json`，支持热重载）

| 字段 | 默认值 | 说明 |
| --- | --- | --- |
| `enabled` | `true` | 是否启用模组 |
| `previewSteps` | `12` | 向前延伸预测的格数 `X`（范围 `1–30`） |
| `dashLength` | `12.0` | 虚线单段长度（像素） |
| `gapLength` | `8.0` | 虚线间隔长度（像素） |
| `dashSpeed` | `26.0` | 动态虚线缓慢前行的速度（像素/秒，`0` 为静态） |
| `lineWidth` | `4.5` | 虚线线条粗细（像素） |
| `heightOffset` | `0.45` | 路线相对地块的 3D 高度偏移 |
| `fontSize` | `18` | 岔路口概率与标签字号 |
| `clickRadius` | `28.0` | 点击模型外缘时的容差半径（像素；点在模型内直接命中） |
| `clearOnEmptyClick` | `true` | 点击空白处取消预览 |
| `showReasonTag` | `true` | 是否在岔路口显示简短判定依据 |
| `showDeterministicPercent` | `false` | 100% 确定方向是否也显示百分比（多分支始终显示） |
| `simulateCombatOnFork` | `true` | 路径经过站有玩家的岔路口时按战后续走（红蓝箭头）推演 |
| `postCombatMode` | `"continue"` | 遇敌后续行策略：`continue`（按攻略实测半透明续行）/ `stop`（首次遇敌即截断）/ `redBlue`（遇敌后后续岔路一律走红蓝） |
| `allowNonAggressiveMonsters` | `true` | 允许点击小偷等非主动攻击怪（顶部注明） |
| `pickerHotkey` | `"F8"` | 打开/关闭快速选怪弹窗的热键 |
| `closePickerOnSelect` | `false` | 在弹窗里选怪后是否自动关闭弹窗 |
| `clearHotkey` | `"Escape"` | 关闭弹窗或取消当前预览的快捷键 |
| `excludedPlayerBuffIds` | `""` | 额外排除出追踪名单的玩家 Buff ID（逗号分隔） |

## 本地开发与构建

### 运行单元测试（无需游戏 DLL）

```powershell
dotnet test tests/MonsterPathPredictor.Tests/MonsterPathPredictor.Tests.csproj -c Release
```

### 构建模组 DLL 并打包 ZIP

1. 将 `CesiumLoader.SDK.dll` 及游戏热更引用程序集（`AstralParty.Runtime.dll`、`UnityEngine.CoreModule.dll`、`UniTask.dll`、`Google.Protobuf.Runtime.dll`）放入项目根目录的 `refs/` 文件夹（已加入 `.gitignore`）。
2. 执行打包脚本：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/package.ps1
   ```
3. 输出包位于 `dist/MonsterPathPredictor-1.0.0.zip`，可直接导入 `AstralParty.Toys` 或提交到 `forge.hiynet.com`。

## 许可

[MIT License](LICENSE)
