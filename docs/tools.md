# Tools

Scripts for working on Folio, in `tools/`. They need what [building](building.md) does, plus Microsoft Edge, which renders the benchmark chart and the icon. They work from any checkout, including git worktrees, and act on the checkout they're in.

| Command | What it does |
| --- | --- |
| `tools\dev.ps1` | Builds this checkout, registers it as a PDF app and starts it |
| `dotnet run --project tools/Folio.Tools -- bench` | Measures Folio and other PDF viewers, and File Explorer thumbnail handlers |
| `dotnet run --project tools/Folio.Tools -- chart <results.json>` | Redraws a benchmark chart |
| `dotnet run --project tools/Folio.Tools -- icon` | Rebuilds the app icon files |

## Trying a build

```powershell
tools\dev.ps1                         # build (Release, Native AOT, as CI ships), register and start
tools\dev.ps1 a.pdf b.pdf             # ...and open files
tools\dev.ps1 -Configuration Debug    # faster JIT build
tools\dev.ps1 -NoLaunch               # build and register only
tools\dev.ps1 -SetDefault             # also open Settings to make it the default PDF app
tools\dev.ps1 -List                   # every registered build
tools\dev.ps1 -Unregister             # remove this checkout's registration (-All: every one)
```

Each checkout is registered under its own name, "Folio Dev (*folder*)", so builds from several worktrees can be installed at once: right-click a PDF in File Explorer, then **Open with** to pick one. Each build runs as its own instance, so opening a file with one never lands in another's window; About shows the checkout and commit a build came from. Registration is per user and needs no admin rights. Registrations of deleted checkouts are cleaned up on the next run. Builds share settings and recent files with an installed Folio.

If PowerShell refuses to run scripts, use `pwsh tools\dev.ps1` (PowerShell 7) or `powershell -ExecutionPolicy Bypass -File tools\dev.ps1`.

## Benchmark

```powershell
dotnet run --project tools/Folio.Tools -- bench
dotnet run --project tools/Folio.Tools -- bench --apps Folio,Edge --runs 10
dotnet run --project tools/Folio.Tools -- bench --apps PowerToys    # only PowerToys' thumbnails; opens no windows
```

It publishes this checkout's Folio, and its File Explorer thumbnail handler (`src/Folio.Thumbnails`) next to it, to `out\bench\folio`. It measures them against whichever of Microsoft Edge, Adobe Acrobat, SumatraPDF and PowerToys are installed, on two documents generated into `out\bench\documents`: two pages of text, and 1,000 pages whose first page is a topographic map (a 15-megapixel image under some 140,000 contour line segments), the rest text with photos and charts.

The viewers (Folio, Edge, Acrobat, SumatraPDF) are measured first:

- **Page**: time from starting the viewer to page 1 on screen. Page 1 of each document has a magenta band across the top, drawn last; the benchmark watches the viewer's windows with Windows.Graphics.Capture until the band appears, and takes the time DWM composed that frame. The machine can be used during a run, though that adds noise.
- **Memory**: private working set (Task Manager's Memory column) summed over the viewer's processes once the viewer has finished loading: when the total has stayed within 2% for 2 seconds, or 5 seconds after the page appears, whichever is first. Also recorded: the peak, and commit (private bytes).

Each viewer is closed between runs, and every viewer/document pair gets a warm-up run first (timed, not recorded), so the numbers are warm starts. With the defaults, a run of all four viewers takes about 5 minutes. Viewers take turns, to spread out noise. Edge runs with a blank temporary profile: not signed in, no extensions, and no background downloads, which otherwise change its results from one session to the next. SumatraPDF gets temporary settings too, so neither touches yours. Each viewer's window is kept in front until the page shows, without taking the focus, as Windows would otherwise open it behind the app you're using. Runs still go ahead while the screen is locked, but viewers are slower then (Folio by about 70% when tested), so the benchmark warns if it notices. Folio, Acrobat and SumatraPDF would hand the file to a copy that's already running, so the benchmark stops if one is; `--close-running` closes them instead (losing unsaved work).

Then the thumbnail handlers (Folio's and PowerToys') make a `--thumbnail-size` thumbnail of each document, taking turns, with the same runs and warm-up. PowerToys' PDF thumbnails don't have to be turned on, as the benchmark loads the handler from its DLL. Each handler is loaded into a host process of its own, as Explorer loads it into a COM surrogate, and reads the PDF through a stream that lives in the benchmark's process, so every call on the stream is a round trip between processes, as it is from Explorer. Hosts stay up between thumbnails, as the surrogate does while Explorer keeps asking.

- **Thumbnail**: time from creating the handler to getting the bitmap, as Explorer does for each file. The first thumbnail of each handler also loads it, so it's reported separately; the others are warm.
- **CPU**: user and kernel time of the host and every process it started, from a job object. Windows counts it in scheduler ticks (about 16 ms), so small values are rough.
- **Processes**: processes started for the thumbnail. PowerToys' handler copies the file to a temporary folder and starts an exe that renders it with Windows.Data.Pdf, then reads back a BMP; Folio's renders in the host with PDFium.
- **Stream calls** and **Read**: calls on the stream, and the bytes they read.

Results go to `out\bench\<date-time>\`: `runs.csv` (every viewer run), `thumbnails.csv` (every thumbnail), `thumbnails\` (each handler's thumbnail of each document, to check what was measured), `results.json` (medians) and `chart.png`. To update the README chart, redraw it from a run, picking the apps to show:

```powershell
dotnet run --project tools/Folio.Tools -- chart out\bench\<date-time>\results.json --apps Folio,Edge,Acrobat,Sumatra,PowerToys --out .github\assets\benchmark.png
```

| Option | Default | |
| --- | --- | --- |
| `--apps` | Folio,Edge,Acrobat,Sumatra,PowerToys | Viewers and thumbnail handlers to measure; Folio is both |
| `--runs` | 5 | Recorded runs per app and document |
| `--warmup` | 1 | Unrecorded runs first |
| `--folio <exe>` | | Measure this Folio build instead of publishing one, and the `Folio.Thumbnails.dll` next to it if there is one |
| `--thumbnail-size` | 256 | Thumbnail size in pixels to ask for; 256 is what Explorer's Large icons view uses |
| `--close-running` | | Close running viewers instead of stopping |
| `--save-frames` | | Save the frame each run detected the page in, to check what was measured |
| `--settle` | 5 | Most seconds to wait for memory to settle after the page appears |
| `--timeout` | 60 | Seconds to wait for the page, or for a thumbnail before restarting the handler's host |
| `--cooldown` | 0.5 | Seconds between runs |
| `--fraction` | 0.01 | Share of a window the band must cover; low enough for a page at fit-page zoom, high enough that a thumbnail doesn't count |
| `--out <dir>` | `out\bench\<date-time>` | Where results go |

## App icon

The icon is drawn in `tools/Folio.Tools/AppIcon` at three levels of detail: `AppIcon-full.svg`, `AppIcon-medium.svg` (thicker text lines, for 32 to 48 px) and `AppIcon-small.svg` (no text lines, for 24 px and below). After editing them, run

```powershell
dotnet run --project tools/Folio.Tools -- icon
```

to rebuild `src/Folio/Assets/AppIcon.png`, `AppIcon.ico` and the MSIX logos in `src/Folio/Assets/Package`. `out\icon-preview.png` shows every size of the .ico on a light and a dark background.
