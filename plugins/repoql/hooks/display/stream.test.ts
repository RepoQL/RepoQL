import { expect, test } from 'claude-code/testing'

import type { View } from '../../types'
import { INITIAL, describe, label, line, offline, parse, reason, sourceRows, sourcesOf, status, step, uplinkNames, uplinkState } from './stream'

const HOST = '"host_pid":41247,"host_started_at_unix_ms":1791280375986'
const progress = (fields: string) => `{"type":"host.signal","signal":"progress",${HOST},${fields}}`
const IMPORT = progress(
  '"operation":"import:github.com/sharkdp/bat","operation_total":905,"operation_done":199,"registry_total":235523,"registry_complete":225610,"settled":false',
)
const SWEEP = progress('"operation":"sweep","operation_total":12,"operation_done":3,"registry_total":200,"registry_complete":100,"settled":false')
const ANALYZE = progress('"operation":"analyze","operation_total":0,"operation_done":0,"registry_total":200,"registry_complete":200,"settled":false')
const SETTLED = progress('"operation":"","operation_total":0,"operation_done":0,"registry_total":235523,"registry_complete":235523,"settled":true')

/** Plays literal monitor lines, one per second from `startMs`. */
function play(lines: string[], from: View = INITIAL, startMs = 0, everyMs = 1000) {
  let view = from
  const notices: string[] = []

  lines.forEach((text, index) => {
    const record = parse(text)

    if (record !== null) {
      const next = step(view, record, startMs + index * everyMs)
      view = next.view

      if (next.notice !== null) {
        notices.push(next.notice)
      }
    }
  })

  return { view, notices }
}

test('the display words imports and first indexing, and stays quiet about housekeeping and worktrees', () => {
  expect(describe('import:github.com/sharkdp/bat')).toBe('importing github://sharkdp/bat')
  expect(describe('import:github-pulls://RepoQL/RepoQL.Core')).toBe('importing github-pulls://RepoQL/RepoQL.Core')
  expect(describe('import:gitlab.com/group/project')).toBe('importing gitlab.com/group/project')
  expect(describe('import:')).toBe(null)
  expect(describe('import:github.com/sharkdp/bat', 'github://sharkdp/bat/')).toBe('importing github://sharkdp/bat')
  expect(describe('import:gitlab.com/group/project', 'git://gitlab.com/group/project')).toBe('importing git://gitlab.com/group/project')
  expect(describe('import:worktree://wt-keepwarm/')).toBe(null)
  expect(describe('import:wt-keepwarm', 'worktree://wt-keepwarm/')).toBe(null)
  expect(describe('startup')).toBe('indexing this repository')
  expect(describe('sweep')).toBe(null)
  expect(describe('analyze')).toBe(null)
  expect(describe('')).toBe(null)
  expect(describe(undefined)).toBe(null)
})

test('a settled host reads rql, and a history burst changes nothing after the first record', () => {
  const first = play([`{"type":"session.snapshot",${HOST}}`])
  const again = step(first.view, { type: 'session.snapshot', host_pid: 41247, host_started_at_unix_ms: 1791280375986 }, 5)

  expect(label(first.view)).toBe('rql · ready')
  expect(again.view).toBe(first.view)
  expect(again.notice).toBe(null)
})

test('an import shows its own percentage and clears on the settled record alone', () => {
  const busy = play([IMPORT])
  const still = play(
    [`{"type":"host.signal","signal":"index_ready",${HOST}}`, `{"type":"host.signal","signal":"engine_idle",${HOST}}`],
    busy.view,
  )
  const done = play([SETTLED], busy.view, 5000)

  expect(label(busy.view)).toBe('rql · working 21%')
  expect(label(still.view)).toBe('rql · working 21%')
  expect(label(done.view)).toBe('rql · ready')
  expect(done.notices).toEqual([])
})

test('a sweep or an analyze draws nothing at all', () => {
  for (const housekeeping of [SWEEP, ANALYZE]) {
    const quiet = play([housekeeping, housekeeping])

    expect(label(quiet.view)).toBe('rql · ready')
    expect(line(quiet.view, 600_000)).toBe(null)
    expect(reason({ ...quiet.view, slowTool: 'explore' })).toBe('rql explore')
    expect(play([SETTLED], quiet.view, 600_000).notices).toEqual([])
  }
})

test('the line names the import after ten seconds and hides while housekeeping follows it', () => {
  const importing = play([IMPORT]).view
  const sweeping = play([SWEEP], importing, 20_000).view

  expect(line(importing, 9_000)).toBe(null)
  expect(line(importing, 10_000)).toBe('importing github://sharkdp/bat 21%')
  expect(line(sweeping, 30_000)).toBe(null)
  expect(label(sweeping)).toBe('rql · ready')
})

test('work ending raises no notice, however long it ran', () => {
  const importing = play([IMPORT]).view

  expect(play([SETTLED], importing, 29_000).notices).toEqual([])
  expect(play([SETTLED], importing, 3_600_000).notices).toEqual([])
  expect(play([SWEEP, ANALYZE, SETTLED], importing, 40_000).notices).toEqual([])
})

test('first indexing reads in plain words', () => {
  const starting = play([progress('"operation":"startup","operation_total":1000,"operation_done":400,"registry_total":1000,"registry_complete":400,"settled":false')]).view

  expect(line(starting, 10_000)).toBe('indexing this repository 40%')
  expect(label(starting)).toBe('rql · working 40%')
})

test('waiting and lost read no host, clear the work, and keep the host for comparison', () => {
  const busy = play([IMPORT]).view
  const lost = play(['{"type":"monitor.connection","state":"lost","text":"host connection lost"}'], busy)
  const waiting = play(['{"type":"monitor.connection","state":"waiting","text":"no host"}'])

  expect(label(lost.view)).toBe('rql · not running')
  expect(lost.view.operation).toBe(null)
  expect(lost.view.host).toEqual({ pid: 41247, startedAtUnixMs: 1791280375986 })
  expect(lost.notices).toEqual([])
  expect(label(waiting.view)).toBe('rql · not running')
})

test('the first host of a session raises no notice, with or without waiting before it', () => {
  const connected = `{"type":"monitor.connection","state":"connected",${HOST}}`

  expect(play([connected]).notices).toEqual([])
  expect(play(['{"type":"monitor.connection","state":"waiting"}', connected]).notices).toEqual([])
})

test('a reconnect to the same host is silent; a different host is a restart', () => {
  const lost = '{"type":"monitor.connection","state":"lost"}'
  const same = `{"type":"monitor.connection","state":"connected",${HOST}}`
  const other = '{"type":"monitor.connection","state":"connected","host_pid":50001,"host_started_at_unix_ms":1791280999000}'
  const samePidLater = '{"type":"monitor.connection","state":"connected","host_pid":41247,"host_started_at_unix_ms":1791280999000}'
  const busy = play([IMPORT]).view

  expect(play([lost, same], busy).notices).toEqual([])
  expect(label(play([lost, same], busy).view)).toBe('rql · ready')
  expect(play([lost, other], busy).notices).toEqual(['RepoQL host restarted'])
  expect(play([lost, samePidLater], busy).notices).toEqual(['RepoQL host restarted'])
})

test('an rql that sends no signal key or numbers leaves the label at rql', () => {
  const old = play([`{"type":"host.signal","text":"progress — files 100/200",${HOST}}`, 'not json', '[1,2]'])

  expect(label(old.view)).toBe('rql · ready')
  expect(line(old.view, 60_000)).toBe(null)
})

test('a slow call names its tool, and the named work while there is some', () => {
  expect(reason(INITIAL)).toBe(null)
  expect(reason({ ...INITIAL, slowTool: 'explore' })).toBe('rql explore')
  expect(reason({ ...play([IMPORT]).view, slowTool: 'explore' })).toBe('rql explore · importing github://sharkdp/bat 21%')
})

test('the pane\'s head says the state in a word and only what the host has said', () => {
  const hello = `{"type":"host.signal","signal":"hello","version":"1.7.13",${HOST}}`
  const counted = `{"type":"host.signal","signal":"progress","registry_complete":182598,"settled":true,${HOST}}`

  expect(status(INITIAL)).toEqual({ state: 'Connecting', fact: 'Looking for the RepoQL host.', release: '', isAwake: false })
  expect(status(play(['{"type":"monitor.connection","state":"waiting"}']).view)).toEqual({
    state: 'Not running',
    fact: 'It starts with your next RepoQL call.',
    release: '',
    isAwake: false,
  })
  // a host that was settled on arrival has sent no count
  expect(status(play([hello]).view)).toEqual({ state: 'Ready', fact: '', release: 'rql 1.7.13', isAwake: true })
  expect(status(play([hello, counted]).view)).toEqual({ state: 'Ready', fact: '182,598 files ready to search', release: 'rql 1.7.13', isAwake: true })
  expect(status(play([hello, IMPORT]).view).state).toBe('Working')
  // another host's release and count are not this host's
  const other = '{"type":"monitor.connection","state":"connected","host_pid":50001,"host_started_at_unix_ms":1791280999000}'
  expect(status(play([hello, counted, other]).view)).toEqual({ state: 'Ready', fact: '', release: '', isAwake: true })
})

test('the uplinks are the names rql uplinks prints, none when it says so, and unknown when it says neither', () => {
  expect(uplinkNames('Accessible uplinks: fotg-gcp, stu-pc\nPass uplink="name" to a read-only RepoQL tool.\n')).toEqual(['fotg-gcp', 'stu-pc'])
  expect(uplinkNames('No accessible uplinks.\n')).toEqual([])
  expect(uplinkNames('')).toBeNull()
  expect(uplinkNames('Sign in with rql account login\n')).toBeNull()
})

test('the sources of an uplink are the rows under the header rql query prints; its workspace is named so, and no header is no answer', () => {
  const printed = '2 rows · [18 tok | 38 ms | ready]\nsource_uri\tfile_count\nfile://\t9830\ngithub://sharkdp/bat\t236\n\n'

  expect(sourcesOf(printed)).toEqual([
    { uri: 'workspace', files: 9830 },
    { uri: 'github://sharkdp/bat', files: 236 },
  ])
  expect(sourcesOf('0 rows · [4 tok | 6 ms | ready]\nsource_uri\tfile_count\n\n')).toEqual([])
  expect(sourcesOf('Error: Unknown uplink')).toBeNull()
})

test('an uplink that does not answer says why in words a person can act on', () => {
  expect(offline('this instance has never connected; no `rql uplink` has dialled in with its credential yet')).toBe('Never connected')
  expect(offline('')).toBe('Not answering')
})

test('an uplink says its state in a few words', () => {
  expect(uplinkState({ name: 'a', state: 'checking' })).toBe('checking')
  expect(uplinkState({ name: 'a', state: 'offline', reason: 'Never connected' })).toBe('never connected')
  expect(uplinkState({ name: 'a', state: 'online', sources: [] })).toBe('online · no sources')
  expect(uplinkState({ name: 'a', state: 'online', sources: [{ uri: 'workspace', files: 3 }] })).toBe('online · 1 source')
})

test('a few sources are listed one to a line; many are counted by scheme, the workspace first and the largest next', () => {
  const few = [
    { uri: 'workspace', files: 10 },
    { uri: 'github://a/b', files: 5 },
  ]
  expect(sourceRows(few, 8)).toEqual([
    { label: 'workspace', files: 10 },
    { label: 'github://a/b', files: 5 },
  ])

  const many = [
    { uri: 's3://bucket/x', files: 4 },
    ...Array.from({ length: 35 }, (_, at) => ({ uri: `github://org/repo-${at}`, files: 2 })),
    { uri: 'workspace', files: 100 },
    { uri: 'local:///docs', files: 7 },
    { uri: 'local:///notes', files: 1 },
  ]
  expect(sourceRows(many, 8)).toEqual([
    { label: 'workspace', files: 100 },
    { label: 'github:// × 35', files: 70 },
    { label: 'local:// × 2', files: 8 },
    { label: 's3://bucket/x', files: 4 },
  ])
})
