// Unit tests for the hint plumbing's pure parts: which file a tool call read
// or wrote, how a target is addressed, how a hint joins a tool result, and how
// Codex-held concepts are kept per run. Run with `npm test`.
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";
import {
  HeldConcepts,
  append,
  codexPatchTargets,
  literalTarget,
  nativeResponseText,
  pipelineStages,
  readTarget,
  resultText,
  shellReadTarget,
  writeTargets,
} from "../dist/src/hints.js";

const root = mkdtempSync(join(tmpdir(), "repoql-hints-"));
mkdirSync(join(root, "src"));
mkdirSync(join(root, "app", "[slug]"), { recursive: true });
const a = join(root, "src", "a.cs");
const b = join(root, "src", "b.cs");
const readme = join(root, "src", "README.md");
const page = join(root, "app", "[slug]", "page.tsx");
for (const file of [a, b, readme, join(root, "README.md"), page]) {
  writeFileSync(file, "content\n");
}

// --- shell reads ---

test("a Codex sed read targets the file it printed", () => {
  assert.equal(shellReadTarget({ command: `sed -n '1,240p' ${a}` }, "/elsewhere"), a);
});

test("a relative read resolves against the tool's working directory", () => {
  assert.equal(shellReadTarget({ command: "cat src/a.cs" }, root), a);
  assert.equal(readTarget({ path: "src/a.cs" }, root), a);
});

test("exec's workdir moves where a relative path resolves", () => {
  assert.equal(shellReadTarget({ command: "cat README.md", workdir: "src" }, root), readme);
});

test("a numbered read cut to a line range still names the file", () => {
  assert.equal(shellReadTarget({ command: `nl -ba ${a} | sed -n '1,40p'` }, root), a);
  assert.equal(shellReadTarget({ command: `cat ${a} | head -n 20` }, root), a);
});

test("a pipeline stage that transforms the content has no target", () => {
  assert.equal(shellReadTarget({ command: "cat src/a.cs | grep foo" }, root), "");
  assert.equal(shellReadTarget({ command: "cat src/a.cs | sed 's/a/b/'" }, root), "");
});

test("a read of several files has no single target", () => {
  assert.equal(shellReadTarget({ command: "cat src/a.cs src/b.cs" }, root), "");
});

test("a | inside a quoted sed script is not a pipe", () => {
  assert.equal(shellReadTarget({ command: "sed -n '/a|b/p' src/a.cs" }, root), a);
});

test("chains, redirections, substitutions, and escapes have no target", () => {
  for (const command of [
    "rg Invoice src",
    "dotnet build",
    "cat src/a.cs > copy.cs",
    "cat src/a.cs; rm -rf x",
    "cat $(echo src/a.cs)",
    'cat "$HOME/a.cs"',
    "cat src/a.cs && true",
    "cat src/a\\ b.cs",
  ]) {
    assert.equal(shellReadTarget({ command }, root), "", command);
  }
});

test("a read of a path that is not a file has no target", () => {
  assert.equal(shellReadTarget({ command: "cat src/missing.cs" }, root), "");
  assert.equal(shellReadTarget({ command: "cat src" }, root), "");
});

test("pipeline stages honour quotes and reject unbalanced ones", () => {
  assert.deepEqual(pipelineStages(`sed -n '1,80p' "a b.cs" | head -5`), [["sed", "-n", "1,80p", "a b.cs"], ["head", "-5"]]);
  assert.equal(pipelineStages("cat 'a.cs"), null);
});

// --- targets ---

test("glob metacharacters in a real path are percent-encoded, a literal % is not", () => {
  assert.equal(literalTarget("/r/app/[slug]/page.tsx"), "/r/app/%5Bslug]/page.tsx");
  assert.equal(literalTarget("/r/a*b?c{d;e"), "/r/a%2Ab%3Fc%7Bd%3Be");
  assert.equal(literalTarget("/r/50%off/x.tsx"), "/r/50%off/x.tsx");
  assert.equal(readTarget({ path: page }, "/"), literalTarget(page));
});

test("apply_patch targets come from OpenClaw's input and the patch text alike", () => {
  const patch = [
    "*** Begin Patch",
    "*** Update File: src/a.cs",
    "*** Add File: /abs/New.cs",
    "*** Delete File: src/Old.cs",
    "*** Update File: src/Moved.cs",
    "*** Move to: src/Renamed.cs",
    "*** End Patch",
  ].join("\n");
  const expected = [a, "/abs/New.cs", join(root, "src/Moved.cs"), join(root, "src/Renamed.cs")];
  assert.deepEqual(writeTargets({ input: patch }, root), expected);
  assert.deepEqual(writeTargets({ command: patch }, root), expected);
});

test("write and edit target their path", () => {
  assert.deepEqual(writeTargets({ path: "src/a.cs" }, root), [a]);
  assert.deepEqual(writeTargets({ file_path: a }, "/elsewhere"), [a]);
});

test("a Codex patch targets exactly the files its report added or modified", () => {
  const report = `Exit code: 0\nOutput:\nSuccess. Updated the following files:\nM ${a}\nA src/b.cs\nD src/gone.cs\n`;
  assert.deepEqual(codexPatchTargets("apply_patch", report, root), [a, b]);
});

test("a Codex patch that did not apply has no targets", () => {
  assert.deepEqual(codexPatchTargets("apply_patch", "apply_patch verification failed: context not found", root), []);
  assert.deepEqual(codexPatchTargets("exec", `Updated the following files:\nM ${a}`, root), []);
});

test("a relayed object response is read from its raw strings, not escaped JSON", () => {
  const raw = { output: `Success. Updated the following files:\nM ${a}\n`, metadata: { exit_code: 0 } };
  const relayed = { content: [{ type: "text", text: JSON.stringify(raw, null, 2) }], details: raw };
  assert.deepEqual(codexPatchTargets("apply_patch", nativeResponseText(relayed), root), [a]);
});

// --- results ---

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

// --- held concepts ---

test("held concepts are delivered only to the run that produced them", () => {
  const held = new HeldConcepts(new Map());
  held.add("s", "run-1", "concept A");
  held.add("s", "run-2", "concept B");
  assert.equal(held.take("s", "run-2"), "concept B");
  assert.equal(held.take("s", "run-1"), undefined, "taking clears the session, so an aborted run's concepts go stale");
});

test("held concepts accumulate within a run and are taken once", () => {
  const held = new HeldConcepts(new Map());
  held.add("s", "run", "concept A");
  held.add("s", "run", "concept B");
  assert.equal(held.take("s", "run"), "concept A\n\nconcept B");
  assert.equal(held.take("s", "run"), undefined);
});

test("held concepts are capped by session count", () => {
  const store = new Map();
  const held = new HeldConcepts(store);
  for (let i = 0; i < 300; i++) {
    held.add(`s${i}`, "run", "concept");
  }
  assert.equal(store.size, 256);
  assert.equal(held.take("s0", "run"), undefined, "the oldest sessions are dropped first");
  assert.equal(held.take("s299", "run"), "concept");
});
