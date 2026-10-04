"""Shared plumbing for the hook tests: which implementations run here, and a fake rql every shell can start.

Each hook exists twice, a bash script and a PowerShell script with the same behaviour. A test names the hook
and loops over IMPLEMENTATIONS, so one set of cases holds both to the same contract.
"""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
WINDOWS = os.name == 'nt'
# On Windows the hooks run under Windows PowerShell, which the launchers start by that name.
POWERSHELL = shutil.which('powershell') if WINDOWS else shutil.which('pwsh')
# Git Bash on Windows rewrites paths, so the bash scripts are held to their cases on macOS and Linux only.
BASH = None if WINDOWS else shutil.which('bash')
IMPLEMENTATIONS = [name for name, present in (('sh', BASH), ('ps1', POWERSHELL)) if present]
POWERSHELL_FILE = [POWERSHELL, '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File']


def scripts(harness):
    return ROOT / 'plugins' / harness / 'scripts'


def argv(implementation, harness, hook):
    script = str(scripts(harness) / f'{hook}.{implementation}')
    return [BASH, script] if implementation == 'sh' else [*POWERSHELL_FILE, script]


def install_fake(bin_dir, name, source):
    """Put a Python program on a scratch PATH under a name bash and PowerShell both resolve."""
    bin_dir.mkdir(parents=True, exist_ok=True)
    if not WINDOWS:
        (bin_dir / name).write_text('#!/usr/bin/env python3\n' + source, encoding='utf-8')
        (bin_dir / name).chmod(0o755)
        return
    program = bin_dir / f'{name}.py'
    program.write_text(source, encoding='utf-8')
    # PowerShell and a directly started process run the batch file; Git Bash finds the extensionless shell script.
    (bin_dir / f'{name}.cmd').write_text(f'@"{sys.executable}" "{program}" %*\r\n', encoding='utf-8', newline='')
    (bin_dir / name).write_text(
        f'#!/bin/sh\nexec "{Path(sys.executable).as_posix()}" "{program.as_posix()}" "$@"\n', encoding='utf-8', newline='\n')


def hook_env(bin_dir, **extra):
    return {**os.environ, 'PATH': f'{bin_dir}{os.pathsep}{os.environ["PATH"]}', 'PYTHONUTF8': '1',
            'REPOQL_NO_BOOTSTRAP': '1', **extra}


def run(command, payload, env, cwd, timeout=15):
    stdin = payload if isinstance(payload, str) or payload is None else json.dumps(payload, ensure_ascii=False)
    return subprocess.run(command, input=stdin, text=True, encoding='utf-8', capture_output=True,
                          env=env, cwd=cwd, timeout=timeout)


def same_path(left, right):
    return os.path.normcase(os.path.realpath(left)) == os.path.normcase(os.path.realpath(right))


class Cases:
    """Mixin: run a block once per implementation and harness, each as its own subtest."""

    def case(self, **parameters):
        if getattr(self, 'log', None) is not None:
            self.log.unlink(missing_ok=True)
        return self.subTest(**parameters)


def context_of(result, event):
    """The text a hook handed to the model, from its hookSpecificOutput reply."""
    output = json.loads(result.stdout)['hookSpecificOutput']
    assert output['hookEventName'] == event, output
    return output['additionalContext']
