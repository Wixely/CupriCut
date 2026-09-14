# Build order

[docs/SCOPE.md](docs/SCOPE.md) is the reasoning: the question, the measurements, the split between
engine and product, and the risks. This file is what to do, in what order, and what is already
decided so nobody re-opens it.

Everything the engine had to provide is **done**. Fonts landed in CupriFace v0.21.0 (`@font-face`,
`LoadFont`/`LoadFonts`, weight buckets, `FontPolicy.RegisteredOnly`, a font report, and the
cross-platform text gate), the keyframe-clock question was answered by experiment, and
`doc.Settle(w, h, timeout)` landed in v0.23.0. As of **v0.24.0** there is no engine prerequisite
left. What remains is this repository.

---

## Milestone 1 — a frame, then a video (4–5 days)

The proof of concept with a server around it. Nothing here needs the timeline layer: a composition
is a plain document and `t` drives it.

1. **Skeleton in house style.** `Directory.Build.props` (net10.0, nullable, `Deterministic`),
   `Directory.Packages.props` with central pinning, `NuGet.config` that starts with `<clear />`
   and then declares its sources — the CupriFace package comes from GitHub Packages, which will not
   serve even a public package anonymously, so a token is needed and `NuGet.config` should say so.
   `Program.cs`, `Hosting/`, `Configuration/`, `Services/`, `Tools/`, `.gitignore`, MIT `LICENSE`.
2. **`CupriCutService`** over `CupriFace` 0.24.0: load a composition from a path under
   `Cut:CompositionRoots`, register fonts, set `FontPolicy.RegisteredOnly`, `doc.Settle(...)` before
   frame 0, then sweep `Animate(t)` → `RenderToImage`. **One sweep function that every tool calls**
   — this is the decision the re-measurement forces, and it must not be re-implemented per tool.
3. **`render_frame`** and **`contact_sheet`** first. They are what an agent iterates with, and a
   contact sheet is the single highest-value thing here: one image, a whole motion.
4. **`render_frames`**, then **`render_video`** — raw RGBA on ffmpeg's stdin. Encode PNGs on a
   thread pool; the video path never pays the PNG cost.
5. **`probe`** — is ffmpeg there, which codecs, what does the engine report as its renderer.
6. **The `cupricut` CLI** over the same services, same verbs.
7. **Docker** — the one departure from house style: the image carries ffmpeg. The zips do not, and
   `Cut:FfmpegPath` names it.

**Done when** an agent can point a tool at an HTML file and get back a contact sheet, and a CI job
can turn the same file into an MP4.

## Milestone 1b — the project file (1–2 days)

The thing that makes the agent loop survive the end of a session. Everything above renders a file
an agent has to keep somewhere else; this gives the work a home that CupriCut itself owns, so a
second MCP run can open what a first one made, read it, adjust it and re-render — with no
filesystem tool and no memory of the conversation that produced it.

8. **`.cut.json`, one self-contained file.** HTML, CSS, the render settings that regenerate the
   animation (`width`, `height`, `scale`, `fps`, `duration`, `background`, `alpha`, `codec`) and
   free-text `meta` — intent, notes, revision history. **Assets are inlined as `data:` URIs**, which
   is what makes it genuinely one file: the engine's `SourceResolver` already takes a `data:` URI
   everywhere it takes a path, so an inlined logo needs no new engine road.
9. **A project is a composition.** `render_frame`, `contact_sheet`, `render_frames` and
   `render_video` take a `.cut.json` wherever they take an `.html`, and its `render` block supplies
   every argument the caller left out. One sweep function still, one meaning of `t` still — the
   project is a *source of defaults*, never a second render path.
10. **`Cut:ProjectRoot`** — the one directory that is both read and written, alongside the
    read-only `Cut:CompositionRoots` and the write-only `Cut:OutputRoot`. Only `.cut.json` may be
    written there, so a read-write root does not become a general file drop.
11. **Tools:** `save_project` (create or replace), `load_project` (read it back whole, which is the
    tool the second run actually needs), `update_project` (patch named fields, so an agent can
    change the CSS without resending the HTML), `list_projects`, and `attach_asset` (inline a file
    from a composition root as a `data:` URI). Same verbs on the CLI.

**Done when** one run can `save_project`, and a second run with no shared context can
`load_project`, `update_project` and `render_video` from it.

## Milestone 1c — the studio window (3–4 days)

The loop this whole tool is built around is *look, adjust, look* — and so far only the agent gets to
look. A person reviewing the output has to open a PNG somewhere else, and then describe what is
wrong in prose. The window closes that: it previews the render, and it lets the reviewer **point at
the pixels** rather than describe them.

12. **Two modes, one binary.** `CupriCut` opens a window by default; `-c` / `--console` runs
    headless. **Both host the MCP server** — the window is a second face on the same service, never
    a separate app. Docker and Windows Service imply `-c`, and a window that cannot open falls back
    to console with a message rather than exiting.
13. **The preview.** The GUI is itself a `CupriApp`, so CupriCut's own interface is drawn by the
    engine it renders with. The rendered frame reaches the window through `ISurfaceSource` — the
    seam the engine already has for live pixel producers — so a preview costs no PNG encode and no
    base64, just the `SKImage` the sweep already made.
14. **Annotations: the reviewer points, the agent reads.** Drag a box on the preview, type a note.
    It is stored **in the project**, because that is already the file a later run opens, and in
    **normalised 0–1 coordinates**, so a note survives the composition being re-rendered at another
    size. `list_annotations` / `resolve_annotation` are how the agent picks them up and closes them.

**Done when** a reviewer can scrub to a frame, draw a box round the thing that is wrong, type
"logo enters too late", and a separate agent run can read that back with the time and the region.

## Milestone 1d — the studio as a tool, not a viewer (6–8 days)

Everything here came from using the window in anger. They divide into things the renderer must be
able to DO, and things the window must let a person DO — and the first group is what the second is
built on, so it goes first.

**Backend**

16. ~~**Parallel video.**~~ **Done** — 5.2x on the render, 15% on a clip, because the encoder is
    now the floor. See the commit for the two designs that measured slower and were reverted.
    Originally: A 10-minute clip at 120 fps is 72,000 frames and no amount of clever `t`
    handling changes that — but a composition that is pure in `t` has independent frames, so they
    shard across cores. Render to a temp directory and mux, or feed an ordered queue straight into
    ffmpeg; the first is simpler and the disk is cheap. ~12x on this machine.
17. ~~**Calibrate.**~~ **Done.** Encodes a two-frame clip per capability with the real arguments and
    probes the result, and times the render at every plausible worker count. Errors-only by default.
    It found both of the things it was built for without being told: vp9 drops alpha on this ffmpeg,
    and the best parallelism here is 8 — not the 12 `ProcessorCount` suggested, nor the 6 the
    built-in guess used. `--apply` writes the measured count to `CupriCut.Local.json`.
18. **Special elements.** `--cupricut-background` marks an element as the backdrop, so it can be
    shown or hidden per render: the same composition gives an opaque video for review and a
    transparent one for the edit. Recognised, reported by `inspect`, and toggled per render.
19. **Mask output.** A black-and-white alpha video beside (or instead of) the colour, for the many
    editors that will not take a transparent one.
20. **Export formats.** One verb, a list of targets — mp4, webm, mov, gif, PNG sequence, mask,
    matte — rather than a codec argument people have to already understand.
21. **Resolution and scaling in the project.** Width, height and the `PresentInfo` mode
    (responsive / fixed / hybrid / adaptive) stored as project data, so "render this at 4K the way
    it looks at 1080p" is a setting rather than a re-authoring.

**The window**

22. ~~**A settings page, tabbed.**~~ **Done.** Three tabs: the MCP server with copyable URLs
    (`data-cupri-copy`, which the desktop host already wires to the clipboard), the paths and render
    settings, and Calibrate — which runs on a worker and shows failures only until asked otherwise.
23. ~~**Annotations that can be worked with.**~~ **Done.** The marks are composited *into* the
    frame rather than layered over it — the rectangle is in frame coordinates, and that is the only
    space it means anything in, so there is one mapping instead of two that can drift. A live
    marquee follows the pointer while dragging, with the pixel size read out beside it, and each
    mark is on screen for a full second after its timestamp so it can be found by scrubbing rather
    than by landing on the exact frame. Notes are editable after the fact; choosing one jumps the
    preview to its moment. The frame number is *stamped* at the rate the note was made at rather
    than derived on read, so changing the project's fps later does not silently renumber what the
    reviewer saw.

    Two things had to be fixed to get here. `RenderNode.X`/`Y` are parent-relative, so the origin
    has to be accumulated up the tree the way `HitTesting.Hit` does — taken literally, every note
    landed in the wrong place. And the overlays had to become *children* of the stage: the engine
    has no `pointer-events`, so a sibling drawn on top swallowed the drag and the walk up its
    ancestors never reached the element carrying the mark attribute. A test now asserts that
    whatever sits under the preview centre still leads to that attribute.
24. **Project folders**, with drag and drop in and out.

## Milestone 2 — the timeline (3–4 days)

12. **Timeline layer.** `data-start` / `data-duration` / `data-track` decide what is in the document
   at `t`, through the ordinary model and binding — elements are kept out until their window opens
   and removed when it closes, so a `@keyframes` on an element starts when the element appears.
   Emit `animation-delay: {start}s` alongside each element's window: the engine's clock is
   absolute and stamps no creation time, and `animation-delay` is measured against that same clock,
   so this is what gives a late element its own zero. Verified by experiment at four sample times.
13. **`inspect`** — the timeline as data: tracks, windows, fonts asked for, images referenced,
   duration.
14. **`lint`** — the determinism verdict: platform-resolved fonts, sources that never loaded,
    wall-clock content, and **whether the composition is pure in `t`** (no transitions, no toasts,
    no scroll-driven easing). A pure composition can be sampled at any single `t` for 5.6 ms with no
    sweep, and that is worth telling an author.

## Milestone 3 — interaction (2 days)

15. **Interaction track.** `data-cut-click="1.2"`, or a JSON sidecar of `(t, action)` pairs, over
    `DispatchClick` and the typing APIs. The engine takes input with no window, so this is something
    a Chrome pipeline cannot do at all. Replay from zero on every seek — correct, and cheap at
    179 fps. Caching per `t` is an optimisation for later, not now.

## Phase 2 — deferred, deliberately

16. Video seek-to-time in the engine (a renderer needs *the frame at t*, not playback), WOFF 2 in
    the engine (a real decoder — Brotli plus `glyf`/`loca` reconstruction — not a decompression
    call), audio mux here.

---

## Decided — do not re-open

| | |
|---|---|
| **Name** | CupriCut. The film word: a cut, the final cut. Says video without saying animation tool. |
| **`render_frame(t)` means** | The frame a sweep from 0 would produce — but reached directly when the composition is **pure in `t`**, which is measured byte-identical and 10–45× faster. Sweeping is the fallback, not the rule. Conservative: anything unrecognised is treated as impure. |
| **The preview holds its document open** | Opening and settling a composition costs 25–370 ms against 3–7 ms for a frame, so a scrub bar that reopens per position is a slideshow. One render thread owns the document for its life; superseded scrub positions are never rendered. |
| **Fresh document per frame** | No. 15.7× the cost for frames *less* like the finished video, because every transition restarts. |
| **Composition attributes** | hyperframes' `data-start` / `data-duration` / `data-track`, for agent familiarity. |
| **Engine changes** | None. CupriCut consumes the `CupriFace` package the way Khalkos3D does; timelines and encoders stay out of the engine. |
| **Determinism advertised** | Identical pixels per OS, identical layout across OSes. Never "render anywhere, reproduce anywhere". |
| **Fonts** | `FontPolicy.RegisteredOnly`, always. A family that would resolve to the machine is an error naming the family. |
| **Project file** | `.cut.json`, self-contained, assets inlined as `data:` URIs. A project is a composition every render tool accepts, and the source of defaults for arguments the caller omitted — never a second render path. |
| **The window is a second face, not a second app** | GUI and console both host the MCP server over one `CupriCutService`. The window renders nothing the server could not; it exists so a person can look and point. |
| **Annotations live in the project** | Not a sidecar. `.cut.json` is already "everything needed to regenerate this", and review feedback is part of that. Annotating therefore requires a project — a bare `.html` composition must be saved as one first. |
| **Annotation coordinates are normalised** | 0–1 of the frame, never pixels. A note drawn on a 1280×720 preview has to still mean the same region when the project is rendered at 3840×2160. |
| **An inexact length is snapped and announced** | A clip is whole frames, so a length that is not a whole number of them moves to the nearest one — and the delta is reported **always**, with a note naming a frame rate that would have been exact. Never absorbed silently. |
| **Transparency has two shapes** | `embedded` is a real alpha channel and needs vp9 or prores; a **matte** packs colour and alpha into one opaque frame and works with any codec, h264 included. H.264 has no alpha channel and never will, so refusing it for `embedded` and offering the matte is the honest pair. |
| **Alpha is verified, not assumed** | An alpha-capable codec can still drop alpha — libvpx-vp9 on ffmpeg N-91454 does, silently. The finished file's pixel format is probed and a missing channel is an error naming the alternatives. Validating the request was never enough; validate the result. |
| **No frame ceiling** | `Cut:MaxFrames` defaults to **0**. A ten-minute title sequence is an ordinary thing to want, and a ceiling refused it. The cost of length is handled where it arises — a PNG sequence streams through `FrameSequenceWriter` in bounded memory instead of collecting the run — not by refusing the request. The knob stays as an opt-in control for a shared instance. |
| **The three roots** | `Cut:CompositionRoots` read-only, `Cut:OutputRoot` write-only, `Cut:ProjectRoot` read-write and `.cut.json` only. A renderer's safety model is about what it may write, so the write surface is named in three places and nowhere else. |
| **CLI assembly name** | The command is `cupricut`; the assembly is `CupriCut.Cli`. NuGet refuses two assemblies in one solution whose names differ only by case, and the server stays `CupriCut.exe`, so packaging installs the CLI under the name people type. |

## Open — decide before the code that depends on them

- ~~**Server only, or server plus CLI.**~~ **Decided: both**, over one set of services. `cli/`
  references the server project rather than re-implementing anything below the tool layer.
- ~~**Safety limits and their defaults.**~~ **Decided**, and shipped in `CupriCut.json`:
  ~~`MaxFrames` **1800**~~ → **0, no limit** (see below), `MaxPixels` **8294400** (3840×2160), `EnableVideo` **true** (a renderer that cannot render
  video is not the safe default, it is a broken one — the write surface is bounded by `OutputRoot`,
  which is the actual risk), `SettleTimeoutSeconds` **15**, `MaxInlineImageBytes` **4000000** (over
  it, a tool writes the PNG and returns the path rather than filling a context with base64).
- **Where the sweep cache lives, if one ever exists.** Milestone 3 note only — replay first.
- **Whether a project may embed a font.** Assets inline as `data:` URIs today, which would work for
  a font too — but `FontPolicy.RegisteredOnly` reads faces from `Cut:FontDirectories`, not from the
  document, so an embedded font needs `@font-face` to be the registration path. Decide with M2.

## Risks to keep in view

- **First-frame readiness.** A frame rendered before an image or font arrives is wrong *and*
  deterministic, which is worse than wrong and flaky. `doc.Settle` before frame 0, always, and
  treat a `false` return as a failed render rather than a warning.
- **The PNG path is the slow path** — 45 ms/frame at 940×720, roughly 100 ms at 1080p. Fine for a
  contact sheet, ten seconds for a 3-second sequence.
- **Interaction changes every later frame.** A click at `t` that opens a dialog is part of the state
  the sweep carries, which is another reason the sweep is the only honest reading of `t`.
- **Scope creep toward hyperframes' ecosystem** — a block catalogue, Lambda rendering, a hosted
  playground. None of that is the advantage. The advantage is a renderer that needs no browser and
  an agent that can look at frame 45 in seven milliseconds.

---

## Intended layout

Flat, like `GithubMCPSharp` — one project, not a `src/` tree.

```
CupriCut.sln
CupriCut.csproj            the MCP server
CupriCut.json              Cut: / Server: / Serilog: sections
Directory.Build.props      net10.0, nullable, Deterministic
Directory.Packages.props   central package pinning
NuGet.config               <clear /> first; GitHub Packages for CupriFace
Dockerfile                 the one that carries ffmpeg
Program.cs
Configuration/             CutOptions
Hosting/                   password middleware, console icon
Services/                  CupriCutService — load, settle, sweep, encode
Tools/                     [McpServerToolType] classes, one per group
cli/                       the cupricut CLI over the same services
compositions/              worked examples, used by the tests
docs/                      SCOPE.md and what follows it
tests/
```

Configuration layers as the house style does: `CupriCut.json`, then `.Local`, then environment
under a `CUPRICUT_` prefix, then the command line. Feature toggles are enforced by the service
throwing, not by the tool asking.
