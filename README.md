# CupriCut

**Write HTML, render video. No browser.**

CupriCut renders an HTML + CSS composition to frames — and to an MP4 — by driving the
[CupriFace](https://github.com/Wixely/CupriFace) engine's clock directly. It is built for agents:
the first tool returns a *picture*, because the loop an agent actually runs is look, adjust, look.

It exists because of one property of the engine underneath it. **Time is an argument.**
`doc.Animate(t)` drives keyframes, transitions and every other animated thing, and the only
`Stopwatch` in the engine is for profiling. A renderer that wants frame 45 asks for frame 45.

The comparable tool, [hyperframes](https://github.com/heygen-com/hyperframes), seeks a headless
Chrome frame by frame into FFmpeg and has to build "seekable" adapters to approximate what this gets
for nothing. CupriCut needs no browser at all.

> **Status: Milestone 1 + 1b are in.** The renderer, the MCP server, the CLI, the project file and
> the Docker image all work: `render_frame`, `contact_sheet`, `render_frames`, `render_video`,
> `probe`, `list_fonts`, and the five project tools. Still to come are the timeline layer
> (`data-start` / `data-duration`), `inspect`, `lint` and the interaction track — see
> [PLAN.md](PLAN.md) for the build order and [docs/SCOPE.md](docs/SCOPE.md) for the reasoning and
> the measurements behind it.

---

## Measured before any of it was written

On the engine as it stands, rendering the CupriFace Showcase's Motion page — looping `@keyframes`
plus transitions — at 940×720, CPU, headless, on a laptop:

| | |
|---|---|
| render only, sweeping one document | **179 fps** — 5.6 ms/frame |
| the same sweep run twice | **byte-identical** |
| the same `t` from a separate document instance | **byte-identical** |
| 3 s / 90 frames to MP4, raw RGBA piped to ffmpeg | **0.74 s**, 181 KB, h264 30 fps |
| a PNG sequence | 45.6 ms/frame — the **encoder** is the cost, not the render |

And one measurement that shapes the whole tool surface:

| | |
|---|---|
| a frame at `t` re-rendered **after seeking away** | **differs**, on a composition that uses transitions |
| a forward sweep vs a fresh document at each `t` | **1 of 45 frames matched** |
| a fresh document per frame | 87 ms/frame — **15.7×** the cost of sweeping |

**The engine is reproducible, but a frame is not a pure function of `t`.** It is a function of `t`
*and the frames rendered before it*: transitions, toasts, reorder easing and overscroll interpolate
from what the last frame held. Both strategies repeat perfectly; they simply disagree with each
other.

So every tool here takes one reading of "the frame at `t`": **sweep from 0 and return the last
frame.** At 5.6 ms that is half a second for `t = 3 s` at 30 fps, and it is *correct* — the agent
sees the frame the video will contain. Anything cheaper is a preview that lies. `lint` reports
whether a composition is pure in `t`, because a pure one can be sampled directly at 5.6 ms.

## What it will promise about determinism

**Identical pixels on any machine of one OS. Identical layout on all three.**

Not "render on Linux, reproduce on a Mac". That narrower claim is not a guess — CupriFace built a
cross-platform text gate to prove the wider one and the gate returned three distinct pixel hashes,
because Skia rasterises glyphs through FreeType on Linux, DirectWrite on Windows and CoreText on
macOS. What is guaranteed everywhere is the same advances from the same font bytes, so the gate
compares a *layout* hash across the three and reports pixel hashes without comparing them.

For a renderer that is still the useful guarantee: a CI runner and a developer laptop of the same OS
agree byte for byte. Fonts must be registered rather than resolved from the machine, which is what
`FontPolicy.RegisteredOnly` is for, and CupriCut will set it.

## Shape

A standalone Streamable-HTTP MCP server in the same house style as `GithubMCPSharp`, plus a thin
`cupricut` CLI over the same services, because a CI job is not an MCP client.

| tool | does | state |
|---|---|---|
| `render_frame` | one PNG at `t`, swept from 0 so it matches the video | **in** |
| `contact_sheet` | N frames tiled into one timestamped PNG — an agent reads one image and sees a whole motion | **in** |
| `render_frames` | the image sequence, encoded on the thread pool | **in** |
| `render_video` | raw RGBA into ffmpeg; `alpha` selects a transparent clear and an alpha-capable codec | **in** |
| `probe` | whether ffmpeg answers, which codecs, what the limits are | **in** |
| `list_fonts` | what is registered, and what each family a composition asked for resolved to | **in** |
| `save_project` / `load_project` / `update_project` / `list_projects` / `attach_asset` | the work, in a file a later run can reopen | **in** |
| `inspect` | the timeline: tracks, elements and their windows, images referenced, duration | Milestone 2 |
| `lint` | the determinism verdict, and whether the composition is pure in `t` | Milestone 2 |

Compositions are plain HTML + CSS in the engine's documented subset. Timeline attributes —
`data-start`, `data-duration`, `data-track`, borrowed from hyperframes on purpose so the agents and
skills that already know them transfer — arrive with Milestone 2.

## Projects: the work survives the session

A `.cut.json` project is one self-contained file holding the HTML, the CSS, the render settings
that regenerate the animation, any assets inlined as `data:` URIs, and free-text notes. It exists
so a **second** run — a different session, no shared context, possibly no filesystem tool at all —
can `load_project`, read what the first one was thinking, change one rule with `update_project`,
and render it again at the same size and rate without being told any of it.

A project *is* a composition: pass its name wherever an `.html` file is asked for, and everything
you leave out comes off its `render` block.

```
save_project(name: "hero", html: "...", css: "...", width: 800, height: 450, fps: 25, duration: 2)
   → projects/hero.cut.json

# …a week later, a different run:
load_project(name: "hero")            → the HTML, the CSS, the settings, the notes
update_project(name: "hero", css: "…") → only the stylesheet changes
render_video(composition: "hero.cut.json")  → 800×450 at 25 fps for 2 s, because the project said so
```

## Running it

```bash
# The CupriFace package comes from GitHub Packages, which will not serve even a public package
# anonymously, so this is needed once per machine.
dotnet nuget update source GitHub-Wixely-Packages   --username <your-github-username>   --password <a PAT with read:packages>   --store-password-in-clear-text

dotnet build
dotnet test
dotnet run --project CupriCut.csproj      # studio window + MCP server
dotnet run --project CupriCut.csproj -- -c   # headless: MCP server only
```

**Two faces, one binary.** `CupriCut` opens the studio window by default and `-c` runs headless —
and **both host the MCP server**. The window is a second face on the same service, never a separate
app. A Windows Service or a container implies `-c`, and a window that cannot open falls back to
serving headlessly rather than taking the server down with it.

The CLI has the same verbs over the same services:

```bash
cupricut probe
cupricut frame  --composition lower-third.html --t 1.2
cupricut sheet  --composition lower-third.html --duration 3 --count 9
cupricut video  --composition lower-third.html --duration 3 --codec h264
cupricut video  --composition timebase.html --duration 10 --fps 120   # 1200 frames, 10.000s
cupricut projects
```

Docker is the one departure from house style — **the image carries ffmpeg**, because a container
whose job is turning HTML into MP4 and which then cannot encode one is broken. The release zips do
not; `Cut:FfmpegPath` names it and `probe` says whether it answered.

```bash
# The token goes in as a BuildKit secret, so it never reaches an image layer or `docker history`.
export GITHUB_TOKEN=<a PAT with read:packages>
docker build --secret id=NUGET_AUTH_TOKEN,env=GITHUB_TOKEN -t cupricut .

docker run --rm -p 5722:5722   -v "$PWD/compositions:/app/compositions:ro"   -v "$PWD/projects:/app/projects"   -v "$PWD/output:/app/output"   cupricut
```

## The studio window: point instead of describing

The loop is look, adjust, look — and until the window, only the agent got to look. A reviewer had to
open a PNG somewhere else and then put the problem into prose.

![The studio window](docs/studio.png)

Pick a project, scrub to a frame, drag a box round what is wrong and type a sentence. The agent then
reads it back:

```
list_annotations(project: "review")

  [ad54d3d2] t=1.2s  512x180 at (64,216) of 1280x720  "logo enters too late and sits too far left"
  see it with: render_frame(composition: "review.cut.json", t: 1.2)

resolve_annotation(project: "review", id: "ad54d3d2",
                   resolution: "moved the logo keyframe from 1.2s to 0.6s")
```

Annotations are stored **in the project**, because that is already the file a later run opens, and
in **normalised 0–1 coordinates**, so a note drawn on a 1280×720 preview still means the same region
when the project renders at 3840×2160.

The GUI is itself a `CupriApp` — CupriCut's interface is drawn by the engine CupriCut renders with.
That is why it was cheap: the preview is an `ISurfaceSource`, the seam the engine already has for
live pixel producers, so it costs no PNG encode; and the region drag is `OnPointer`, the same one a
pinch gesture uses. Nothing here needed an engine change.

## Safety, for a tool that writes files

A GitHub server is read-only by default. A renderer's risk is the mirror image, so the boundaries
are about writing:

| setting | default | is |
|---|---|---|
| `Cut:CompositionRoots` | `compositions` | the only directories compositions, stylesheets, images and fonts are **read** from |
| `Cut:OutputRoot` | `output` | the only directory PNGs and videos are **written** to |
| `Cut:ProjectRoot` | `projects` | the one **read-write** directory, and only `.cut.json` may land in it |
| `Cut:MaxFrames` | `0` | **no limit.** Set it only to bound what one caller can spend on a shared instance |
| `Cut:MaxPixels` | `8294400` | ceiling on pixels per frame, after `scale` (3840×2160). A single huge *frame* is a different question from a long *clip* |
| `Cut:EnableVideo` | `true` | off means PNG only, ffmpeg never launched |

### `duration` and `to` differ by one frame, on purpose

`duration` is a **length**: `duration x fps` frames covering `[from, from+duration)`. `--duration 10
--fps 120` is 1200 frames and a file ffprobe reports as exactly `10.000000`. `to` is an **inclusive
endpoint** and keeps the frame at `t=to`, which is one frame more — right for sampling, wrong for a
clip length, and the reason a renderer that conflates them quietly overruns. A project's `duration`
is a length. Naming both is refused rather than resolved.

### A length that isn't a whole number of frames always says so

A clip is frames, so its length is quantised to `1/fps`. Ten seconds at 120 fps lands exactly;
ten seconds at 29.97 fps is 299.7 frames, and there is no such thing. CupriCut takes the **nearest
whole frame** and **always reports the difference** — absorbing it silently is how someone finds
out much later that their ten-second cut is 10.010s.

```
$ cupricut video --composition timebase.html --duration 10 --fps 29.97
output/timebase.mp4  (252,554 bytes, 300 frames, 10.01001s, h264)
duration 10.01001s, asked for 10s (+10.01ms)
  10s at 29.97 fps is 299.7 frames, not a whole number. Snapped to the nearest whole
  frame (300), so the clip is 10.01s (+10.01ms). For exactly 10s, use 30 fps (300 frames).
```

The MCP tools carry the same thing as a `timing` object (`requestedSeconds`, `actualSeconds`,
`frames`, `exact`, `deltaMs`, `note`). When the request lands exactly, `exact` is true and there is
no note.

## Licence

MIT, matching CupriFace. The two Noto Sans faces in `fonts/` are SIL OFL 1.1 — see `fonts/OFL.txt`.
They ship because `FontPolicy.RegisteredOnly` is always on, so a bare install needs at least one
registered family for anything to render at all.
