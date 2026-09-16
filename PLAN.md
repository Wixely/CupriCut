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
7. **Docker and the release.** The one departure from house style: the image carries ffmpeg. The
   zips do not, and `Cut:FfmpegPath` names it.

   `release.yml` cuts four zips (win-x64 / linux-x64 x framework-dependent / self-contained) and a
   multi-arch ghcr image on a `v*` tag. Each RID builds on its OWN runner rather than
   cross-publishing: SkiaSharp and HarfBuzz ship native binaries per RID, and a cross-publish
   resolves them from NuGet without ever loading one — so it succeeds, and the first person to find
   out otherwise is whoever downloaded the zip.

   **Writing this found a bug that no test could have.** The CLI references the server project, and
   two executables where one references the other must AGREE about self-containment once a
   `RuntimeIdentifier` is present, or the SDK refuses with NETSDK1151. `dotnet build` never hits it.
   So the whole suite passed, on every commit, while **both the release workflow and the Dockerfile
   were incapable of producing a binary at all** — and neither had ever been run.

   Neither `--self-contained` nor a global `-p:SelfContained=` crosses a `ProjectReference`; both
   were measured. A custom property does, so `CupriCut.csproj` reads `CupriCutSelfContained` and the
   matrix sets one thing that both halves agree on.

   CI now publishes and runs the PUBLISHED binary on both OSes, so the next thing of that shape
   fails on the commit that causes it rather than on a release tag.

   **Still not executed anywhere:** there is no git remote, so no workflow has ever run, and this
   machine has no Docker, so the image has never been built. What IS verified is every step that
   can be run locally — both publish shapes, the published binary rendering a real contact sheet,
   and the Dockerfile's file list against the repository.

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
    built-in guess used.

    **Applying it is the product, not a footnote.** `--apply`, the button in the window and
    `apply:true` on the tool all write one key to `CupriCut.Local.json`, the per-machine layer that
    every host loads with `reloadOnChange` — so it takes effect in the running process and on every
    run after, with no config editing. The window's button used to set it for the session and print
    "run cupricut calibrate --apply to make it permanent", which is not a button that applies a
    measurement; it is a button that tells you how to.
18. ~~**Special elements.**~~ **Done.** `--cupricut-background` marks an element as the backdrop,
    so the same composition gives an opaque clip for review and a transparent one for the edit.
    Every render answer reports what the marked backdrop did, and the window gets a checkbox.

    The mechanism was measured rather than assumed, and the obvious one does not work: the engine
    parses the attribute perfectly, but a CSS attribute selector on it —
    `[--cupricut-background] { display:none }` — matches nothing and fails SILENTLY. An inline
    `style="display:none"` works, and beats an author's own `display` rule on the same element,
    which is what makes it safe to apply to markup CupriCut did not write. Only the opening tag is
    touched, so an element that comes back when the flag flips is the one that was never hidden.
19. ~~**Mask output.**~~ **Done.** `alphaextract` to grey, as a fourth `AlphaMode`. White where the
    composition is opaque, black where it is not, no colour at all — the travelling matte an editor
    asks for when its format will not take a transparent clip. A mask of an OPAQUE render is a flat
    white rectangle, so that is refused rather than written.
20. ~~**Export formats.**~~ **Done.** One verb, a list of outcomes — mp4, h265, webm, mov, gif,
    frames, mask, matte — each carrying the codec, pixel format and alpha handling that outcome
    needs. "h264 in an mp4 container at yuv420p" is a true description of what someone wants and a
    useless way to ask for it.

    **The targets share the render**, which is the point: a frame written to four pipes costs no
    more to produce than a frame written to one, and rendering is what a clip costs. mp4 + mask +
    gif + frames from one 90-frame sweep took 4.5 s in total. Sharing the render means sharing its
    alpha, and that is exactly right for the pair an editor keys: with alpha on, a format that
    cannot carry an alpha channel keeps the straight colour and goes black where nothing was drawn.

    Asking for a mask IS asking for a transparent render, so alpha is inferred rather than demanded
    twice — but an explicit `alpha:false` beside one is a contradiction and is still refused.
21. ~~**Resolution and scaling in the project.**~~ **Done.** An output size and a scaling mode
    stored as project data, so "render this at 4K the way it looks at 1080p" is a setting rather
    than a re-authoring. The difference is not cosmetic: laying a 1280x720 design out in a
    3840-wide viewport REFLOWS it, so a 584px lower third stays 584 physical pixels and becomes a
    badge in the corner of a huge frame. Measured in pixels — a card that covers a quarter of the
    frame scaled covers a sixteenth reflowed.

    Five modes. Four are the engine's own `PresentInfo` strategies, used rather than re-derived:
    `responsive`, `fixed`, `hybrid`, `adaptive`. The fifth, **`fit`**, is CupriCut's and is the
    default: the design scaled uniformly until it fits, centred, letterboxed if the aspects differ.
    The engine has no equivalent on purpose — a WINDOW host never letterboxes, it reflows the loose
    axis, because bars in an application are a bug. A frame of video is not a window: its size is
    fixed by the format, the composition's aspect is a decision someone made, and bars are the
    correct way to hold both. When the aspects match, which is the ordinary case, `fit`, `hybrid`
    and `adaptive` agree exactly.

    The geometry moved into one type while this was done. The sweep, the parallel renderer and the
    encoder each worked the frame size out for themselves from the same three fallbacks — and the
    encoder has to agree with the renderer EXACTLY, because it tells ffmpeg the frame size up front
    and a disagreement is not an error but a pipe full of misaligned bytes. That is the same
    triplication that had already produced the silent alpha bug in `export`; it is one function now.

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
24. ~~**Project folders**, with drag and drop.~~ **Done, with one half refused.**

    A folder is a directory under the project root — not an index, not a field in a file — so
    nothing can get out of step with where the projects actually are, and organising them with a
    file manager works exactly as well. `ListFolders`, `CreateFolder` and `MoveProject` on the
    service; `move_project` and `create_folder` as tools; a **Projects** page in the window where
    folders are columns and projects are cards.

    The dragging is `cupri-board`'s, not ours — that component exists for precisely this. `OnReorder`
    hands back the source and target lists as *elements*, so each column carries its folder path as
    an attribute and a drop resolves straight back to a destination. Reordering WITHIN a folder is
    declined rather than accepted: projects are listed by name and there is nowhere to record a
    hand-made order, so taking the drag would show a reordering that vanished on the next refresh.

    **Drag and drop in and out of the OS is not possible and was not faked.** The desktop host has
    no file-drop support at all — checked, not assumed — so dragging an `.html` in from Explorer, or
    a project out to the desktop, cannot work without a host change. The honest substitutes are
    there instead: **Open in file manager** on the projects page, and **Open folder** after an
    export. Raised as [CupriFace#182](https://github.com/Wixely/CupriFace/issues/182), because it is a
    gap for any desktop application built on it rather than a CupriCut problem.

## Milestone 2 — the timeline (3–4 days)

12. ~~**Timeline layer.**~~ **Done**, and not the way this item described. `data-start` /
   `data-duration` / `data-track` decide what is on screen when.

   **Nothing is added to or removed from the document per frame.** The original plan said elements
   would be kept out until their window opened; doing that means reloading the document, which
   costs 25–370 ms against 3–7 ms for a frame, and would have thrown away the single biggest
   property this renderer has. Instead each timed element gets a generated `@keyframes` holding it
   at `opacity: 0` outside its window and `1` inside, with ADJACENT stops so the change is a cut
   rather than a fade. `Animate(t)` does the rest, and **a composition with a timeline is still
   pure in `t`** — so it still renders directly rather than being swept.

   Four things were measured first, and three of the obvious mechanisms do not exist:

   | | |
   |---|---|
   | `visibility` in a keyframe | **not animated** |
   | `display` in a keyframe | **not animated** |
   | two animations on one element | **does nothing**, comma-separated, shorthand or longhand |
   | `opacity` with adjacent stops | works, and steps exactly |
   | `animation-delay` against the absolute clock | works — the one claim in the original item that held |
   | `var(--x)` as an `animation-delay` | works |
   | `calc()` in an `animation-delay` | **not supported**, with or without a variable |

   Because an element cannot carry two animations, **a timed element's own `animation` is reserved
   by CupriCut**, and a violation is reported rather than letting the author's animation vanish. A
   wrapper element would have avoided the rule and was the first design; it also silently changes
   the layout, because the wrapper becomes the flex child rather than the element.

   For a late scene's own zero, `--cut-start` is published on each timed element and inherits into
   its subtree, so a child writes `animation-delay: var(--cut-start)`. `calc()` not working means
   stagger inside a scene goes in the keyframe percentages instead — `compositions/scenes.html`
   does both and says so.

   Verified in pixels at six sample times, and by eye across a nine-second three-scene sheet where
   each late scene is caught mid-entrance.
13. ~~**`inspect`**~~ **Done.** The timeline as data — tracks, windows and their generated classes,
   duration — plus the backdrop, the stylesheets and references, and every font family the
   composition asked for with what answered it.
14. ~~**`lint`**~~ **Done.** Three sources in one verdict: the engine's own reader (`CF*`), what only
    CupriCut knows (`CUT*` — a machine-resolved font, a resource that never arrived, a timeline it
    could not make sense of), and whether the composition is pure in `t`. Impurity is reported as
    **info, not a fault**: it is correct and slower, and a verdict of "errors" for a composition
    that renders exactly what its author meant would be a lie. `clean` / `warnings` / `errors`, and
    the CLI exits non-zero only on errors.

    Both come from one `Inspector.Examine`, because they are the same work asked two ways and
    neither should open the document twice or disagree with the other about what it found.

    **Two things made it confidently wrong before they were found**, and both are the kind of
    mistake this tool exists to catch:

    - `CupriDoctor.Check(html, null)` turns the CSS pass off ENTIRELY, inline `<style>` included —
      which is where nearly every composition keeps its rules. Passing `""` checks the same
      document and finds them. `SampleTests` had been running the doctor over every shipped
      composition since it was written and had only ever read the markup. Raised as
      [CupriFace#183](https://github.com/Wixely/CupriFace/issues/183).
    - The doctor's viewport defaults to 1024x768, so its overflow checks reported a 1280-wide
      composition as broken for being 1280 wide. It is asked at the composition's own frame now.

    And one defect of CupriCut's own, found by pointing the new tool at the repository's own
    samples: the reserved-animation check matched any selector CONTAINING the timed element's
    class, so `.scene .title { animation: ... }` — the shape this feature recommends — warned on
    every correctly written composition. Only the selector's subject is examined now. A warning
    that is wrong is worse than no warning.

    All seven shipped compositions lint clean, and a test keeps them that way.

## Milestone 3 — interaction (2 days)

15. **Interaction track.** `data-cut-click="1.2"`, or a JSON sidecar of `(t, action)` pairs, over
    `DispatchClick` and the typing APIs. The engine takes input with no window, so this is something
    a Chrome pipeline cannot do at all. Replay from zero on every seek — correct, and cheap at
    179 fps. Caching per `t` is an optimisation for later, not now.

## Milestone 4 — cue the animation off the audio (4–5 days)

**The idea.** Point CupriCut at an audio file and let the motion be driven by what is in it — hits
on the beat, a title that lands on the downbeat, a lower third that appears when the speaker starts.
Timing an animation to a track by hand is tedious and inexact; the track already knows where its
events are.

25. **`analyse_audio` — the cues as data.** ffmpeg is already a dependency and already does the
    hard part: decode to mono PCM at a known rate and the envelope falls out. Compute per-frame RMS
    and spectral flux in-process (no new dependency, and no black box whose version changes the
    answer), and derive:
    - **onsets** — a flux peak above an adaptive threshold. Where a hit is.
    - **beats and a tempo** — autocorrelation of the onset envelope. Report the confidence, because
      a spoken-word track has no beat and pretending otherwise is worse than saying so.
    - **silence boundaries** — where speech or music starts and stops, which is what a lower third
      actually wants to key off.
    - **the loudness envelope itself**, decimated to the project's frame rate, for anything that
      should breathe with the track rather than snap to it.

    Every cue comes back with a time, a kind and a strength in 0–1, snapped to the nearest frame
    with the delta reported — the same rule `ClipPlanner` already applies to clip
    lengths, for the same reason.

26. **Cues live in the project.** Stored in `.cut.json` alongside a hash of the audio, not
    recomputed at render time. Two reasons. A render must be reproducible on a machine that has a
    different ffmpeg, and analysis is exactly the kind of thing that drifts between versions. And
    an agent that has read the cues once should not pay to read them again on every iteration —
    the loop is look, adjust, look, and the cues do not change between adjustments.

    The hash is what catches the audio being swapped underneath a project that was timed to it.

27. **Two ways to use them, and the cheap one first.**

    **The agent writes the keyframes.** `analyse_audio` hands back a list of times; the agent emits
    `@keyframes` and `animation-delay` at those times. This needs no engine change, no new binding
    vocabulary, and nothing in the renderer — and it plays to what is actually good at authoring
    motion. Build this one first and find out whether the second is needed at all.

    **A cue track binds directly.** `data-cut-cue="beat"` / `data-cut-cue-index="4"` resolved to a
    time at build, feeding the same `animation-delay` machinery Milestone 2 uses for
    `data-start`. Worth it only if the round trip through the agent turns out to be the slow part.

28. **Cues from the FRAMES too.** The same shape of answer, read off a video the composition is
    meant to sit over: scene changes (`ffmpeg`'s `scdet`), and a per-frame motion and luminance
    measure. What this is for is the opposite problem — not timing motion to music, but timing a
    caption so it does not land on a cut, or picking the calm part of a shot to put text on. Same
    cue record, different source.

29. **Audio on the way out.** `export` muxes the track into the clip — `-i audio -c:a aac -shortest`,
    and a `--no-audio` for the matte half of a keying pair, which must not carry it. This is the
    part of Phase 2's "audio mux here" that belongs with the rest of the audio work rather than
    on its own.

**What has to be decided before the code.**

| | |
|---|---|
| **Where the audio lives** | A project inlines its assets as `data:` URIs, and that is right for a logo and wrong for four minutes of WAV. Likely: reference by path with a hash, and inline only under a size cap — but that breaks "one file you can hand to another machine", which was a founding property. Decide it, do not drift into it. |
| **Whether the analysis is a tool or a service** | `analyse_audio` as an MCP tool is obvious. It is less obvious whether the CLI needs it, or whether the window should show the envelope under the scrub bar — which is the thing that would make timing by hand pleasant, and is a chunk of work on its own. |
| **Beat detection honesty** | Autocorrelation over an onset envelope is a real method and a mediocre one. It will be confidently wrong on rubato, on half/double tempo, and on anything without a drum. The confidence number is not decoration — an agent has to be able to tell "beats at 128 BPM" from "no usable beat here". |

## Milestone 5 — frame packs, and CupriLex (5–8 days, and a decision)

**The idea.** HyperFrames (HeyGen, Apache 2.0) is the same premise as this one — *"Write HTML.
Render video. Built for agents."* — done through headless Chrome. It has something CupriCut does
not: a catalogue of designed **frame packs**, each a palette, a typography system and a `frame.md`
prompt that tells an agent how to compose in that look. Their own description of `frame.md` is the
tell: *"the missing translation layer — it takes your web-context design spec and inverts it for the
frame."*

Taking a pack, appending what CupriFace does differently as an explicit override, and rendering it
here would give CupriCut a designed starting point without designing one.

**What actually transfers, measured rather than assumed.**

| their half | here | |
|---|---|---|
| `:root` design tokens as CSS custom properties | **unchanged** | `var()` and `var(--x, fallback)` both work in the engine, for colours and for lengths. Verified before this was written down. This is the whole palette and typography layer and it costs nothing. |
| `data-start` / `data-duration` / `data-track-index` on scenes | **nearly unchanged** | Milestone 2's timeline layer already plans `data-start` / `data-duration` / `data-track`. Arriving at the same vocabulary independently is a good sign about the vocabulary. |
| Typography discipline — minimum sizes, weight contrast, banned families, tabular numerals | **as guidance** | Prose in a prompt, not code. Free. |
| GSAP timelines (`tl.from()`, `tl.set()`, `autoAlpha`) | **does not transfer at all** | There is no JavaScript engine, by design. This is the hard half and the whole reason a translator is a project rather than a function. |
| HyperShader transitions | **no equivalent** | Drop them, and say so rather than rendering something that silently lacks them. |
| `autoAlpha` visibility juggling | **strip it** | It exists to work around their shader blanketing every scene to `opacity:0`. Carrying the workaround for a problem we do not have is worse than not carrying the feature. |

30. **The prompt overlay — cheap, do it first.** Take the pack's spec and append a CupriFace section
    that is explicitly labelled as OVERRIDING what came before it: no JavaScript and no GSAP, motion
    is `@keyframes` plus `animation-delay`, binding is `{{Path}}` and `data-repeat` and nothing else,
    no `pointer-events`, no `border-left/right/bottom`, no `letter-spacing`, no repeating gradients,
    fonts must be registered rather than named. Most of that list already exists as this repository's
    hard-won notes; it wants collecting into one document, not discovering again.

    This is a markdown file and an hour, and it is most of the value. Do it before anything with a
    parser in it.

31. **CupriLex — the translator.** HTML and CSS in, CupriFace-safe HTML and CSS out, with a REPORT
    of what it had to change and what it could not carry. The report matters more than the
    conversion: silently dropping a shader transition is how someone ships a video missing its
    transitions.

    The tractable rewrites are mechanical — `border-left: 2px solid x` to a child div, a repeating
    gradient to explicit stops, `letter-spacing` dropped with a note, `<img>` to `<cupri-image>`.
    The hard one is a **GSAP timeline to `@keyframes`**, which is a small compiler: read the tween
    calls, resolve their targets, turn each into a named keyframes block and an `animation-delay`
    against the absolute clock. Tractable for the `tl.from`/`tl.to`/`tl.set` subset a frame pack
    actually uses; not tractable in general, and it should refuse rather than guess.

**Should it be its own repo?** Not yet, and probably eventually. Start it as `Services/Lex/` here,
because the only way to find out which rewrites matter is to run real packs through it and look at
the output — and that loop is much tighter inside the thing that renders. Extract it when two
things are true: there is a corpus of real inputs worth regression-testing against, and something
other than CupriCut wants it (CupriFace itself is the obvious candidate, since "make this browser
HTML work in the engine" is the engine's problem too, not this tool's). A repo boundary drawn
before either is true buys a release process and costs every experiment.

**Licensing.** Apache 2.0, so the spec text and catalogue blocks can be used with attribution. Say
in the output which pack a composition came from — not because the licence demands a notice in the
rendered video, but because a project that cannot say where its design came from is a project
nobody can re-license later.

**The honest risk.** This makes CupriCut's compositions look like someone else's design system, and
a translator is a maintenance surface that tracks a project we do not control. The mitigation is
the direction of travel: import a pack ONCE into a `.cut.json`, which is already self-contained, and
never depend on the translator at render time. A project that has been imported is just a project.

## Phase 2 — deferred, deliberately

16. Video seek-to-time in the engine (a renderer needs *the frame at t*, not playback), and WOFF 2
    in the engine (a real decoder — Brotli plus `glyf`/`loca` reconstruction — not a decompression
    call). Audio mux was here; it has moved to Milestone 4, where the rest of the audio work is.

---

## Engine behaviour to pin down

Found while writing the sample compositions, and written down rather than worked around silently.
None is confirmed as a bug: each is a case where the engine differs from a browser and where the
first guess about the cause was wrong, so each wants an isolated reproduction before it is filed.
The rule this repository already follows applies — measure it, then claim it.

**`line-height` in `px` produces a line box 3x too tall.** Filed as
[CupriFace#181](https://github.com/Wixely/CupriFace/issues/181). Measured on 0.25.0 by reading the
text node's own box off the render tree, at `font-size: 48px`:

| declaration | expected | actual | |
|---|---|---|---|
| *(none)* | 57.6 (48 x 1.2) | 57.6 | correct |
| `line-height: 120px` | 120 | **360** | x3 |
| `line-height: 60px` | 60 | **180** | x3 |
| `line-height: 48px` | 48 | **144** | x3 |
| `line-height: 24px` | 24 | **72** | x3 |
| `line-height: 1.5` | 72 | 72 | correct |
| `line-height: 2em` | 96 | **57.6** | ignored, no diagnostic |
| `line-height: 150%` | 72 | **57.6** | ignored, no diagnostic |

Exactly 3x at four different values, so a fixed factor rather than rounding or a font-metric
interaction. The glyph sits at the BOTTOM of that oversized box, which is why a box whose height
equals its line-height paints its text entirely outside itself.

**The first write-up of this, before the numbers, said it was "a downward offset added to the text
position".** That was wrong, and wrong in a way that would have made a useless bug report: the text
is not offset, its line box is too tall. Worth remembering that the plausible mechanism arrived at
by looking at renders was not the mechanism.

It explains every layout problem the samples hit, and the conclusions drawn from them were wrong:

- Text pushed down by a line-height pushed everything after it off the frame — the title card lost
  its last three elements this way.
- The odometer's digits were each a full cell BELOW the window meant to show them, so the window
  looked as though it was clipping nothing, or clipping everything. **`overflow: hidden` was never
  broken**; the text simply was not where it was supposed to be. An odometer should work once this
  does.
- A box with an explicit height and a matching line-height rendered as an empty rectangle.

Filed as [CupriFace#181](https://github.com/Wixely/CupriFace/issues/181): reproducible in six lines,
silently ruins vertical rhythm, and the workaround (never set `line-height`) is not one anybody
would guess.

**OS file drop** is filed separately as
[CupriFace#182](https://github.com/Wixely/CupriFace/issues/182) — `DesktopHost` surfaces no drop
event, so no app on this engine can accept a file dragged onto its window. The platform primitives
(`SDL_DROPFILE`, `glfwSetDropCallback`) are already there in both hosts.

Still open, and genuinely not understood:

| what happened | what is not yet known |
|---|---|
| `align-items: center` centred correctly in isolation, but spread a flex item's children across the full height when a full-height `position:absolute` sibling shared the flex line | Whether an absolutely positioned child is still participating as a flex item. Possibly also a line-height artefact — it has not been re-tested since that was isolated. |
| `position: absolute` children inside a nested positioned box landed outside their container | Isolated probes behaved correctly, so the cause is something else in the real composition. Same caveat. |
| `align-self: center` and `margin: auto` do not centre a flex item | Probably simply unsupported; `align-items` on the parent works and is the answer. |
| `letter-spacing` is silently ignored | CF0050. Still the case on 0.25.0. |

~~`border-left` / `border-right` / `border-bottom` are ignored~~ — **fixed in CupriFace 0.25.0.** The
studio's three separators, which had never been drawn, now are.

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
