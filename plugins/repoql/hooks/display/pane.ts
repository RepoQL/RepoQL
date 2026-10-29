import type { Uplink } from '../../types'
import { eye } from './marks'
import { sourceRows, uplinkState } from './stream'

// The status pane, drawn as cards in RepoQL's own look: deep navy, teal
// network, teal-tinted white. Each card is one SVG at a fixed width.
const BG = '#060d14'
const RAISED = '#0b1620'
const NET = '#2dd4bf'
const PULSE = '#5eead4'
const TEXT = '#d0ece4'
const DIM = '#6f8f8a'
const SANS = 'font-family="ui-sans-serif,system-ui,-apple-system,sans-serif"'

export const CARD_W = 520
export const HERO_H = 168
export const NOW_H = 84
/** The longest line of work a card holds at its type size; a longer one is cut. */
const NOW_CHARS = 60

const esc = (text: string) => text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/"/g, '&quot;')
const card = (height: number, body: string) =>
  `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${CARD_W} ${height}" width="${CARD_W}" height="${height}">${body}</svg>`

// A mycelium growing in from the right edge. Each thread leaves its parent at a
// shallow angle, thins and fades as it goes, and forks until it is hair-fine;
// only the last tips carry a node. Grown once from a fixed seed, so it never shifts.
type Thread = { path: string; width: number; opacity: number }
type Tip = { x: number; y: number }

function grow() {
  let seed = 20261011
  const random = () => {
    seed = (seed * 1664525 + 1013904223) % 4294967296

    return seed / 4294967296
  }
  const threads: Thread[] = []
  const tips: Tip[] = []
  const reach = (x: number, y: number, angle: number, length: number, depth: number) => {
    const bend = (random() - 0.5) * 0.9
    const endAngle = angle + bend
    const midX = x + Math.cos(angle + bend * 0.2) * length * 0.5
    const midY = y + Math.sin(angle + bend * 0.2) * length * 0.5
    const endX = x + Math.cos(angle) * length * 0.55 + Math.cos(endAngle) * length * 0.45
    const endY = y + Math.sin(angle) * length * 0.55 + Math.sin(endAngle) * length * 0.45

    threads.push({
      path: `M${x.toFixed(1)} ${y.toFixed(1)}Q${midX.toFixed(1)} ${midY.toFixed(1)} ${endX.toFixed(1)} ${endY.toFixed(1)}`,
      width: Math.max(0.45, 1.5 - depth * 0.28),
      opacity: Math.max(0.1, 0.34 - depth * 0.055),
    })

    const isSpent = depth >= 5 || length < 16 || endX < 356 || endY < 10 || endY > HERO_H - 10

    if (isSpent) {
      tips.push({ x: endX, y: endY })

      return
    }

    const forks = random() < 0.72 ? 2 : 1

    for (let fork = 0; fork < forks; fork += 1) {
      const lean = forks === 1 ? (random() - 0.5) * 0.5 : (fork === 0 ? -1 : 1) * (0.28 + random() * 0.42)

      reach(endX, endY, endAngle + lean, length * (0.66 + random() * 0.2), depth + 1)
    }
  }

  reach(CARD_W + 4, 46, Math.PI * 1.02, 62, 0)
  reach(CARD_W + 4, 118, Math.PI * 0.97, 70, 0)
  reach(CARD_W + 4, 84, Math.PI, 40, 1)

  return { threads, tips }
}

const MYCELIUM = grow()

/** The head of the pane: the eye, one word of state, one line of fact, and the release in small type. Without a host the eye sleeps and the network is gone. */
export function hero(state: string, fact: string, release: string, isAwake: boolean) {
  const lines = [fact, release].filter(each => each !== '')
  const top = 94 - lines.length * 10
  const threads = MYCELIUM.threads
    .map(each => `<path d="${each.path}" fill="none" stroke="${NET}" stroke-opacity="${each.opacity.toFixed(2)}" stroke-width="${each.width.toFixed(2)}" stroke-linecap="round"/>`)
    .join('')
  const tips = MYCELIUM.tips
    .map((tip, index) => {
      const glows = index % 4 === 0
      const pulse = glows ? `<animate attributeName="opacity" values=".3;.95;.3" dur="${6 + (index % 5) * 1.3}s" repeatCount="indefinite"/>` : ''

      return `<circle cx="${tip.x.toFixed(1)}" cy="${tip.y.toFixed(1)}" r="${glows ? 1.7 : 1.1}" fill="${glows ? PULSE : NET}" opacity="${glows ? 0.6 : 0.4}">${pulse}</circle>`
    })
    .join('')

  return card(
    HERO_H,
    `<defs><clipPath id="h"><rect width="${CARD_W}" height="${HERO_H}" rx="16"/></clipPath>
<linearGradient id="f" x1="0" x2="1"><stop offset=".58" stop-color="#fff" stop-opacity="0"/><stop offset=".8" stop-color="#fff" stop-opacity="1"/></linearGradient><mask id="fade"><rect width="${CARD_W}" height="${HERO_H}" fill="url(#f)"/></mask>
<radialGradient id="glow" cx="18%" cy="50%" r="55%"><stop offset="0" stop-color="${NET}" stop-opacity=".16"/><stop offset="1" stop-color="${NET}" stop-opacity="0"/></radialGradient></defs>
<g clip-path="url(#h)"><rect width="${CARD_W}" height="${HERO_H}" fill="${BG}"/>${isAwake ? `<rect width="${CARD_W}" height="${HERO_H}" fill="url(#glow)"/><g mask="url(#fade)">${threads}${tips}</g>` : ''}</g>
<rect x=".5" y=".5" width="${CARD_W - 1}" height="${HERO_H - 1}" rx="16" fill="none" stroke="${NET}" stroke-opacity=".18"/>
${eye(34, 34, 100, isAwake)}
<text x="164" y="${top}" ${SANS} font-size="26" font-weight="600" letter-spacing="-.3" fill="${TEXT}">${esc(state)}</text>
${lines.map((each, index) => `<text x="165" y="${top + 26 + index * 20}" ${SANS} font-size="${each === release ? 11.5 : 13.5}" fill="${DIM}"${each === release ? ' fill-opacity=".75"' : ''}>${esc(each)}</text>`).join('')}`,
  )
}

/** The most lines a card lists: sources one to a line, or counted by scheme when there are more. */
export const SOURCES_SHOWN = 8

/** One uplink: a still eye, its name, whether it answers, and the sources it holds, by scheme when there are many. */
export function uplinkCard(uplink: Uplink) {
  const isOnline = uplink.state === 'online'
  const all = sourceRows(isOnline ? uplink.sources : [], SOURCES_SHOWN)
  const shown = all.slice(0, SOURCES_SHOWN)
  const more = all.length - shown.length
  const state = uplinkState(uplink)
  const said = `${state.charAt(0).toUpperCase()}${state.slice(1)}${uplink.state === 'checking' ? '…' : ''}`
  const rows = shown.length + (more > 0 ? 1 : 0)
  const height = 64 + (rows === 0 ? 0 : 10 + rows * 19)
  const cut = (text: string) => (text.length > NOW_CHARS - 12 ? `${text.slice(0, NOW_CHARS - 13)}…` : text)

  return {
    height,
    source: card(
      height,
      `<rect x=".5" y=".5" width="${CARD_W - 1}" height="${height - 1}" rx="14" fill="${RAISED}" stroke="${NET}" stroke-opacity=".14"/>
${eye(30, 16, 32, isOnline, true)}
<text x="82" y="29" ${SANS} font-size="13.5" font-weight="600" fill="${TEXT}">${esc(cut(uplink.name))}</text>
<text x="82" y="48" ${SANS} font-size="12" fill="${DIM}">${esc(said)}</text>
${shown
  .map(
    (each, index) =>
      `<text x="82" y="${83 + index * 19}" ${SANS} font-size="12.5" fill="${TEXT}">${esc(cut(each.label))}</text>` +
      `<text x="${CARD_W - 28}" y="${83 + index * 19}" text-anchor="end" ${SANS} font-size="11.5" fill="${DIM}">${each.files.toLocaleString('en-US')} files</text>`,
  )
  .join('')}
${more > 0 ? `<text x="82" y="${83 + shown.length * 19}" ${SANS} font-size="11.5" fill="${DIM}">and ${more} more</text>` : ''}`,
    ),
  }
}

/** The work under way, in the words the band uses for it, with how far it has come when the host has said. */
export function nowCard(work: string, pct: number | null) {
  const radius = 17
  const around = 2 * Math.PI * radius
  const drawn = (around * Math.min(100, pct ?? 0)) / 100
  const said = work.charAt(0).toUpperCase() + work.slice(1)
  const shown = said.length > NOW_CHARS ? `${said.slice(0, NOW_CHARS - 1)}…` : said

  return card(
    NOW_H,
    `<rect x=".5" y=".5" width="${CARD_W - 1}" height="${NOW_H - 1}" rx="14" fill="${RAISED}" stroke="${NET}" stroke-opacity=".14"/>
<circle cx="46" cy="42" r="${radius}" fill="none" stroke="${NET}" stroke-opacity=".16" stroke-width="4"/>
<circle cx="46" cy="42" r="${radius}" fill="none" stroke="${PULSE}" stroke-width="4" stroke-linecap="round" stroke-dasharray="${drawn} ${around - drawn}" transform="rotate(-90 46 42)"/>
${pct === null ? '' : `<text x="46" y="46" text-anchor="middle" ${SANS} font-size="11" font-weight="600" fill="${TEXT}">${pct}%</text>`}
<text x="82" y="36" ${SANS} font-size="12" fill="${DIM}">Now</text>
<text x="82" y="57" ${SANS} font-size="13.5" fill="${TEXT}">${esc(shown)}</text>`,
  )
}
