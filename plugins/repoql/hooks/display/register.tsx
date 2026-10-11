import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { View } from '../../types'
import { H, RING_W, badge, ring } from './marks'
import { INITIAL, label, line, parse, reason, step } from './stream'

// A display for the host's own stream: one `rql monitor` child per session.
// It runs no query, calls no tool and never changes what the model reads.
const REPOQL_TOOL = /^mcp__(.*repoql.*|rql)__/i
const MONITOR = ['rql', 'monitor', '--jsonl', '--progress', '--progress-interval', '5s']
const SLOW_CALL_MS = 10_000
const RETRY_MS = 60_000

const view = atom({ plugin: 'repoql', key: 'view' } as const, INITIAL)

let isMonitoring = false
let retryAtMs = 0
let canDraw = false
const slow: string[] = []

async function take($: EngineInterface, text: string) {
  const record = parse(text)

  if (record === null) {
    return
  }

  const was = await read($, view)
  const { view: now, notice } = step(was, record, await $.clock.now())

  if (now !== was) {
    await update($, view, current => ({ ...now, slowTool: current.slowTool }))
  }

  if (notice !== null) {
    $.ui.toast(notice)
  }
}

/** Runs the child for as long as it lives. A child that exits is tried again after a minute, or after the next RepoQL call. */
async function monitor($: EngineInterface) {
  if (!canDraw || isMonitoring || (await $.clock.now()) < retryAtMs) {
    return
  }

  isMonitoring = true
  let tail = ''

  try {
    await update($, view, was => ({ ...was, slowTool: slow[0] ?? null }))

    for await (const { stream, text } of $.process.spawn({ argv: MONITOR })) {
      if (stream === 'stdout') {
        const lines = (tail + text).split('\n')
        tail = lines.pop() ?? ''

        for (const each of lines) {
          await take($, each)
        }
      }
    }
  } catch (error) {
    $.ui.log(`rql monitor did not run: ${String(error)}`, { to: 'debug' })
  }

  retryAtMs = (await $.clock.now()) + RETRY_MS
  isMonitoring = false
  await update($, view, (was): View => ({ ...was, connection: 'lost', operation: null, busySinceMs: null }))
}

/** Opens the host's live dashboard in the browser; `rql dashboard` knows the port and the secret. */
async function openDashboard($: EngineInterface) {
  try {
    await $.process.run(['rql', 'dashboard'], { timeoutMs: 15_000 })
  } catch (error) {
    $.ui.toast('Could not open the RepoQL dashboard')
    $.ui.log(`rql dashboard did not run: ${String(error)}`, { to: 'debug' })
  }
}

async function showSlow($: EngineInterface) {
  await update($, view, was => ({ ...was, slowTool: slow[0] ?? null }))
}

export const register: Register = (on, options) => {
  if (options.display === false) {
    return
  }

  // The child starts only once something can draw: a terminal at start, or a
  // surface that attaches later. Under `claude -p` and the SDK nothing runs.
  on('session.start', async ($, e, next) => {
    const started = await next(e)

    // A reload starts the module again with no surface named, though one is still attached.
    if (e.surface !== null || (await $.session.surfaces()).length > 0) {
      canDraw = true
      void monitor($)
    }

    return started
  })

  on('session.attach', async ($, e, next) => {
    const attached = await next(e)
    canDraw = true
    void monitor($)

    return attached
  })

  on('tool.call', { tool: REPOQL_TOOL }, async ($, e, next) => {
    const name = String(e.tool).split('__').pop() ?? 'tool'
    const timer = $.clock.after(SLOW_CALL_MS, () => {
      slow.push(name)
      void showSlow($)
    })

    try {
      return await next(e)
    } finally {
      timer.cancel()
      const at = slow.indexOf(name)

      if (at >= 0) {
        slow.splice(at, 1)
        void showSlow($)
      }

      retryAtMs = 0
      void monitor($)
    }
  }).catch(($, e, next) => next(e))

  on('ui.render', { component: 'SessionMode' }, async ($, e, next) =>
    next({ ...e, props: { ...e.props, modes: [...e.props.modes, label(await read($, view))] } }),
  )

  on('ui.render', { component: 'Spinner' }, async ($, e, next) => {
    const why = reason(await read($, view))

    return why === null ? next(e) : next({ ...e, props: { ...e.props, suffix: `${e.props.suffix} ${why}` } })
  })

  on('ui.render', { component: 'AbovePrompt' }, async ($, e, next) => {
    if (e.props.hasSurvey) {
      return next(e)
    }

    const now = await read($, view)
    const text = line(now, await $.clock.now())
    const { Box, Button, Text, ...rest } = $.ui.resolve(e)
    const Svg = (rest as { Svg?: any }).Svg

    // The terminal has the footer label for presence, so its band speaks only while there is work.
    if (e.surface === 'terminal' || !Svg) {
      return text === null ? next(e) : <Text dimColor wrap="truncate-end">rql · {text}</Text>
    }

    // A surface that draws SVG paints no footer label, so here the band is the
    // presence: the eye at rest, a ring while named work runs. The ring is a
    // plain image, since an interactive frame reloads, and flashes, on every
    // change. The eye never changes, so it can be a frame, and can look around.
    const hasHost = now.connection !== 'waiting' && now.connection !== 'lost'
    const resting = badge(hasHost)

    return (
      <Box>
        {text !== null && now.operation !== null ? (
          <Box>
            <Svg source={ring(now.operation.percent ?? 0)} alt={`rql ${now.operation.percent ?? 0}%`} width={RING_W} height={H} />
            <Text dimColor wrap="truncate-end"> {now.operation.name}</Text>
          </Box>
        ) : (
          <Svg source={resting.source} alt={hasHost ? 'rql' : 'rql no host'} width={resting.width} height={H} isInteractive={hasHost} />
        )}
        <Text> </Text>
        {hasHost && <Button key="dashboard" label="Dashboard" onPress={() => openDashboard($)} />}
      </Box>
    )
  })
}
