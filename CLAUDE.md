# Working on CupriCut

CupriCut turns HTML and CSS into frames and video, headlessly, for agents. The engine underneath is
[CupriFace](https://github.com/Wixely/CupriFace) — **no browser, no DOM at runtime, no JavaScript.**

Two audiences read this file, and the first half is for both:

- **Using CupriCut** to make an animation, over MCP or the `cupricut` CLI. Start at *The engine
  fails silently* and *The loop*.
- **Changing this repository.** Read all of it. [PLAN.md](PLAN.md) is what to do and what is already
  decided; [docs/SCOPE.md](docs/SCOPE.md) is the reasoning behind it.

---

## The one rule

**Measure it, then claim it.**

Never write "the engine does X" from reasoning, from what a browser does, or from a previous
version. Render something, read the pixels, and let the number decide. Where a claim survives that,
put the measurement in a test so the claim cannot rot.

This is not a stylistic preference. Nearly every confident, plausible, *wrong* claim in this
repository's history was written by someone who had looked at a render:

| claimed | measured |
|---|---|
| "a downward offset is added to the text position" | the line box was 3x too tall; the text was not offset at all |
| "a comma-separated animation list runs the first one" | it runs **neither** — the shorthand fails to parse and is dropped |
| "`animation-timing-function` is ignored" | honoured exactly; reasoning as if linear is how one sample declared its overtake six frames late |
| "`overflow: hidden` is broken" | it was never broken; the text simply was not where it was supposed to be |

Three documents here were quietly wrong for four engine releases because nobody re-read them after
an upgrade. A claim with a version next to it is a claim with an expiry date.

---

## The engine fails silently. That is the whole problem.

**It is forgiving on purpose.** A CSS property it does not support is ignored. An element it has no
primitive for lays out and stays blank. A box too small for its contents does not clip — they paint
over whatever follows. A font it cannot register may resolve to whatever the machine happens to
have. **Nothing throws, nothing logs, no render fails.**

A mistake therefore looks exactly like a composition you have not finished yet, and **reasoning
about the markup will not find it.** Neither will a glance at the output: overflowing content reads
as a z-order bug rather than as a height that is too small.

Every expensive mistake this repository has made has had that one shape — something that rendered
cleanly, passed every automatic check, and was wrong:

- Every `cupri-*` element expanded to **nothing**, because the render path never passed a component
  registry. It laid out at its CSS size, painted nothing, settled cleanly and linted clean. The
  studio wired its own registry, so the window worked and the gap stayed invisible. Meanwhile
  `lint` was telling authors to replace `<img>` with `<cupri-image>` — correct advice, which then
  drew nothing at all.
- Inline `<svg>` did the same, for the same reason, until the SVG package was installed. **0 pixels
  before, 6400 after**, on an 80x80 square.
- `CupriDoctor.Check(html, null)` turned the CSS checks off **entirely**, inline `<style>` included.
  The sample tests had been running the doctor over every shipped composition for months and had
  only ever read the markup.
- The engine kept one diagnostics sink per process, so a check returned findings produced by other
  documents on other threads — **157 of 200** checks of a clean document polluted by a thread doing
  nothing but rendering a noisy one.

None of these threw. All of them were found by counting pixels or by running a measurement twice.

---

## The loop

In order, as a habit rather than as a reaction to something looking wrong.

### 1. `lint` everything, before you render it

Fast, needs no ffmpeg, and it is the only thing that sees the silent failures. Run it on anything
new, anything you edited, anything you were handed, and again after each change.

```
$ cupricut lint --composition promo.html
error   CF0030 (line 1): <img> is not something the engine draws - it lays out, and then stays empty.
        -> Use <cupri-image src="..."> - the engine has no raw <img> primitive.
warning CUT001: 'Helvetica Neue' was answered by Arial (System), not by a registered file.
        -> Put the face in a Cut:FontDirectories folder, or name a family that is registered.
```

It is three sources in one verdict. `CF*` is the engine's own reader — a tag that never closed, a
component nothing registered, a property it ignored, a box that laid out with no area, text too
low-contrast to read, characters no font can draw. `CUT*` is what only CupriCut knows — a font
answered by the **machine**, a resource that never arrived, a timeline it could not parse, an audio
hash that no longer matches. And it says whether the composition is pure in `t`.

It is asked at the composition's **own** frame, not a default viewport. That matters: the doctor
defaults to 1024x768 and once reported a 1280-wide composition as broken for being 1280 wide.

### 2. Fix what is mechanical yourself

A raw `<img>`, a property the engine ignores, a box with no area, a stylesheet that never arrived,
a repeating gradient that paints nothing. One right answer each; nobody needs consulting.

### 3. Tell the person about the rest, in the finding's own words

Text too low-contrast to read. A font answered by the machine rather than by a registered file. A
composition impure in `t`. A duration that had to move to land on a whole frame. **These are
judgement calls about somebody's design, brand or constraints**, and quietly deciding them is how
an agent ships something nobody asked for.

The distinction is not severity, it is **ownership**. "Use `<cupri-image>`" is a fact about the
engine. "4.3:1 is under the 4.5:1 this text size needs" is a fact about their colours.

### 4. Look at the pixels

`contact_sheet` puts a whole motion in one image, which is the single highest-value thing here —
one read, and you can see an entrance land late. **Open the PNG and look at it.** A screenshot
settles in one glance what a paragraph of reasoning about flexbox cannot.

A clean lint means no *silent* failure was found. It says nothing about whether the animation is any
good.

### 5. Starting from browser HTML? `translate` first

Any document written for a browser, from anywhere — a page you were sent, a template, a CodePen, an
exported design, a frame pack, something you wrote out of browser habit. It compiles JavaScript
motion into `@keyframes` and applies the rewrites the engine needs. That is a compiler's job, not
something to do by hand.

Then do two things with the result, because they answer different questions:

- **Read the refusals.** What the translator could not carry. An empty list means exact *or quietly
  incomplete*.
- **Lint what came out.** What this engine will not draw. The translator reasons about markup and
  never had the engine.

Measured on one real block: the compiler refused **nothing**, and lint then found a
`repeating-linear-gradient` that parses and paints no pixels at all. Both answers are correct.
Either one alone misleads.

---

## What the engine gives you that CupriCut does not wrap

CupriFace ships four instruments and CupriCut exposes two of them as verbs. Worth knowing, because
when you are stuck the other two are still there from a test or a scratch program:

| CupriFace | here | |
|---|---|---|
| `CupriDoctor.Check` | **`lint`** | wrapped, plus `CUT*`, asked at the composition's own frame |
| `RenderToImage` | **`render_frame`, `contact_sheet`** | the sheet is the one to reach for first |
| `DumpTree` | *not exposed* | `inspect` answers the structural questions — timeline, fonts, backdrops, events — but not box geometry. When you need to know that a box is `312x0`, call `DumpTree` from a test. It is greppable, diffable, and far cheaper in context than a PNG |
| `ImageDiff` | *not exposed* | "did my change move anything else?" has no verb. Re-render a contact sheet, or compare in a test. Eyes are poor at spotting that one row moved three pixels; subtraction is perfect at it |

[docs/AUTHORING.md](docs/AUTHORING.md) is the measured list of what the engine does and does not
support, and every claim in it is asserted by `AuthoringGuideTests`. Prefer it to what you know a
browser would do. **Do not trust it against an engine newer than the one it names** — that is how
three of its claims came to be false with nothing saying so.

---

## House style

**Comments explain why, not what.** The reason a thing is the way it is, especially when the obvious
alternative was tried and failed. A comment that records a measurement — "62 of 200 clean documents
reported the other's warning" — is worth ten that restate the code.

**Name the failure that motivated the code.** `A_comma_separated_list_runs_NEITHER_animation` beats
`TestAnimationParsing`, because it says what breaks.

**One function, not three.** The recurring defect here is the same work implemented per caller and
then disagreeing. The sweep, the parallel renderer and the encoder each worked out the frame size
for themselves, and the encoder has to agree with the renderer *exactly* or the pipe fills with
misaligned bytes. The optional engine packages were installed by the renderer and not by the
doctor, so `lint` advised installing a package the tool already shipped. If two callers must agree,
give them one function.

**A lint that is wrong costs more than a missing one.** CUT006 was deleted for this: once the engine
was fixed, it warned authors away from something harmless. Advice that does not work is worse than
silence, because people act on it.

**Report faithfully.** If something is unverified, say so. If a number came from one machine, say
which. Never describe work as done when it is done-except-for.

**Prose in commits.** What changed, why, what it cost, and what was learned that the next person
would otherwise learn the hard way.

---

## Definition of done, for a change here

- [ ] The claim is measured, and the measurement is a test.
- [ ] `dotnet test` passes in full, not just the file you touched.
- [ ] Anything that could not be done is **named**, not quietly dropped.
- [ ] The docs that assert behaviour were re-read against the engine version in
      `Directory.Packages.props`, not against the version they were written for.
- [ ] If it changed what an agent should do, the server's own instructions in `Program.cs` say so.

---

## The three roots, which are the safety model

`Cut:CompositionRoots` is read-only, `Cut:OutputRoot` is write-only, `Cut:ProjectRoot` is read-write
and holds projects only. **A renderer's safety model is about what it may write**, so the write
surface is named in those places and nowhere else.

One deliberate exception, and it is worth understanding before adding another: a file **dragged onto
the studio window** is read from wherever it lives. The roots bound what an *agent* can reach,
because every other way in is a request arriving over a socket. A drop is a person at this machine
pointing at their own file. What does not move is the write surface — an import goes through
`SaveProject` into the project root, and no tool can raise a drop.

---

## The sibling repositories, and who owns what

| | |
|---|---|
| [CupriFace](https://github.com/Wixely/CupriFace) | The engine. **CupriCut makes no changes to it** — timelines and encoders stay out of the engine. When something is wrong *there*, raise an issue with an isolated reproduction and a measurement, and say so here rather than working around it silently. Five were raised this way and all five are closed |
| [CupriLex](https://github.com/Wixely/CupriLex) | The browser-HTML translator, consumed as `CupriLex.Compiler`. Only its compiler — this repository owns the `.cut.json` and `.cutpkg` schema and builds projects with its own types |

When you work around an engine defect, **write down the measurement and file it upstream.** The
reason this repository's notes are worth anything is that each one names a number. The reason three
of them went stale is that nobody went back when the issue closed — the OS file-drop refusal stood
for three weeks after the thing blocking it was fixed.
