"""Verify the uplink plugin stays a remote-only mirror of the Claude Code plugin's portable parts."""
import json
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parent.parent
EXEMPLAR = ROOT / 'plugins' / 'repoql'
UPLINK = ROOT / 'plugins' / 'repoql-uplink'

# Skills written for the remote connection; the exemplar's version assumes a local host.
OWNED_SKILLS = {'troubleshooting-repoql', 'using-uplinks'}

# Exemplar skills that drive the local rql binary, which this plugin never installs.
LOCAL_ONLY_SKILLS = {'monitoring-repoql', 'statusline-builder'}


def files_under(directory):
    return {path.relative_to(directory).as_posix(): path.read_bytes()
            for path in directory.rglob('*') if path.is_file()}


def skill_names(plugin):
    return {path.name for path in (plugin / 'skills').iterdir() if path.is_dir()}


class UplinkPluginTests(unittest.TestCase):
    def test_carries_every_portable_skill_and_no_local_one(self):
        self.assertEqual(skill_names(UPLINK), (skill_names(EXEMPLAR) - LOCAL_ONLY_SKILLS) | OWNED_SKILLS)

    def test_shared_skills_and_agents_match_the_exemplar_byte_for_byte(self):
        for name in sorted(skill_names(UPLINK) - OWNED_SKILLS):
            with self.subTest(skill=name):
                self.assertEqual(files_under(UPLINK / 'skills' / name), files_under(EXEMPLAR / 'skills' / name))
        self.assertEqual(files_under(UPLINK / 'agents'), files_under(EXEMPLAR / 'agents'))

    def test_connects_to_one_remote_endpoint_and_starts_nothing(self):
        servers = json.loads((UPLINK / '.mcp.json').read_text(encoding='utf-8'))['mcpServers']
        self.assertEqual(list(servers), ['repoql'])
        self.assertEqual(servers['repoql'],
                         {'type': 'http', 'url': '${REPOQL_UPLINK_URL:-https://mcp.repoql.com/uplink}'})
        self.assertFalse((UPLINK / 'hooks').exists())
        self.assertFalse((UPLINK / 'scripts').exists())

    def test_owned_skills_never_load_through_the_connection_they_serve(self):
        text = (UPLINK / 'skills' / 'troubleshooting-repoql' / 'SKILL.md').read_text(encoding='utf-8')
        self.assertNotIn('## Load', text)
        self.assertNotIn('help:///skills/', text)
        self.assertFalse((UPLINK / 'skills' / 'troubleshooting-repoql' / 'references').exists())

    def test_marketplace_lists_the_plugin_under_its_manifest_name(self):
        manifest = json.loads((UPLINK / '.claude-plugin' / 'plugin.json').read_text(encoding='utf-8'))
        marketplace = json.loads((ROOT / '.claude-plugin' / 'marketplace.json').read_text(encoding='utf-8'))
        sources = {plugin['name']: plugin['source'] for plugin in marketplace['plugins']}
        self.assertEqual(manifest['name'], 'repoql-uplink')
        self.assertEqual(sources['repoql-uplink'], './plugins/repoql-uplink')


if __name__ == '__main__':
    unittest.main()
