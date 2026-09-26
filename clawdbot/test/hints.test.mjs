// Unit tests for the hint plumbing's pure parts: which file a tool call read
// or wrote, and how a hint joins a tool result. Run with `npm test`.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  append,
  countConcepts,
  readTarget,
  resultText,
  shellReadTarget,
  shellWords,
  writeTargets,
} from "../dist/src/hints.js";

const root = mkdtempSync(join(tmpdir(), "repoql-hints-"));
mkdirSync(join(root, "src"));
const file = join(root, "src", "Invoice.cs");
writeFileSync(file, "class Invoice {}\n");

test("a Codex sed read targets the file it printed", () => {
  assert.equal(shellReadTarget({ command: `sed -n '1,240p' ${file}` }, "/elsewhere"), file);
});

test("a relative read resolves against the tool's working directory", () => {
  assert.equal(shellReadTarget({ command: "cat src/Invoice.cs" }, root), file);
  assert.equal(readTarget({ path: "src/Invoice.cs" }, root), file);
});

test("the first stage of a numbered read pipeline names the file", () => {
  assert.equal(shellReadTarget({ command: `nl -ba ${file} | sed -n '1,40p'` }, root), file);
});

test("searches, builds, and redirections have no single read target", () => {
  assert.equal(shellReadTarget({ command: `rg Invoice ${root}` }, root), "");
  assert.equal(shellReadTarget({ command: "dotnet build" }, root), "");
  assert.equal(shellReadTarget({ command: `cat ${file} > copy.cs` }, root), "");
  assert.equal(shellReadTarget({ command: `cat ${file}; rm -rf x` }, root), "");
});

test("a read of a path that is not a file has no target", () => {
  assert.equal(shellReadTarget({ command: `cat ${join(root, "missing.cs")}` }, root), "");
  assert.equal(shellReadTarget({ command: `cat ${root}` }, root), "");
});

test("apply_patch targets come from OpenClaw's input and Codex's command alike", () => {
  const patch = [
    "*** Begin Patch",
    "*** Update File: src/Invoice.cs",
    "*** Add File: /abs/New.cs",
    "*** Delete File: src/Old.cs",
    "*** Update File: src/Moved.cs",
    "*** Move to: src/Renamed.cs",
    "*** End Patch",
  ].join("\n");
  const expected = [join(root, "src/Invoice.cs"), "/abs/New.cs", join(root, "src/Moved.cs"), join(root, "src/Renamed.cs")];
  assert.deepEqual(writeTargets({ input: patch }, root), expected);
  assert.deepEqual(writeTargets({ command: patch }, root), expected);
});

test("write and edit target their path", () => {
  assert.deepEqual(writeTargets({ path: "src/Invoice.cs" }, root), [file]);
  assert.deepEqual(writeTargets({ file_path: file }, "/elsewhere"), [file]);
});

test("a hint joins the result as one more text block and keeps details", () => {
  const result = { content: [{ type: "text", text: "body" }], details: { tool: "read" } };
  const joined = append(result, "hint");
  assert.deepEqual(joined.content, [
    { type: "text", text: "body" },
    { type: "text", text: "hint" },
  ]);
  assert.deepEqual(joined.details, { tool: "read" });
  assert.equal(resultText(joined), "body\nhint");
});

test("concepts count by their unindented lines, not their why lines", () => {
  const rendered = "concept:///a.md\tA\n  why: because\nconcept:///b.md\tB";
  assert.equal(countConcepts(rendered), 2);
});

test("shell words honour quotes", () => {
  assert.deepEqual(shellWords(`sed -n '1,80p' "a b.cs"`), ["sed", "-n", "1,80p", "a b.cs"]);
});
