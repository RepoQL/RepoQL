#!/usr/bin/env node
/* Syntax-only TypeScript/JavaScript parser for RepoQL. */

const readline = require("readline");
let ts;

try {
  ts = require("typescript");
} catch (err) {
  process.stdout.write(
    JSON.stringify({
      id: "startup",
      ok: false,
      error:
        "TypeScript compiler API not found. Install dependencies with `npm install` in the Node helper directory.",
    }) + "\n"
  );
  process.exit(1);
}

const rl = readline.createInterface({
  input: process.stdin,
  output: process.stdout,
  terminal: false,
});

rl.on("line", (line) => {
  if (!line.trim()) return;
  let req;
  try {
    req = JSON.parse(line);
  } catch (err) {
    writeResponse({ id: "unknown", ok: false, error: "Invalid JSON input" });
    return;
  }

  if (req.id === "shutdown") {
    process.exit(0);
  }

  try {
    const result = parseOnce(req);
    writeResponse({ id: req.id, ok: true, result });
  } catch (err) {
    writeResponse({
      id: req.id,
      ok: false,
      error: err && err.message ? err.message : String(err),
    });
  }
});

function writeResponse(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

function pickScriptKind(path, mediaKind) {
  if (mediaKind === "code.typescript.react") return ts.ScriptKind.TSX;
  if (mediaKind === "code.javascript.react") return ts.ScriptKind.JSX;
  if (mediaKind === "code.typescript") return ts.ScriptKind.TS;
  if (mediaKind === "code.javascript") return ts.ScriptKind.JS;

  const lower = (path || "").toLowerCase();
  if (lower.endsWith(".tsx")) return ts.ScriptKind.TSX;
  if (lower.endsWith(".jsx")) return ts.ScriptKind.JSX;
  if (lower.endsWith(".ts")) return ts.ScriptKind.TS;
  return ts.ScriptKind.JS;
}

function spanOf(node, sf) {
  const start = node.getStart(sf);
  const end = node.getEnd();
  return { start, end };
}

function classifyImportStyle(clause) {
  if (!clause) return "sideEffect";
  if (clause.name && clause.namedBindings) {
    return "mixed";
  }
  if (clause.name) return "default";
  if (clause.namedBindings) {
    if (ts.isNamespaceImport(clause.namedBindings)) return "namespace";
    return "named";
  }
  return "sideEffect";
}

function isPascalCase(name) {
  return !!name && /^[A-Z][A-Za-z0-9]*$/.test(name);
}

function containsJsx(node) {
  let found = false;
  const visit = (n) => {
    if (
      n.kind === ts.SyntaxKind.JsxElement ||
      n.kind === ts.SyntaxKind.JsxSelfClosingElement ||
      n.kind === ts.SyntaxKind.JsxFragment
    ) {
      found = true;
      return;
    }
    ts.forEachChild(n, visit);
  };
  ts.forEachChild(node, visit);
  return found;
}

function collectMembers(node, sf) {
  const members = [];
  if (!node.members) return members;
  for (const m of node.members) {
    if (!m.name || !m.name.getText) continue;
    const name = m.name.getText(sf);
    if (!name) continue;
    let memberKind = "member";
    switch (m.kind) {
      case ts.SyntaxKind.MethodDeclaration:
      case ts.SyntaxKind.MethodSignature:
        memberKind = "method";
        break;
      case ts.SyntaxKind.Constructor:
        memberKind = "constructor";
        break;
      case ts.SyntaxKind.GetAccessor:
        memberKind = "getter";
        break;
      case ts.SyntaxKind.SetAccessor:
        memberKind = "setter";
        break;
      case ts.SyntaxKind.PropertyDeclaration:
      case ts.SyntaxKind.PropertySignature:
        memberKind = "field";
        break;
      case ts.SyntaxKind.EnumMember:
        memberKind = "enumMember";
        break;
    }
    members.push({
      name,
      memberKind,
      span: spanOf(m.name, sf),
    });
  }
  return members;
}

function parseOnce(req) {
  const scriptKind = pickScriptKind(req.path || "", req.mediaKind || "");
  const sf = ts.createSourceFile(
    req.path || "module.ts",
    req.text || "",
    ts.ScriptTarget.Latest,
    /*setParentNodes*/ true,
    scriptKind
  );

  const imports = [];
  const exports = [];
  const declarations = [];

  const addDecl = (decl) => declarations.push(decl);

  for (const diag of sf.parseDiagnostics || []) {
    // We'll return diagnostics later; parser recovers, so continue traversal.
  }

  const isTopLevel = (node) => node.parent && node.parent.kind === ts.SyntaxKind.SourceFile;

  const visit = (node) => {
    switch (node.kind) {
      case ts.SyntaxKind.ImportDeclaration: {
        const mod = node.moduleSpecifier;
        imports.push({
          specifier: mod && mod.text ? String(mod.text) : "",
          importKind: node.importClause && node.importClause.isTypeOnly ? "type" : "value",
          importStyle: classifyImportStyle(node.importClause),
          span: spanOf(mod, sf),
        });
        break;
      }
      case ts.SyntaxKind.ExportDeclaration: {
        if (node.exportClause && ts.isNamedExports(node.exportClause)) {
          for (const el of node.exportClause.elements) {
            exports.push({
              name: el.name.text,
              exportKind: "named",
              targetName: el.propertyName ? el.propertyName.text : el.name.text,
              span: spanOf(el.name, sf),
            });
          }
        } else {
          exports.push({
            name: "*",
            exportKind: "reexport",
            span: spanOf(node, sf),
          });
        }
        break;
      }
      case ts.SyntaxKind.ExportAssignment: {
        exports.push({
          name: "default",
          exportKind: "default",
          span: spanOf(node, sf),
        });
        break;
      }
      case ts.SyntaxKind.FunctionDeclaration:
      case ts.SyntaxKind.ClassDeclaration:
      case ts.SyntaxKind.InterfaceDeclaration:
      case ts.SyntaxKind.TypeAliasDeclaration:
      case ts.SyntaxKind.EnumDeclaration:
      case ts.SyntaxKind.ModuleDeclaration:
      case ts.SyntaxKind.VariableStatement: {
        if (isTopLevel(node)) collectDeclaration(node, sf, scriptKind, addDecl);
        break;
      }
      default:
        break;
    }
    ts.forEachChild(node, visit);
  };

  ts.forEachChild(sf, visit);

  const diagnostics = (sf.parseDiagnostics || []).map((d) => ({
    message: ts.flattenDiagnosticMessageText(d.messageText, "\n"),
  }));

  return {
    path: req.path || "",
    scriptKind: ts.ScriptKind[scriptKind],
    imports,
    exports,
    declarations,
    diagnostics,
  };
}

function collectDeclaration(node, sf, scriptKind, addDecl) {
  const isExported =
    Array.isArray(node.modifiers) &&
    node.modifiers.some((m) => m.kind === ts.SyntaxKind.ExportKeyword);
  const isDefault =
    Array.isArray(node.modifiers) &&
    node.modifiers.some((m) => m.kind === ts.SyntaxKind.DefaultKeyword);

  switch (node.kind) {
    case ts.SyntaxKind.FunctionDeclaration: {
      const name = node.name ? node.name.text : null;
      addDecl({
        name,
        declKind: "function",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent: isPascalCase(name) && (scriptKind === ts.ScriptKind.TSX || scriptKind === ts.ScriptKind.JSX),
        members: [],
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.ClassDeclaration: {
      const name = node.name ? node.name.text : null;
      addDecl({
        name,
        declKind: "class",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent:
          isPascalCase(name) &&
          (scriptKind === ts.ScriptKind.TSX ||
            scriptKind === ts.ScriptKind.JSX ||
            containsJsx(node)),
        members: collectMembers(node, sf),
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.InterfaceDeclaration: {
      const name = node.name ? node.name.text : null;
      addDecl({
        name,
        declKind: "interface",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent: false,
        members: collectMembers(node, sf),
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.TypeAliasDeclaration: {
      const name = node.name ? node.name.text : null;
      addDecl({
        name,
        declKind: "type",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent: false,
        members: [],
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.EnumDeclaration: {
      const name = node.name ? node.name.text : null;
      addDecl({
        name,
        declKind: "enum",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent: false,
        members: collectMembers(node, sf),
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.ModuleDeclaration: {
      const name = node.name ? node.name.getText(sf) : null;
      addDecl({
        name,
        declKind: "namespace",
        isExported,
        exportKind: isExported ? (isDefault ? "default" : "named") : undefined,
        isComponent: false,
        members: [],
        span: node.name ? spanOf(node.name, sf) : spanOf(node, sf),
      });
      return;
    }
    case ts.SyntaxKind.VariableStatement: {
      const vs = node;
      const isExport = isExported;
      const exportKind = isExport ? (isDefault ? "default" : "named") : undefined;

      for (const decl of vs.declarationList.declarations || []) {
        const nameNode = decl.name;
        if (ts.isIdentifier(nameNode)) {
          addDecl({
            name: nameNode.text,
            declKind: "variable",
            isExported: isExport,
            exportKind,
            isComponent:
              isPascalCase(nameNode.text) &&
              (scriptKind === ts.ScriptKind.TSX ||
                scriptKind === ts.ScriptKind.JSX) &&
              containsJsx(decl),
            members: [],
            span: spanOf(nameNode, sf),
          });
        }
      }
      return;
    }
    default:
      return;
  }
}
