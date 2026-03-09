import fs from "node:fs";
import { writeJsonFileAtomic } from "./io.js";
import { Paths, fileExists } from "./paths.js";

interface DecisionLogRow {
  stateFingerprint?: string;
  playableActionCount?: number;
  top1?: {
    cardId?: string;
    targetCombatId?: number | null;
    score?: number;
  } | null;
}

interface TrainingLogRow {
  reward?: number;
  rewardComponents?: Record<string, number>;
}

function readNdjson<T>(filePath: string): T[] {
  if (!fileExists(filePath)) {
    return [];
  }
  try {
    const text = fs.readFileSync(filePath, "utf8");
    return text
      .split(/\r?\n/)
      .filter((line) => line.trim().length > 0)
      .map((line) => JSON.parse(line) as T);
  } catch {
    return [];
  }
}

function mean(values: number[]): number {
  if (values.length <= 0) {
    return 0;
  }
  return values.reduce((a, b) => a + b, 0) / values.length;
}

function computeConsistency(rows: DecisionLogRow[]): {
  repeatedStateCount: number;
  consistency: number;
} {
  const grouped = new Map<string, string[]>();
  for (const row of rows) {
    const fp = row.stateFingerprint;
    if (!fp || !row.top1?.cardId) {
      continue;
    }
    const actionKey = `${row.top1.cardId}::${row.top1.targetCombatId ?? "none"}`;
    const arr = grouped.get(fp) ?? [];
    arr.push(actionKey);
    grouped.set(fp, arr);
  }

  let repeatedStateCount = 0;
  let weightedSum = 0;
  let weightedTotal = 0;
  for (const actions of grouped.values()) {
    if (actions.length < 2) {
      continue;
    }
    repeatedStateCount += 1;
    const freq = new Map<string, number>();
    for (const action of actions) {
      freq.set(action, (freq.get(action) ?? 0) + 1);
    }
    const maxCount = Math.max(...freq.values());
    const ratio = maxCount / actions.length;
    weightedSum += ratio * actions.length;
    weightedTotal += actions.length;
  }

  return {
    repeatedStateCount,
    consistency: weightedTotal > 0 ? weightedSum / weightedTotal : 0
  };
}

function verdict(samples: number, rewardMean: number, consistency: number): string {
  if (samples < 40) {
    return "弱（数据量不足，不能判断算法强度）";
  }
  if (rewardMean > 0.25 && consistency > 0.82) {
    return "中强（稳定且有正收益趋势）";
  }
  if (rewardMean > 0 && consistency > 0.7) {
    return "中等（可用，但仍需要持续调权重）";
  }
  return "偏弱（建议优化特征与奖励设计）";
}

function main(): void {
  const decisions = readNdjson<DecisionLogRow>(Paths.decisionsLog);
  const training = readNdjson<TrainingLogRow>(Paths.trainingLog);
  const rewards = training
    .map((row) => Number(row.reward ?? 0))
    .filter((value) => Number.isFinite(value));
  const recentRewards = rewards.slice(-200);
  const positiveRewardRatio =
    rewards.length > 0
      ? rewards.filter((value) => value > 0).length / rewards.length
      : 0;

  const playableRows = decisions.filter(
    (row) => Number(row.playableActionCount ?? 0) > 0
  );
  const consistencyInfo = computeConsistency(playableRows);
  const report = {
    generatedAtUtc: new Date().toISOString(),
    files: {
      decisionsLog: Paths.decisionsLog,
      trainingLog: Paths.trainingLog
    },
    stats: {
      decisions: decisions.length,
      playableDecisionStates: playableRows.length,
      trainingSamples: training.length,
      rewardMeanAll: mean(rewards),
      rewardMeanRecent200: mean(recentRewards),
      positiveRewardRatio,
      repeatedStateCount: consistencyInfo.repeatedStateCount,
      decisionConsistency: consistencyInfo.consistency
    },
    verdict: verdict(
      training.length,
      mean(recentRewards.length > 0 ? recentRewards : rewards),
      consistencyInfo.consistency
    ),
    suggestions: [
      "训练样本小于 100 时，优先增加对局数据而非改复杂模型。",
      "若决策一致性低于 0.7，先减少随机性并加强硬规则。",
      "若 rewardMeanRecent200 为负，优先调低进攻特征权重、提高防守与来伤相关权重。"
    ]
  };

  writeJsonFileAtomic(Paths.strengthReportJson, report);
  console.log("[Sts2DecisionEngine] strength report generated:");
  console.log(JSON.stringify(report, null, 2));
}

main();
