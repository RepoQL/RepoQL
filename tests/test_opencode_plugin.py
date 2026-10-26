"""Verify the OpenCode plugin installs from this repository's root and registers RepoQL's server and skills."""
import json
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
PLUGIN = ROOT / 'plugins' / 'repoql-opencode'
CURSOR = ROOT / 'plugins' / 'repoql-cursor'

# Runs the plugin's setup against a fake OpenCode context and prints what it registered.
# The fake mirrors OpenCode 2's Promise plugin API: transform(callback) hands the callback an editor.
HARNESS = r"""
const plugin = (await import(process.argv[1])).default
const servers = {}
const skills = new Map(JSON.parse(process.env.EXISTING_SKILLS ?? "[]").map((skill) => [skill.id, skill]))
const ctx = {
  mcp: { transform: async (callback) => { callback({ set: (name, config) => { servers[name] = config }, get: (name) => servers[name] }); return { dispose: async () => {} } } },
  skill: { transform: async (callback) => { callback({ add: (skill) => skills.set(skill.id, skill), get: (id) => skills.get(id) }); return { dispose: async () => {} } } },
}
const warnings = []
console.warn = (message) => warnings.push(String(message))
await plugin.setup(ctx)
process.stdout.write(JSON.stringify({ id: plugin.id, servers, skills: [...skills.values()], warnings }))
"""


def files_under(directory):
    return {path.relative_to(directory).as_posix(): path.read_bytes()
            for path in directory.rglob('*') if path.is_file()}


def skill_names(plugin):
    return {path.name for path in (plugin / 'skills').iterdir() if path.is_dir()}


@unittest.skipIf(shutil.which('node') is None, 'node is required to load the plugin')
class OpencodePluginTests(unittest.TestCase):
    def setUp(self):
        self.temp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.temp)
        self.home = self.temp / 'home'
        self.home.mkdir()

    def run_plugin(self, path_dirs=(), existing_skills=()):
        env = {
            'PATH': os.pathsep.join(str(d) for d in path_dirs),
            'HOME': str(self.home),
            'USERPROFILE': str(self.home),
            'EXISTING_SKILLS': json.dumps(list(existing_skills)),
        }
        node = shutil.which('node')
        result = subprocess.run([node, '--input-type=module', '-e', HARNESS, (PLUGIN / 'server.js').as_uri()],
                                env=env, capture_output=True, text=True, timeout=60)
        self.assertEqual(result.returncode, 0, result.stderr)
        return json.loads(result.stdout)

    def fake_rql(self, directory):
        directory.mkdir(parents=True, exist_ok=True)
        name = 'rql.exe' if os.name == 'nt' else 'rql'
        binary = directory / name
        binary.write_text('#!/bin/sh\n', encoding='utf-8')
        binary.chmod(binary.stat().st_mode | stat.S_IXUSR)
        return binary

    def test_registers_rql_from_path_as_the_repoql_server(self):
        rql = self.fake_rql(self.temp / 'bin')

        result = self.run_plugin(path_dirs=[self.temp / 'bin'])

        self.assertEqual(result['id'], 'repoql')
        self.assertEqual(list(result['servers']), ['repoql'])
        server = result['servers']['repoql']
        self.assertEqual(server['type'], 'local')
        self.assertEqual(server['command'], [str(rql), 'mcp'])
        self.assertEqual(result['warnings'], [])

    def test_finds_rql_in_local_bin_when_path_lacks_it(self):
        rql = self.fake_rql(self.home / '.local' / 'bin')

        result = self.run_plugin()

        self.assertEqual(result['servers']['repoql']['command'], [str(rql), 'mcp'])

    def test_registers_no_server_without_rql_and_says_where_to_get_it(self):
        result = self.run_plugin()

        self.assertEqual(result['servers'], {})
        self.assertEqual(len(result['warnings']), 1)
        self.assertIn('https://repoql.com', result['warnings'][0])

    def test_registers_every_skill_with_its_frontmatter_name_and_body(self):
        result = self.run_plugin()

        skills = {skill['id']: skill for skill in result['skills']}
        self.assertEqual(set(skills), skill_names(PLUGIN))
        for skill_id, skill in skills.items():
            with self.subTest(skill=skill_id):
                self.assertEqual(skill['name'], skill_id)
                self.assertTrue(skill['description'])
                self.assertFalse(skill['content'].startswith('---'), 'the body excludes the frontmatter')
                self.assertEqual(Path(skill['path']), PLUGIN / 'skills' / skill_id / 'SKILL.md')
        self.assertNotIn('"', skills['effective-repoql']['description'][:1], 'a quoted YAML scalar is unquoted')

    def test_keeps_a_skill_the_user_already_has_under_the_same_id(self):
        mine = {'id': 'research', 'name': 'research', 'path': '/mine/SKILL.md', 'content': 'mine'}

        result = self.run_plugin(existing_skills=[mine])

        research = [skill for skill in result['skills'] if skill['id'] == 'research']
        self.assertEqual(research, [mine])


class OpencodePluginPackageTests(unittest.TestCase):
    def test_root_package_exports_the_plugin_and_ships_only_its_folder(self):
        package = json.loads((ROOT / 'package.json').read_text(encoding='utf-8'))
        server = package['exports']['./server']
        self.assertTrue((ROOT / server).is_file(), server)
        self.assertEqual(package['type'], 'module')
        self.assertEqual(package['files'], ['plugins/repoql-opencode/'])
        self.assertNotIn('dependencies', package, 'OpenCode provides the plugin runtime; nothing is installed')
        self.assertNotIn('scripts', package, 'OpenCode installs plugins with scripts disabled')

    def test_carries_the_cursor_plugins_skills_byte_for_byte(self):
        self.assertEqual(skill_names(PLUGIN), skill_names(CURSOR))
        for name in sorted(skill_names(PLUGIN)):
            with self.subTest(skill=name):
                self.assertEqual(files_under(PLUGIN / 'skills' / name), files_under(CURSOR / 'skills' / name))


if __name__ == '__main__':
    unittest.main()
