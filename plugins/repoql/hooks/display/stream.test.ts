import { expect, test } from 'claude-code/testing'

import type { View } from '../../types'
import { INITIAL, label, line, parse, reason, step } from './stream'

const HOST = { host_pid: 41247, host_started_at_unix_ms: 1791280375986 }
const IMPORT =
  '{"type":"host.signal","signal":"progress","text":"progress — import","host_pid":41247,"host_started_at_unix_ms":1791280375986,"operation":"import:github.com/enricomi/publish-unit-test-result-action","operation_total":147458,"operation_done":122330,"registry_total":216906,"registry_complete":182598}'
const SETTLED =
  '{"type":"host.signal","signal":"progress","text":"progress — settled","host_pid":41247,"host_started_at_unix_ms":1791280375986,"settled":true,"registry_total":216906,"registry_complete":216906}'

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

test('a settled host reads rql, and a history burst changes nothing after the first record', () => {
  const first = play(['{"type":"session.snapshot","host_pid":41247,"host_started_at_unix_ms":1791280375986}'])
  const again = step(first.view, { type: 'session.snapshot', ...HOST }, 5)

  expect(label(first.view)).toBe('rql')
  expect(again.view).toBe(first.view)
  expect(again.notice).toBe(null)
})

test('an unsettled host reads a percentage and settles on the settled record alone', () => {
  const busy = play([IMPORT])
  const done = play([SETTLED], busy.view, 5000)

  expect(label(busy.view)).toBe('rql 84%')
  expect(label(done.view)).toBe('rql')
  expect(done.notices).toEqual([])
})

test('index_ready and engine_idle do not settle the display', () => {
  const still = play(
    [
      `{"type":"host.signal","signal":"index_ready","host_pid":41247,"host_started_at_unix_ms":1791280375986}`,
      `{"type":"host.signal","signal":"engine_idle","host_pid":41247,"host_started_at_unix_ms":1791280375986}`,
    ],
    play([IMPORT]).view,
  )

  expect(label(still.view)).toBe('rql 84%')
})

test('work shorter than thirty seconds ends without a notice; longer work ends with one', () => {
  const busy = play([IMPORT]).view

  expect(play([SETTLED], busy, 29_000).notices).toEqual([])
  expect(play([SETTLED], busy, 30_000).notices).toEqual(['RepoQL index settled'])
})

test('the line names a long operation after ten seconds and never a routine reindex', () => {
  const importing = play([IMPORT]).view
  const reindex = play([
    '{"type":"host.signal","signal":"progress","host_pid":41247,"host_started_at_unix_ms":1791280375986,"registry_total":200,"registry_complete":100}',
  ]).view

  expect(line(importing, 9_000)).toBe(null)
  expect(line(importing, 10_000)).toBe('import publish-unit-test-result-action 82%')
  expect(line(reindex, 60_000)).toBe(null)
  expect(label(reindex)).toBe('rql 50%')
})

test('waiting and lost read no host, clear the work, and keep the host for comparison', () => {
  const busy = play([IMPORT]).view
  const lost = play(['{"type":"monitor.connection","state":"lost","text":"host connection lost"}'], busy)
  const waiting = play(['{"type":"monitor.connection","state":"waiting","text":"no host"}'])

  expect(label(lost.view)).toBe('rql no host')
  expect(lost.view.percent).toBe(null)
  expect(lost.view.host).toEqual({ pid: 41247, startedAtUnixMs: 1791280375986 })
  expect(lost.notices).toEqual([])
  expect(label(waiting.view)).toBe('rql no host')
})

test('the first host of a session raises no notice, with or without waiting before it', () => {
  const connected = '{"type":"monitor.connection","state":"connected","host_pid":41247,"host_started_at_unix_ms":1791280375986}'

  expect(play([connected]).notices).toEqual([])
  expect(play(['{"type":"monitor.connection","state":"waiting"}', connected]).notices).toEqual([])
})

test('a reconnect to the same host is silent; a different host is a restart', () => {
  const lost = '{"type":"monitor.connection","state":"lost"}'
  const same = '{"type":"monitor.connection","state":"connected","host_pid":41247,"host_started_at_unix_ms":1791280375986}'
  const other = '{"type":"monitor.connection","state":"connected","host_pid":50001,"host_started_at_unix_ms":1791280999000}'
  const samePidLater = '{"type":"monitor.connection","state":"connected","host_pid":41247,"host_started_at_unix_ms":1791280999000}'
  const busy = play([IMPORT]).view

  expect(play([lost, same], busy).notices).toEqual([])
  expect(label(play([lost, same], busy).view)).toBe('rql')
  expect(play([lost, other], busy).notices).toEqual(['RepoQL host restarted'])
  expect(play([lost, samePidLater], busy).notices).toEqual(['RepoQL host restarted'])
})

test('an rql that sends no signal key or numbers leaves the label at rql', () => {
  const old = play([
    '{"type":"host.signal","text":"progress — files 100/200","host_pid":41247,"host_started_at_unix_ms":1791280375986}',
    'not json',
    '[1,2]',
  ])

  expect(label(old.view)).toBe('rql')
  expect(line(old.view, 60_000)).toBe(null)
})

test('a slow call names its tool, and the index percentage only while the host is unsettled', () => {
  expect(reason(INITIAL)).toBe(null)
  expect(reason({ ...INITIAL, slowTool: 'explore' })).toBe('rql explore')
  expect(reason({ ...play([IMPORT]).view, slowTool: 'explore' })).toBe('rql explore · index 84%')
})
