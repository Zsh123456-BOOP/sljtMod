import fs from "node:fs";
import os from "node:os";
import path from "node:path";

function getProbeDir(): string {
  const home = os.homedir();
  switch (process.platform) {
    case "win32": {
      const localAppData =
        process.env.LOCALAPPDATA ?? path.join(home, "AppData", "Local");
      return path.join(localAppData, "Sts2McpProbe");
    }
    case "darwin":
      return path.join(home, "Library", "Application Support", "Sts2McpProbe");
    default:
      return path.join(home, ".local", "share", "Sts2McpProbe");
  }
}

function ensureDir(dir: string): void {
  fs.mkdirSync(dir, { recursive: true });
}

const probeDir = getProbeDir();
const engineDir = path.join(probeDir, "decision_engine");
ensureDir(engineDir);

export const Paths = {
  probeDir,
  stateJson: path.join(probeDir, "state.json"),
  dictionaryJson: path.join(probeDir, "dictionary.json"),
  statusJson: path.join(probeDir, "status.json"),
  commandJson: path.join(probeDir, "command.json"),
  adviceJson: path.join(engineDir, "advice.json"),
  weightsJson: path.join(engineDir, "weights.json"),
  decisionsLog: path.join(engineDir, "decisions.ndjson"),
  trainingLog: path.join(engineDir, "training.ndjson"),
  strengthReportJson: path.join(engineDir, "strength_report.json")
};

export function fileExists(filePath: string): boolean {
  try {
    return fs.existsSync(filePath);
  } catch {
    return false;
  }
}
