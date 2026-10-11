import { expect, test } from 'claude-code/testing'

import type { View } from '../../types'
import { INITIAL, describe, label, line, parse, reason, step } from './stream'

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

test('the host names work; the display words imports and first indexing, and nothing else', () => {
  expect(describe('import:github.com/sharkdp/bat')).toBe('importing sharkdp/bat')
  expect(describe('startup')).toBe('indexing this repository')
  expect(describe('sweep')).toBe(null)
  expect(describe('analyze')).toBe(null)
  expect(describe('')).toBe(null)
  expect(describe(undefined)).toBe(null)
})

test('a settled host reads rql, and a history burst changes nothing after the first record', () => {
  const first = play([`{"type":"session.snapshot",${HOST}}`])
  const again = step(first.view, { type: 'session.snapshot', host_pid: 41247, host_started_at_unix_ms: 1791280375986 }, 5)

  expect(label(first.view)).toBe('rql')
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

  expect(label(busy.view)).toBe('rql 21%')
  expect(label(still.view)).toBe('rql 21%')
  expect(label(done.view)).toBe('rql')
  expect(done.notices).toEqual([])
})

test('a sweep or an analyze draws nothing at all', () => {
  for (const housekeeping of [SWEEP, ANALYZE]) {
    const quiet = play([housekeeping, housekeeping])

    expect(label(quiet.view)).toBe('rql')
    expect(line(quiet.view, 600_000)).toBe(null)
    expect(reason({ ...quiet.view, slowTool: 'explore' })).toBe('rql explore')
    expect(play([SETTLED], quiet.view, 600_000).notices).toEqual([])
  }
})

test('the line names the import after ten seconds and hides while housekeeping follows it', () => {
  const importing = play([IMPORT]).view
  const sweeping = play([SWEEP], importing, 20_000).view

  expect(line(importing, 9_000)).toBe(null)
  expect(line(importing, 10_000)).toBe('importing sharkdp/bat 21%')
  expect(line(sweeping, 30_000)).toBe(null)
  expect(label(sweeping)).toBe('rql')
})

test('one notice ends a stretch of named work that lasted thirty seconds, housekeeping included', () => {
  const importing = play([IMPORT]).view

  expect(play([SETTLED], importing, 29_000).notices).toEqual([])
  expect(play([SETTLED], importing, 30_000).notices).toEqual(['RepoQL index settled'])
  expect(play([SWEEP, ANALYZE, SETTLED], importing, 40_000).notices).toEqual(['RepoQL index settled'])
})

test('first indexing reads in plain words', () => {
  const starting = play([progress('"operation":"startup","operation_total":1000,"operation_done":400,"registry_total":1000,"registry_complete":400,"settled":false')]).view

  expect(line(starting, 10_000)).toBe('indexing this repository 40%')
  expect(label(starting)).toBe('rql 40%')
})

test('waiting and lost read no host, clear the work, and keep the host for comparison', () => {
  const busy = play([IMPORT]).view
  const lost = play(['{"type":"monitor.connection","state":"lost","text":"host connection lost"}'], busy)
  const waiting = play(['{"type":"monitor.connection","state":"waiting","text":"no host"}'])

  expect(label(lost.view)).toBe('rql no host')
  expect(lost.view.operation).toBe(null)
  expect(lost.view.host).toEqual({ pid: 41247, startedAtUnixMs: 1791280375986 })
  expect(lost.notices).toEqual([])
  expect(label(waiting.view)).toBe('rql no host')
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
  expect(label(play([lost, same], busy).view)).toBe('rql')
  expect(play([lost, other], busy).notices).toEqual(['RepoQL host restarted'])
  expect(play([lost, samePidLater], busy).notices).toEqual(['RepoQL host restarted'])
})

test('an rql that sends no signal key or numbers leaves the label at rql', () => {
  const old = play([`{"type":"host.signal","text":"progress — files 100/200",${HOST}}`, 'not json', '[1,2]'])

  expect(label(old.view)).toBe('rql')
  expect(line(old.view, 60_000)).toBe(null)
})

test('a slow call names its tool, and the named work while there is some', () => {
  expect(reason(INITIAL)).toBe(null)
  expect(reason({ ...INITIAL, slowTool: 'explore' })).toBe('rql explore')
  expect(reason({ ...play([IMPORT]).view, slowTool: 'explore' })).toBe('rql explore · importing sharkdp/bat 21%')
})
