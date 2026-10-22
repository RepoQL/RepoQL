import type { View } from '../../types'

/** A progress line is for work the person had time to notice. */
export const BAND_AFTER_MS = 10_000

export const INITIAL: View = {
  connection: 'unknown',
  host: null,
  operation: null,
  busySinceMs: null,
  version: null,
  files: null,
  dashboard: null,
  uplinks: null,
  slowTool: null,
}

/** The names in what `rql uplinks` prints: `Accessible uplinks: a, b`, or `No accessible uplinks.`; null when it says neither. */
export function uplinkNames(stdout: string): string[] | null {
  const said = stdout.split('\n').map(each => each.trim())

  if (said.includes('No accessible uplinks.')) {
    return []
  }

  const names = said.find(each => each.startsWith('Accessible uplinks: '))

  return names === undefined
    ? null
    : names
        .slice('Accessible uplinks: '.length)
        .split(',')
        .map(each => each.trim())
        .filter(each => each !== '')
}

export type Step = { view: View; notice: string | null }

const number = (value: unknown) => (typeof value === 'number' && Number.isFinite(value) ? value : null)

const percentOf = (done: unknown, total: unknown) => {
  const all = number(total)
  const complete = number(done)

  return all === null || complete === null || all <= 0 ? null : Math.min(100, Math.floor((complete * 100) / all))
}

/**
 * The host's operation in a new user's words, or null for work the display
 * stays quiet about: the host's own housekeeping (`sweep`, `analyze`), another
 * session's worktree being mounted, and any operation this build does not know.
 * `filesystem` is the address the host sends for an import; an older host sends
 * none, and the address is then read from the operation's name.
 */
export function describe(operation: unknown, filesystem?: unknown) {
  if (typeof operation !== 'string') {
    return null
  }

  if (operation === 'startup') {
    return 'indexing this repository'
  }

  if (!operation.startsWith('import:')) {
    return null
  }

  // The name carries a full URI for a connector or a local mount,
  // `github.com/owner/name` for GitHub, and host/path for other git.
  const source = operation.slice('import:'.length).replace(/^\/+|\/+$/g, '')
  const named = source.startsWith('github.com/') ? `github://${source.slice('github.com/'.length)}` : source
  const address = typeof filesystem === 'string' && filesystem !== '' ? filesystem.replace(/\/+$/, '') : named

  return address === '' || address.startsWith('worktree://') ? null : `importing ${address}`
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
      view = { ...view, operation: null, busySinceMs: null, version: null, files: null, host: { pid, startedAtUnixMs } }
    }

    if (view.connection !== 'connected') {
      view = { ...view, connection: 'connected' }
    }
  }

  if (typeof record.version === 'string' && record.version !== '' && record.version !== view.version) {
    view = { ...view, version: record.version }
  }

  if (record.signal !== 'progress') {
    return { view, notice }
  }

  const files = number(record.registry_complete)

  if (files !== null && files !== view.files) {
    view = { ...view, files }
  }

  if (record.settled === true) {
    if (view.busySinceMs === null && view.operation === null) {
      return { view, notice }
    }

    // The work leaving the screen is the news; no notice says it again.
    return { view: { ...view, operation: null, busySinceMs: null }, notice }
  }

  const what = describe(record.operation, record.operation_filesystem)

  if (what === null) {
    // Housekeeping between or after named work: hide the line.
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

/** The footer's label: the name and the state, so the name alone never has to be read as a state. */
export function label(view: View) {
  const state = status(view).state.toLowerCase()

  return view.operation?.percent == null ? `rql · ${state}` : `rql · ${state} ${view.operation.percent}%`
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

export type Status = { state: string; fact: string; release: string; isAwake: boolean }

const grouped = (count: number) => String(count).replace(/\B(?=(\d{3})+$)/g, ',')

/** The pane's head: one word for the host's state, one line of what the host itself has said, and its release. */
export function status(view: View): Status {
  if (view.connection === 'waiting' || view.connection === 'lost') {
    return { state: 'Not running', fact: 'It starts with your next RepoQL call.', release: '', isAwake: false }
  }

  if (view.connection === 'unknown') {
    return { state: 'Connecting', fact: 'Looking for the RepoQL host.', release: '', isAwake: false }
  }

  return {
    state: view.operation === null ? 'Ready' : 'Working',
    fact: view.files === null ? '' : `${grouped(view.files)} files ready to search`,
    release: view.version === null ? '' : `rql ${view.version}`,
    isAwake: true,
  }
}
