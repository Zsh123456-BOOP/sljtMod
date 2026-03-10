# Sts2Mcp 桌宠接口规范（给外部 Codex）

本文档用于让“桌宠侧”快速接入 `Sts2McpProbe`，读取游戏状态并下发出牌指令。

## 1. 运行时目录

- Windows: `%LOCALAPPDATA%\Sts2McpProbe\`
- macOS（约定）: `~/Library/Application Support/Sts2McpProbe/`

## 2. 文件总览

- `state.json`: 主状态（玩家/敌人/手牌/动作空间/战斗历史/路线建议）
- `analytics.json`: 统计指标（当前战斗+总计+单机/联机拆分 + 团队贡献榜）
- `dashboard.json`: 一屏汇总（适合 UI 展示）
- `dictionary.json`: 英文稳定 ID -> 当前语言文本
- `status.json`: Mod 状态与错误信息
- `probe.log`: 调试日志
- `command.json`: 桌宠写入指令文件（出牌）
- `command.done.<timestamp>.json`: 已消费指令归档

## 3. 刷新频率与监听建议

- 状态刷新节奏：
  - `state.json / analytics.json / dashboard.json` 约每 `250ms` 刷新一次（战斗内）
  - 命令检查约每 `100ms` 一次
- 建议策略：
  - 轮询 `200~300ms` 或使用文件监听
  - 以 `state.json.TimestampUtc` 去重
  - 读取时若 JSON 解析失败，等待 `30~80ms` 重试（文件可能正在写入）

## 4. 出牌接口（command.json）

当前只支持 `play_card`：

```json
{
  "action": "play_card",
  "combat_card_index": 12,
  "target_combat_id": 3
}
```

字段来源：
- `combat_card_index`: 从 `state.ActionSpace.PlayCardActions[].CombatCardIndex`
- `target_combat_id`: 若 `RequiresTarget=true`，从 `ValidTargets[].CombatId` 选一个

执行后：
- 成功或失败会写入 `status.json`（如 `command_ok` / `command_rejected` / `command_error`）
- 指令文件会被改名归档到 `command.done.*.json`

## 5. 桌宠最小决策闭环

1. 读 `state.json`
2. 若 `ActionSpace.IsPlayerTurn=true` 且 `CurrentSide=="Player"` 且 `PlayableActionCount>0`
3. 从 `PlayCardActions` 里筛 `CanPlayNow=true`
4. 选一条动作并写入 `command.json`
5. 观察 `status.json` 与下一个 `state.json`（确认执行结果）

## 6. 推荐读取字段（桌宠对话 + 决策）

优先读取：
- `state.LocalPlayer.CurrentHp/MaxHp/Block/Energy/Stars`
- `state.LocalPlayer.Hand[]`
- `state.Enemies[]`（含 `Intents`）
- `state.ActionSpace.PlayCardActions[]`
- `state.RunContext`（楼层、房间、地图分叉）
- `state.CombatHistory.RecentEntries[]`

展示增强：
- `analytics.CurrentCombat / Total / Singleplayer / Multiplayer`
- `analytics.CurrentContributionBoard[]`（贡献分、直伤、辅伤、减伤、团队护盾、状态分）
- `dashboard.Summary / Alerts / FunInsights`

文本本地化：
- 决策层尽量使用稳定键：`CardId/IdEntry/ModelId`
- 显示层通过 `dictionary.json` 映射 `Title/Description`

## 7. 状态文件（status.json）常见 Phase

- `initialized`
- `dictionary_ready`
- `combat_ended`
- `command_ok`
- `command_rejected`
- `command_error`
- `dump_error`

## 8. TypeScript 接入示例（简化版）

```ts
import fs from "node:fs";
import path from "node:path";

const dir = path.join(process.env.LOCALAPPDATA!, "Sts2McpProbe");
const stateFile = path.join(dir, "state.json");
const cmdFile = path.join(dir, "command.json");

let lastTs = "";

setInterval(() => {
  let state: any;
  try {
    state = JSON.parse(fs.readFileSync(stateFile, "utf8"));
  } catch {
    return;
  }

  if (!state?.TimestampUtc || state.TimestampUtc === lastTs) return;
  lastTs = state.TimestampUtc;

  const as = state.ActionSpace;
  if (!as?.IsPlayerTurn || state.CurrentSide !== "Player") return;

  const playable = (as.PlayCardActions ?? []).filter((a: any) => a.CanPlayNow);
  if (playable.length === 0) return;

  const a = playable[0];
  const target = a.RequiresTarget ? a.ValidTargets?.[0]?.CombatId ?? null : null;

  const cmd = {
    action: "play_card",
    combat_card_index: a.CombatCardIndex,
    target_combat_id: target
  };

  // 避免覆盖正在处理的指令
  if (!fs.existsSync(cmdFile)) {
    fs.writeFileSync(cmdFile, JSON.stringify(cmd, null, 2), "utf8");
  }
}, 250);
```

## 9. 与 Sts2DecisionEngine 的关系（可选）

- `Sts2McpProbe`：数据采集 + 指令执行（游戏内）
- `Sts2DecisionEngine`：外部策略建议（可输出 `decision_engine/advice.json`）

桌宠可以：
- 直接基于 `state.json` 决策
- 或读取 `advice.json` 作为建议，再二次确认后写 `command.json`
