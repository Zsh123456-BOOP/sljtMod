# Sts2DecisionEngine

无模型（No-Model）策略引擎，基于你当前 `Sts2McpProbe` 导出的 `state.json` 做：

- 规则过滤 + 线性打分决策（`action_space` 内选动作）
- 目标级候选评分（同一张牌对不同目标单独评分）
- 每步记录 `state/action/score/reward`
- 轻量在线更新权重（持续迭代）
- 生成 `advice.json` 给桌宠/LLM 做高价值解释

## 1) 设计目标

- 可迁移：Windows / macOS 都可跑
- 可控：默认不自动出牌，只输出建议
- 可迭代：不用训练大模型，直接在线调权重
- 可解释：每次建议都能给出特征贡献和理由
- 可迁移：策略核心已抽离到 `src/pet-core`，可直接搬到桌宠仓库

## 2) 代码分层（建议迁移到桌宠）

- `src/pet-core/*`：纯算法层（无文件系统依赖）
  - `metrics.ts`: 局势统计（来伤估计、状态指纹）
  - `features.ts`: 特征提取
  - `policy.ts`: 候选动作生成 + 目标级评分 + 规则惩罚
- `src/engine.ts`：STS2 适配层（读写 JSON、循环、日志、在线更新）

这样可以做到：
- Mod 只做采集；
- 桌宠代码承载“策略卖点”；
- 同一策略内核可复用于不同游戏适配器。

## 3) 目录与输出

运行后会读写 `Sts2McpProbe` 目录：

- 输入：`state.json`
- 可选输出命令：`command.json`（仅 `autoplay` 模式）
- 输出建议：`decision_engine/advice.json`
- 权重文件：`decision_engine/weights.json`
- 决策日志：`decision_engine/decisions.ndjson`
- 训练日志：`decision_engine/training.ndjson`
- 强度报告：`decision_engine/strength_report.json`

跨平台路径自动识别：

- Windows: `%LOCALAPPDATA%\\Sts2McpProbe`
- macOS: `~/Library/Application Support/Sts2McpProbe`

## 4) 决策闭环（无模型）

1. 从 `state.json` 读取局势与 `ActionSpace.PlayCardActions`。  
2. 仅在合法动作里打分（`CanPlayNow=true`）。  
3. 选 Top1（或给出 Top3 建议）。  
4. 执行后等待下一帧状态，计算转移奖励 `R`。  
5. 在线更新权重 `w := w*(1-l2) + lr*R*x`。  
6. 长期累积日志，持续稳定提升。

## 5) 奖励函数（当前实现）

当前默认是工程化启发式：

- `enemy_hp_delta * 0.6`
- `hp_loss * -1.4`
- `block_gain * 0.12`
- `energy_spent * 0.08`
- `combat_end_bonus`（战斗结束时加减分）

这不是最优公式，但足够做第一阶段在线迭代。

## 6) 算法强度到底够不够？

当前版本是“中等强度工程策略器”，优点与上限都很明确：

- 优点：
  - 合法动作空间内决策，稳定性高于纯 LLM。
  - 高压场景有硬规则兜底（致死威胁偏防守）。
  - 支持在线学习，权重可随数据收敛。
  - 目标级评分（同卡不同目标可区分）。
- 局限：
  - 没有完整前瞻模拟，仍是单步近似最优。
  - 对卡牌真实效果理解不完整（依赖导出特征，不是规则解释器）。
  - 奖励函数是启发式，不等价于最终胜率。

建议判断标准不是“像顶尖玩家”，而是：
- 比随机/固定规则明显更好；
- 同局势决策更一致；
- 近期 reward 趋势向上；
- 实战胜率随时间提升。

## 7) 运行方式

在本目录执行：

```bash
npm install
```

### 建议模式（默认，不出牌）

```bash
npm run start
```

或显式：

```bash
STS2_ENGINE_MODE=advise npm run start
```

Windows PowerShell:

```powershell
$env:STS2_ENGINE_MODE="advise"; npm run start
```

### 自动出牌模式（会写 command.json）

```bash
STS2_ENGINE_MODE=autoplay npm run start
```

Windows PowerShell:

```powershell
$env:STS2_ENGINE_MODE="autoplay"; npm run start
```

## 8) 强度评估命令

运行后可直接生成算法强度报告：

```bash
npm run analyze
```

它会读取 `decisions.ndjson` 和 `training.ndjson`，输出：

- 平均/近期 reward
- 正奖励比例
- 决策一致性（同状态指纹下 top1 一致率）
- 自动结论（弱 / 中等 / 中强）

## 9) 桌宠集成建议（高价值回答）

桌宠不直接“脑补决策”，而是消费这三类数据：

- `state.json`：事实状态
- `advice.json`：本地策略器建议 + 理由
- `dictionary.json`：英文 ID -> 中文可读文案

推荐回答模板（每回合）：

1. 局势：血量、能量、来伤、敌人数。  
2. 建议动作 Top1/Top2：卡牌 + 目标 + 理由。  
3. 风险提示：如果不做防御，最坏会损失多少。  
4. 可执行命令：直接展示 `command` JSON（可一键下发）。

这样桌宠至少具备“解释 + 建议 + 执行”价值，而不是纯闲聊。

## 10) 为什么不需要大模型训练

- 你不是在做自然语言生成为主，而是做战斗策略优化。  
- 这类问题先用规则 + 线性权重 + 在线更新，成本最低且收敛快。  
- 等日志积累大了，再考虑加小型 reranker（不是必须）。

## 11) 下一步建议

1. 增加动作类型：`end_turn`、`use_potion`。  
2. 增加战斗分段权重：前期/斩杀期/高压期。  
3. 用离线回放对比多组权重，挑胜率更高的版本。  
4. 桌宠按“解释优先”，执行前先确认，避免误触自动出牌。
