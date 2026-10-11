export type Host = { pid: number; startedAtUnixMs: number }
export type Operation = { name: string; percent: number | null }

/** What the display draws from. Every field but `slowTool` is a fact the host's stream sent. */
export type View = {
  /** `unknown` until the monitor says anything. */
  connection: 'unknown' | 'waiting' | 'connected' | 'lost'
  /** The last host seen, kept across a lost connection so a restart can be told from a reconnect. */
  host: Host | null
  /** The named work the display shows, in a new user's words; null while settled or while the host does its own housekeeping. */
  operation: Operation | null
  /** When named work was first seen in the current unsettled stretch. */
  busySinceMs: number | null
  /** The host's release, from its greeting. */
  version: string | null
  /** How many files the host last said are indexed. It says so only while it works, so a host that was settled on arrival has not said. */
  files: number | null
  /** The link that opens this host's dashboard, asked of `rql dashboard --url` once per host; null until it answers. */
  dashboard: string | null
  /** The RepoQL tool whose call has been in flight long enough to explain. */
  slowTool: string | null
}

declare module 'claude-code' {
  interface PluginState {
    'repoql': {
      view: View
    }
  }
}
