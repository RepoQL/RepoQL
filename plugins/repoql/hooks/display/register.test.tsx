import { expect, mock, test } from 'claude-code/testing'

const IMPORT =
  '{"type":"host.signal","signal":"progress","host_pid":41247,"host_started_at_unix_ms":1791280375986,"operation":"import:github.com/enricomi/publish-unit-test-result-action","operation_total":147458,"operation_done":122330,"registry_total":216906,"registry_complete":182598}\n'

test('the label is drawn from the monitor child on each surface that has a footer', { timeoutMs: 20_000 }, async ($, on) => {
  mock.clock(on)
  let exit = () => {}
  const exited = new Promise<void>(resolve => {
    exit = resolve
  })
  const argvs: string[][] = []
  const ran: string[][] = []

  on('process.spawn', async function* (_$, e) {
    argvs.push([...e.argv])
    // split mid-record: pieces are text as it came, not lines
    yield { stream: 'stdout', text: IMPORT.slice(0, 40) }
    yield { stream: 'stdout', text: IMPORT.slice(40) }
    // the monitor outlives a settled host; here it lives until the test ends it
    await exited

    return { value: { code: 0, signal: null } }
  })

  on('process.run', (_$, e) => {
    ran.push([...e.argv])

    return { value: { exitCode: 0, stdout: 'http://127.0.0.1:5173/#k=abc\n', stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })

  const drawn: string[][] = []
  const logged: string[] = []

  on('ui.log', (_$, e) => {
    logged.push(e.text)

    return { value: undefined }
  })

  on('ui.render', { component: 'SessionMode' }, ($, e) => {
    const { Text } = $.ui.resolve(e)
    drawn.push([...e.props.modes])

    return <Text>{e.props.modes.join(' & ')}</Text>
  })

  on('session.attach', (_$, e) => ({ clientId: e.clientId }))

  // nothing can draw yet, so no child runs
  const before = await $.ui.mount({ plugin: 'repoql', surface: 'terminal', component: 'SessionMode', props: { modes: [] } })
  await before.unmount()
  expect(argvs).toEqual([])
  // not awaited: the attach settles only when the child it started has exited
  const attached = $.session.attach({ surface: 'desktop', clientId: 'desktop:default' })

  for (const surface of ['terminal', 'desktop'] as const) {
    const ui = await $.ui.mount({ plugin: 'repoql', surface, component: 'SessionMode', props: { modes: ['focus'] } })
    await ui.unmount()
  }

  // the first draw started the child; every draw after its record shows the host's percentage
  const ui = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'SessionMode', props: { modes: ['focus'] } })
  expect(logged).toEqual([])
  expect((await ui.find({ type: 'Text' }))?.text).toBe('focus & rql · working 82%')
  await ui.unmount()

  // the terminal has no pane, so its label ends in a link to the dashboard, asked of the host once
  const linked = await $.ui.mount({ plugin: 'repoql', surface: 'terminal', component: 'SessionMode', props: { modes: ['focus'] } })
  // underlined at full brightness in a dim line, so it reads as something to click
  expect((await linked.find({ type: 'Link' }))?.props).toEqual({ href: 'http://localhost:5173/#k=abc', label: 'dashboard' })
  expect((await linked.findAll({ type: 'Text' })).map(each => [each.text, each.props])).toEqual([
    ['focus & rql · working 82% · dashboard', {}],
    ['focus & rql · working 82% · ', { dimColor: true }],
    ['dashboard', { underline: true }],
  ])
  await linked.unmount()
  expect(ran).toEqual([['rql', 'dashboard', '--url'], ['rql', 'uplinks']])
  expect(argvs).toEqual([['rql', 'monitor', '--jsonl', '--progress', '--progress-interval', '5s']])

  // the child exits: the label says so, and one retry is made a minute later, not before
  exit()
  await attached
  const after = await $.ui.mount({ plugin: 'repoql', surface: 'terminal', component: 'SessionMode', props: { modes: [] } })
  expect(await after.find({ type: 'Link' })).toBeUndefined()
  expect((await after.find({ type: 'Text' }))?.text).toBe('rql · not running')
  await after.unmount()
})

// The band's own props, as an idle prompt hands them; only `hasSurvey` matters to the mod.
const band = (hasSurvey: boolean) =>
  ({ hasSurvey, isWorking: false, maxRows: 6, bodyColumns: 80, scroll: { top: 0, rows: 0 }, view: { top: 0, rows: 0 } }) as never

test('where SVG is drawn the band is the presence: the eye at rest, and a button that opens the status pane', { timeoutMs: 20_000 }, async ($, on) => {
  mock.clock(on)
  const ran: string[][] = []

  on('process.run', (_$, e) => {
    ran.push([...e.argv])

    return { value: { exitCode: 0, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('ui.render', { component: 'AbovePrompt' }, ($, e) => {
    const { Text } = $.ui.resolve(e)

    return <Text>nothing from the mod</Text>
  })

  const opened: string[] = []

  on('ui.open', (_$, e) => {
    opened.push(`${e.id}: ${e.title}`)

    return { value: { isPlaced: true } }
  })

  const desktop = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'AbovePrompt', props: band(false) })
  expect((await desktop.find({ type: 'Svg' }))?.props.alt).toBe('rql')
  await desktop.press({ key: 'details' })
  expect(opened).toEqual(['repoql-status: RepoQL'])
  expect(ran).toEqual([])
  await desktop.unmount()

  // the terminal has its footer label, so a settled host draws nothing in its band
  const terminal = await $.ui.mount({ plugin: 'repoql', surface: 'terminal', component: 'AbovePrompt', props: band(false) })
  expect((await terminal.find({ type: 'Text' }))?.text).toBe('nothing from the mod')
  await terminal.unmount()

  // a survey holds the band: the mod yields to it on every surface
  const survey = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'AbovePrompt', props: band(true) })
  expect(await survey.find({ type: 'Svg' })).toBeUndefined()
  await survey.unmount()
})

const HELLO = '{"type":"host.signal","signal":"hello","host_pid":41247,"host_started_at_unix_ms":1791280375986,"version":"1.7.13"}\n'
const pane = { title: 'RepoQL' } as never
const UPLINKS = 'Accessible uplinks: fotg-gcp, stu-pc\nPass uplink="name" to a read-only RepoQL tool, or --uplink name to the CLI. Omit it for the local repository.\n'

test('the status pane says the state, the work under way, the uplinks within reach, and opens the dashboard', { timeoutMs: 20_000 }, async ($, on) => {
  mock.clock(on)
  let exit = () => {}
  const exited = new Promise<void>(resolve => {
    exit = resolve
  })
  const ran: string[][] = []

  on('process.spawn', async function* () {
    yield { stream: 'stdout', text: HELLO + IMPORT }
    await exited

    return { value: { code: 0, signal: null } }
  })
  on('process.run', (_$, e) => {
    ran.push([...e.argv])
    const stdout = e.argv[1] === 'uplinks' ? UPLINKS : ''

    return { value: { exitCode: 0, stdout, stderr: '', isStdoutTruncated: false, isStderrTruncated: false } }
  })
  on('session.attach', (_$, e) => ({ clientId: e.clientId }))

  // before any surface attaches the mod has heard nothing, and offers no dashboard
  const early = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'Pane', requestId: 'repoql-status', props: pane })
  expect((await early.find({ type: 'Svg' }))?.props.alt).toBe('RepoQL: Connecting. Looking for the RepoQL host.')
  expect(await early.find({ key: 'dashboard' })).toBeUndefined()
  await early.unmount()

  const attached = $.session.attach({ surface: 'desktop', clientId: 'desktop:default' })
  const warm = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'AbovePrompt', props: band(false) })
  await warm.unmount()

  const desktop = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'Pane', requestId: 'repoql-status', props: pane })
  const alts = (await desktop.findAll({ type: 'Svg' })).map(each => each.props.alt)
  expect(alts).toEqual([
    'RepoQL: Working. 182,598 files ready to search · rql 1.7.13',
    'importing github://enricomi/publish-unit-test-result-action 82%',
    'Uplinks: fotg-gcp, stu-pc',
  ])
  await desktop.press({ key: 'dashboard' })
  expect(ran).toEqual([['rql', 'dashboard', '--url'], ['rql', 'uplinks'], ['rql', 'dashboard']])
  await desktop.unmount()

  // the host goes: the pane says how it comes back, and has no dashboard to open
  exit()
  await attached
  const gone = await $.ui.mount({ plugin: 'repoql', surface: 'desktop', component: 'Pane', requestId: 'repoql-status', props: pane })
  expect((await gone.findAll({ type: 'Svg' })).map(each => each.props.alt)).toEqual(['RepoQL: Not running. It starts with your next RepoQL call.'])
  expect(await gone.find({ key: 'dashboard' })).toBeUndefined()
  await gone.unmount()
})
