import type { View } from '../../types'

/** A progress line and a settled notice are for work the person had time to notice. */
export const LONG_WAIT_MS = 30_000
export const BAND_AFTER_MS = 10_000

export const INITIAL: View = {
  connection: 'unknown',
  host: null,
  operation: null,
  busySinceMs: null,
  slowTool: null,
}

export type Step = { view: View; notice: string | null }

const number = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : null)

const percentOf = (done: unknown, total: unknown) => {
  const all = number(total)
  const complete = number(done)

  return all === null || complete === null || all <= 0 ? null : Math.min(100, Math.floor((complete * 100) / all))
}

/**
 * The host's operation name in a new user's words, or null for work the
 * display stays quiet about: the host's own housekeeping (`sweep`, `analyze`)
 * and any operation this build does not know.
 */
export function describe(operation: unknown) {
  if (typeof operation !== 'string') {
    return null
  }

  if (operation === 'startup') {
    return 'indexing this repository'
  }

  if (!operation.startsWith('import:')) {
    return null
  }

  // The name carries the import's address: a full URI for a connector or a
  // local mount, `github.com/owner/name` for GitHub, host/path for other git.
  const source = operation.slice('import:'.length).replace(/^\/+|\/+$/g, '')
  const address = source.startsWith('github.com/') ? `github://${source.slice('github.com/'.length)}` : source

  return address === '' ? null : `importing ${address}`
}

/** One line of `rql monitor --jsonl`, or null when it is not a record. */
export function parse(line: string): Record<string, unknown> | null {
  try {
    const record: unknown = JSON.parse(line)

    return typeof record === 'object' && record !== null && !Array.isArray(record)
      ? (record as Record<string, unknown>)
      : null
  } catch {
    return null
  }
}

/**
 * Folds one monitor record into the view. Returns the same view when the
 * record changes nothing, so a burst of history costs no redraw.
 */
export function step(was: View, record: Record<string, unknown>, nowMs: number): Step {
  if (record.type === 'monitor.connection' && (record.state === 'waiting' || record.state === 'lost')) {
    return { view: { ...was, operation: null, busySinceMs: null, connection: record.state }, notice: null }
  }

  const pid = number(record.host_pid)
  const startedAtUnixMs = number(record.host_started_at_unix_ms)
  let view = was
  let notice: string | null = null

  if (pid !== null && startedAtUnixMs !== null) {
    const isSameHost = was.host?.pid === pid && was.host.startedAtUnixMs === startedAtUnixMs

    if (!isSameHost) {
      notice = was.host === null ? null : 'RepoQL host restarted'
      view = { ...view, operation: null, busySinceMs: null, host: { pid, startedAtUnixMs } }
    }

    if (view.connection !== 'connected') {
      view = { ...view, connection: 'connected' }
    }
  }

  if (record.signal !== 'progress') {
    return { view, notice }
  }

  if (record.settled === true) {
    if (view.busySinceMs === null && view.operation === null) {
      return { view, notice }
    }

    const waitedMs = view.busySinceMs === null ? 0 : nowMs - view.busySinceMs

    return {
      view: { ...view, operation: null, busySinceMs: null },
      notice: waitedMs >= LONG_WAIT_MS ? 'RepoQL index settled' : notice,
    }
  }

  const what = describe(record.operation)

  if (what === null) {
    // Housekeeping between or after named work: hide the line, keep the stretch's start for the settled notice.
    return view.operation === null ? { view, notice } : { view: { ...view, operation: null }, notice }
  }

  const percent = percentOf(record.operation_done, record.operation_total)

  return view.operation?.name === what && view.operation.percent === percent
    ? { view, notice }
    : { view: { ...view, operation: { name: what, percent }, busySinceMs: view.busySinceMs ?? nowMs }, notice }
}

const progress = (view: View) =>
  view.operation === null
    ? null
    : view.operation.percent === null
      ? view.operation.name
      : `${view.operation.name} ${view.operation.percent}%`

export function label(view: View) {
  if (view.connection === 'waiting' || view.connection === 'lost') {
    return 'rql no host'
  }

  return view.operation?.percent == null ? 'rql' : `rql ${view.operation.percent}%`
}

export function line(view: View, nowMs: number) {
  return view.busySinceMs === null || nowMs - view.busySinceMs < BAND_AFTER_MS ? null : progress(view)
}

export function reason(view: View) {
  if (view.slowTool === null) {
    return null
  }

  const doing = progress(view)

  return doing === null ? `rql ${view.slowTool}` : `rql ${view.slowTool} · ${doing}`
}
