---
description: Survey of patterns, libraries, and techniques for high-performance realtime data display in SolidJS
tags: [solid-js, realtime, performance, signals, streaming, visualization, tables, canvas]
audience: { human: 60, agent: 40 }
purpose: { research: 85, reference: 15 }
---

# High-Performance Realtime Data in SolidJS

Research for surveying what patterns and libraries exist in SolidJS for high-performance realtime rendering.

*Research date: 2026-02-22*

## Context

This surveys the SolidJS ecosystem and techniques for displaying data that updates at high frequency (10-100+ updates/second) from sources like WebSockets, SSE, or gRPC streams. Covers reactivity primitives, rendering strategies, charting, tables, and supporting infrastructure.

Complements [solid-js-dashboard.md](solid-js-dashboard.md), which evaluates SolidJS as a framework choice. This document goes deeper on *how* to build realtime displays once SolidJS is chosen.

**In scope:** Reactivity mechanics, streaming data integration, virtualization, charting, canvas/WebGL, throttling, workers, memory management, ecosystem libraries.

**Out of scope:** SSR/hydration for realtime apps (briefly noted), framework comparison (covered in companion doc), deployment strategies.

---

## Reactivity Primitives for Realtime

SolidJS's reactive system is push-based, synchronous, and fine-grained. When a signal changes, it updates only the specific DOM nodes bound to that signal. Components execute once; there is no re-rendering, no VDOM diffing.

> [Fine-grained reactivity](https://docs.solidjs.com/advanced-concepts/fine-grained-reactivity), [Deep Dive Into How Signals Work](https://www.thisdot.co/blog/deep-dive-into-how-signals-work-in-solidjs)

### Choosing the Right Primitive

| Primitive | Granularity | Overhead | Realtime fit |
|-----------|-------------|----------|--------------|
| `createSignal` | Whole value | Minimal (1 value + subscriber Set) | Individual scalar metrics, counters, status flags |
| `createStore` | Per-property (lazy proxy) | Moderate (proxy + lazy signals) | Structured objects where different UI regions subscribe to different fields |
| `createMutable` | Per-property (lazy proxy) | Same as store + mutation risks | External system integration; may be deprecated in 2.0 |

> [createSignal](https://docs.solidjs.com/reference/basic-reactivity/create-signal), [Stores](https://docs.solidjs.com/concepts/stores), [createMutable](https://docs.solidjs.com/reference/store-utilities/create-mutable)

Stores are the natural fit for realtime object data. When a WebSocket delivers an object with 50 fields, `setStore` path syntax updates exactly the changed properties. Only DOM nodes bound to those specific properties update.

```js
// Pinpoint update: only subscribers of rows[42].status see this
setStore("rows", 42, "status", "active");
```

> [Path-based store syntax](https://jrhicks.netlify.app/posts/2022-12-07-soldjs-path-based-syntax/)

### Batching

Signal updates are synchronous and immediate by default. `batch()` groups multiple signal writes so downstream effects run once instead of N times.

```js
batch(() => {
  setField1(msg.field1);
  setField2(msg.field2);
  // ... 48 more signals
});
// All 50 updates coalesce; effects run once
```

Automatic batching already occurs inside `createEffect`, `onMount`, and store setters. A single `setStore` call is automatically batched.

> [batch](https://docs.solidjs.com/reference/reactive-utilities/batch), [Updated batch function (LogRocket)](https://blog.logrocket.com/understanding-solidjs-updated-batch-function/)

### reconcile vs produce

For merging incoming data payloads:

- **`reconcile(newData)`** diffs recursively against the existing store. Only fires updates for properties that actually changed. Good for structured objects arriving as complete JSON payloads. Uses a `key` option (default `"id"`) for array item matching.
- **`produce(fn)`** provides Immer-style mutation syntax against a draft, translated into pinpoint store updates.

> [reconcile](https://docs.solidjs.com/reference/store-utilities/reconcile), [produce](https://docs.solidjs.com/reference/store-utilities/produce)

Avoid `reconcile` for large numeric arrays (chart data, time-series). The recursive diff has O(n) cost and recreates the array in memory. For those, use raw signals with typed arrays or direct mutation.

> [Store array discussion](https://github.com/solidjs/solid/discussions/2513)

### Controlling Reactivity

| Utility | What it does | Realtime use |
|---------|-------------|--------------|
| `untrack(fn)` | Reads signals without subscribing | Read a fast signal's current value in an effect triggered by something else |
| `on(deps, fn)` | Explicit dependency declaration | Fire an effect only when specific signals change, ignoring others |
| `on(dep, fn, { defer: true })` | Skip initial execution | Only run on first *change*, not on mount |
| `createDeferred(fn)` | Defer until browser idle | Non-critical derived computations (stats, aggregates) |
| `createSelector(signal)` | O(1) selection tracking | Highlight active row in a list without O(n) updates |
| `{ equals: false }` | Force notify on every write | Event-stream semantics where every message matters |
| Custom `equals` | Threshold-based comparison | Only propagate if delta exceeds a threshold |

> [untrack](https://docs.solidjs.com/reference/reactive-utilities/untrack), [on](https://docs.solidjs.com/reference/reactive-utilities/on-util), [createDeferred](https://docs.solidjs.com/reference/basic-reactivity/create-deferred), [createSelector](https://docs.solidjs.com/reference/secondary-primitives/create-selector)

---

## Streaming Data Into Signals

### WebSocket

**@solid-primitives/websocket** provides `createWS` (reactive WebSocket with auto-cleanup), `createWSState` (reactive readyState), plus reconnecting and heartbeat variants via `makeReconnectingWS` and `makeHeartbeatWS`.

```js
import { createWS } from "@solid-primitives/websocket";
import { createEventSignal } from "@solid-primitives/event-listener";

const ws = createWS("ws://localhost:5000");
const messageEvent = createEventSignal(ws, "message");
const message = () => messageEvent()?.data;
```

> [@solid-primitives/websocket](https://primitives.solidjs.community/package/websocket/), [@solid-primitives/event-listener](https://github.com/solidjs-community/solid-primitives/tree/main/packages/event-listener)

**solid-socket** (by devagrawal09) provides "socket memos" -- serializable reactive values shared between client and server.

> [solid-socket](https://github.com/devagrawal09/solid-socket)

### Server-Sent Events

**solidjs-use** provides `useEventSource` (VueUse-inspired). For manual integration, the pattern is: centralized `EventSource` handling, messages staged and exposed via context, consumed by components via `createAsync` or `createAsyncStore`. For multiple streams, multiplex onto a single SSE connection and demux client-side.

> [solidjs-use useEventSource](https://solidjs-use.github.io/solidjs-use/core/useEventSource), [solid-start-sse-counter](https://github.com/peerreynders/solid-start-sse-counter)

### gRPC-Web

gRPC-Web supports unary and server streaming in the browser (not bidirectional). No SolidJS-specific wrapper exists. Integration pattern: receive stream messages, update a signal in the callback. Use `protobuf-ts` or `protoc-gen-ts_proto` for TypeScript code generation.

> [gRPC with Rust and SolidJS](https://blog.consol.de/software-engineering/grpc-with-rust-and-solidjs/), [gRPC-Web](https://github.com/grpc/grpc-web)

### RxJS / Observable Interop

SolidJS has built-in bidirectional interop:

- `from(observable$)` converts any subscribable into a Signal
- `observable(signal)` converts a Signal into an Observable

This enables using RxJS operators (buffer, throttleTime, scan, switchMap) for stream processing before data enters the reactive graph.

> [from](https://docs.solidjs.com/reference/reactive-utilities/from), [observable](https://docs.solidjs.com/reference/reactive-utilities/observable), [Discussion #410](https://github.com/solidjs/solid/discussions/410)

---

## Throttling High-Frequency Updates

When data arrives faster than 60fps, rendering every update wastes CPU. Several throttling strategies:

### @solid-primitives/scheduled

| Function | Behavior |
|----------|----------|
| `debounce(fn, ms)` | Trailing edge, cancellable |
| `throttle(fn, ms)` | Trailing edge, cancellable |
| `scheduleIdle(fn)` | Uses `requestIdleCallback` (falls back to throttle on Safari) |
| `leading(fn, ms)` | Leading edge only |
| `createScheduled(scheduleFn)` | Signal wrapper that defers tracking to a custom scheduler |

`createScheduled` is powerful: pass a `requestAnimationFrame`-based scheduler to coalesce all signal updates to the next frame.

> [@solid-primitives/scheduled](https://primitives.solidjs.community/package/scheduled/)

### requestAnimationFrame Pattern

A common approach for decoupling data ingestion from rendering:

1. Incoming data writes to a plain variable (not a signal) or a "raw" signal read via `untrack`
2. A rAF loop reads the latest value and writes it to a "display" signal
3. The display signal drives the reactive UI

This caps rendering at frame rate regardless of data frequency.

> [Signal throttle/debounce discussion](https://github.com/solidjs/solid/discussions/923)

### Effect Timing

- `createEffect` runs after rendering, before browser paint. Good for most updates.
- `createRenderEffect` runs synchronously during rendering. Slightly more performant for direct DOM mutations.

> [createRenderEffect](https://docs.solidjs.com/reference/secondary-primitives/create-render-effect), [Discussion #2168](https://github.com/solidjs/solid/discussions/2168)

---

## Rendering Large Datasets

### `<For>` vs `<Index>`

| Component | Keying | Item | Index | Best for |
|-----------|--------|------|-------|----------|
| `<For>` | By reference | Direct value | Signal | Lists that reorder, insert, delete |
| `<Index>` | By position | Signal | Direct number | Fixed-size grids where values mutate at positions |

`<For>` uses `mapArray` internally; when items reorder, DOM nodes move. `<Index>` uses `indexArray`; DOM nodes stay put, content signals fire when values change at a given position.

For a fixed-size table of sensor readings (values update, positions don't change), `<Index>` avoids DOM node creation/destruction entirely. For a log viewer where entries are appended and old ones removed, `<For>` handles disposal.

> [List rendering](https://docs.solidjs.com/concepts/control-flow/list-rendering)

**@solid-primitives/keyed** provides `<Key>` for items identified by an explicit key function (not by reference). Both value and index are signals. Useful when item references change across API responses but logical identity persists via an `id` field.

> [keyed](https://primitives.solidjs.community/package/keyed/)

### Virtualization

| Library | Size | Variable heights | Grid mode | Notes |
|---------|------|-------------------|-----------|-------|
| **@tanstack/solid-virtual** | Full-featured | Yes (estimateSize) | Yes | Official Solid adapter. Pairs with @tanstack/solid-table |
| **virtua** | ~3kB | Yes (auto-detect) | No | Zero-config. Handles iOS quirks, reverse scrolling |
| **@solid-primitives/virtual** | Minimal | No (fixed only) | No | Simple `VirtualList` component |

Only 15-20 visible items plus a buffer are mounted, regardless of dataset size.

> [@tanstack/solid-virtual](https://tanstack.com/virtual/v3/docs/framework/solid/solid-virtual), [virtua](https://github.com/inokawa/virtua), [@solid-primitives/virtual](https://primitives.solidjs.community/package/virtual/)

### CSS containment (Complementary)

`content-visibility: auto` tells the browser to skip rendering off-screen content. Simpler than virtualization but still creates all DOM nodes (just defers rendering). Effective for hundreds to low thousands of rows; beyond that, virtualization is needed.

> [web.dev: content-visibility](https://web.dev/articles/content-visibility)

### Tables

**@tanstack/solid-table** (v8.21.3): Headless table with sorting, filtering, grouping, pagination. Does not render or virtualize; pair with @tanstack/solid-virtual for large datasets. Supply row data via a signal; table recomputes when data changes.

> [TanStack Table Solid](https://tanstack.com/table/v8/docs/framework/solid/solid-table)

**AG Grid**: Native Solid rendering (not a wrapper) since v28.2. The community adapter `solid-ag-grid` exists but is pinned to AG Grid 31.1.1 while AG Grid is at 35.x. AG Grid 33 introduced breaking module reorganization. Using current AG Grid with Solid requires either a fork or vanilla JS API integration.

> [AG Grid SolidJS](https://www.ag-grid.com/react-data-grid/solidjs/), [solid-ag-grid](https://github.com/solidjs-community/solid-ag-grid)

No other enterprise grids (Handsontable, Bryntum, Syncfusion) have SolidJS adapters.

### Store Performance for Array Mutations

Performance ranking for array operations (from community testing):

1. **Path syntax**: `setStore('rows', store.rows.length, newItem)` -- fastest
2. **produce with push**: `setStore('rows', produce(r => r.push(newItem)))` -- slightly slower
3. **Spread**: `setStore('rows', [...store.rows, newItem])` -- slowest (copies entire array)

> [Discussion #866](https://github.com/solidjs/solid/discussions/866), [Discussion #2417](https://github.com/solidjs/solid/discussions/2417)

---

## Charting and Visualization

### SolidJS Charting Libraries

| Library | Engine | Renderer | Maintenance | Realtime fit |
|---------|--------|----------|-------------|-------------|
| **@dschz/solid-uplot** | uPlot | Canvas 2D | Active (dsnchz ecosystem) | High. uPlot: 166k points in 25ms, ~100k pts/ms scaling |
| **@dschz/solid-lightweight-charts** | TradingView LW Charts | Canvas | Active | High. Purpose-built for financial time-series |
| **@dschz/solid-highcharts** | Highcharts | SVG/Canvas | Active | Medium. Commercial license required |
| **@dschz/solid-plotly** | Plotly.js | SVG/WebGL | New (May 2025), early | Medium. Heavy bundle |
| **solid-chartjs** | Chart.js | Canvas | ~1yr since publish | Medium. Chart.js: 40% CPU at 3,600pts@60fps vs uPlot's 10% |
| **solid-apexcharts** | ApexCharts | SVG/Canvas | Unclear | Medium |
| **echarts-solid** | Apache ECharts | Canvas/SVG/WebGL | Inactive (Oct 2023) | Stale wrapper. ECharts itself supports streaming via appendData() |
| **solid-charts (SolidCharts)** | D3 | SVG | Early (v0.0.2) | Low for high-frequency. SVG-based, small datasets |

> [solid-uplot](https://github.com/dsnchz/solid-uplot), [solid-lightweight-charts](https://github.com/dsnchz/solid-lightweight-charts), [solid-chartjs](https://github.com/s0ftik3/solid-chartjs), [SolidCharts](https://solidcharts.dev/), [uPlot benchmarks](https://github.com/leeoniya/uPlot)

The **dsnchz ecosystem** (Daniel Sanchez) is the most consistently maintained set of SolidJS charting wrappers: solid-uplot, solid-lightweight-charts, solid-highcharts, solid-plotly, and solid-auto-sizer.

> [dsnchz GitHub](https://github.com/dsnchz)

### Imperative Library Integration Pattern

SolidJS components run once. This is advantageous for imperative charting libraries because initialization naturally happens once without the React double-mount problem.

```typescript
function Chart(props) {
  let container: HTMLDivElement;
  let chart: uPlot;

  onMount(() => {
    chart = new uPlot(opts, props.data, container);
  });

  createEffect(() => {
    const data = props.data; // tracked
    chart?.setData(data);
  });

  onCleanup(() => chart?.destroy());

  return <div ref={container!} />;
}
```

Pitfalls: event listeners inside `createEffect` accumulate without cleanup; `onCleanup` inside an effect runs before each re-execution AND on unmount (scoping matters); closures over signal getters capture the value at call time, not the getter.

> [SolidJS refs](https://docs.solidjs.com/concepts/refs), [Side effects in SolidJS](https://jonathan-frere.com/posts/side-effects-in-solidjs/)

### SVG vs Canvas vs WebGL

| Renderer | Scale | Strength | Weakness |
|----------|-------|----------|----------|
| SVG | Up to ~1,000 elements | Declarative, interactive (DOM events), accessible, SolidJS reactivity applies per-element | DOM overhead beyond 1k elements; exponential degradation on Safari |
| Canvas 2D | Up to ~100,000 points | Low overhead, good perf/size ratio | Opaque to reactivity; manual hit-testing for interaction |
| WebGL | Millions of points | GPU-accelerated | Complex, large dependencies, limited interactivity |

> [SVG vs Canvas vs WebGL](https://dev3lop.com/svg-vs-canvas-vs-webgl-rendering-choice-for-data-visualization/), [ECharts Canvas vs SVG](https://apache.github.io/echarts-handbook/en/best-practices/canvas-vs-svg/)

### High-Frequency Chart Update Strategies

For data at 10-100+ updates/second:

**Data structure strategies** (from uPlot community):
1. Naive array recreation per update: simple, creates GC pressure
2. Typed arrays with doubling: better, but CPU spikes on resize
3. Pre-allocated with subarray views: good initial perf, memory grows
4. **Ring buffer with modular indexing**: fixed-size, overwrites oldest, constant memory, no GC pressure

> [uPlot issue #1122](https://github.com/leeoniya/uPlot/issues/1122), [uPlot stream demo](https://github.com/leeoniya/uPlot/blob/master/demos/stream-data.html)

**Client-side downsampling**: LTTB (Largest Triangle Three Buckets) reduces N points to M while preserving visual shape. O(n), deterministic, retains peaks and valleys.

> [downsample-lttb](https://github.com/pingec/downsample-lttb), [LTTB explained](https://rajnandan.com/posts/largest-triangle-three-buckets-downsampling/)

### Dashboard Coordination

**uPlot cursor sync**: Built-in cursor synchronization across chart instances in the same sync group. Shared cursor position, selection, and hover state.

> [uPlot sync-cursor demo](https://leeoniya.github.io/uPlot/demos/sync-cursor.html)

**ECharts connect**: `echarts.connect(groupId)` links instances for shared tooltip, synchronized zoom/pan, and data brush selection.

> [ECharts features](https://echarts.apache.org/en/feature.html)

**SolidJS reactivity for cross-filtering**: Shared signals (e.g., `timeRange`) naturally coordinate charts. Because SolidJS tracks at the signal level, only charts that read a shared signal update when it changes.

No SolidJS-specific dashboard framework exists. Layouts require manual CSS Grid/Flexbox with reactive state sharing.

---

## Web Workers

### @solid-primitives/workers

Three tiers:

- **`createWorker(fn)`**: Basic worker from inline function
- **`createWorkerPool(concurrency, fn)`**: Round-robin pool across N workers
- **`createSignaledWorker({ input, output, func })`**: Signal-bridged worker. Input signal change triggers worker processing, output signal receives result

> [@solid-primitives/workers](https://primitives.solidjs.community/package/workers/)

### Architecture for High-Frequency Data

A viable pipeline: Worker receives raw stream data, parses/transforms/aggregates, posts processed results back. Main thread writes to a signal inside `batch()`. A rAF-based `createScheduled` coalesces to frame rate. The throttled signal drives virtualized list or canvas render.

No benchmarks for this full pipeline in SolidJS exist.

### OffscreenCanvas

**solid-canvas** (WIP, by bigmistqke) supports an Offscreen prop to move canvas rendering entirely off the main thread. Combines data processing AND rendering in the worker.

> [solid-canvas](https://github.com/bigmistqke/solid-canvas)

---

## Memory Management

### Signal Disposal

SolidJS uses an ownership tree. `render()` and `createRoot()` create root owners. When a parent computation re-executes, all child computations are disposed (cleanup callbacks fire, subscriptions removed).

> [createRoot](https://docs.solidjs.com/reference/reactive-utilities/create-root)

### Memory Leak Vectors

| Pattern | Risk | Mitigation |
|---------|------|------------|
| Computations outside `createRoot`/`render` | Never disposed | Always create within an owner |
| Event listeners in `createEffect` without cleanup | Accumulate on each re-run | Use `onCleanup` inside the effect |
| Unbounded growing lists | Roots accumulate per item | Ring buffer (cap at N items), or virtualization (only visible items have reactive roots) |
| Subscriptions to long-lived signals | Never released while signal lives | Explicit lifecycle control via `onCleanup` and `createSubRoot` |

> [Discussion #1379](https://github.com/solidjs/solid/discussions/1379), [onCleanup](https://docs.solidjs.com/reference/lifecycle/on-cleanup)

**@solid-primitives/rootless** provides `createRootPool` for reusing reactive roots. Combined with `<For>`, already-created DOM elements are reused for different data items, preventing unbounded root creation.

> [rootless](https://primitives.solidjs.community/package/rootless/)

---

## WASM Integration

**useRust** provides custom Rust WebAssembly hooks for SolidJS. Offload CPU-intensive transformations (aggregations, statistics, binary protocol parsing) to Rust while SolidJS handles the view. Data crossing the JS-WASM boundary is copied (wasm-bindgen), so keep large data in WASM memory and pass only display-ready results back.

> [useRust](https://github.com/ollipal/useRust), [Rust WASM performance](https://medium.com/@oemaxwell/rust-webassembly-performance-javascript-vs-wasm-bindgen-vs-raw-wasm-with-simd-687b1dc8127b)

**Tauri** (SolidJS + Rust desktop): 30-40MB RAM at idle vs 300-500MB for Electron. Rust backend processes data and pushes results to SolidJS via IPC.

> [Rust, SolidJS, and Tauri](https://blog.logrocket.com/rust-solid-js-tauri-desktop-app/)

---

## Ecosystem Summary

### solid-primitives Packages Relevant to Realtime

| Package | What it provides |
|---------|------------------|
| `websocket` | Reactive WebSocket with reconnect and heartbeat |
| `scheduled` | Debounce, throttle, scheduleIdle, createScheduled |
| `workers` | Worker creation, pools, signal-bridged workers |
| `virtual` | Fixed-height virtual list |
| `keyed` | Key-based list rendering |
| `event-listener` | Reactive event signals |
| `event-bus` | In-app event system with event history |
| `resize-observer` | Shared singleton for container sizing |
| `rootless` | Root pooling for DOM element reuse |

> [solid-primitives](https://primitives.solidjs.community/)

### Developer Tooling

- **solid-devtools**: Chrome extension with reactive graph visualization and performance profiler
- **Solid Structure**: Live dependency graph rendering and signal update log monitor

> [solid-devtools](https://github.com/thetarnav/solid-devtools), [Solid Structure](https://medium.com/@solidstructuredevtool/solid-structure-a-frontend-dev-tool-for-solidjs-5755beb8b37b)

---

## SolidJS 2.0 Implications

SolidJS 2.0 is experimental with no published timeline. Planned changes relevant to realtime:

- **Automatic batching**: Removes need for manual `batch()` calls
- **Flush boundaries**: Finer control over when reactive updates propagate to DOM
- **Immutable diffable stores**: `reconcile` semantics may change
- **Projections**: New primitive for granular derived lenses over reactive data. Could enable optimistic UI layers over streaming data without mutating core state
- **`createMutable` may be deprecated**

> [The Road to 2.0](https://github.com/solidjs/solid/discussions/2425), [Beyond Signals (JSNation)](https://gitnation.com/contents/beyond-signals)

The reactive core is being decoupled as **@solidjs/signals** (pre-alpha, not production-ready).

> [@solidjs/signals](https://github.com/solidjs/signals)

---

## Gaps

**No SolidJS-specific realtime benchmarks exist.** All published performance data is from general framework benchmarks (js-framework-benchmark) or underlying charting library benchmarks (uPlot). No one has published measurements for: SolidJS store updates at 1000/second, @tanstack/solid-virtual with rapidly mutating cell data, or signal-bridged worker throughput.

**No real-world open-source SolidJS realtime applications found.** No publicly documented trading platforms, monitoring dashboards, or log viewers built with SolidJS were identified.

**AG Grid SolidJS adapter is stale.** Pinned to v31.1.1 while AG Grid is at v35.x. AG Grid 33 introduced breaking module changes. The adapter's viability for new projects is uncertain.

**Per-signal memory cost is undocumented.** SolidJS heap usage is reported as 70%+ lower than React in large-update scenarios, but exact per-node memory cost is not published.

**No priority scheduling.** SolidJS has `batch` (sync) and `startTransition` (async) but no priority lanes. Distinguishing critical updates (price changes) from cosmetic updates (animations) requires manual implementation.

> [Priority scheduling request](https://github.com/solidjs/solid/issues/671)

**SharedArrayBuffer + SolidJS reactivity**: No one appears to have built a reactive bridge where a worker writes to shared memory and SolidJS signals react. This remains an open engineering problem.

**solid-canvas maturity**: Marked WIP. No published benchmarks, production usage, or detailed documentation for the OffscreenCanvas support.

**SolidJS 2.0 timeline**: Explicitly unstated. APIs covered in this research (stores, `createMutable`, `reconcile`) may change shape.
