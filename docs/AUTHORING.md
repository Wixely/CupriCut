# Writing a composition

Everything you need to compose for CupriCut, and every way the engine differs from a browser.

**This document is measured, not remembered**, and the measurements are **tests**. Every claim
below was produced by rendering a document and reading the pixels, against **CupriFace 0.26.1** —
and each one is asserted in `AuthoringGuideTests`, so this page is a description of what that file
measures rather than prose about the engine.

That matters more than it sounds. Three of the notes this page replaces were true of 0.25.0 and
false by 0.25.1, and nothing said so. Now, when the engine changes, the suite fails and this page
is *known* to be wrong instead of quietly becoming so.

Written for whoever composes — a person or an agent. If you are an agent, the
[checklist](#the-checklist) at the end is the short version.

---

## The one rule worth knowing first

**Keep the composition pure in `t`.**

A composition is *pure in `t`* when the picture at any moment depends only on that moment — which
is true when all its motion is `@keyframes` plus `animation-delay`, and false when anything carries
state forward (a CSS transition, a toast, scroll-driven easing).

A pure composition is rendered **directly** at any time you ask for. An impure one has to be
reached by *sweeping* every frame before it. Measured at **10–45× faster**, and it is the single
most important property your markup has. It is why the interaction track was dropped, and why
events are declared rather than executed.

`cupricut lint` tells you which you have. Impurity is reported as **info, not an error** — it is
correct and slower, and calling it a fault would be a lie. But if you did not mean to be impure,
it is almost always one stray `transition:` property.

---

## Motion

### What actually animates

Measured by animating each property from one value to another over two seconds and comparing the
rendered box at `t=0` with `t=2`:

| animates | does nothing |
|---|---|
| `width` | `margin-left` |
| `height` | `left`, `top` (even on a positioned element) |
| `opacity` | `padding` |
| `transform: translateX/Y` | `border-radius` |
| `transform: scale` | `font-size` |
| `transform: rotate` | `background`, `background-color` |
| | `color` |
| | a gradient's colour stops |
| | `visibility`, `display` |

Nothing in the right-hand column raises a diagnostic. The animation is accepted, runs, and changes
nothing — so the failure looks like "my animation did not play" rather than like an error.

**The four workarounds you will actually need:**

- **To move something**, use `transform: translateX()`, never `margin-left` or `left`. This is the
  most common mistake and the one that looks most like a broken engine.
- **To change a colour**, cross-fade two stacked elements with `opacity`. There is no other way —
  every colour property is inert under animation.
- **To show and hide**, animate `opacity`, not `visibility` or `display`. For a hard cut rather
  than a fade, put two `opacity` stops adjacent to each other (`49.99%` and `50%`). That is exactly
  what the [timeline](#the-timeline) generates.
- **To reveal by wiping**, animate `width` or `height` on a parent with `overflow: hidden`. Layout
  re-runs, so the content behind is revealed rather than squashed.

### One animation per element. Exactly one.

A comma-separated list does **not** run both animations. It runs **neither** — the declaration
fails to parse and the whole `animation` shorthand is dropped, leaving the element at its static
CSS values, silently.

```css
/* BROKEN. Not "the first one wins" - nothing happens at all. */
.bar { animation: grow 2s linear both, drop 2s linear both; }
```

If an element needs two things to happen, you have two options, and the first is usually right:

1. **Put the second animation on a child.** A wrapper that scales around a child that fades.
2. **Put both into one `@keyframes` block**, using percentages for the timing. `bar-race.html`
   does this for an overtake that has to pause, surge and settle: three shapes in one animation,
   because two animations on one element do nothing.

This is also why a timed element's own `animation` slot **belongs to CupriCut** — see the
[timeline](#the-timeline).

### Delays, and the one that silently does not work

```css
.a { animation: rise 0.6s ease-out 0.25s both; }   /* works */
.b { animation: rise 0.6s ease-out var(--d) both; } /* works - var() is fine */
.c { animation: rise 0.6s ease-out calc(var(--d) + 0.2s) both; } /* DOES NOT WORK */
```

`calc()` in `animation-delay` is not supported. It does not error — the delay is treated as **zero**,
so the element plays its entrance immediately and is already finished by the time you expected it to
start. Measured: with `calc(var(--d) + 0.5s)` where `--d: 1s`, the element was already 50% through
its animation at `t=0.5s`.

This matters because `calc()` is the obvious thing to reach for when staggering children inside a
late scene. **Put the stagger in the keyframe percentages instead:**

```css
/* Both children start on the scene's clock; the hold at the front is the stagger. */
.first  { animation: fadeIn 1.4s ease-out var(--cut-start) both; }
.second { animation: fadeLate 1.4s ease-out var(--cut-start) both; }

@keyframes fadeIn   { 0% { opacity: 0; } 40%  { opacity: 1; } 100% { opacity: 1; } }
@keyframes fadeLate { 0% { opacity: 0; } 20%  { opacity: 0; } 55%  { opacity: 1; } 100% { opacity: 1; } }
```

### Timing functions work

`ease-out`, `ease-in`, `cubic-bezier(...)` are all applied. Measured: a width animating 10→300px
with `ease-out` is at **208px** halfway through, where linear would be 155px — matching
`cubic-bezier(0, 0, 0.58, 1)` exactly.

Do not assume linear when reasoning about where something is at a given moment. If you need to know
when two animated values cross, **measure it off rendered frames** rather than solving it — see
`bar-race.html`, whose declared overtake was six frames wrong until it was measured.

### Comments inside `@keyframes` are fine now

Through 0.25.0, a CSS comment between keyframe stops silently corrupted their percentages — two of
them moved a bar's final width from 545px to 714px with nothing reporting it. Fixed in 0.25.1
([CupriFace#184](https://github.com/Wixely/CupriFace/issues/184)) and re-measured: 545px either way.

---

## The timeline

`data-start`, `data-duration` and `data-track` say when an element is on screen.

```html
<div class="scene" data-start="0" data-duration="6" data-track="card"> ... </div>
<div class="scene" data-start="6" data-duration="6" data-track="card"> ... </div>
```

Nothing is added to or removed from the document per frame — that would mean reloading it, at
25–370 ms against 3–7 ms for a frame. Each timed element gets a generated `@keyframes` holding it
at `opacity: 0` outside its window and `1` inside, with **adjacent stops** so the change is a cut
and not a fade. A composition with a timeline is **still pure in `t`**.

Two rules, both the engine's rather than choices:

**A timed element's own `animation` belongs to CupriCut.** Since the engine runs exactly one
animation per element, the window and your animation cannot share the slot. Put your motion on a
child. Writing one anyway is reported (`CUT003`) rather than silently eating the window.

**A late scene's children need `var(--cut-start)`.** The clock is absolute and stamps no creation
time, so a child of a scene appearing at 6s would otherwise have played its entrance at `t=0` and
be sitting still by the time you saw it. Each scene publishes `--cut-start`; custom properties
inherit; a child writes `animation-delay: var(--cut-start)`.

---

## Events

Something has to happen partway through — a caption pushed, an impression logged, a sting fired.
Declare the moment; CupriCut reports it.

```html
<div class="scene" data-start="6" data-duration="6"
     data-cut-event="+0.5:boost-shown; +2.4:boost-landed">
```

A `+` time is an offset from the element's own `data-start`, so marks move with their scene instead
of needing recalculating when it moves. Several go in one attribute, separated by semicolons.

Every render writes them beside its own output as `<name>.events.json`, with the frame each lands
on **at that render's rate** — the same marks at 25 fps are different frames, so the sidecar belongs
to the render and not to the composition.

**They are never executed.** Nothing plays here: a render *seeks*. Frames come out in whatever
order the sweep chooses, workers render different stretches at once, and a pure composition is
never swept at all — so a callback would fire out of order, repeatedly, or never. A number in a
file is correct under all of it, and survives the render.

An event changes no rendering decision, so declaring fifty of them leaves a composition pure in `t`.

---

## The backdrop

Mark the element that exists only so a human can see the composition against something:

```html
<div --cupricut-background class="backdrop"></div>
```

`export` drops it automatically when the formats asked for imply transparency — a mask of a
composition with an opaque backdrop would be a white rectangle. One file therefore gives you both
an opaque clip to review and a transparent one to key.

---

## Fonts

`FontPolicy.RegisteredOnly`, always. A family that would resolve to whatever this machine has
installed is an **error naming the family**, not a silent substitution — that is the difference
between a render CI reproduces and one it merely resembles.

Name a family that is registered, or put the face in a `Cut:FontDirectories` folder. `cupricut
inspect` lists every family the composition asked for and what answered it; `CUT001` warns when the
machine answered.

Always set a family explicitly on `body, html` — every shipped composition starts with
`body, html { font-family: "Noto Sans"; }`.

---

## Layout and paint

**Verified working:** `linear-gradient`, `border-left` / `-right` / `-bottom` individually,
`overflow: hidden` clipping to `border-radius` (a square child inside a round clipping parent does
lose its corners), `var()` and `var(--x, fallback)` for both colours and lengths, flexbox with
`align-items`.

**`line-height` works correctly** as of 0.25.1 — measured at `font-size: 20px`: no declaration →
24px (20 × 1.2), `20px` → 20px, `1.5em` → 30px, `150%` → 30px. Through 0.25.0 a `px` value produced
a box **3× too tall** and `em`/`%` were ignored outright
([CupriFace#181](https://github.com/Wixely/CupriFace/issues/181)), which is why no shipped
composition sets one. They can now.

**Still does not work:**

| | |
|---|---|
| `letter-spacing` | Silently ignored — measured, text is the same width with and without. Reported as `CF0050`, so `lint` catches it. |
| repeating gradients | Parse but paint **nothing**. Use one gradient with hard stops: `linear-gradient(90deg, #a 0 20px, #b 20px 40px)`. Reported as `CF0051`. |
| `align-self: center`, `margin: auto` | Do not centre a flex item. Use `align-items` on the parent, which does work. |
| `<img>` | Not a primitive the engine draws — it lays out and stays empty. Use `<cupri-image src="...">`, which takes a `data:` URI or a path beside the composition. Reported as `CF0030`. |
| JavaScript | There is none, by design. No `<script>`, no event handlers, no GSAP. |

### Components

The engine ships a large catalogue of `cupri-*` elements — `cupri-image`, `cupri-text`,
`cupri-video`, `cupri-markdown`, and chart primitives including `cupri-bar-chart`, `cupri-line-chart`
and `cupri-sparkline`.

`<cupri-image>` is verified: a `data:` URI and a file beside the composition both paint, and there
is a test asserting the right number of pixels. The rest are worth trying rather than documented —
build one, run `cupricut lint`, and look at a frame.

> **Look at a frame, specifically.** Writing this page turned up that none of these worked at all.
> A component expands only when the document has a component registry, and CupriCut's rendering
> path never gave it one — so every `cupri-*` element laid out at its CSS size, painted **nothing**,
> settled cleanly and passed `lint`. The studio wired its own registry from the start, so the
> window's controls worked and the gap stayed invisible, while `lint` went on telling authors to
> replace `<img>` with `<cupri-image>` — the one substitution the tool recommends, and the one that
> did not work. Fixed, and the ten shipped compositions render byte-for-byte identically either
> way, because none of them had been able to use a component.

---

## What `lint` tells you

Three sources in one verdict.

`CF*` comes from the engine's own reader — a tag that never closed, a component nothing registered,
a property it ignored, an element that laid out with no area while holding content (`CF0070`).

`CUT*` is what only CupriCut knows:

| | |
|---|---|
| `CUT001` | A family answered by the **machine** rather than by a registered file. |
| `CUT002` | A resource that never arrived — the document did not settle. |
| `CUT003` | A timeline it could not make sense of, including an animation on a timed element. |
| `CUT004` | Not pure in `t`. **Info, not a fault.** |
| `CUT005` | A font the composition asked for that nothing could answer. |
| `CUT007` | A declared event that cannot be read, or that nothing will ever reach. |

The CLI exits non-zero only on errors, so a CI step can gate on it.

---

## The checklist

Before calling a composition finished:

- [ ] `body, html { font-family: "..."; }` names a **registered** family.
- [ ] Every animated property is in the left-hand column: `width`, `height`, `opacity`, `transform`.
- [ ] No element has two animations. Check for a comma in any `animation:` line.
- [ ] No `calc()` in an `animation-delay`.
- [ ] Timed elements (`data-start`) carry no `animation` of their own — it is on their children.
- [ ] Children of a late scene use `animation-delay: var(--cut-start)`.
- [ ] Movement is `transform: translateX()`, not `margin-left` or `left`.
- [ ] Colour changes are cross-faded, not animated.
- [ ] `cupricut lint <file>` says **clean**.
- [ ] `cupricut inspect <file>` reports it as **pure in `t`**.
- [ ] A contact sheet has been looked at: `cupricut sheet <file> --columns 4 --rows 3 --duration N`.

That last one is not optional. Most of what is written above was found by rendering something and
seeing that it was wrong.

---

## The samples are the documentation

Ten in [`compositions/`](../compositions), each with a header explaining what it is for and which
constraint shaped it. The three most useful to read first:

- **`title-card.html`** — staggering. Four elements, one `@keyframes`, four delays.
- **`scenes.html`** — the timeline, and a late scene giving its children their own zero.
- **`fixture-card.html`** — a composition that is really a template, with declared events.

All ten lint clean, and a test keeps them that way.
