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

  on('process.spawn', async function* (_$, e) {
    argvs.push([...e.argv])
    // split mid-record: pieces are text as it came, not lines
    yield { stream: 'stdout', text: IMPORT.slice(0, 40) }
    yield { stream: 'stdout', text: IMPORT.slice(40) }
    // the monitor outlives a settled host; here it lives until the test ends it
    await exited

    return { value: { code: 0, signal: null } }
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
  expect((await ui.find({ type: 'Text' }))?.text).toBe('focus & rql 84%')
  await ui.unmount()
  expect(argvs).toEqual([['rql', 'monitor', '--jsonl', '--progress', '--progress-interval', '5s']])

  // the child exits: the label says so, and one retry is made a minute later, not before
  exit()
  await attached
  const after = await $.ui.mount({ plugin: 'repoql', surface: 'terminal', component: 'SessionMode', props: { modes: [] } })
  expect((await after.find({ type: 'Text' }))?.text).toBe('rql no host')
  await after.unmount()
})
