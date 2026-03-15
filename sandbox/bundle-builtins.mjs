#!/usr/bin/env node

import { build } from "esbuild";
import { mkdir, stat } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const sandboxRoot = fileURLToPath(new URL(".", import.meta.url));
const outputRoot = path.join(sandboxRoot, "wasm", "dist", "modules");

const libraries = [
  { name: "yaml", packageName: "js-yaml" },
  { name: "toml", packageName: "smol-toml" },
  { name: "json5", packageName: "json5" },
  { name: "xml", packageName: "txml" },
  { name: "ini", packageName: "ini" },
  { name: "semver", packageName: "semver" },
  { name: "diff", packageName: "diff" },
  { name: "microdiff", packageName: "microdiff" },
  { name: "ohash", packageName: "ohash" },
  { name: "fuse", packageName: "fuse.js" },
  { name: "ignore", packageName: "ignore" },
  { name: "base64", packageName: "js-base64" },
  { name: "dayjs", packageName: "dayjs" },
  { name: "change-case", packageName: "change-case" },
  { name: "mustache", packageName: "mustache" },
  { name: "radash", packageName: "radash" },
  { name: "picomatch", packageName: "picomatch" },
  { name: "toposort", packageName: "toposort" },
  { name: "front-matter", packageName: "front-matter" },
  { name: "parse-diff", packageName: "parse-diff" }
];

const builtinModuleNames = new Set([
  "assert",
  "buffer",
  "child_process",
  "cluster",
  "console",
  "constants",
  "crypto",
  "dgram",
  "diagnostics_channel",
  "dns",
  "domain",
  "events",
  "fs",
  "fs/promises",
  "http",
  "http2",
  "https",
  "inspector",
  "module",
  "net",
  "os",
  "path",
  "path/posix",
  "path/win32",
  "perf_hooks",
  "process",
  "punycode",
  "querystring",
  "readline",
  "repl",
  "stream",
  "stream/consumers",
  "stream/promises",
  "stream/web",
  "string_decoder",
  "sys",
  "timers",
  "timers/promises",
  "tls",
  "tty",
  "url",
  "util",
  "util/types",
  "v8",
  "vm",
  "wasi",
  "worker_threads",
  "zlib"
]);

const builtinShimPlugin = {
  name: "repoql-node-builtin-shims",
  setup(pluginBuild) {
    pluginBuild.onResolve({ filter: /^(node:)?[^/].*$/ }, args => {
      const normalized = normalizeBuiltin(args.path);
      if (!normalized) {
        return null;
      }

      return {
        path: normalized,
        namespace: "repoql-node-builtin"
      };
    });

    pluginBuild.onLoad({ filter: /.*/, namespace: "repoql-node-builtin" }, args => ({
      contents: createBuiltinShim(args.path),
      loader: "js"
    }));
  }
};

await mkdir(outputRoot, { recursive: true });

const results = [];

for (const library of libraries) {
  const outputFile = path.join(outputRoot, `${library.name}.mjs`);

  try {
    await build({
      stdin: {
        contents: createEntryModule(library.packageName),
        resolveDir: sandboxRoot,
        sourcefile: `${library.name}.entry.mjs`,
        loader: "js"
      },
      outfile: outputFile,
      bundle: true,
      format: "esm",
      platform: "neutral",
      target: ["es2020"],
      minify: true,
      treeShaking: true,
      legalComments: "none",
      mainFields: ["module", "main"],
      conditions: ["import", "module", "default"],
      logLevel: "silent",
      charset: "utf8",
      plugins: [builtinShimPlugin]
    });

    const { size } = await stat(outputFile);
    results.push({ ...library, ok: true, size });
    console.log(`OK   ${library.name.padEnd(12)} ${formatBytes(size)}`);
  } catch (error) {
    const message = formatBuildError(error);
    results.push({ ...library, ok: false, message });
    console.error(`FAIL ${library.name.padEnd(12)} ${message}`);
  }
}

const totalSize = results
  .filter(result => result.ok)
  .reduce((sum, result) => sum + result.size, 0);

console.log("");
console.log(
  `Bundled ${results.filter(result => result.ok).length}/${libraries.length} libraries, total size ${formatBytes(totalSize)}`
);

const failures = results.filter(result => !result.ok);
if (failures.length > 0) {
  process.exitCode = 1;
}

function createEntryModule(packageName) {
  return [
    `import * as moduleNamespace from ${JSON.stringify(packageName)};`,
    `export * from ${JSON.stringify(packageName)};`,
    "const defaultExport = Object.prototype.hasOwnProperty.call(moduleNamespace, 'default')",
    "  ? moduleNamespace.default",
    "  : moduleNamespace;",
    "export default defaultExport;"
  ].join("\n");
}

function normalizeBuiltin(specifier) {
  const normalized = specifier.startsWith("node:")
    ? specifier.slice("node:".length)
    : specifier;

  return builtinModuleNames.has(normalized) ? normalized : null;
}

function createBuiltinShim(moduleName) {
  const message = JSON.stringify(`Node builtin "${moduleName}" is not available in the RepoQL QuickJS sandbox.`);

  if (moduleName === "buffer") {
    return `
const unavailable = () => {
  throw new Error(${message});
};
const Buffer = {
  from: unavailable,
  alloc: unavailable,
  allocUnsafe: unavailable,
  isBuffer: () => false
};
export { Buffer };
export default { Buffer };
`;
  }

  if (moduleName === "process") {
    return `
const processShim = {
  env: Object.create(null),
  argv: [],
  cwd: () => "/",
  nextTick: callback => {
    if (typeof callback === "function") {
      callback();
    }
  }
};
export default processShim;
export const env = processShim.env;
export const argv = processShim.argv;
export const cwd = processShim.cwd;
export const nextTick = processShim.nextTick;
`;
  }

  return `
const fail = () => {
  throw new Error(${message});
};
const handler = {
  get(_target, property) {
    if (property === "then") {
      return undefined;
    }

    if (property === Symbol.toPrimitive) {
      return () => "";
    }

    return stub;
  },
  apply: fail,
  construct: fail
};
const stub = new Proxy(function () {}, handler);
export default stub;
export const stubValue = stub;
export const createRequire = fail;
export const pathToFileURL = fail;
export const fileURLToPath = fail;
export const EventEmitter = class {};
export const Readable = class {};
export const Writable = class {};
export const Duplex = class {};
export const Transform = class {};
export const PassThrough = class {};
export const Buffer = {
  from: fail,
  alloc: fail,
  allocUnsafe: fail,
  isBuffer: () => false
};
export const process = {
  env: Object.create(null),
  argv: [],
  cwd: () => "/",
  nextTick: callback => {
    if (typeof callback === "function") {
      callback();
    }
  }
};
`;
}

function formatBuildError(error) {
  if (Array.isArray(error?.errors) && error.errors.length > 0) {
    return error.errors
      .map(buildError => {
        const location = buildError.location
          ? `${buildError.location.file}:${buildError.location.line}:${buildError.location.column}: `
          : "";
        return `${location}${buildError.text}`;
      })
      .join(" | ");
  }

  return error instanceof Error ? error.message : String(error);
}

function formatBytes(bytes) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const kib = bytes / 1024;
  if (kib < 1024) {
    return `${kib.toFixed(1)} KiB`;
  }

  return `${(kib / 1024).toFixed(2)} MiB`;
}
