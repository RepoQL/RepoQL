User:
When the app starts up it will index all of the files in the repository, after which it will watch the filesystem for changes. I want to have a live view in the console of what it is doing. can you please create a mockup of a text-based layout for the live console dashboard. Also decide whether we need one design or two (one all the time or having it switch between bulk index and watch). Assume that it will be redrawn rather than append only

GPT5-pro:

One design. It adapts by toggling the right‑hand panel between **Progress** (bulk index) and **Watch** (steady state). No layout swap, so the eye does not relearn.

---

## 120×36 mockup (wide terminals)

```
┌─ RepoGraph  ⟦/repo/acme⟧  Mode: INDEXING  Uptime 00:07:14  DB: open  Threads:16  ───────────────────────────────────────────────┐
│ Files 13,542  New 0  Changed 8  Skipped 2  Artifacts 12,334  Nodes 88,209  Edges 431,002  Errors 1                           │
│ Rate  Discover 240/s | Hash 180/s | Parse 75/s | Index 60/s | FS events 12/s                                                 │
├────────────────────────────────────────────────────────── Activity ───────────────────────────────────────┬──── Progress ──────┤
│ 12:14:02  parse     src/api/users.ts              OK   nodes:+12 spans:+6 edges:+14                       │ Total     [███████▉.............]  38%   5,143/13,542  │
│ 12:14:01  hash      src/web/app.css               OK   sha256:1d3…                                        │ Discover  [███████████▏.........]  58%                   │
│ 12:14:00  link      src/api/users.ts -> models    OK   CALLS:+3 REFERS_TO:+11                              │ Hash      [███████▎.............]  37%                   │
│ 12:13:59  parse     README.md                     OK   nodes:+4 spans:+4                                   │ Parse     [█████................]  25%                   │
│ 12:13:58  ingest    dist/app.jar                  OK   artifact:+1                                         │ Link      [███▌..................]  18%                   │
│ 12:13:58  error     src/api/orders.ts             FAIL SyntaxError at line 212                             │ Queues    fs 0 | hash 2 | parse 6 | index 1 | db 0       │
│ 12:13:57  discover  tests/                        OK   +123 files                                          │ Workers   P:6  H:4  L:4  DB:2  Idle:0                     │
│ 12:13:55  parse     src/app.py                    OK   nodes:+9 spans:+5 edges:+6                          │ ETA       ~02:41 (rough)                                   │
│ 12:13:54  link      src/app.py -> templates       OK   REFERS_TO:+2                                        │                                                        │
│ 12:13:53  parse     openapi.yaml                  OK   nodes:+31 spans:+31 edges:+48                       │                                                        │
│ …                                                                                                          │                                                        │
├──────────────────────────────────────────────────────── Last Errors ───────────────────────────────────────┴────────────────────┤
│ 12:13:58  src/api/orders.ts  SyntaxError: unexpected token at line 212 (#line=212)                                             │
│ 12:10:21  src/data/users.json  JSON parse error near byte 1843 (#char=1843)                                                    │
├───────────────────────────────────────────── Hotspots / Slow files ─────────────────────────────────────────────────────────────┤
│ 3.2s  openapi.yaml   2.8s  src/app.py   2.5s  dist/app.jar!/lib/a.class   1.9s  src/api/users.ts                                │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

### Same layout in WATCH mode (right panel switches)

```
┌─ RepoGraph  ⟦/repo/acme⟧  Mode: WATCH  Uptime 03:44:31  DB: open  Threads:8 ───────────────────────────────────────────────────┐
│ Files 13,782  New 2  Changed 5  Skipped 0  Artifacts 12,557  Nodes 90,311  Edges 441,905  Errors 0                             │
│ Rate  FS events 4/s | Reindex 2/s | Parse 1/s | Link 1/s                                                                        │
├────────────────────────────────────────────────────────── Activity ───────────────────────────────────────┬──── FS Watch ───────┤
│ 15:22:09  change    src/api/users.ts              REPARSE   nodes:+1 edges:+2                              │ 15:22:09  MODIFY  src/api/users.ts                      │
│ 15:22:08  change    tests/orders.spec.ts          REPARSE   nodes:+3 edges:+4                              │ 15:22:07  CREATE  src/new/feature.md                   │
│ 15:22:07  link      src/api/users.ts -> models    OK       REFERS_TO:+2                                     │ 15:22:06  DELETE  src/tmp.tmp                           │
│ 15:22:06  parse     src/new/feature.md            OK       nodes:+4 spans:+4                               │ 15:22:04  MODIFY  openapi.yaml                          │
│ 15:22:04  hash      openapi.yaml                  OK       sha256:ae9…                                     │ Queue    fs 0 | parse 1 | link 0 | db 0                 │
│ …                                                                                                          │ Recent burst: peak 11/s  last 60s avg 4/s               │
├──────────────────────────────────────────────────────── Last Errors ───────────────────────────────────────┴────────────────────┤
│ none                                                                                                                              │
├───────────────────────────────────────────── Hotspots / Slow files ─────────────────────────────────────────────────────────────┤
│ 1.3s  openapi.yaml   1.1s  src/app.py   0.9s  src/api/users.ts                                                                   │
└────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## 80×24 mockup (narrow terminals)

```
┌ RepoGraph ⟦/repo/acme⟧  INDEXING  Up 00:07:14  DB:open ┐
│ Files 13542  Art 12334  Nodes 88209  Edges 431002      │
│ Rate D:240/s H:180/s P:75/s I:60/s  FS:12/s            │
├ Activity───────────────────────────────────────────────┤
│ 12:14:02 parse  src/api/users.ts   OK n:+12 s:+6 e:+14 │
│ 12:14:01 hash   src/web/app.css    OK sha256:1d3…      │
│ 12:14:00 link   users.ts -> models OK CALLS:+3 REF:+11 │
│ 12:13:59 parse  README.md          OK n:+4 s:+4        │
│ 12:13:58 error  orders.ts          FAIL line 212       │
├ Progress───────────────────────────────────────────────┤
│ Total  [███████▉.....] 38%  5,143/13,542               │
│ Disc.  [██████████....] 58%   Parse [█████......] 25%  │
│ Hash   [███████.......] 37%   Link  [███.........] 18% │
│ Q fs0 h2 p6 i1 db0  Workers P:6 H:4 L:4 DB:2          │
├ Last Errors────────────────────────────────────────────┤
│ 12:13:58 orders.ts  SyntaxError line 212               │
└────────────────────────────────────────────────────────┘
```

---

## Panels and fields

* **Header**: repo path, mode, uptime, DB state, worker threads.
* **KPI line**: Files, Artifacts, Nodes, Edges, Errors. Event/throughput rates.
* **Activity**: most recent operations. Columns: time, op, subject, result.
* **Right panel**:

    * **Progress** in indexing: total and phase bars; queue depths; workers; rough ETA.
    * **FS Watch** in steady state: recent filesystem events, queue summary, burst stats.
* **Last Errors**: rolling few with precise selectors (`#line=` or `#char=`).
* **Hotspots**: slowest files this session.

---

## Interaction hints (optional)

* `Tab` switch right panel (if you ever want manual control).
* `f` filter path prefix. `e` toggle errors only. `q` quit.

This single layout keeps muscle memory. Mode only changes the right panel’s purpose and the rate line.

User:

Can you please create this using spectre.console live display. ensure you read the documentation so that you know what is available. Ideally I'd like a viewmodel approach where the data is stored on a data class and the renderable elements are updated from it periodically

Below is a self‑contained Spectre.Console “live dashboard” with a view‑model. One layout adapts to **Indexing** or **Watch** mode. Uses `Layout`, `Table`, `Panel`, `BarChart`, and `AnsiConsole.Live` with periodic refresh. API usage mirrors the docs for Live, Layout, Table, and BarChart. ([spectreconsole.net][1])

```csharp
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Spectre.Console;

namespace RepoGraph.Dashboard
{
    // --------------------------- View model ---------------------------

    public enum DashboardMode { Indexing, Watch }

    public sealed class DashboardState
    {
        public string RepositoryPath { get; set; } = "";
        public DashboardMode Mode { get; set; } = DashboardMode.Indexing;
        public DateTimeOffset StartTime { get; set; } = DateTimeOffset.UtcNow;

        public string DatabaseState { get; set; } = "open";
        public int WorkerThreads { get; set; } = Environment.ProcessorCount;

        // Totals
        public long FileCount { get; set; }
        public long NewFileCount { get; set; }
        public long ChangedFileCount { get; set; }
        public long SkippedFileCount { get; set; }
        public long ArtifactCount { get; set; }
        public long NodeCount { get; set; }
        public long EdgeCount { get; set; }
        public long ErrorCount { get; set; }

        // Rates (per second)
        public double DiscoverRate { get; set; }
        public double HashRate { get; set; }
        public double ParseRate { get; set; }
        public double IndexRate { get; set; }
        public double FileSystemEventRate { get; set; }

        // Queues and workers (optional; used in panels)
        public int QueueDiscover { get; set; }
        public int QueueHash { get; set; }
        public int QueueParse { get; set; }
        public int QueueIndex { get; set; }
        public int QueueDatabase { get; set; }

        public int WorkersParse { get; set; }
        public int WorkersHash { get; set; }
        public int WorkersLink { get; set; }
        public int WorkersDatabase { get; set; }
        public int WorkersIdle { get; set; }

        // Progress (Indexing)
        public int TotalDiscovered { get; set; }
        public int TotalToDiscover { get; set; }
        public int TotalHashed { get; set; }
        public int TotalToHash { get; set; }
        public int TotalParsed { get; set; }
        public int TotalToParse { get; set; }
        public int TotalLinked { get; set; }
        public int TotalToLink { get; set; }

        // Activity stream (left pane)
        public readonly ConcurrentQueue<ActivityRow> Activity = new();
        public int ActivityCapacity { get; set; } = 12;

        // Errors (bottom right)
        public readonly ConcurrentQueue<ErrorRow> Errors = new();
        public int ErrorCapacity { get; set; } = 4;

        // Hotspots (bottom full width)
        public readonly ConcurrentQueue<HotspotRow> Hotspots = new();
        public int HotspotCapacity { get; set; } = 6;

        // File system events (right pane in watch mode)
        public readonly ConcurrentQueue<FileEventRow> FileEvents = new();
        public int FileEventCapacity { get; set; } = 8;

        public void PushActivity(ActivityRow row)
        {
            Activity.Enqueue(row);
            while (Activity.Count > ActivityCapacity && Activity.TryDequeue(out _)) { }
        }

        public void PushError(ErrorRow row)
        {
            Errors.Enqueue(row);
            while (Errors.Count > ErrorCapacity && Errors.TryDequeue(out _)) { }
            ErrorCount = Errors.Count;
        }

        public void PushHotspot(HotspotRow row)
        {
            Hotspots.Enqueue(row);
            while (Hotspots.Count > HotspotCapacity && Hotspots.TryDequeue(out _)) { }
        }

        public void PushFileEvent(FileEventRow row)
        {
            FileEvents.Enqueue(row);
            while (FileEvents.Count > FileEventCapacity && FileEvents.TryDequeue(out _)) { }
        }
    }

    public readonly record struct ActivityRow(DateTimeOffset Timestamp, string Stage, string Subject, string Result);
    public readonly record struct ErrorRow(DateTimeOffset Timestamp, string Path, string Message, string? Selector);
    public readonly record struct HotspotRow(double Seconds, string Path);
    public readonly record struct FileEventRow(DateTimeOffset Timestamp, string Kind, string Path);

    // --------------------------- Renderer ---------------------------

    public sealed class LiveDashboard
    {
        private readonly DashboardState state;
        private readonly Layout layout;

        public LiveDashboard(DashboardState state)
        {
            this.state = state;
            layout = CreateLayoutSkeleton();
        }

        public void Run(CancellationToken token, TimeSpan? refresh = null)
        {
            var interval = refresh ?? TimeSpan.FromMilliseconds(200);

            // Seed initial UI
            UpdateAllRegions();

            // Live render loop
            AnsiConsole.Live(layout)
                .Overflow(VerticalOverflow.Crop)   // crop tall panels
                .Cropping(VerticalOverflowCropping.Top) // crop from top when needed
                .AutoClear(false)                  // keep dashboard on exit
                .Start(ctx =>                     // Live API per docs
                {
                    while (!token.IsCancellationRequested)
                    {
                        UpdateAllRegions();
                        ctx.Refresh();            // push changes
                        Thread.Sleep(interval);
                    }
                });
        }

        // ---------- Layout skeleton ----------

        private static Layout CreateLayoutSkeleton()
        {
            // Root split: header, body, footer
            var header = new Layout("Header").Size(3);
            var footer = new Layout("Footer").Size(8);

            var left = new Layout("Left");
            var right = new Layout("Right");

            // Left: Activity (top) + Hotspots (bottom)
            left.SplitRows(
                new Layout("Activity").Ratio(3),
                new Layout("Hotspots").Ratio(1));

            // Right: Mode panel (Progress/Watch) + Errors
            right.SplitRows(
                new Layout("ModePanel").Ratio(2),
                new Layout("Errors").Ratio(1));

            var body = new Layout("Body").SplitColumns(left, right);

            return new Layout("Root").SplitRows(header, body, footer);
        }

        // ---------- Region updates ----------

        private void UpdateAllRegions()
        {
            layout["Header"].Update(RenderHeader());
            layout["Activity"].Update(RenderActivity());
            layout["ModePanel"].Update(RenderModePanel());
            layout["Errors"].Update(RenderErrors());
            layout["Hotspots"].Update(RenderHotspots());
            layout["Footer"].Update(RenderFooter());
        }

        private IRenderable RenderHeader()
        {
            var uptime = DateTimeOffset.UtcNow - state.StartTime;
            var top = new Markup(
                $"[bold]RepoGraph[/]  ⟦{Markup.Escape(state.RepositoryPath)}⟧  " +
                $"Mode: [yellow]{state.Mode}[/]  Uptime {uptime:hh\\:mm\\:ss}  DB: {state.DatabaseState}  Threads:{state.WorkerThreads}");

            var grid = new Grid().AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap());

            grid.AddRow(
                $"Files [bold]{state.FileCount:N0}[/]",
                $"New [bold]{state.NewFileCount:N0}[/]",
                $"Changed [bold]{state.ChangedFileCount:N0}[/]",
                $"Artifacts [bold]{state.ArtifactCount:N0}[/]",
                $"Nodes [bold]{state.NodeCount:N0}[/]",
                $"Edges [bold]{state.EdgeCount:N0}[/]"
            );

            var rate = state.Mode == DashboardMode.Indexing
                ? $"Rate  Discover {state.DiscoverRate:F0}/s | Hash {state.HashRate:F0}/s | Parse {state.ParseRate:F0}/s | Index {state.IndexRate:F0}/s | FS {state.FileSystemEventRate:F0}/s"
                : $"Rate  FS {state.FileSystemEventRate:F0}/s | Reindex {state.IndexRate:F0}/s | Parse {state.ParseRate:F0}/s | Link {state.HashRate:F0}/s";

            return new Rows(
                top,
                grid,
                new Markup(rate)
            );
        }

        private IRenderable RenderActivity()
        {
            var table = new Table().Border(TableBorder.None).ShowHeaders(true);
            table.AddColumn(new TableColumn("[dim]Time[/]").NoWrap());
            table.AddColumn(new TableColumn("Stage").NoWrap());
            table.AddColumn(new TableColumn("Subject").LeftAligned());
            table.AddColumn(new TableColumn("Result").LeftAligned());

            foreach (var row in state.Activity.Reverse().Take(state.ActivityCapacity))
            {
                table.AddRow(
                    $"[dim]{row.Timestamp:HH:mm:ss}[/]",
                    RowStageStyle(row.Stage),
                    Markup.Escape(row.Subject),
                    row.Result
                );
            }

            return new Panel(table).Header("Activity").Expand();
        }

        private IRenderable RenderModePanel()
        {
            if (state.Mode == DashboardMode.Indexing)
                return new Panel(RenderProgressChart()).Header("Progress").Expand();
            else
                return new Panel(RenderWatchTable()).Header("File System Watch").Expand();
        }

        private IRenderable RenderProgressChart()
        {
            // Convert counts to percentages safely
            double pct(double done, double total) => total <= 0 ? 0 : Math.Clamp(done / total * 100.0, 0, 100);

            var totalPct = pct(
                Math.Min(state.TotalLinked, state.TotalToLink > 0 ? state.TotalToLink : state.TotalParsed),
                Math.Max(1, Math.Max(state.TotalToLink, Math.Max(state.TotalToParse, state.TotalToDiscover)))
            );

            var bc = new BarChart()
                .Width(40)
                .CenterLabel()
                .Label($"[bold]Total[/] {totalPct,5:F1}%")
                .ShowValues();

            bc.AddItem($"Discover {pct(state.TotalDiscovered, state.TotalToDiscover),5:F1}%", pct(state.TotalDiscovered, state.TotalToDiscover), Color.SteelBlue1);
            bc.AddItem($"Hash     {pct(state.TotalHashed,    state.TotalToHash),5:F1}%", pct(state.TotalHashed,    state.TotalToHash),    Color.MediumPurple4);
            bc.AddItem($"Parse    {pct(state.TotalParsed,    state.TotalToParse),5:F1}%", pct(state.TotalParsed,    state.TotalToParse),   Color.Yellow3);
            bc.AddItem($"Link     {pct(state.TotalLinked,    state.TotalToLink),5:F1}%", pct(state.TotalLinked,    state.TotalToLink),    Color.SpringGreen1);

            var queues = new Markup(
                $"Queues  discover {state.QueueDiscover} | hash {state.QueueHash} | parse {state.QueueParse} | index {state.QueueIndex} | db {state.QueueDatabase}\n" +
                $"Workers parse {state.WorkersParse} | hash {state.WorkersHash} | link {state.WorkersLink} | db {state.WorkersDatabase} | idle {state.WorkersIdle}"
            );

            return new Rows(bc, new Rule(), queues);
        }

        private IRenderable RenderWatchTable()
        {
            var table = new Table().Border(TableBorder.None).ShowHeaders(true);
            table.AddColumn(new TableColumn("[dim]Time[/]").NoWrap());
            table.AddColumn(new TableColumn("Event").NoWrap());
            table.AddColumn(new TableColumn("Path"));

            foreach (var ev in state.FileEvents.Reverse().Take(state.FileEventCapacity))
                table.AddRow($"[dim]{ev.Timestamp:HH:mm:ss}[/]", ev.Kind, Markup.Escape(ev.Path));

            var queues = new Markup(
                $"Queues  fs {state.QueueDiscover} | parse {state.QueueParse} | link {state.QueueIndex} | db {state.QueueDatabase}\n" +
                $"Recent burst: peak ?/s  last60s {state.FileSystemEventRate:F1}/s");

            return new Rows(table, new Rule(), queues);
        }

        private IRenderable RenderErrors()
        {
            var table = new Table().Border(TableBorder.None).ShowHeaders(true);
            table.AddColumn(new TableColumn("[dim]Time[/]").NoWrap());
            table.AddColumn(new TableColumn("Path"));
            table.AddColumn(new TableColumn("Message"));
            table.AddColumn(new TableColumn("Selector").NoWrap());

            if (state.Errors.IsEmpty)
            {
                table.AddRow("[dim]—[/]", "none", "[dim]—[/]", "[dim]—[/]");
            }
            else
            {
                foreach (var e in state.Errors.Reverse().Take(state.ErrorCapacity))
                {
                    table.AddRow(
                        $"[dim]{e.Timestamp:HH:mm:ss}[/]",
                        Markup.Escape(e.Path),
                        Markup.Escape(e.Message),
                        e.Selector is null ? "[dim]—[/]" : Markup.Escape(e.Selector)
                    );
                }
            }

            return new Panel(table).Header("Last Errors").Expand();
        }

        private IRenderable RenderHotspots()
        {
            var table = new Table().Border(TableBorder.None).ShowHeaders(true);
            table.AddColumn(new TableColumn("Wall time").NoWrap());
            table.AddColumn(new TableColumn("Path"));

            if (state.Hotspots.IsEmpty)
                table.AddRow("[dim]—[/]", "[dim]—[/]");
            else
            {
                foreach (var h in state.Hotspots.OrderByDescending(h => h.Seconds).Take(state.HotspotCapacity))
                    table.AddRow($"{h.Seconds,4:F1}s", Markup.Escape(h.Path));
            }

            return new Panel(table).Header("Hotspots / Slow files").Expand();
        }

        private IRenderable RenderFooter()
        {
            // Simple help/footer line
            var grid = new Grid().AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap())
                                 .AddColumn(new GridColumn().NoWrap());
            grid.AddRow(
                "[grey]Tab: switch focus[/]",
                "[grey]F: filter[/]",
                "[grey]E: errors only[/]",
                "[grey]Q: quit[/]"
            );
            return new Rows(new Rule(), grid);
        }

        private static string RowStageStyle(string stage)
        {
            var s = stage.ToLowerInvariant();
            var label = stage;
            var color = s switch
            {
                "discover" => "blue",
                "hash"     => "purple",
                "parse"    => "yellow",
                "link"     => "green",
                "error"    => "red",
                "ingest"   => "deepskyblue1",
                "change"   => "springgreen1",
                _          => "silver"
            };
            return $"[{color}]{Markup.Escape(label)}[/]";
        }
    }

    // --------------------------- Example Program ---------------------------

    public static class Program
    {
        public static void Main()
        {
            var vm = new DashboardState
            {
                RepositoryPath = "/repo/acme",
                Mode = DashboardMode.Indexing,
                WorkerThreads = 16,
                FileCount = 13542,
                ArtifactCount = 12334,
                NodeCount = 88209,
                EdgeCount = 431002
            };

            // Seed some rows
            vm.PushActivity(new ActivityRow(DateTimeOffset.Now, "parse", "src/api/users.ts", "OK  nodes:+12 spans:+6 edges:+14"));
            vm.PushActivity(new ActivityRow(DateTimeOffset.Now, "hash", "src/web/app.css", "OK  sha256:1d3…"));
            vm.PushActivity(new ActivityRow(DateTimeOffset.Now, "link", "users.ts -> models", "OK  CALLS:+3 REFERS_TO:+11"));
            vm.PushError(new ErrorRow(DateTimeOffset.Now, "src/api/orders.ts", "SyntaxError at line 212", "#line=212"));
            vm.PushHotspot(new HotspotRow(3.2, "openapi.yaml"));
            vm.PushHotspot(new HotspotRow(2.8, "src/app.py"));

            // Fake progress
            vm.TotalToDiscover = 13542; vm.TotalDiscovered = 8200;
            vm.TotalToHash = 13542;     vm.TotalHashed = 5100;
            vm.TotalToParse = 9000;     vm.TotalParsed = 2300;
            vm.TotalToLink = 7000;      vm.TotalLinked = 1250;

            var dash = new LiveDashboard(vm);

            using var cts = new CancellationTokenSource();

            // Simulate updates on a timer
            var t = new Thread(() =>
            {
                var r = new Random();
                while (!cts.IsCancellationRequested)
                {
                    vm.DiscoverRate = 240; vm.HashRate = 180; vm.ParseRate = 75; vm.IndexRate = 60; vm.FileSystemEventRate = 12;
                    vm.QueueDiscover = r.Next(0, 3); vm.QueueHash = r.Next(0, 3); vm.QueueParse = r.Next(0, 8); vm.QueueIndex = r.Next(0, 3); vm.QueueDatabase = 0;
                    vm.WorkersParse = 6; vm.WorkersHash = 4; vm.WorkersLink = 4; vm.WorkersDatabase = 2; vm.WorkersIdle = 0;

                    // advance progress a bit
                    vm.TotalDiscovered = Math.Min(vm.TotalToDiscover, vm.TotalDiscovered + r.Next(0, 50));
                    vm.TotalHashed     = Math.Min(vm.TotalToHash,     vm.TotalHashed     + r.Next(0, 40));
                    vm.TotalParsed     = Math.Min(vm.TotalToParse,    vm.TotalParsed     + r.Next(0, 25));
                    vm.TotalLinked     = Math.Min(vm.TotalToLink,     vm.TotalLinked     + r.Next(0, 18));

                    if (r.NextDouble() < 0.2)
                        vm.PushActivity(new ActivityRow(DateTimeOffset.Now, "parse", $"src/file{r.Next(1, 999)}.ts", "OK  nodes:+3 spans:+1 edges:+2"));

                    Thread.Sleep(250);
                }
            })
            { IsBackground = true };
            t.Start();

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };

            dash.Run(cts.Token, TimeSpan.FromMilliseconds(200));
        }
    }
}
```

Notes:

* Uses `AnsiConsole.Live(...).Start(ctx => { ... ctx.Refresh(); ... })` and `Layout.Update(...)` per docs. ([spectreconsole.net][1])
* `Table`, `Panel`, and `BarChart` are standard widgets. ([spectreconsole.net][2])
* Single design; right panel switches by `DashboardMode`.
* Replace the timer block with your real indexer and file‑watcher updates.

[1]: https://spectreconsole.net/live/live-display?utm_source=chatgpt.com "Live Display"
[2]: https://spectreconsole.net/widgets/table?utm_source=chatgpt.com "Table"
