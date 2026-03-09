import fs from "node:fs";

export function readJsonFile<T>(filePath: string): T | null {
  try {
    if (!fs.existsSync(filePath)) {
      return null;
    }
    const text = fs.readFileSync(filePath, "utf8");
    return JSON.parse(text) as T;
  } catch {
    return null;
  }
}

export function writeJsonFileAtomic(filePath: string, payload: unknown): void {
  const tmp = `${filePath}.tmp`;
  const text = JSON.stringify(payload, null, 2);
  fs.writeFileSync(tmp, text, "utf8");
  fs.renameSync(tmp, filePath);
}

export function appendNdjson(filePath: string, payload: unknown): void {
  const line = `${JSON.stringify(payload)}\n`;
  fs.appendFileSync(filePath, line, "utf8");
}
