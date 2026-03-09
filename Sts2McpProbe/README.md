# Sts2McpProbe

最小探针 Mod（C#）：
- 导出战斗状态到 `state.json`
- 导出运行状态到 `status.json`
- 导出全局字典到 `dictionary.json`（卡牌/遗物/药水/能力/怪物/角色）
- 读取 `command.json`（`play_card`）并尝试出牌

## 1) 构建

在游戏根目录执行：

```powershell
.\ModDev\Sts2McpProbe\build.ps1 -Configuration Release
```

构建后会生成：
- `mods\Sts2Mcp\Sts2Mcp.dll`
- `mods\Sts2Mcp\PckRoot\mod_manifest.json`
- `mods\Sts2Mcp\Sts2Mcp.pck`

## 2) 运行时输出目录

探针输出写在：

`%LOCALAPPDATA%\Sts2McpProbe\`

主要文件：
- `status.json`
- `state.json`
- `dictionary.json`
- `analytics.json`（当前战斗 / 总计 / 单机 / 联机 / 分场统计）
- `dashboard.json`（一屏仪表盘汇总）
- `probe.log`

同时会在游戏内创建可拖拽 Overlay（默认开启）：
- 小尺寸半透明面板，不遮挡主画面
- 可拖拽移动、可折叠
- 可切换面板：`战斗` / `总计` / `路线` / `日志`
- 快捷键：`F8` 显示/隐藏

## 3) 命令文件格式

把命令写到 `%LOCALAPPDATA%\Sts2McpProbe\command.json`：

```json
{
  "action": "play_card",
  "combat_card_index": 12,
  "target_combat_id": 3
}
```

说明：
- `combat_card_index` 来自 `state.json` 中手牌的 `CombatCardIndex`
- `target_combat_id` 可为空；单体牌建议传敌人 `combatId`

命令处理后会归档成 `command.done.<timestamp>.json`。

## 4) 字典与多语言建议

建议外部逻辑一律用英文稳定键（`IdEntry` / `ModelId`），显示层再查字典：

- `state.json`：
  - 手牌卡牌键：`IdEntry`
  - 敌人键：`ModelId`
  - 玩家职业键：`CharacterId`
- `dictionary.json`：
  - `Cards` / `Relics` / `Potions` / `Powers` / `Monsters` / `Characters`
  - key 是稳定英文 ID，value 是当前游戏语言下的显示文本与补充信息

这样你可以让 LLM 和自动出牌层用英文键交互，同时在 UI 对话层展示中文。

## 5) `state.json` 新增结构（LLM 决策 + 统计）

为了支持“边打边聊 + 稳定决策”，`state.json` 现在还包含：

- `LocalPlayer.Piles`
  - `Draw` / `Hand` / `Discard` / `Exhaust` / `Play`
  - 每个牌堆都含 `Count` 与 `Cards`
- `ActionSpace`
  - `SupportedActions`（当前为 `play_card`）
  - `PlayCardActions`：每张手牌是否可打、不可打原因、可选目标、示例命令
- `RunContext`
  - `CurrentActId` / `ActFloor` / `TotalFloor` / `CurrentRoomType`
  - `CurrentMapCoord` / `CurrentMapPointType` / `NextMapChoices`
  - `Modifiers` / `SeedString` / `Encounter`
- `CombatHistory`
  - 最近事件流（默认最多 80 条）
  - 每条含 `EntryType`、`Description`、`Details`
- `Analytics`
  - `CurrentCombat`：当前战斗统计
  - `Total`：累计统计
  - `Singleplayer` / `Multiplayer`：按模式拆分累计与近期战斗
  - `RecentBattles`：分场战斗摘要
  - `Highlights`：危险度、节奏、MVP 卡等
- `RoutePlan`
  - 当前地图分叉评分、风险标签、建议路线与理由
  - 历史路线统计（问号/小怪/精英/商店/休息等）：
  - 各类型次数、占比、均掉血、均回合、均收益评分
  - 自动给出“最多类型 / 最优类型 / 最亏类型”

## 6) `analytics.json` 指标说明

覆盖你要求的核心项：

- 伤害 / 格挡 / 能量 / 过量伤害
- 卡牌使用率（按卡牌统计 `Played` + `UsageRate`）
- 卡牌能量效率（`DamagePerEnergy`）
- Buff / Debuff 施加统计
- 战斗日志（最近事件简表）
- 当前战斗 / 总计 / 每场分片（`RecentBattles`）
- 单机与联机分通道统计（`Singleplayer`、`Multiplayer`）
- 路线历史统计（`RouteHistory`）：
- 哪类节点走得最多、哪类历史收益最好/最差

## 7) `dashboard.json`（一屏看全部）

用于桌宠或前端直接渲染：

- `Summary`：核心一句话概览
- `Alerts`：风险提示（如致死风险、能量效率低）
- `CurrentCombat` / `Total` / `SingleplayerTotal` / `MultiplayerTotal`
- `RecentBattles`（最近 10 场）
- `RecentCombatLog`（最近 30 条）
- `RoutePlan`（路线建议）
- `RouteHistory`（路线历史统计榜）
- `FunInsights`（趣味洞察）

## 8) 游戏内 Overlay

设计目标：
- 优先服务“游戏内看数据”，桌宠只是并行消费者
- 不侵入、低干扰（小面板 + 半透明 + 可拖拽）
- 同时保留文件监听给桌宠（`state.json` / `analytics.json` / `dashboard.json`）

面板说明：
- `战斗`：当前血量、能量、来伤、伤害/格挡/效率、风险提醒
- `总计`：累计统计、单机/联机拆分、卡牌使用率 Top
- `路线`：当前分叉评分、建议路线、风险标签
- `路线`：额外显示历史统计榜（最多/最优/最亏）与最近路径
- `日志`：最近战斗事件流（可快速复盘）

## 9) 关键注意

游戏内置 Mod 加载器要求同名的：
- `Sts2Mcp.pck`
- `Sts2Mcp.dll`

`build.ps1` 已自动完成 `pck` 打包（调用 `SlayTheSpire2.exe --headless --script`）。

## 10) 策略引擎（无模型）

如果你要做“规则 + 打分 + 在线权重更新”的自动决策，请看：

`..\Sts2DecisionEngine\README.md`
