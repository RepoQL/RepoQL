---
description: Research on Solid.js as a UI framework for RepoQL's real-time visualization dashboard
tags: [solid-js, dashboard, visualization, react, framework-choice]
audience: { human: 70, agent: 30 }
purpose: { research: 90, reference: 10 }
---

# Solid.js for RepoQL Dashboard

Research for choosing the UI framework for RepoQL's real-time visualization dashboard.

*Research date: 2026-02-16*

## Context

RepoQL has an embedded React 18 dashboard showing pipeline status, file processing state, language distribution, and query activity. It receives real-time updates via SSE (Server-Sent Events) and renders custom SVG visualizations (treemap, Sankey diagram, progress rings, bar charts, activity stream). The dashboard runs alongside IDE, LLM client, and build tools on a developer laptop.

The question: is Solid.js a better fit for this use case than React?

**In scope:** Reactivity model, real-time data handling, visualization ecosystem, performance, migration cost, ecosystem health, alternatives.

**Out of scope:** Server-side rendering, SEO, routing, full-stack meta-frameworks.

---

## Solid.js Core Architecture

Solid uses fine-grained reactivity built on three primitives: **signals** (reactive state), **effects** (side effects that auto-track dependencies), and **memos** (cached derived values). No virtual DOM. The JSX compiler transforms templates into direct DOM creation instructions at build time — static parts are cloned efficiently, dynamic parts become reactive subscriptions.

> [Solid Docs: Fine-Grained Reactivity](https://docs.solidjs.com/advanced-concepts/fine-grained-reactivity), [Solid Docs: Intro to Reactivity](https://docs.solidjs.com/concepts/intro-to-reactivity)

**The fundamental difference from React:** Component functions execute exactly once. After initialization, only the reactive expressions within them update. There is no component re-rendering, no virtual DOM diffing, no reconciliation step. When a signal changes, it pushes updates through a dependency graph to the specific DOM nodes that read that signal.

> [Solid Docs: Component Basics](https://docs.solidjs.com/concepts/components/basics)

**Stores** (`createStore`) extend reactivity to nested objects via Proxies. Each property gets its own signal, created lazily on first access. The `setStore` path syntax enables surgical updates:

```js
setStore("files", 4217, "status", "indexed");  // updates one signal
setStore("files", f => f.id === targetId, "status", "complete");  // predicate
```

> [Solid Docs: Stores](https://docs.solidjs.com/concepts/stores), [Karim Ould: Path Pattern Syntax](https://blog.karimould.dev/solidjs-stores-and-the-path-pattern-syntax)

**TypeScript:** Built-in support, types ship with the package. Known pain points: type narrowing breaks with signals (they're functions returning potentially different values over time), discriminated unions are cumbersome, IDE auto-imports sometimes pull from wrong paths.

> [Solid Docs: TypeScript](https://docs.solidjs.com/configuration/typescript), [GitHub Discussion #1527](https://github.com/solidjs/solid/discussions/1527)

---

## Real-Time Data Handling

### SSE Integration

No built-in SSE primitive. The pattern is native `EventSource` wired into signals via `createEffect` + `onCleanup`:

```js
const [data, setData] = createSignal(null);
createEffect(() => {
  const es = new EventSource("/api/events");
  es.onmessage = (e) => setData(JSON.parse(e.data));
  onCleanup(() => es.close());
});
```

Community library `solidjs-use` provides `useEventSource` (port of VueUse), but maintenance status is uncertain.

> [solidjs-use: useEventSource](https://solidjs-use.github.io/solidjs-use/core/useEventSource), [AnswerOverflow Discussion](https://www.answeroverflow.com/m/1250867748506964119)

Ryan Carniato has noted that signals are a poor substitute for true streaming patterns at high throughput — an actual stream library may be more appropriate as the transport, with signals at the consumption layer.

> [GitHub Discussion #442](https://github.com/solidjs/solid/discussions/442)

### Fine-Grained Updates at Scale

The core advantage for a dashboard tracking 10,000+ files: updating `store.files[4217].status` writes to one signal. Only the DOM node displaying that file's status updates. The `<For>` component does not re-run. No other files are touched. Zero overhead per unchanged item.

In React, the same update re-executes the component holding the file list, produces 10,000 virtual elements, diffs them all, and patches one real DOM node. `React.memo` on list items mitigates but adds its own overhead and requires discipline.

> [Marmelab: SolidJS for React Developers](https://marmelab.com/blog/2025/05/28/solidjs-for-react-developper.html), [Toptal: SolidJS vs React](https://www.toptal.com/react/solidjs-vs-react)

### Batching

Solid's reactivity is synchronous by default. Explicit `batch()` defers downstream effects until the block completes — necessary for SSE `onmessage` callbacks that update multiple signals:

```js
batch(() => {
  setFileStatus(id1, "complete");
  setFileStatus(id2, "complete");
  setPipelineProgress(0.75);
});
// DOM updates once
```

Since v1.5: signals update immediately within a batch (reads reflect current values), but effects are deferred. No glitches.

> [Solid Docs: batch](https://docs.solidjs.com/reference/reactive-utilities/batch), [LogRocket: Understanding batch](https://blog.logrocket.com/understanding-solidjs-updated-batch-function/)

### Initial Snapshot Loading

`createResource` handles async data fetching with `.loading`, `.error`, `.latest` and integrates with `<Suspense>`. For loading then switching to SSE, `reconcile` diffs incoming data against existing store and triggers updates only for changed values:

```js
import { reconcile } from "solid-js/store";
setStore("files", reconcile(serverSnapshot));  // only changed properties trigger updates
```

> [Solid Docs: createResource](https://docs.solidjs.com/reference/basic-reactivity/create-resource), [Solid Docs: reconcile](https://docs.solidjs.com/reference/store-utilities/reconcile)

### State Management

External state management libraries are largely unnecessary. Signals and stores cover atomic state and nested objects. Context providers work without React's "context re-render" problem (signals do the work, not the provider). `observable`/`from` interop with RxJS if needed.

> [HackerNoon: State Management in SolidJS](https://hackernoon.com/state-management-in-solidjs-applications), [This Dot Labs: Sharing Signals and Stores](https://www.thisdot.co/blog/sharing-signals-and-stores-context-api-in-solidjs)

---

## Performance

### Benchmarks (JS Framework Benchmark — Krausest)

| Metric | Solid.js | React (hooks) | Ratio |
|--------|----------|---------------|-------|
| Geometric mean vs vanilla JS | ~1.05x | ~2.0x | Solid ~2x faster overall |
| Create 1,000 rows | ~37-44ms | ~46-52ms | Solid ~20-30% faster |
| Time to first paint | ~56ms | ~234ms | Solid ~4x faster |
| Scripting time | — | — | Solid ~3x faster |
| Rendering time | — | — | Solid ~2x faster |

> [BounDev: Performance Comparison 2025](https://www.boundev.com/blog/solidjs-vs-react-performance-comparison-2025), [markaicode: Benchmarks](https://markaicode.com/solidjs-vs-react19-performance-benchmarks/), [aalpha: Comparison](https://www.aalpha.net/blog/solidjs-vs-react-comparison/)

### Memory

Solid uses ~26% more memory than vanilla JS. React uses ~80-120% more. Net: Solid uses **~30-40% less memory** than React. The difference: no virtual DOM tree, no synthetic event system, no fiber tree in memory.

> [aalpha: Comparison](https://www.aalpha.net/blog/solidjs-vs-react-comparison/), [OpenReplay: Solid vs React](https://blog.openreplay.com/solid-vs-react-the-fastest-vs-the-most-popular-ui-library/)

Stores create signals lazily — only when a property is accessed in a tracking scope. For 10,000 files where 50 are visible, signals exist only for those 50 files' accessed properties.

### Bundle Size

| Framework | Core (min+gzip) | Ratio |
|-----------|-----------------|-------|
| Solid.js | ~6-7 KB | 1x |
| React + ReactDOM | ~44-45 KB | ~6-7x larger |

> [euroitsourcing](https://www.euroitsourcing.com/en/blog/why-solidjs-is-gaining-popularity-in-2025), [FrontendTools](https://www.frontendtools.tech/blog/reduce-javascript-bundle-size-2025)

### Large Lists

Solid's fine-grained updates give a natural advantage for lists with frequent individual item updates. However, **virtualized scrolling has a known issue**: Solid re-creates elements when scrolling (the `<For>` component sees new viewport items as new array entries), whereas React's VDOM can diff and realize nodes haven't structurally changed. Workaround: debounce scroll events to next animation frame.

Virtualization libraries exist: `@tanstack/solid-virtual`, `@solid-primitives/virtual`, `virtua`.

> [GitHub Discussion #2004](https://github.com/solidjs/solid/discussions/2004)

### Concurrent Rendering

Solid provides `startTransition` and `useTransition` (similar to React 18), but needs them less. React needed concurrent rendering to solve expensive component tree re-renders blocking the main thread. Solid's fine-grained updates don't have that problem.

> [The New Stack: Fine-Grained Reactivity](https://thenewstack.io/solidjs-creator-on-fine-grained-reactivity-as-next-frontier/)

---

## Visualization Ecosystem

### Charting Libraries

| Library | Wraps | Chart Types | Status |
|---------|-------|-------------|--------|
| solid-charts | Custom (D3 internals) | Line, Area, Bar, Point | New, limited types |
| solid-chartjs | Chart.js | All Chart.js types | v1.3.11, last published ~1yr ago |
| solid-apexcharts | ApexCharts | 14 types | Community maintained |
| echarts-solid | Apache ECharts | All ECharts types (incl. treemap, sankey) | Active |

> [solidcharts.dev](https://solidcharts.dev/), [npm: solid-chartjs](https://www.npmjs.com/package/solid-chartjs), [GitHub: echarts-solid](https://github.com/alxnddr/echarts-solid)

**None of these directly provide** the specific visualizations the dashboard needs (custom treemap, Sankey with bezier curves, concentric progress rings). The paths are: echarts-solid (ECharts has native treemap/sankey), D3 utility functions + custom SVG, or raw SVG + signals.

### D3 + Solid Integration

The proven pattern: **D3 for math, Solid for DOM.** Use D3's non-DOM modules (`d3-scale`, `d3-shape`, `d3-hierarchy`, `d3-sankey`, `d3-interpolate`) for calculations. Render SVG elements reactively with Solid's JSX. Never use `d3-selection` or `d3-transition`.

A developer on Hacker News (August 2025) reported "a lot of success with working with d3 + solid.js" using this approach — it "feels simpler, especially if you want interactions in the visualization to modify the app's state."

> [Medium: D3 + Declarative Frameworks](https://medium.com/@SnapdragonCao/integrate-d3-js-into-declarative-web-frameworks-ce1fc8e398a0), [HN Discussion](https://news.ycombinator.com/item?id=44973454)

| Target Visualization | D3 Module | Solid's Role |
|---------------------|-----------|-------------|
| File treemap | `d3-hierarchy` (`.treemap()`) | Render `<rect>` with reactive x, y, width, height |
| Pipeline Sankey | `d3-sankey` | Render `<rect>` for nodes, `<path>` for links |
| Progress rings | Not needed (simple SVG math) | Reactive `stroke-dashoffset` on `<circle>` |
| Language spectrum | `d3-scale` | Render `<rect>` with reactive dimensions |

This is the same pattern used by Airbnb's visx for React, but Solid doesn't need a wrapper library — the reactivity model makes the bridge trivial.

**No dedicated `solid-d3` bridge library exists.** No published SolidJS treemap or Sankey example repositories were found. The pattern is theoretically sound and community-validated, but you'd be building from scratch.

### SVG Reactivity

Solid's JSX compiler handles SVG natively. Signal-bound SVG attributes update with fine-grained precision — no diffing. A progress ring is:

```tsx
const dashOffset = () => circumference * (1 - progress());
<circle stroke-dashoffset={dashOffset()} />
```

When `progress` changes, only `stroke-dashoffset` updates. This is architecturally superior to React for visualization work.

**Performance threshold:** SVG handles ~1,000 interactive elements before degradation. Canvas 2D handles ~10,000 at 60fps. For the treemap (1,000-3,000 files), SVG is likely adequate. If needed, `solid-pixi` (PixiJS integration) provides a Canvas/WebGL escalation path.

> [DigitalAdBlog: Canvas vs WebGL](https://digitaladblog.com/2025/05/21/comparing-canvas-vs-webgl-for-javascript-chart-performance/)

### Animation

| Library | What it does | Size | Relevance |
|---------|-------------|------|-----------|
| solid-motionone | Declarative `<Motion>` component (like Framer Motion) | 5.8 KB | Enter/exit transitions, state changes |
| solid-transition-group | CSS transitions + FLIP for lists | Small | Activity stream items, treemap reordering |
| @solid-primitives/spring | Spring physics on reactive values | Small | Progress ring filling, bar heights, treemap resizing |
| @solid-primitives/tween | Eased interpolation on reactive values | Small | Linear transitions |

> [GitHub: solid-motionone](https://github.com/solidjs-community/solid-motionone), [solid-primitives: spring](https://primitives.solidjs.community/package/spring/)

No GSAP adapter for Solid (unlike React's `@gsap/react`). Manual lifecycle management with `onMount`/`createEffect`/`onCleanup` required.

### Comparison with React's Visualization Ecosystem

| Capability | React | Solid.js |
|------------|-------|----------|
| Batteries-included charts | recharts, nivo, Victory (massive) | Thin wrappers around Chart.js/ECharts/ApexCharts |
| Low-level D3 bridge | visx (Airbnb, ~19k stars) | No equivalent — manual D3 + JSX |
| Declarative animation | Framer Motion (~25k stars), react-spring (~28k stars) | solid-motionone (5.8kb, community) |
| Treemap component | visx, nivo, recharts all have treemap | None native — D3 + custom SVG or echarts-solid |
| Sankey component | nivo sankey, visx sankey | None native — D3 + custom SVG or echarts-solid |
| 3D/WebGL | react-three-fiber (~28k stars) | solid-three (port, much smaller) |
| Community scale | 190M+ weekly npm downloads | ~1.1M weekly downloads |

The gap is 100-1000x by every measure: downloads, stars, library count, tutorials, Stack Overflow answers.

---

## Ecosystem Health & Maturity

**Current version:** Solid 1.9.11 (early 2026). First stable release: June 2021.

**Downloads:** ~1.1-1.5M weekly (tripled in the past year). React: ~190M weekly. Solid is ~0.5-0.8% of React's scale.

> [npm.chart.dev/solid-js](https://npm.chart.dev/solid-js)

**Satisfaction:** Highest satisfaction rating in State of JavaScript survey for five consecutive years, though only ~10% of respondents use it.

> [2025.stateofjs.com](https://2025.stateofjs.com/en-US/libraries/front-end-frameworks/)

**Backing:** Ryan Carniato (creator) employed at Sentry (previously Netlify). Google Chrome Aurora fund sponsor. Small core team — bus factor concern is real.

> [OpenCollective: solid](https://opencollective.com/solid)

**Solid 2.0:** Experimental phase, no firm release date. Ground-up rewrite of the reactive core. Breaking changes: resources replaced, stores API evolving, automatic batching, streamlined JSX. v1.7 began deprecations to ease migration. Adopting 1.x now means a future migration with uncertain timeline (late 2026? 2027?).

> [GitHub Discussion #2425](https://github.com/solidjs/solid/discussions/2425)

**Developer availability:** You wouldn't hire a "Solid.js developer." You'd hire a React/TypeScript developer and expect a moderate learning curve. The JSX is 80% familiar; the mental model is fundamentally different.

> [marmelab.com](https://marmelab.com/blog/2025/05/28/solidjs-for-react-developper.html)

---

## Learning Curve

**Consensus: moderate but deceptive.** JSX familiarity creates false comfort. React-trained intuitions actively interfere.

Top struggles for React developers:

1. **Components run once** — the hardest mental shift. No re-renders.
2. **Props destructuring breaks reactivity** — a deeply ingrained React habit that silently fails.
3. **Control flow components** (`<Show>`, `<For>`, `<Switch>`) instead of ternaries and `.map()`.
4. **Async operations** — described as "the weakest point of Solid." Transitions are buggy, Suspense triggers hard to debug.
5. **Thinner community** — fewer Stack Overflow answers, smaller ecosystem of examples.

> [Vladislav Lipatov: SolidJS Pain Points](https://vladislav-lipatov.medium.com/solidjs-pain-points-and-pitfalls-a693f62fcb4c), [GitHub Discussion #1042](https://github.com/solidjs/solid/discussions/1042)

---

## Migration from Current Dashboard

The current dashboard has ~15 React components, custom SVG visualizations, SSE hooks via `useRepoQLDashboard`, Storybook stories, and comprehensive TypeScript types.

**Automated tooling:** `react2solid` exists (MVP stage, Babel-based). Not production-grade.

**Manual migration difficulty:** Moderate to high. Each component needs thoughtful rewrite, not find-and-replace. JSX looks similar but semantics differ fundamentally. SSE hooks need reimplementation with `createSignal`/`createEffect`. Custom SVG visualizations would translate reasonably well. Storybook supports Solid.

**Estimated effort:** 2-4 days of focused work for someone who knows both frameworks.

> [vladislav-lipatov.medium.com](https://vladislav-lipatov.medium.com/from-reactjs-to-solidjs-3e1b28ccc27a), [LogRocket: SolidJS Adoption Guide](https://blog.logrocket.com/solidjs-adoption-guide/)

---

## Alternatives

### React + React Compiler (stay put)

React Compiler v1.0 shipped October 2025. Automatically memoizes components and hooks at build time. Up to 12% faster loads, 2.5x quicker interactions. Works with React 17+. Adding it is a build config change, not a rewrite.

However: testing showed the compiler only fixed 2 out of 10 cases of unnecessary re-renders in one analysis. The fundamental VDOM overhead remains.

> [react.dev/blog/2025/10/07/react-compiler-1](https://react.dev/blog/2025/10/07/react-compiler-1), [developerway.com](https://www.developerway.com/posts/i-tried-react-compiler)

### Preact

3 KB gzipped. Near-identical React API. Drop-in replacement via `preact/compat`. Optional Signals for fine-grained reactivity on hot paths. Sentry chose Preact over Svelte for their embedded feedback widget specifically because developer familiarity with React improved maintainability.

**Lowest migration cost** — existing React components would largely work with `preact/compat`.

> [preactjs.com](https://preactjs.com/), [sentry.engineering](https://sentry.engineering/blog/preact-or-svelte-an-embedded-widget-use-case)

### Svelte 5 (Runes)

Explicit reactive primitives (`$state`, `$derived`, `$effect`) similar to Solid's signals. Compiler-based, no virtual DOM. ~50% smaller bundles than Svelte 4. Larger ecosystem and community than Solid. Higher developer satisfaction. Stable release, no imminent breaking version.

**Higher migration cost** — `.svelte` files, not JSX. Full rewrite regardless.

> [khromov.se](https://khromov.se/svelte-5-brings-up-to-50-bundle-size-decrease-for-existing-svelte-4-apps/), [leapcell.io](https://leapcell.io/blog/next-gen-reactivity-rethink-preact-solidjs-signals-vs-svelte-5-runes)

---

## Comparison

| Dimension | React (current) | React + Compiler | Preact | Solid.js | Svelte 5 |
|-----------|----------------|-----------------|--------|----------|----------|
| Bundle size (gzip) | ~45 KB | ~45 KB | ~3 KB | ~7 KB | ~3-4 KB |
| Update model | VDOM diff | VDOM diff (auto-memo) | VDOM diff | Fine-grained signals | Compiled, fine-grained |
| Memory vs vanilla | ~2x | ~2x | ~1.3x | ~1.26x | ~1.2x |
| Perf vs vanilla (geo mean) | ~2x slower | ~1.5-1.7x slower | ~1.6x slower | ~1.05x slower | ~1.1x slower |
| Migration cost | 0 | Build config change | Low (`preact/compat`) | Medium-high (rewrite) | High (full rewrite, new syntax) |
| Viz ecosystem | Massive | Massive | React compat | Thin wrappers + manual D3 | Thin wrappers + manual D3 |
| Community scale | 190M/wk | 190M/wk | ~4M/wk | ~1.1M/wk | ~6M/wk |
| Real-time data fit | Adequate | Adequate | Adequate | Excellent (native) | Very good |
| Upcoming breaking ver. | No (Compiler is additive) | No | No | Solid 2.0 (timeline unclear) | No |
| Embedding story | Standard | Standard | Standard | Standard | Standard |
| Testing | Mature | Mature | React compat | Adequate but thinner | Solid |
| A11y | React Aria (complete) | React Aria (complete) | React Aria via compat | Solid Aria (~50%) | Comparable |

---

## Gaps

- **No production monitoring dashboard built with Solid.js was found.** Claims of production usage exist but no detailed public case studies for this specific use case.
- **Solid 2.0 release date unknown.** Could be late 2026, could be 2027. Migration burden unclear since APIs aren't finalized.
- **Per-signal memory cost not published.** Solid's internals suggest small closures, but no measurements in bytes.
- **Sustained high-frequency SSE behavior unverified.** No benchmarks for 100+ events/second over minutes with Solid stores.
- **SVG reactivity at 5,000+ elements not benchmarked.** The ~1,000 element threshold is a general guideline, not Solid-specific data.
- **React Compiler effectiveness on SVG visualization patterns specifically is untested.** The 12% improvement is a general figure.
- **Exact current Krausest benchmark numbers** come from secondary sources citing various benchmark runs. The interactive page is the authoritative source.

---

## Summary

Solid.js's fine-grained reactivity model is architecturally well-matched to a real-time dashboard that receives frequent SSE events updating individual items in a large state tree. The performance characteristics (2x faster than React overall, 30-40% less memory, 6-7x smaller bundle) are meaningful for an embedded tool running alongside heavy developer applications.

The cost is a significantly thinner visualization ecosystem (no visx, no nivo, no Framer Motion equivalents at scale), a smaller community for troubleshooting, an impending 2.0 with breaking changes on an uncertain timeline, and a 2-4 day migration effort for the existing ~15-component dashboard.

The "do nothing" alternative — React with React Compiler — gets partial performance improvement with zero migration cost. Preact gets most of the bundle size benefit with minimal migration cost and full React ecosystem access. Both avoid the ecosystem and stability risks.
