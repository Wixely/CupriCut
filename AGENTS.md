# Agent instructions

See **[CLAUDE.md](CLAUDE.md)** — the instructions live there in full, and this file exists so that
agents which look for `AGENTS.md` find them too.

The short version, because it is the thing most often missed and it changes the result the most:

**The engine renders HTML to pixels with no browser, and it fails silently by design.** An
unsupported CSS property is ignored. An element it has no primitive for lays out and stays blank. A
box too small for its contents does not clip — they paint over whatever follows. A font it cannot
register may resolve to whatever this machine happens to have. Nothing throws, nothing logs, no
render fails. A mistake looks exactly like a composition you have not finished yet, so **reasoning
about the markup will not find it** — and the render can mislead you too, because overflowing
content reads as a z-order bug rather than as a height that is too small.

So, as a habit rather than as a reaction:

```
cupricut lint --composition promo.html      # before you render. Fast, no ffmpeg, sees the silent failures
cupricut sheet --composition promo.html     # then open the PNG and LOOK at it
```

1. **Lint everything** you write, edit or are handed, and again after each change.
2. **Fix what is mechanical yourself** — a raw `<img>`, a property the engine ignores, a box with no
   area. One right answer each.
3. **Put the rest to the person, in the finding's own words** — contrast, a font answered by the
   machine rather than a registered file, a stack that lost every family it named, a composition
   impure in `t`. Those are judgement calls about someone's design, and deciding them quietly is how
   an agent ships something nobody asked for.
4. **Look at the pixels.** A clean lint means no *silent* failure was found. It says nothing about
   whether the animation is any good.
5. **Starting from browser HTML?** Run `translate` first — any of it, from anywhere, not just a
   frame pack. Then read its refusals *and* lint what came out: the translator reasons about markup
   and never had the engine.

[docs/AUTHORING.md](docs/AUTHORING.md) is the measured list of what the engine will and will not do,
and every claim in it is asserted by a test. Prefer it to what you know a browser would do.

The same five points are sent to every MCP client at the handshake — see `Program.AgentInstructions`
in [Program.cs](Program.cs). Keep the two in step.
