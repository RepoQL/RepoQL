import { atom, read, update } from 'claude-code'
import type { EngineInterface, Register } from 'claude-code'

import type { Uplink, View } from '../../types'
import { H, RING_W, UPLINK_W, badge, ring, uplinkMark } from './marks'
import { CARD_W, HERO_H, NOW_H, hero, nowCard, uplinkCard } from './pane'
import { INITIAL, SOURCES_SQL, label, line, offline, parse, reason, status, step, sourcesOf, uplinkNames } from './stream'

// A display for the host's own stream: one `rql monitor` child per session.
// It runs no query, calls no tool and never changes what the model reads.
const REPOQL_TOOL = /^mcp__(.*repoql.*|rql)__/i
const MONITOR = ['rql', 'monitor', '--jsonl', '--progress', '--progress-interval', '5s']
const SLOW_CALL_MS = 10_000
const RETRY_MS = 60_000
const PANE = 'repoql-status'
/** The most uplinks the band marks; the pane lists them all. */
const UPLINKS_SHOWN = 5

const view = atom({ plugin: 'repoql', key: 'view' } as const, INITIAL)

let isMonitoring = false
let retryAtMs = 0
let canDraw = false
let linkedHost: string | null = null
let isDiscovering = false
const slow: string[] = []

const hostKey = (now: View) => (now.host === null ? null : `${now.host.pid}:${now.host.startedAtUnixMs}`)

/** Asks the host for its dashboard link, once per host. The link carries the port and the secret, so only the host can say it. */
async function link($: EngineInterface, key: string) {
  try {
    const { exitCode, stdout } = await $.process.run(['rql', 'dashboard', '--url'], { timeoutMs: 15_000 })
    // A link is drawn only to `localhost` by name; the host answers to that name and to the address alike.
    const href = stdout.trim().replace(/^http:\/\/127\.0\.0\.1(?=[:/])/, 'http://localhost')

    if (exitCode === 0 && /^http:\/\/localhost[:/]\S*$/.test(href)) {
      await update($, view, was => (hostKey(was) === key && was.connection === 'connected' ? { ...was, dashboard: href } : was))
    }
  } catch (error) {
    $.ui.log(`rql dashboard --url did not run: ${String(error)}`, { to: 'debug' })
  }
}

/** Asks one uplink what it holds; an uplink that does not answer says why, in a few words. */
async function probe($: EngineInterface, name: string): Promise<Uplink> {
  try {
    const { exitCode, stdout, stderr } = await $.process.run(['rql', 'query', '--uplink', name, SOURCES_SQL], { timeoutMs: 20_000 })
    const sources = exitCode === 0 ? sourcesOf(stdout) : null

    return sources === null ? { name, state: 'offline', reason: offline(`${stdout}\n${stderr}`) } : { name, state: 'online', sources }
  } catch (error) {
    $.ui.log(`rql query --uplink ${name} did not run: ${String(error)}`, { to: 'debug' })

    return { name, state: 'offline', reason: offline('') }
  }
}

/**
 * Asks which uplinks the account can reach, then what each holds. Uplinks
 * belong to the account, not the host: an uplink keeps what it last said
 * until it answers again, and a failed `rql uplinks` keeps the list it had.
 */
async function discover($: EngineInterface) {
  if (isDiscovering) {
    return
  }

  isDiscovering = true

  try {
    const { exitCode, stdout } = await $.process.run(['rql', 'uplinks'], { timeoutMs: 15_000 })
    const names = exitCode === 0 ? uplinkNames(stdout) : null

    if (names === null) {
      return
    }

    await update($, view, (was): View => ({
      ...was,
      uplinks: names.map(name => was.uplinks?.find(each => each.name === name) ?? { name, state: 'checking' }),
    }))
    await Promise.all(
      names.map(async name => {
        const said = await probe($, name)

        await update($, view, (was): View => ({ ...was, uplinks: was.uplinks?.map(each => (each.name === name ? said : each)) ?? null }))
      }),
    )
  } catch (error) {
    $.ui.log(`rql uplinks did not run: ${String(error)}`, { to: 'debug' })
  } finally {
    isDiscovering = false
  }
}

async function take($: EngineInterface, text: string) {
  const record = parse(text)

  if (record === null) {
    return
  }

  const was = await read($, view)
  const { view: now, notice } = step(was, record, await $.clock.now())

  if (now !== was) {
    const key = hostKey(now)

    // The link is not the stream's to set: it stays while the host it came from stays.
    await update($, view, current => ({
      ...now,
      slowTool: current.slowTool,
      uplinks: current.uplinks,
      dashboard: now.connection === 'connected' && hostKey(current) === key ? current.dashboard : null,
    }))

    if (now.connection !== 'connected') {
      linkedHost = null
    } else if (key !== null && key !== linkedHost) {
      linkedHost = key
      void link($, key)
      void discover($)
    }
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
  linkedHost = null
  await update($, view, (was): View => ({ ...was, connection: 'lost', operation: null, busySinceMs: null, dashboard: null }))
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

/** Opens the status pane, or shows it when it is already open, and asks the uplinks again so the pane is current. */
async function openDetails($: EngineInterface) {
  void discover($)

  try {
    await $.ui.open({ id: PANE, title: 'RepoQL' })
  } catch (error) {
    $.ui.log(`the RepoQL status pane did not open: ${String(error)}`, { to: 'debug' })
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

  on('ui.render', { component: 'SessionMode' }, async ($, e, next) => {
    const now = await read($, view)
    const modes = [...e.props.modes, label(now)]

    if (e.surface !== 'terminal' || now.dashboard === null) {
      return next({ ...e, props: { ...e.props, modes } })
    }

    // The terminal has no pane to open, so its label carries the one thing
    // the pane is for: the way to the dashboard. A mode is a string, so the
    // line is drawn here, as the engine draws it, with the link at its end.
    // The link is underlined and at full brightness: in a dim line that is
    // the only thing that says it can be clicked.
    const { Link, Text } = $.ui.resolve(e)

    return (
      <Text>
        <Text dimColor>{modes.join(' & ')} · </Text>
        <Text underline>
          <Link href={now.dashboard} label="dashboard" />
        </Text>
      </Text>
    )
  })

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

    // The row spans the band: the badge at its leading edge, the button at its
    // trailing edge, both centred on the row's height. Beside the button, a
    // still eye for each uplink, lit while it answers. The button opens the
    // status pane, which says why there is no host, and what each uplink holds.
    return (
      <Box alignItems="center" justifyContent="space-between" width="100%">
        {text !== null && now.operation !== null ? (
          <Box>
            <Svg source={ring(now.operation.percent ?? 0)} alt={`rql ${now.operation.percent ?? 0}%`} width={RING_W} height={H} />
            <Text dimColor wrap="truncate-end"> {now.operation.name}</Text>
          </Box>
        ) : (
          <Svg source={resting.source} alt={hasHost ? 'rql' : 'rql no host'} width={resting.width} height={H} isInteractive={hasHost} />
        )}
        <Box alignItems="center" gap={1}>
          {(now.uplinks ?? []).slice(0, UPLINKS_SHOWN).map(each => (
            <Svg
              key={`uplink:${each.name}`}
              source={uplinkMark(each.state === 'online')}
              alt={each.state === 'online' ? `uplink ${each.name}` : `uplink ${each.name}, ${each.state === 'checking' ? 'checking' : each.reason.toLowerCase()}`}
              width={UPLINK_W}
              height={H}
            />
          ))}
          <Button key="details" label="Details" onPress={() => openDetails($)} />
        </Box>
      </Box>
    )
  })

  // The pane answers four things and no more: is RepoQL working, what is it
  // doing, which shared indexes can it reach, and where is the rest. It is drawn from the view the band reads.
  on('ui.render', { component: 'Pane', requestId: PANE }, async ($, e) => {
    const now = await read($, view)
    const { state, fact, release, isAwake } = status(now)
    const said = [fact, release].filter(each => each !== '').join(' · ')
    const work = now.operation
    const doing = work === null ? null : work.percent === null ? work.name : `${work.name} ${work.percent}%`
    const uplinks = isAwake ? (now.uplinks ?? []) : []
    const { Box, Button, Text, ...rest } = $.ui.resolve(e)
    const Svg = (rest as { Svg?: any }).Svg

    // Nothing opens the pane where nothing draws it: the terminal's footer has the link instead.
    if (!Svg) {
      return <Text>RepoQL · {state}</Text>
    }

    // One column, centred in the pane. The head changes only with the host's
    // state, so it is a frame and its eye and network move; the work card
    // changes with every percentage, so it is a plain image and does not flash.
    return (
      <Box flexDirection="column" alignItems="center" width="100%">
        <Box flexDirection="column" gap={1}>
          <Svg source={hero(state, fact, release, isAwake)} alt={said === '' ? `RepoQL: ${state}` : `RepoQL: ${state}. ${said}`} width={CARD_W} height={HERO_H} isInteractive={isAwake} />
          {work !== null && <Svg source={nowCard(work.name, work.percent)} alt={doing} width={CARD_W} height={NOW_H} />}
          {uplinks.map(each => {
            const drawn = uplinkCard(each)
            const said =
              each.state === 'online'
                ? [`${each.name}: online`, ...each.sources.map(source => `${source.uri} ${source.files} files`)].join('. ')
                : `${each.name}: ${each.state === 'checking' ? 'checking' : each.reason.toLowerCase()}`

            return <Svg key={`uplink:${each.name}`} source={drawn.source} alt={said} width={CARD_W} height={drawn.height} />
          })}
          {isAwake && (
            <Box justifyContent="center" marginTop={1}>
              <Button key="dashboard" label="Open dashboard" onPress={() => openDashboard($)} />
            </Box>
          )}
        </Box>
      </Box>
    )
  })
}
