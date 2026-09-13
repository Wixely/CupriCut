# CupriCut: which of it is the engine's?

> **Where this came from.** Written in the CupriFace repository while deciding which parts of this
> product belonged to the engine. It is kept here unedited as the record of the reasoning and the
> measurements — [PLAN.md](../PLAN.md) is what to do about it. Everything it lists as an engine
> prerequisite shipped: fonts in CupriFace v0.21.0, the keyframe-clock experiment and the settle
> signal in v0.23.0. The engine is at v0.24.0.

[hyperframes](https://github.com/heygen-com/hyperframes) is "write HTML, render video, built for
agents": HTML + CSS compositions with `data-start` / `data-duration` on elements, "seekable"
animation adapters, and a headless Chrome that seeks each frame into FFmpeg for a deterministic MP4.
The proposal is the same product on this engine — a frame-by-frame renderer that agents can drive
and, more importantly, *look at* — as its own MCPSharp-style repository, **CupriCut**.

`PACKAGING-GL.md` is the standard: a question, a measurement, a split, and a decision. The
measurement came first.

---

## Measured before any of this was written

A proof of concept outside the repo rendered the Showcase's Motion page — looping `@keyframes` plus
transitions, the same page a host paints — through `Animate(t)` → `RenderToPixels`, 940×720, CPU,
headless, on a laptop:

| claim | result |
|---|---|
| a frame at `t` re-rendered after seeking away | **byte-identical** |
| the same `t` from a **separate document instance** | **byte-identical** |
| render only | **135 fps** — 7.4 ms/frame |
| 3 s / 90 frames to MP4, raw RGBA piped into ffmpeg (libx264) | **0.74 s**, 181 KB; `ffprobe`: h264, 30 fps, 3.000 s, 90 frames |
| PNG sequence | 45.6 ms/frame — the **encoder** is the cost, not the render |

Determinism is structural, not engineered. The engine's animation clock is a parameter: `Animate(t)`
drives keyframes, transitions, toasts, reorder easing, fling and overscroll, and the only
`Stopwatch` in the engine is for profiling timings. hyperframes has to build "seekable" adapters and
seek a browser to approximate what this gets because time is an argument. That is the whole pitch,
and it is true today with no engine change.

---

## Re-measured on the engine as it stands (2026-09-12, after v0.23.0)

Four releases have landed since the table above. **The pitch survives, but one row of it does not,
and the correction changes the tool design rather than the case for building this.**

| claim | now |
|---|---|
| render only, sweeping one document | **179 fps** — 5.6 ms/frame, faster than the original measurement |
| the same `t` from a **separate document instance** | **byte-identical** — unchanged |
| the same sweep run twice | **byte-identical** — unchanged |
| a frame at `t` re-rendered **after seeking away** | **DIFFERS** on the Motion page (a pure-`@keyframes` document is fine) |
| a forward sweep vs a fresh document at each `t` | **1 of 45 frames match** |
| a fresh document per frame | 87 ms/frame — **15.7× the cost** of sweeping |

### What that means, precisely

**The engine is reproducible, but a frame is not a pure function of `t`.** It is a function of `t`
*and the frames rendered before it*. Both strategies are perfectly repeatable — sweep the same
composition twice and every frame is byte-identical — they simply do not agree with each other,
because transitions, toasts, reorder easing and overscroll interpolate from what the previous frame
held. `Animate(t)` drives them, but they carry state between calls.

Determinism — the thing a renderer actually needs — is intact. What is not true is the assumption
hiding in the tool table below: that `render_frame(composition, t)` can render frame 45 on its own
and get the frame the video will contain. It will not, unless the composition is pure in `t`.

**So the strategy has to be one decision, applied everywhere:**

- **`render_frame(t)` sweeps from 0 to `t`** and returns the last frame. At 5.6 ms that is 0.5 s for
  `t = 3 s` at 30 fps — acceptable for a preview loop, and *correct*: the agent sees exactly the
  frame the video will contain. Anything cheaper is a preview that lies.
- **`contact_sheet` and `render_frames` sweep once** and keep the frames they were asked for, which
  is what they would do anyway.
- **`lint` reports whether a composition is pure in `t`** — no transitions, no toasts, no
  scroll-driven easing. A pure composition can be rendered at any single `t` in 5.6 ms with no
  sweep, and that is worth telling an author.

A fresh document per frame is the other consistent choice, and it is the wrong one: 15.7× the cost
for frames that are *less* like what a viewer of the finished video sees, because every transition
restarts at every frame.

---

## The split, and why it decides everything

The rule is the one `PACKAGING-GL.md` settled on for the 3D: **if a host would want it, it is the
engine's; if only a frame-by-frame renderer wants it, it is Cut's.** Applied to every element the
product needs:

| element | home | why |
|---|---|---|
| ~~**Installable fonts**~~ — `@font-face`, files and directories, TTF/OTF/WOFF, weight buckets, a strict "registered faces only" policy | **CupriFace — SHIPPED in v0.21.0** | Every host needs fonts an app ships. Done: `@font-face` with `url()` through the same `SourceResolver` images use, `LoadFont`/`LoadFonts`, `CupriApp.Fonts`, weight buckets 100–900, `FontPolicy.RegisteredOnly`, and a font report. **WOFF 2 is the only piece outstanding** — recognised and refused by name, as planned |
| Explicit time — `Animate(t)`, transitions, keyframes | CupriFace (exists) | Unchanged; the thing that makes any of this possible |
| Frame capture — `RenderToPixels`, `Render(canvas)` at scale | CupriFace (exists) | `tools/Screenshots` already renders at 2× this way |
| ~~"Everything is loaded" — a settle signal before the first frame~~ | **CupriFace — done** | `doc.Settle(w, h, timeout)` renders until nothing is outstanding and returns false rather than hand back a frame with holes. `tools/Screenshots` uses it and now skips a capture that did not settle |
| Video seek-to-time | CupriFace, **phase 2** | The seek slider exists, so the player seeks; a renderer needs the *frame at t* rather than playback. Deferred on purpose |
| **Composition timeline** — `data-start` / `data-duration` / tracks, what is on stage at `t` | **CupriCut** | Time as a *product* is a renderer's concern; a desktop window has no scene clock. App-level, over the engine's model/binding — no engine change |
| **Scripted interaction** — click at 1.2 s, type at 2.0 s | CupriCut | Rides `DispatchClick` and the typing APIs; the engine already takes input without a window. Something a Chrome pipeline cannot do |
| The frame loop, PNG sequences, the ffmpeg pipe, alpha output | CupriCut | ffmpeg never enters the engine |
| MCP server, config, tools, packaging, Docker, the agent skill | CupriCut | House style, below |

Two things fell out of the table. Fonts were the one piece of *engine* work, and they were worth
doing first because every host benefits whether or not Cut ever ships — **that stage is done**
(v0.21.0), which is the single biggest change to this document since it was written. And nothing Cut
needs is a `CupriDocument` internal: it consumes the `CupriFace` package exactly as Khalkos3D does,
so the engine stays free of timelines and encoders.

---

## The engine work: fonts — delivered in v0.21.0

**This section is kept as the record of what was planned, and all of it shipped except item 3's
WOFF 2.** Items 1, 2, 4, 5 and 6 are in the engine today; the cross-platform gate at the end of the
section was built too, and corrected the claim it was written to prove (see Risks). What follows is
the original plan, unedited apart from this note.

What existed when this was written: `FontService.RegisterFont(byte[])` and
`CupriDocument.LoadFont(byte[])`. Registered faces win over platform lookup, the first registered
family becomes the generic-sans target, and `WebFonts.props` embeds Noto Sans for the two web hosts.
Faces were keyed by (family, bold ≥ 600, italic) — four styles per family, nearest-style fallback.

What was missing, in the order it should land:

1. **`@font-face`.** The CSS-native way in, and the one an agent writing HTML expects:
   `font-family`, `src: url(…) format(…)` (multiple sources, first readable wins), `font-weight`
   (single or range), `font-style`. `CssParser.ParseInto` already skips every at-rule but `@media`
   at one line; this is that line. Sources resolve through `SourceResolver` — embedded / file /
   `data:` / policied https — **the same pipeline images and video use**, so an app that ships a
   font next to its logo loads both the same way.
2. **Loading convenience.** `LoadFont(CupriSource)`, `LoadFonts(directory)`, and `CupriApp.Fonts`
   as the declarative list an app can enumerate; Cut's `--fonts` flag is one line over it.
3. **Formats.** TTF/OTF/TTC are what `SKTypeface.FromData` reads. **WOFF 1** is zlib per table and
   `ZLibStream` is in the box — half a day. **WOFF 2** is Brotli (`BrotliDecoder`, also in the box)
   *plus* reconstruction of the transformed `glyf`/`loca` tables — a real decoder, one to two days,
   and the second thing to ship, not the first. A font that will not decode must fail with its
   name, not fall silently to Noto.
4. **Matching fidelity.** Four styles per family is enough for a demo and not for a design system.
   Key by weight bucket (100–900) and stretch; nearest-weight per the CSS algorithm. Variable fonts
   need `SKFontArguments` variation coordinates — *verify* SkiaSharp 3.116 exposes them before
   promising the axis.
5. **The strict policy.** `FontPolicy.RegisteredOnly`: a family that would resolve to the platform
   is an error naming the family, not a substitution. This is what Cut sets, and what makes a
   render identical on a Windows laptop and a Linux runner. It is also what the web host should
   run under, where "the platform" is one monospace face.
6. **A report.** `doc.FontReport()` — every family the stylesheet asked for and which face answered
   (registered / platform / default). Cut's `lint` is this plus a verdict.

Proof, in the repo's usual shape: unit tests for parse, resolution, strict failure and WOFF 1;
`tools/Screenshots` moved onto registered fonts, so the committed images stop depending on what the
committing machine had installed; and a **cross-platform determinism gate** — the same text sample
rendered under the strict policy on the win / linux / macOS Viewer jobs, hashes compared. That gate
is the claim Cut will make, tested where Cut cannot test it.

---

## The product: CupriCut

**The house style, taken from `GithubMCPSharp`:** a standalone Streamable-HTTP MCP server on
`ModelContextProtocol.AspNetCore` 1.3.0 / net10.0; `CupriCut.json` with `Cut:` / `Server:` /
`Serilog:` sections, layered under `appsettings` and `.Local` files, env overrides under a
`CUPRICUT_` prefix, command line last; `Hosting/` (password middleware, console icon),
`Configuration/`, `Services/`, `Tools/` as `[McpServerToolType]` classes whose feature toggles are
enforced by the service throwing; console app or Windows Service; `Directory.Build.props` with
`Deterministic` and CI builds; a release matrix of win-x64 / linux-x64 × framework-dependent /
self-contained zips; a multi-arch image on ghcr on tag. **One departure, stated:** the image must
carry ffmpeg. The zips do not; `Cut:FfmpegPath` names it and `probe` says whether it is there.

The safety posture translates directly. A GitHub server is read-only by default with repository
allow-lists; a renderer *writes files*, so the analogue is `Cut:OutputRoot` (the only directory a
tool may write under), `Cut:CompositionRoots` (the only directories it may read compositions and
fonts from), `Cut:MaxFrames` / `MaxPixels` (a runaway request is a disk full), and
`Cut:EnableVideo` (off → no ffmpeg, PNG only).

**Tools** — the agent's loop is *look, adjust, look*, so the first tool is the one that returns a
picture:

| tool | does |
|---|---|
| `render_frame(composition, t, width, height, scale)` | one PNG at `t`, swept from 0 so it matches the video — 5.6 ms per frame of sweep (0.5 s at `t = 3 s`, 30 fps), or one 5.6 ms frame if `lint` says the composition is pure in `t` |
| `contact_sheet(composition, times[] \| every, columns)` | N frames tiled into one PNG, timestamped. An agent reads one image and sees a whole motion |
| `render_frames(…, fps, from, to)` | the image list |
| `render_video(…, fps, from, to, codec, alpha)` | raw RGBA into ffmpeg; `alpha` selects a transparent clear + an alpha-capable codec |
| `inspect(composition)` | the timeline: tracks, elements and their windows, fonts requested, images referenced, duration |
| `lint(composition)` | the determinism verdict: platform-resolved fonts, unloaded sources, wall-clock content; hyperframes has the same verb |
| `list_fonts()` / `probe()` | what is registered; whether ffmpeg answers |

A thin `cupricut` CLI with the same verbs over the same services, because a CI job is not an MCP
client.

**The composition format** is plain HTML + CSS in the engine's documented subset, plus timeline
attributes. Adopt hyperframes' names — `data-start`, `data-duration`, `data-track` — deliberately:
agents already know them, and the skills that teach them transfer. Cut's timeline layer keeps
elements out of the document until their window opens and removes them when it closes, through the
ordinary model/binding, so a `@keyframes` on an element starts when the element appears. Interaction
steps are a second track: `data-cut-click="1.2"`, or a JSON sidecar of `(t, action)` pairs.

---

## Risks worth naming before starting

- ~~**The keyframe clock reference.**~~ **Answered, 2026-09-12 — no engine change needed.** The
  clock is absolute and the engine does *not* stamp creation time: an element bound into the
  document at `t = 2` with a 4 s animation arrives already half-finished (measured: width 200 of
  400) and wraps at `t = 4`. But `animation-delay` is supported and is measured against the same
  absolute clock, so a timeline layer that emits `animation-delay: {start}s` alongside each
  element's window gets its own zero **exactly** — verified at four sample times. Cut owns this;
  the engine does not change.
- **A frame is not a pure function of `t`.** See the re-measurement above: transitions and the other
  stateful drivers carry state between `Animate` calls, so sweeping and single-frame rendering
  disagree. Pick one strategy for every tool — the recommendation is "sweep from 0" — and have
  `lint` say whether a composition is pure in `t` and therefore cheap to sample.
- **First-frame readiness.** Frame 0 rendered before an image or font arrives is wrong and
  deterministic, which is worse than wrong and flaky. The settle signal is a prerequisite, not a
  refinement.
- **The PNG path is the slow path.** 45 ms/frame at 940×720; roughly 100 ms at 1080p. Fine for a
  contact sheet, ten seconds for a 3-second sequence. Encode on a thread pool; the video path never
  pays it.
- **WOFF 2** is a decoder, not a decompression call. Ship TTF/OTF/WOFF 1 first and say so.
- **Cross-machine text — the claim was wrong, and the gate is what found it.** The gate was built in
  the fonts phase as this said it should be, and its first run returned **three distinct pixel
  hashes**: Skia's glyph rasteriser is FreeType on Linux, DirectWrite on Windows and CoreText on
  macOS, so the same face draws different pixels per OS. What HarfBuzz guarantees is the same
  *advances* from the same bytes anywhere. So the gate compares a **layout** hash across the three
  OSes and reports the pixel hashes without comparing them (`tests/CupriFace.Tests/TextDeterminismTests.cs`).

  **Cut must promise the narrower thing: identical pixels on any machine of one OS, identical layout
  everywhere.** For a renderer that is still the useful guarantee — a CI runner and a developer
  laptop of the same OS agree byte for byte — but "render this on Linux, reproduce it on a Mac" is
  not available and must not be advertised. This is exactly why the doc said to build the gate
  before promising anything.
- **Interaction timelines and layout.** A click at `t` that opens a dialog changes every later frame;
  the timeline must replay from zero on every seek, or cache per `t`. Replay is correct and cheap at
  135 fps; cache is an optimisation for later.
- **Scope creep toward hyperframes' ecosystem** — block catalog, Lambda rendering, a hosted
  playground. None of that is the engine advantage. The advantage is a renderer that needs no
  browser and an agent that can look at frame 45 in seven milliseconds.

---

## Sizing

| piece | estimate |
|---|---|
| ~~Fonts in CupriFace~~ — `@font-face`, sources, loading API, weight matching, strict policy, report, WOFF 1, tests | **done, v0.21.0** |
| ~~the cross-platform text gate~~ | **done** — and it corrected the claim it was built to prove |
| WOFF 2 | 1–2 days, separately — still outstanding |
| CupriCut v1 — skeleton in house style, `render_frame`, `contact_sheet`, `render_frames`, `render_video`, `probe`, CLI, Docker with ffmpeg | 4–5 days (the render loop is the proof of concept) |
| timeline layer + `inspect` + `lint` | 3–4 days, after the keyframe-clock experiment |
| interaction track | 2 days |
| phase 2 — video seek in the engine, audio mux in Cut | not sized; deferred |

---

## Suggested staging

1. ~~**Fonts, in this repository**~~ — **done in v0.21.0**, strict policy and cross-platform gate
   included. WOFF 2 is the remainder and is not a blocker for Cut.
2. ~~**The keyframe-clock experiment**~~ — **done, 2026-09-12.** Absolute clock, no creation stamp,
   and `animation-delay` gives a late element its own zero exactly. The timeline layer owns it.
3. ~~**The settle signal**~~ — **done, 2026-09-12** (`doc.Settle(w, h, timeout)`). It had to be a
   loop rather than a flag: a remote image is not fetched until a layout asks for it, so the
   pre-existing `IsLoaded` reads true before the first render because nothing has *started*
   (measured), and an arrival can change layout enough to pull in a further image. `tools/Screenshots`
   uses it and skips a capture that did not settle. **Every engine prerequisite in this document is
   now closed**; what remains is Cut's own repository.
4. **CupriCut, the repository**, house-style skeleton plus the frame tools and the video pipe —
   the proof of concept with a server around it. `render_frame` and `contact_sheet` first, because
   they are what an agent uses to iterate, **both sweeping from 0** so a preview matches the video.
4. **The timeline and interaction tracks**, `inspect`, `lint`, the agent skill.
5. **Phase 2**: video seek, WOFF 2, audio.

---

## The decisions this needs

- ~~The name.~~ **Decided: CupriCut.** "Cut" is the film word — a cut, the final cut — so it says
  video without saying animation tool, carries none of Flash's baggage, and describes the
  product: a composition on a timeline is what an editor cuts. The one connotation to correct is
  editing existing footage, which becomes true the day phase 2 lands video seeking.
- **Server-only, or server plus CLI.** The house style is a server; a renderer is also a build
  step. This document assumes both over one set of services.
- **Composition attribute names.** hyperframes' `data-start` / `data-duration` for agent
  familiarity, or the engine's own. This document recommends theirs.
- ~~**Whether fonts wait for the rest.**~~ **Moot: fonts shipped in v0.21.0**, ahead of everything
  else, exactly as this recommended.
- **What `render_frame(t)` means** — the decision the re-measurement forces. A frame is a function of
  `t` *and* the frames before it, so "the frame at `t`" is ambiguous until Cut picks one reading.
  This document recommends **sweep from 0**: correct by construction, 5.6 ms per swept frame, and it
  makes the preview loop show the agent what the video will contain. The alternative — define
  compositions to be pure in `t` and sample directly — is faster and narrower, and `lint` should
  report which kind a composition is either way.
- **What determinism Cut advertises.** The gate says: identical pixels on any machine of one OS,
  identical layout on all three. Not "render anywhere, reproduce anywhere".
