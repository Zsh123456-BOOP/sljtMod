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
- `probe.log`

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

## 5) `state.json` 新增结构（LLM 决策）

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

## 6) 关键注意

游戏内置 Mod 加载器要求同名的：
- `Sts2Mcp.pck`
- `Sts2Mcp.dll`

`build.ps1` 已自动完成 `pck` 打包（调用 `SlayTheSpire2.exe --headless --script`）。

## 7) 策略引擎（无模型）

如果你要做“规则 + 打分 + 在线权重更新”的自动决策，请看：

`..\Sts2DecisionEngine\README.md`
