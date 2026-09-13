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

## Milestone 2 — the timeline (3–4 days)

8. **Timeline layer.** `data-start` / `data-duration` / `data-track` decide what is in the document
   at `t`, through the ordinary model and binding — elements are kept out until their window opens
   and removed when it closes, so a `@keyframes` on an element starts when the element appears.
   Emit `animation-delay: {start}s` alongside each element's window: the engine's clock is
   absolute and stamps no creation time, and `animation-delay` is measured against that same clock,
   so this is what gives a late element its own zero. Verified by experiment at four sample times.
9. **`inspect`** — the timeline as data: tracks, windows, fonts asked for, images referenced,
   duration.
10. **`lint`** — the determinism verdict: platform-resolved fonts, sources that never loaded,
    wall-clock content, and **whether the composition is pure in `t`** (no transitions, no toasts,
    no scroll-driven easing). A pure composition can be sampled at any single `t` for 5.6 ms with no
    sweep, and that is worth telling an author.

## Milestone 3 — interaction (2 days)

11. **Interaction track.** `data-cut-click="1.2"`, or a JSON sidecar of `(t, action)` pairs, over
    `DispatchClick` and the typing APIs. The engine takes input with no window, so this is something
    a Chrome pipeline cannot do at all. Replay from zero on every seek — correct, and cheap at
    179 fps. Caching per `t` is an optimisation for later, not now.

## Phase 2 — deferred, deliberately

12. Video seek-to-time in the engine (a renderer needs *the frame at t*, not playback), WOFF 2 in
    the engine (a real decoder — Brotli plus `glyf`/`loca` reconstruction — not a decompression
    call), audio mux here.

---

## Decided — do not re-open

| | |
|---|---|
| **Name** | CupriCut. The film word: a cut, the final cut. Says video without saying animation tool. |
| **`render_frame(t)` means** | **sweep from 0** and return the last frame. A frame is a function of `t` *and* the frames before it; sweeping is correct by construction and 5.6 ms per swept frame. |
| **Fresh document per frame** | No. 15.7× the cost for frames *less* like the finished video, because every transition restarts. |
| **Composition attributes** | hyperframes' `data-start` / `data-duration` / `data-track`, for agent familiarity. |
| **Engine changes** | None. CupriCut consumes the `CupriFace` package the way Khalkos3D does; timelines and encoders stay out of the engine. |
| **Determinism advertised** | Identical pixels per OS, identical layout across OSes. Never "render anywhere, reproduce anywhere". |
| **Fonts** | `FontPolicy.RegisteredOnly`, always. A family that would resolve to the machine is an error naming the family. |

## Open — decide before the code that depends on them

- **Server only, or server plus CLI.** The plan above assumes both over one set of services, because
  a renderer is also a build step. Cheap now, awkward to retrofit.
- **Safety limits and their defaults.** A renderer *writes files*, so the analogue of a read-only
  mode is: `Cut:OutputRoot` (the only directory a tool may write under), `Cut:CompositionRoots` (the
  only directories it may read compositions and fonts from), `Cut:MaxFrames` / `Cut:MaxPixels` (a
  runaway request is a full disk), `Cut:EnableVideo` (off → PNG only, no ffmpeg). The names are
  settled; the defaults are not.
- **Where the sweep cache lives, if one ever exists.** Milestone 3 note only — replay first.

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
