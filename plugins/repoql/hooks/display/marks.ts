// What the display draws on a surface that takes SVG: the dashboard's eye at
// rest, and a ring while named work runs. Colours are RepoQL's own.
const BG = '#060d14'
const NET = '#2dd4bf'
const PULSE = '#5eead4'
const TEXT = '#d0ece4'
const DIM = '#6f8f8a'
const FONT = 'font-family="ui-sans-serif,system-ui,sans-serif"'
const MONO = 'font-family="ui-monospace,SFMono-Regular,Menlo,monospace"'

export const H = 26

const svg = (width: number, height: number, body: string) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}">${body}</svg>`

// The dashboard's eye, from its iris renderer: three rings (read, search, deep)
// around a pupil that glances about. Drawn in the renderer's 360-unit space.
const EYE = { read: '#2f6f9e', search: '#c99a2e', deep: '#2fa173', off: '#465160' }
// The dashboard's saccade table darts eleven times in eighteen seconds, which is
// lively on a large eye and restless on a badge that sits by the prompt all day.
// Here the eye rests at centre and takes three unhurried glances in each cycle.
const GLANCES = [
  [0, 0, 0], [0.05, 0, 0], [0.07, -14, -9], [0.13, -14, -9], [0.15, 0, 0],
  [0.4, 0, 0], [0.42, 15, -4], [0.48, 15, -4], [0.5, 0, 0],
  [0.72, 0, 0], [0.74, 8, 11], [0.79, 8, 11], [0.81, 0, 0], [1, 0, 0],
] as const
// The first glance comes two seconds in, so an eye that is redrawn still shows it is alive.
const GLANCE_SECONDS = 36

function eye(x: number, y: number, size: number, isAwake: boolean) {
  // A settled index is three full rings, as the dashboard draws it.
  const rings = ([['read', 65], ['search', 85], ['deep', 105]] as const)
    .map(
      ([stage, radius]) =>
        `<circle cx="180" cy="180" r="${radius}" fill="none" stroke="${isAwake ? EYE[stage] : EYE.off}" stroke-opacity="${isAwake ? 1 : 0.45}" stroke-width="20"/>`,
    )
    .join('')
  const darts = GLANCES.map(([, dx, dy]) => `${dx} ${dy}`).join(';')
  const times = GLANCES.map(([at]) => at).join(';')
  const pupil = isAwake
    ? `<g><animateTransform attributeName="transform" type="translate" values="${darts}" keyTimes="${times}" dur="${GLANCE_SECONDS}s" repeatCount="indefinite"/>
<circle cx="180" cy="180" r="27" fill="url(#p)"><animate attributeName="r" values="26.5;28;26.5" dur="7s" repeatCount="indefinite"/></circle></g>`
    : `<circle cx="180" cy="180" r="26" fill="${EYE.off}"/>`

  return `<svg x="${x}" y="${y}" width="${size}" height="${size}" viewBox="60 60 240 240">
<defs><radialGradient id="p"><stop offset="0" stop-color="#dbf7ec"/><stop offset="1" stop-color="#3da878"/></radialGradient></defs>
<circle cx="180" cy="180" r="116" fill="${BG}"/>${rings}${pupil}</svg>`
}

/** The resting look: the dashboard's eye in miniature, awake while a host is running. */
export function badge(hasHost: boolean) {
  const width = hasHost ? 58 : 112

  return {
    width,
    source: svg(
      width,
      H,
      `<rect x="1" y="1" width="${width - 2}" height="${H - 2}" rx="12" fill="${BG}" stroke="${NET}" stroke-opacity="${hasHost ? 0.35 : 0.15}"/>
${eye(3, 2, 22, hasHost)}
<text x="30" y="17.5" ${FONT} font-size="12" font-weight="700" fill="${hasHost ? TEXT : DIM}">rql</text>
${hasHost ? '' : `<text x="${width - 11}" y="17.5" text-anchor="end" ${MONO} font-size="10.5" fill="${DIM}">no host</text>`}`,
    ),
  }
}

/** Work under way: a ring that fills, then a tick. What the work is sits beside it as text. */
export const RING_W = 78
export function ring(pct: number) {
  const radius = 6.5
  const around = 2 * Math.PI * radius
  const drawn = (around * Math.min(100, pct)) / 100
  const done = pct >= 100

  return svg(
    RING_W,
    H,
    `<rect x="1" y="1" width="${RING_W - 2}" height="${H - 2}" rx="12" fill="${BG}" stroke="${NET}" stroke-opacity=".35"/>
<circle cx="15" cy="13" r="${radius}" fill="none" stroke="${NET}" stroke-opacity=".2" stroke-width="2.4"/>
<circle cx="15" cy="13" r="${radius}" fill="none" stroke="${done ? NET : PULSE}" stroke-width="2.4" stroke-linecap="round" stroke-dasharray="${drawn} ${around - drawn}" transform="rotate(-90 15 13)"/>
${done ? `<path d="M11.8 13.2l2.2 2.2 4-4.4" fill="none" stroke="${NET}" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"/>` : ''}
<text x="28" y="17.5" ${FONT} font-size="12" font-weight="700" fill="${TEXT}">rql</text>
<text x="${RING_W - 9}" y="17.5" text-anchor="end" ${MONO} font-size="10.5" fill="${done ? NET : DIM}">${done ? '' : `${pct}%`}</text>`,
  )
}
