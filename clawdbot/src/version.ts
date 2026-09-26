import { existsSync, readFileSync } from "fs";
import { dirname, resolve } from "path";
import { fileURLToPath } from "url";

/** The plugin's package version, read from its package.json (source and dist layouts differ in depth). */
export const PLUGIN_VERSION: string = findPackageVersion();

function findPackageVersion(): string {
  let dir = dirname(fileURLToPath(import.meta.url));
  for (;;) {
    const candidate = resolve(dir, "package.json");
    if (existsSync(candidate)) {
      const pkg = JSON.parse(readFileSync(candidate, "utf8")) as { name?: string; version?: string };
      if (pkg.name === "@repoql/repoql" && pkg.version) {
        return pkg.version;
      }
    }
    const parent = resolve(dir, "..");
    if (parent === dir) {
      return "0.0.0";
    }
    dir = parent;
  }
}
