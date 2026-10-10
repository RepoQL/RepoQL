import type { View } from '../../types'

/** A progress line and a settled notice are for work the person had time to notice. */
export const LONG_WAIT_MS = 30_000
export const BAND_AFTER_MS = 10_000

export const INITIAL: View = {
  connection: 'unknown',
  host: null,
  percent: null,
  operation: null,
  busySinceMs: null,
  slowTool: null,
}

export type Step = { view: View; notice: string | null }

const QUIET = { percent: null, operation: null, busySinceMs: null }

const number = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : null)

const percentOf = (done: unknown, total: unknown) => {
  const all = number(total)
  const complete = number(done)

  return all === null || complete === null || all <= 0 ? null : Math.min(100, Math.floor((complete * 100) / all))
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
    return { view: { ...was, ...QUIET, connection: record.state }, notice: null }
  }

  const pid = number(record.host_pid)
  const startedAtUnixMs = number(record.host_started_at_unix_ms)
  let view = was
  let notice: string | null = null

  if (pid !== null && startedAtUnixMs !== null) {
    const isSameHost = was.host?.pid === pid && was.host.startedAtUnixMs === startedAtUnixMs

    if (!isSameHost) {
      notice = was.host === null ? null : 'RepoQL host restarted'
      view = { ...view, ...QUIET, host: { pid, startedAtUnixMs } }
    }

    if (view.connection !== 'connected') {
      view = { ...view, connection: 'connected' }
    }
  }

  if (record.signal !== 'progress') {
    return { view, notice }
  }

  if (record.settled === true) {
    const waitedMs = view.busySinceMs === null ? 0 : nowMs - view.busySinceMs

    return view.busySinceMs === null
      ? { view, notice }
      : { view: { ...view, ...QUIET }, notice: waitedMs >= LONG_WAIT_MS ? 'RepoQL index settled' : notice }
  }

  const percent = percentOf(record.registry_complete, record.registry_total)

  if (percent === null) {
    return { view, notice }
  }

  const name = typeof record.operation === 'string' ? record.operation : ''
  const operation = name === '' ? null : { name, percent: percentOf(record.operation_done, record.operation_total) }
  const isSame =
    view.percent === percent && view.operation?.name === operation?.name && view.operation?.percent === operation?.percent

  return isSame
    ? { view, notice }
    : { view: { ...view, percent, operation, busySinceMs: view.busySinceMs ?? nowMs }, notice }
}

export function label(view: View) {
  if (view.connection === 'waiting' || view.connection === 'lost') {
    return 'rql no host'
  }

  return view.percent === null ? 'rql' : `rql ${view.percent}%`
}

/** `import:github.com/owner/name` reads as `import name`. */
export function line(view: View, nowMs: number) {
  if (view.operation === null || view.busySinceMs === null || nowMs - view.busySinceMs < BAND_AFTER_MS) {
    return null
  }

  const [kind, ...rest] = view.operation.name.split(':')
  const target = rest.join(':').split('/').pop() ?? ''
  const what = target === '' ? kind : `${kind} ${target}`
  const percent = view.operation.percent ?? view.percent

  return percent === null ? what : `${what} ${percent}%`
}

export function reason(view: View) {
  if (view.slowTool === null) {
    return null
  }

  return view.percent === null ? `rql ${view.slowTool}` : `rql ${view.slowTool} · index ${view.percent}%`
}
