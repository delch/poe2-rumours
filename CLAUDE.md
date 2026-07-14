# PoE Rumours

A tray-resident Atlas overlay for **Path of Exile 2**. It watches the screen for the Uncharted Waters
tooltip, accumulates everything a tile has ever shown, and tells the player what the tile actually holds —
above all, **how many Grand Expeditions** are on it.

Windows-only: .NET 10 / WinForms, Windows Graphics Capture + `Windows.Media.Ocr`. It builds and runs only
on Windows.

## The game mechanic this exists for

Read this before touching anything — the whole design follows from it.

An Uncharted Waters tile holds a **pool** of rumours, but the tooltip only ever lists **three of them**,
drawn at random and in random order. Verified in-game (2026-07-14, one tile, seven toggles):

1. The panel shows **3**; the pool was **5**.
2. Toggling **any Saga** on or off re-draws which three are shown. Nothing else does — re-hovering the
   tile or reopening the Atlas shows the same three.
3. **Toggling is free.** A Saga is consumed only when a Logbook is charted, so the draw can be re-rolled
   indefinitely with one Saga in the inventory.
4. An active Saga **does not inject its boss** into the tile's rumours; it only re-rolls the visible three.

**Therefore a single reading is a sample, not the tile.** Only the union across many samples is the truth.
Accumulating that union is the entire product. Any change that makes the app show "the current three"
instead of "everything seen so far" defeats its purpose.

## Rules

- **Never invent facts about the game.** If a mechanic is unclear (what counts as a Grand Expedition,
  whether pool size is fixed, whether a Saga name can appear as a rumour line), **ask** — the user is in
  the game and will check. A plausible guess about game data is worse than no answer: it silently corrupts
  the thing the tool exists to report.
- **No code from `pedro-quiterio/PoeAncientsPriceHelper`.** That project (which this one grew out of) has
  **no licence** — all rights reserved. Everything here is written from scratch against public Windows
  APIs. Do not copy its source, comments, or structure. Observations *about the game* are facts and are
  fine; its *code* is not.
- Comments explain **why**, not what. The what is in the code.

## Architecture

The shape is a direct consequence of experience: on the predecessor project, **every real bug was at a
seam, never in the logic.** The logic was unit-tested and correct; the seams silently lied.

- **Core — pure, no screen.** Panel model, name resolution, pool accumulation, completeness estimate.
  Deterministic, unit-tested against real captured readings. Correctness lives here.
- **Sensing — thin, swappable.** Capture, OCR, panel detection. Inherently flaky; kept small and behind an
  interface so the core is testable without a game running.
- **Overlay — dumb.** Renders what the core decided. Holds no state, decides nothing.

## Hard-won constraints

Each of these cost real debugging time on the predecessor. Do not regress them.

- **The overlay must be excluded from our own capture** (`SetWindowDisplayAffinity` /
  `WDA_EXCLUDEFROMCAPTURE`). The scan OCRs the *whole screen*, so it captures the overlay too and reads our
  own text back as if it were the game's — the panel's detected bounds stretch, the overlay moves, the next
  frame differs, and it oscillates once per scan. Worse, the rumour names *we drew* get re-read as panel
  rows and fed back into the pool as fresh observations. Side effect to remember: an excluded window is
  also **invisible in screenshots and OBS** — that is expected, not a bug.
- **Clicks must pass through** the overlay to the game everywhere except its own buttons, and it must
  **never steal focus** from the game (`WS_EX_NOACTIVATE`). A layered window with per-pixel alpha is
  already click-through where alpha is 0; the opaque plate is the part that needs care.
- **OCR is not to be trusted.** It reads the same tooltip line twice (once clean, once garbled), returns
  nothing at all at some upscale factors while working at neighbouring ones, and picks up any of our own
  windows that happen to be on screen. The core must be robust to duplicate, garbled, and phantom rows.

## Status

Design stage. See `docs/epic-app.md` — the living spec. No implementation yet.
