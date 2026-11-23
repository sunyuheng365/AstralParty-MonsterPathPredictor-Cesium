# MonsterPathPredictor（怪物寻路预测）

面向《吉星派对》（Astral Party）**CesiumLoader / AstralParty.Toys** 生态的纯只读 PVE 蹲怪辅助模组。

## 核心效果

- **点击选中怪物**：在 PVE 对局中左键点击任意怪物，即从该怪物当前位置与朝向出发，以**虚线**向前延伸规划 **`X` 格**路径（`X` 可在 `config.json` 中自由配置，默认 `12` 格，支持热重载）。
- **岔路口方向与概率**：
  - 唯一确定方向：绘制高对比度虚线 + 方向箭头（并可附带判定依据标签，如 `起步·红箭`、`追P1(10格)`）。
  - 多分支可能：同时延伸所有可能分支的虚线，并在每个岔路出口展示**精确概率百分比**（如 `62.5%` / `37.5%`、`50%` / `50%`）。
- **实时联动**：选中怪物后，随着玩家移动，路径和岔路口概率会实时更新；再次点击同一怪物、点击棋盘空白处或按 `Esc` 即可取消预览。
- **纯只读安全**：仅声明 `ReadGameState` 权限，不发送任何操作指令，不篡改游戏逻辑。

## 实现的怪物寻路规则

以 B 站大数据回放实测（`BV1Kxsq67E9C`）为准，并整合 3.3.0+ 地图机制：

1. **全图自适应红蓝优先级**：直接读取 `LandManager` 原始 `neighborLandIds` 邻接表顺序（`红 ＞ 蓝 ＞ 无`），零地图硬编码，天然兼容森秋幻境及全部 PVE 地图。
2. **岔路口起步 / 岔路口战后续走（一步索敌）**：不看玩家站位，排除来路不倒车，按「有红走红、没红走蓝」走。
3. **半路遇到岔路口（排队 + 最短路抽签）**：
   - 计算到所有存活且可追踪玩家的全图最短路距离（**回头方向也算、不限距离**）；
   - 按距离由近到远排队，**同距按座次 `P1 → P4` 优先**；
   - 对排到的玩家在所有等长最短路里等概率抽签（每条最短路 = 1 支签）；
   - 抽到掉头则跳过并将该部分概率顺延给下一个人；全员掉头兜底则两个前进方向各 `50%`。
   - 精确支持环路多条最短路概率计算（如森秋幻境 23 号岔路 `62.5% / 37.5%`）。
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
| `lineWidth` | `4.5` | 虚线线条粗细（像素） |
| `heightOffset` | `0.45` | 路线相对地块的 3D 高度偏移 |
| `fontSize` | `18` | 岔路口概率与标签字号 |
| `clickRadius` | `48.0` | 点击选中怪物的命中半径（像素） |
| `clearOnEmptyClick` | `true` | 点击空白处取消预览 |
| `showReasonTag` | `true` | 是否在岔路口显示简短判定依据 |
| `showDeterministicPercent` | `false` | 100% 确定方向是否也显示百分比（多分支始终显示） |
| `simulateCombatOnFork` | `true` | 路径穿过站有玩家的岔路口时按战后续走推演 |
| `allowNonAggressiveMonsters` | `true` | 允许点击小偷等非主动攻击怪（顶部注明） |
| `clearHotkey` | `"Escape"` | 取消预览快捷键 |
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
   pwsh -File scripts/package.ps1
   ```
   （或 `powershell -ExecutionPolicy Bypass -File scripts/package.ps1`）
3. 输出包位于 `dist/MonsterPathPredictor-1.0.0.zip`，可直接导入 `AstralParty.Toys` 或提交到 `forge.hiynet.com`。

## 许可

[MIT License](LICENSE)
