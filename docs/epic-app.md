# Epic: PoE Rumours — the application

Living design document. Requirements get added as we decide them.

**Status: built and running (v0.1.0).** R1–R8 are implemented; a self-contained single-file `.exe` publishes to
`dist/`. Where in-game use forced a rule to change, the requirement below is rewritten rather than annotated —
a living document that describes the first draft instead of the shipped thing is worse than no document.

## Why this exists

An Uncharted Waters tile holds a pool of rumours, but the tooltip only ever shows **three of them**,
picked at random. So a glance at a tile does not tell you what the tile is worth — it tells you what
three rumours it happened to draw this time.

The tool answers the one question the player actually has before spending a Logbook:

> **What is really on this tile, and how many Grand Expeditions does it hold?**

## What we know (verified in-game, 2026-07-14)

These are observations, not assumptions. They are the foundation the whole design rests on.

1. The tooltip lists **3 rumours**, in random order, drawn from a larger pool (one tile: 5 distinct
   rumours behind a 3-line panel).
2. Toggling **any Saga** on or off re-draws which three are shown. Nothing else does — re-hovering the
   tile or reopening the Atlas shows the same three.
3. **Toggling is free.** A Saga is consumed only when a Logbook is charted, so the draw can be re-rolled
   indefinitely with a single Saga in the inventory.
4. An active Saga **does not add its boss** to the tile's rumours. It only re-rolls the visible three.

Consequence, and the entire point of the app: **only the union of many samples tells you what a tile
holds.** A single reading is a sample, not the truth.

## Requirements

Numbered so we can refer to them. Add freely.

### R1 — Tray application
The app lives in the system tray.

- Launching it puts it in the tray.
- Closing or minimising the window sends it **to the tray, not to exit**.
- Exit is an explicit action from the tray menu.

*Open:* does it start with Windows? Does it show a window at all on first launch, or go straight to the
tray? What is on the tray menu?

### R2 — The app states its version
The running build must be identifiable, without guessing.

- The version is read **from the assembly**, never typed into the UI by hand. Two hand-maintained copies
  drift the moment someone forgets one, and a build that misreports its own version poisons every bug
  report and every log that follows.
- It is visible where a user would look for it, and it goes into the diagnostic log header — when we are
  reading a log to work out why a scan misbehaved, the first thing we need to know is *which build wrote
  it*.

*Open:* where exactly is it shown — tray tooltip, tray menu, an About line in settings, all three?

### R3 — Game language is a setting, not a guess
**Default `en`.** The user switches it if their client is in another language; the choice persists across
launches. **No auto-detection.**

Auto-detection was considered and rejected. It is technically workable — the vocabulary is closed (20
rumour lines per locale plus the panel signatures), so scoring OCR output against each locale would pick
the right one. But an auto-detector that gets it wrong gets it wrong *silently*, and we already paid for
that lesson: the predecessor's Atlas gate auto-located its own band, quietly read nothing, and cost an hour
of debugging. What saved it was the manual override. A setting the user controls has no silent mode.

Two consequences that are now **more** important, not less, because there is no fallback:

- **Fail loudly.** The likely failure is a Russian client left on `en`: no signature matches, no rumour
  resolves, and the user gets an empty overlay — exactly the silent nothing we hate. If a panel is on
  screen but nothing matches the selected locale, the app must **say so**: "found the panel but matched no
  rumours — is the game language right?"
- **The OCR language pack is a hard prerequisite.** `Windows.Media.Ocr` needs a recognizer installed per
  language; correct data is not enough. Enumerate `OcrEngine.AvailableRecognizerLanguages`, and if the
  selected language has no recognizer, say that plainly instead of silently reading nothing.
  (This machine has `en-GB`, `en-US`, `ru` — so both supported locales are readable here.)

Data is already bilingual: `data/rumours.json` carries `en` and `ru` tooltip strings keyed by a
locale-invariant id. Adding a language = adding strings, not code.

*Still needed for `ru`:* the localized **panel signatures** (`Uncharted Waters`, `Island Rumours`,
`Expedition Logbook`). Without them the panel itself is not found on a Russian client, even though every
rumour line is known.

### R4 — Trigger and sampling
**Trigger: the Atlas is open and the rumour panel is on screen.**

The panel *is* the trigger. Finding the "Uncharted Waters" tooltip proves we are on the Atlas — it cannot
appear anywhere else — so no separate atlas gate is needed for **correctness**. See Q6: a gate is only a
CPU optimisation, and the word-based one is the most fragile, most locale-bound part of the whole system.

**Sampling: a sample is counted when the displayed triple CHANGES** — not once per panel opening.

This matters because **the tooltip can be pinned in place** (the game has a pin button), which is exactly
what a player doing this seriously will use: pin the panel, then stay in the inventory toggling a Saga.
With a pinned panel there is only ever *one* opening, so "one sample per opening" would record **a single
sample for the whole session** and the accumulator would never accumulate — the feature would silently do
nothing.

"On change" is correct in all three cases, so it does not depend on unresolved behaviour:

| situation | behaviour |
|---|---|
| pinned, content re-rolls live | every re-roll is a change → sampled |
| pinned, content frozen | no change while pinned; re-hovering after a toggle shows a new triple → sampled |
| not pinned (hover each time) | each new triple is a change → sampled |

Known and accepted cost: a genuine re-roll that happens to redraw the *same* triple is not counted. On a
5-rumour pool that is roughly a 1-in-10 event, and it costs only a seen-count, never a missing rumour.

#### The real workflow (confirmed in-game)
Pin the panel → go to the inventory → toggle a Saga → **hover the ship icon** on the map → the pinned panel
re-rolls **in place**. The panel never leaves the screen for the whole session.

Two things follow, and both are load-bearing:

- **"One sample per panel opening" would have recorded exactly one sample, forever.** There is only ever one
  opening. The accumulator would have quietly accumulated nothing while looking perfectly healthy — the
  worst kind of bug, and the reason this rule is written down before any code exists.
- **A pinned panel has fixed screen bounds.** The overlay can be placed once and left alone. The predecessor
  spent an afternoon on an overlay that jittered because it chased per-frame OCR bounds; here the common
  case simply does not move.

### R5 — Icon
`media/sailing-boat.svg` — a sailing boat — is the app and tray icon. Fitting: the mechanic is literally
sailing an uncharted ocean.

Two things it needs before it can be used:

- **A separate silhouette for small sizes.** The tray renders at **16×16** (32×32 at 200% DPI), and the
  source art has outlines, two-tone sail, waves and rivet dots — at 16px that collapses into a coloured
  smudge and stops reading as a boat. `.ico` stores a *different image per size*, so: full art at 32/48/64/
  256, a stripped-down hull-and-sail silhouette at 16/24. This is legibility, not polish.
- **Provenance: Flaticon, under the owner's Premium licence.** Premium removes the attribution requirement,
  so nothing needs to be credited in the app. One caveat if this repo is ever made **public**: the licence
  covers using the icon *in* a product, not redistributing it as a reusable asset — a raw `.svg` sitting in
  a public repo is downloadable by anyone. So ship the compiled `.ico` (embedded in the exe) and keep
  `sailing-boat.svg` out of a public tree. The Premium licence covers the owner, not everyone who clones.

Rasterising SVG → multi-size `.ico` needs no system install: a throwaway SkiaSharp-based tool can do it.

### R6 — The overlay

**Shows, per rumour found:** name · map · kind (grand / boss / unique). Plus counters by kind — *"Found: 2
grand · 1 boss · 0 unique"* — and, in the footer, distinct/samples. No letter ratings from anyone else's
spreadsheet; see Q5.

**The `Seen` count is not on the plate.** It was, and it was removed in use: a bare unlabelled column of
numbers reads as a claim the tool is not making, and it is not something the player acts on. It is still
counted, and it still goes to `scan.log`, where it answers exactly the question it is good for — *did that
rumour turn up in 8 samples out of 8, or 1?*

Sort by **`rating`** (ours, from the data file), not by `kind`. The two do not line up, and that is the
point: `Fallen stars` is a *grand* and `Stardrinker` is a *boss*, and both are S because of what they
actually give the owner. Kind is a fact to count; rating is what you care about. Counting Grands and
ranking the list are different jobs.

**Three buttons:**

| button | does |
|---|---|
| ✕ close | **get out of the way now** — not "never show me this again", which is what the tray is for. It holds only until rumours are back on screen (the rising edge; clearing it while the tooltip still sits there would make the ✕ undo itself the instant it was clicked). |
| 🔒 lock | pin the plate for the whole Atlas session, **empty pool included**. That is the only case it changes anything — see R8, which made "stay up while the Atlas is open" the default. |
| ↺ reset | clear the accumulated pool |

**Reset is mandatory, not a convenience.** We cannot tell one tile from another: nothing on screen
identifies a tile, and the obvious heuristic — "a reading that shares no rumour with the pool must be a new
tile" — is unreliable *here* in a way it would not be elsewhere. There are only **20 rumours** and a pool
is about **5**, so two different tiles will very often share one or two. The heuristic would rarely fire,
and two tiles' pools would **merge silently** into a wrong Grand count that looks perfectly healthy. That
is corruption of the one number the app exists to produce.

Therefore:

- **Automatic reset on one event only: the Atlas closes.** Deterministic, observable, no guessing.
- **No silent reset on a disjoint reading.** If a reading shares nothing with the pool, *ask* — "looks like
  a different tile, reset?" — and let the player answer. We spent a day on automatic machinery that decided
  things silently and got them wrong quietly; we are not building our own.
- **The ✕ and the reset button are the manual escape hatches**, for a tile change within one Atlas session.

### R7 — Overlay placement: the player drags it, the app never moves it

**The overlay does not follow the panel.** It sits where the player put it, and stays there. Position is
remembered across launches.

#### The window is the size of the plate, not the size of the screen
This is the load-bearing decision, and it is a safety property, not a style choice.

The predecessor stretched its overlay across the whole monitor and made it click-through *by hand* — a
`WM_NCHITTEST` that answered "transparent" over the entire desktop. One mistake in that hit-test and the
mouse dies **everywhere**. That is the single riskiest thing in the whole design.

Make the window exactly the plate, and:

- **The rest of the screen is not ours.** It cannot swallow a click, because there is no window there. The
  risk is gone *by construction*, not by careful code.
- **Dragging is free.** Return `HTCAPTION` for the header strip and Windows moves the window itself — no
  manual mouse tracking, no capture, no drag state machine.
- **Rendering is simpler:** the scene is drawn in window coordinates instead of absolute screen pixels.

#### Dragging
- **The header strip is the grab handle** — press, drag, release. It already carries the ✕ / 🔒 / ↺ buttons.
- **The rest of the plate stays click-through**: clicks on the map underneath reach the game.
- Position persists in config.

Honest cost: **the header strip is a dead zone for map clicks.** Anything you can grab with the mouse must
catch the mouse. But it is a ~20px strip that had to catch clicks anyway for its buttons — not the whole
screen. If it ever grates, `Alt`+drag anywhere on the plate (hit-test checks the modifier) removes the dead
zone entirely, at the cost of discoverability.

#### Why not anchor to the panel, like the predecessor did
- Its adaptive "place left of the panel if the panel is right of screen centre" logic existed **only** so the
  overlay would not cover the panel and get read by its own OCR. We exclude the overlay from capture
  (`WDA_EXCLUDEFROMCAPTURE`), so overlap is harmless and the whole constraint evaporates.
- Anchoring means tracking OCR-derived panel bounds — the exact seam that produced an afternoon of a
  jittering plate.
- The panel is not a stable thing to anchor to: it can be **pinned anywhere**, or pop up at any hovered node.
- With the lock button on, **the panel is not on screen at all** while the overlay must be. Anchoring to a
  "last known position" is arbitrary.
- A UI that moves on its own is a UI you have to hunt for. This one is where you left it.

### R8 — When the overlay is on screen

Written last, rewritten most. Every rule here exists because the previous one failed in the game.

**The plate follows the rumours.**

| event | behaviour |
|---|---|
| rumours on screen for **1 s** | the plate appears |
| rumours leave | it goes **1.5 s** later |
| cursor moves onto the plate | the countdown is cancelled; it stays |
| cursor leaves the plate | the countdown restarts |
| rumours come back | it appears again, whatever happened before — dismissed, timed out, dragged |
| Atlas closes | it goes at once, and the pool resets (R6) |

**Why the delay on the way in.** Dragging the cursor across the Atlas sweeps the tooltip on and off half a
dozen tiles on the way to somewhere else. Without the delay the plate flashed at every one of them.

**Why the delay on the way out, and why the cursor cancels it.** The plate's body is `HTTRANSPARENT`, so the
cursor **passes through it to the map underneath** — which moves the cursor off the tile, which closes the
game's tooltip. The first version hid the plate the moment the tooltip went, so it vanished precisely when the
player reached for its buttons, and the buttons were unusable. Note what this means: **Windows never sends the
window a `MouseEnter`** — as far as it is concerned the mouse is never over us. The cursor has to be polled
(100 ms), and there is no way around it.

**Why "on screen" is a fuzzy signal.** The detector *blinks*: OCR finds the tooltip on one tick and misses it
on the next. The 1 s timer originally restarted on every miss — so on a client where OCR is flakier (Russian),
the second never elapsed and **the overlay simply never appeared**, while `scan.log` looked perfectly healthy,
because samples are recorded on the ticks that *did* find the panel. A gap must now outlast ~2 scans before it
counts as gone, and **every panel up/gone transition is logged**, so the next instance of this is visible
instead of inferred.

### R9 — Screenshot mode (debugging)

The overlay is invisible to screen capture by design (R6/architecture), which also means it cannot be
screenshotted — an obstacle the moment anyone wants to show what the app looks like or report a bug with it.

Tray → **Screenshot mode** lifts the exclusion **and pauses scanning in the same breath**. The two are not
separable and the UI does not pretend otherwise: an overlay the scanner can see is an overlay it will read, and
the rumour names *we drew* would come straight back in as if the game had shown them. While it is on, the tray
tooltip says so, so it cannot be left on by accident and quietly explain why the pool stopped growing.

## Non-goals

Decided against, with the reason. Recorded so they are not quietly reinvented — several of these are ideas
that *sound* like improvements.

- **No language auto-detection.** Scoring OCR output against each locale's closed vocabulary would work, but
  an auto-detector that guesses wrong guesses wrong *silently*. Language is a setting (R3). The predecessor's
  self-locating atlas gate is exactly this mistake, and it cost an hour.
- **No probabilistic verdicts.** No "you have probably seen the whole pool", no "you can stop toggling". The
  overlay reports **counts** — *"2 boss · 3 expedition · 1 unique"* — and the player draws the conclusion.
  The estimator was derived and validated, then deliberately not built (Q2).
- **No imported ratings or mods.** The community sheet's letter grades were one person's opinion in a file
  that also swapped two biomes and invented a rumour. `kind` comes from the game's own data; `rating` is the
  owner's own field (Q5).
- **No silent pool reset.** The pool clears on exactly one automatic event — the Atlas closing. A reading
  that shares nothing with the pool *asks* rather than acts: with 20 rumours and pools of ~5, two different
  tiles routinely overlap, so a "looks like a new tile" heuristic would merge two tiles' pools without a
  word and quietly corrupt the only number that matters (R6).
- **No cursor-anchored scanning.** The tooltip can be pinned, and pinning exists precisely so the cursor can
  leave for the inventory. A region around the cursor would lose the panel during the exact activity the app
  is for (Q6).
- **No gate on the `World` / `Мир` banner.** One stylised word that OCR returns *nothing* for at some upscale
  factors, and three letters in Russian. Several plain-text anchors, OR'd, instead (Q6).
- **No fullscreen-exclusive support.** A layered overlay cannot draw over it. Borderless / windowed only —
  and say so plainly rather than rendering nothing and letting the user wonder.
- **No reuse of the predecessor's code.** `pedro-quiterio/PoeAncientsPriceHelper` ships **no licence** —
  all rights reserved. Its *observations about the game* are facts and are ours to use; its source is not.
- **No installer, no auto-updater, no code signing.** Ship a plain `.exe`. An installer solves a *delivery*
  problem we do not have — this is a personal tool, not a distribution. The predecessor carries Velopack for
  this, and pays for it with a non-standard entry point (a custom `StartupObject`, `App.xaml` demoted from
  ApplicationDefinition) purely so the update hooks can run before anything else. Packaging can always be
  added on top of a working app later; unpicking it is the painful direction. When it is time to hand the
  thing to someone, a **self-contained single-file publish** is enough — one exe, no .NET install needed.
- **No D3D11 / Windows Graphics Capture stack.** Measured, not assumed: `Graphics.CopyFromScreen` captures
  the Vulkan game at 93.9% non-black (M0.5). The predecessor's Vortice/D3D backend abstraction buys us
  nothing here.

## Settled

### Q1 — What is a Grand Expedition? — **ANSWERED**
It is a static property of the rumour **name**. The wiki states each rumour is one of exactly three kinds:

> Each rumour corresponds to a unique map, a Grand Expedition map, or a map containing a special boss.

It names the 4 boss rumours and the 5 unique-map rumours explicitly; the **remaining 11 are the Grand
Expeditions** (4 + 5 + 11 = 20). Deduced, then confirmed in-game by the user. Encoded as `kind` in
`data/rumours.json`.

Also from the wiki: every revealed ocean area is **guaranteed at least one** Grand Expedition (patch 0.5.2).

### Q3 — Can a Saga name appear as a rumour line? — **ANSWERED: no**
Sagas are inventory items, not tile rumours. The community sheet listing them in the rumour column is one
of several reasons its data is untrustworthy.

## Known discrepancy with the wiki

The wiki says of the Saga omens:

> This will change the seed of the map spawn and can be used as a way to "reroll" the area.

Taken literally this would **destroy the premise of this app**: if toggling re-rolls the *area*, then the
union across toggles merges rumours from different areas and means nothing.

We proceed on the pool model anyway, for two reasons:

1. **The wiki contradicts itself, and its other half agrees with us:** *"it will display **up to three**
   Rumors that indicate what's in the area … an ocean area can have **more than three** of these special
   maps."* That is exactly our model — the area holds more than it shows.
2. **Direct observation beats prose:** seven toggles on one tile produced five distinct rumours, two
   samples repeated verbatim, and the same five appeared with the Saga active and inactive. A re-seeded
   area would have drifted across the 20 possible rumours instead of circling the same five.

Caveat kept honest: that is **one tile, one session**.

### The app falsifies its own model, for free
This does not need a separate experiment — the accumulator answers it:

- **Pool model true** → as toggles pile up, `Seen` counts rise but the number of *distinct* rumours hits a
  ceiling. The pool **converges**.
- **Wiki literal reading true** → new names keep appearing without end. The pool **never converges**.

So the completeness estimate (Q2) is not just "have I seen it all" — it is a standing test of whether the
whole premise holds. If a tile refuses to converge after a dozen toggles, the app is telling us the model
is wrong.

## Open questions

### Q8 — The first live run did not converge — **ANSWERED: the player was changing tiles**

The premise holds. What the log captured is **two tiles' pools merging silently** — exactly the failure R6
was written to prevent, caught live before any user could be misled by it.

And it landed precisely as predicted: the disjoint-reading heuristic **would not have saved us**. `Endless
cliffs` appears in both groups, so the tiles overlapped, the heuristic would have stayed quiet, and the pools
would have merged just the same. That argument was made on paper from arithmetic — 20 rumours, pools of ~5,
overlap is routine — and the log confirms it.

**The only defence is the reset button** (plus the automatic reset when the Atlas closes). Both are M3, which
is why nothing stopped the merge in this run.

*Original analysis, kept because the reasoning is what mattered:*

M2's scanner ran against the live game for two minutes (`scan.log`, 2026-07-14 18:03). Accumulation works
mechanically — 14 samples, pool grew 3 → 4 → 5 → 7 → 8 distinct. **But 8 distinct is more than one tile can
hold**, and the pool never converged.

The log shows a clean break. First a stable group:

    Endless cliffs · Somethin' fishy · Cold as ice · Sulphite! · It's dry at least

then, after a gap, rumours that had never appeared before:

    sample #7:  A good fellow... | Warm but risky... | Endless cliffs...
    sample #8:  The last to fall... | Endless cliffs... | A good fellow...

and finally a **two-line panel** (sample #14) — i.e. a tile whose pool is exactly 2.

Two incompatible explanations:

1. **The player hovered different tiles.** The model is fine, but two tiles' pools **merged silently** —
   precisely the failure R6 predicts. Note the disjoint-reading heuristic would *not* have caught it:
   `Endless cliffs` appears in both groups. Exactly as argued — 20 rumours, pools of ~5, overlap is routine.
   No reset button exists yet (M3), so nothing stopped it.
2. **Toggling a Saga re-seeds the area** — the wiki's literal reading, and our founding premise is wrong.
   Accumulating a union across toggles would then be meaningless.

Weak evidence for (1): between 18:04:26 and 18:04:52 the panel showed **the same triple for thirty seconds**
and no new sample was counted. Had Sagas been toggled during that window, the triple should have re-rolled.

**This is the question the product rests on.** Do not build further on the pool until it is answered. The
test that settles it: stay on **one** tile, never hover another, toggle a Saga ten times, and see whether the
distinct count stops growing.

### Minor, from the same run
The matcher is at its limit on one string: `Snüiw' fishy...` fell below threshold and was logged as unknown,
while `Somed'liw' fishy...` resolved fine in the next sample. Loosening the threshold is the obvious fix and
the wrong one — it would start snapping junk onto rumours, which corrupts the count instead of merely
under-reporting it.

### Q2 — What the overlay reports — **ANSWERED: plain counters, no verdict**

**Decided: counts by kind (Grand / boss / unique) plus a per-rumour `Seen` count. No completeness verdict,
no "you can stop toggling now".** The tool reports what it has seen; the player draws the conclusion.

The one consequence to keep honest in the UI: these are **"found so far", not "on the tile"**. The count
only ever grows with more toggles. So the label reads *"Found: 2 Grand · 1 boss"*, never *"This tile has 2
Grand"* — the second is a claim we cannot make and would be wrong often enough to matter.

`Seen` carries the completeness information without pronouncing on it: everything sitting at 4–5 across ten
samples means the pool is exhausted; a rumour on 1 out of ten samples means keep going.

*Amended in use (R6):* the column is **not on the plate**. The owner did not want it, and unlabelled numbers
are read as a verdict whether or not one is meant. It survives in `scan.log`.

#### Estimator — NOT built, kept for reference
The statistics below were worked out and validated before the decision above. Recorded so nobody re-derives
them, and because the **non-convergence signal remains useful as an internal diagnostic** (see the premise
check further down) even though no verdict is shown to the player.
Pool of `N` rumours, each sample reveals `s` of them (normally 3) at random. Then

- P(a given rumour still unseen after `k` samples) = `(1 − s/N)^k`
- E[distinct seen after `k` samples] = `N · (1 − (1 − s/N)^k)`

Estimate `N` by matching the observed distinct count; then report the probability that nothing is hiding.
(Use each sample's actual `s`, not a constant 3 — the wiki says the panel shows *up to* three.)

#### It reproduces the one experiment we have
Seven toggles, five distinct rumours found:

| if the pool were N | expected distinct at k=7 | observed |
|---|---|---|
| **5** | **4.99** | **5** ✔ |
| 6 | 5.95 | 5 — already off |
| 7 | 6.86 | 5 — effectively ruled out |

From the counts alone, *without being told the answer*, the estimator lands on N = 5 — which is what the
tile actually held.

#### It tells the player when to stop
Samples needed to push "a rumour is still hiding" below 5%:

| pool | toggles |
|---|---|
| 5 | 4 |
| 8 | 7 |
| 12 | 11 |

Convergence is fast: **4–10 toggles regardless of pool size.** The app can say "you're done" and mean it.

#### And it audits its own premise, for free
Distinct-count that keeps climbing without ever converging = the pool model is wrong (i.e. the wiki's
literal "a Saga re-seeds the area" reading is right). We do not need a separate experiment for this; the
accumulator reports it.

#### Assumption, stated honestly
All of the above assumes each rumour is **equally likely** to be drawn. If the game favours some, the
estimate is optimistic. This is itself testable: seen-counts should cluster around `k·s/N`. If one rumour
is persistently rare while others are not, the uniformity assumption is broken — surface it rather than
quietly trusting the number.

### Q4 — Is there more in the tooltip than the rumour lines? — **ANSWERED: no**
No icons, no colours, no per-rumour hover text. Just the lines. So `kind` must come from our data; it cannot
be read off the screen.

### Q7 — Fewer than three rumours — **ANSWERED: the panel shows `min(3, pool)`**

The panel does not always list three. When it lists **1 or 2, that is the whole pool** — the remaining maps
in the area are ordinary maps: no Grand, no boss, no unique. Confirmed in-game. The panel still draws three
parchment slots; the unused ones are simply blank.

**This changes nothing about what the overlay says.** Per Q2 the overlay reports counts and nothing else —
*"2 boss · 3 expedition · 1 unique"*. It does **not** tell the player whether to keep toggling, whether the
pool is complete, or anything else it might infer. Counts only. This fact is recorded because it explains
why a short panel is legitimate input, not a failed read — not because it earns a verdict.

#### Why a short panel is not a failed read
The obvious worry — "what if OCR just missed the third line, and we wrongly declare the pool complete?" —
is **not supported by anything we observed**. Across a day of real scan logs, every OCR failure ran in the
*same direction*: **too much, never too little.**

- the same line read twice (once clean, once garbled) — seen
- phantom lines (`Consumes:`, our own overlay's text, the app's own window buttons) — seen
- a line read correctly but absent from the dictionary (`Warm but risky`) — seen
- **a rumour line silently not read at all — never once seen**

So the failure mode to defend against is *over*-counting, not under-counting — and over-counting is benign
(at worst you toggle a Saga you did not need to) and is fixed in the detector: **filter the boilerplate**
(`Consumes:`, `Use a logbook to chart the area`, the title, the section header) and **collapse duplicate
lines**. The predecessor failed to filter `Consumes:`, which is why its "unknown rumour" counter was stuck
on for every single tile.

No panel geometry measurement, no multi-pass voting. Just an honest boilerplate filter.

### Q6 — The atlas gate — **now REQUIRED, but not the fragile one**

The gate came back, and for a better reason than it left. R6 resets the pool **when the Atlas closes**, and
the lock button holds the overlay **while the Atlas is open** — so the app must know whether the Atlas is
open. Panel-absence does not prove it: the player may simply have moved the cursor. So the gate is no
longer a CPU optimisation we could drop; it is **correctness**.

**Ruled out: scanning a region around the cursor.** The tooltip can be **pinned**, and the entire point of
pinning is to walk the cursor away into the inventory. A cursor-anchored scan would lose the panel during
exactly the activity the app exists to support.

**Ruled out: gating on the `World` / `Мир` banner.** That is the predecessor's design and it is the single
most fragile thing we met all day: Windows OCR returns *nothing* for the stylised English banner at 4×
upscale while reading it cleanly at 3× and 6× — measured. In Russian it is `Мир`: **three** stylised letters
on a decorated plate. There is no reason to expect that to go better.

**Proposed instead: several plain-text anchors, OR'd together.** The Atlas screen also carries ordinary UI
text, in an ordinary font, which OCR handles incomparably better than decorative banners:

- the search box — `Search here` / `Искать здесь`
- the act tabs — `ACT 1 … ENDGAME` / `Акт 1 … Акт 3`

Any one of them proves the Atlas is open. A single unlucky misread no longer blinds the app: all of them
would have to fail at once. Same job as the old gate, without betting the product on one stylised word.

**Why more than one anchor is not paranoia:** in a real screenshot the rumour panel **partially covers the
search box** — so that anchor disappears exactly when the tooltip is open, which is precisely when the app
matters most. The act tabs and the banner stayed visible. Occlusion by the game's own panels is normal,
not an edge case, and any single anchor can be the one that is covered.

*Older reasoning, kept:* the gate buys only CPU, and it costs:
The predecessor gates all work on a full-screen OCR behind one cheap check: is the word `World` on screen?
It exists purely to avoid OCR'ing 3440×1440 every second. We pay for that optimisation twice:

- **It is the most fragile thing in the system.** Windows OCR returns *nothing* for the stylised `World`
  banner at 4× upscale while reading it cleanly at 3× and 6× — measured, and it cost an hour. In Russian
  the word is **`Мир`**: three stylised letters. There is no reason to expect that to go better.
- **It is locale-bound.** Every new language needs its gate word verified, not merely translated.

The alternative removes the gate entirely: **the tooltip appears next to the tile the cursor is on.** So
OCR a bounded region around the cursor (e.g. 900×700) while the game is foreground, instead of the whole
screen behind a gate. That is *cheaper than the gated full-screen scan*, needs no gate word, and is
locale-independent at the "where to look" stage. Language would still be needed to *identify* the panel
(`Uncharted Waters` / `Island Rumours`) — but that is a match that fails loudly, not a gate that fails
silently.

*To verify before committing:* is the tooltip **always** near the cursor, or can the game park it elsewhere
(e.g. clamped at a screen edge when there is no room)? Both screenshots so far show it adjacent to the
hovered node.

### Q5 — `mods` / `rating` — **ANSWERED: dropped, both**
The overlay shows **name · map · kind**. Nothing else.

Letter ratings (S+/A/B/D) were one person's opinion in a spreadsheet that also swapped two biomes and
invented a rumour that does not exist; `mods` came from the same untrusted columns. Neither is needed once
`kind` comes from the game's own data — the question a player actually has is "how many Grand Expeditions",
and `kind` answers it exactly.

Consequence, and a good one: **our dataset is complete.** Nothing to sync, nothing to keep chasing against
a community sheet, no arguments about whose rating is right, nothing that rots between patches.

## Architecture sketch

Not settled — here to be argued with.

The one lesson from the day spent on the fork: **every real bug was at the seams, not in the logic.**
The atlas gate misread its own band, the overlay's text was captured by its own OCR and fed back into the
pool, the data had holes. The logic itself was fine and unit-tested.

So the shape follows from that:

- **Core (pure, no screen).** Panel model, name resolution, pool accumulation, completeness estimate.
  Testable against real captured readings, no game required. This is where correctness lives.
- **Sensing (thin, swappable).** Screen capture + OCR + panel detection. The part that is inherently
  flaky, kept small and behind an interface so the core can be tested without it.
- **Overlay (dumb).** Renders whatever the core decided. Holds no state, decides nothing.

Hard constraints already learned the hard way:

- The overlay **must be excluded from our own capture** (`WDA_EXCLUDEFROMCAPTURE`), or OCR reads our own
  text back as game text and the pool poisons itself.
- Clicks must pass through the overlay to the game everywhere except its buttons, and it must never steal
  focus from the game.
- Everything is written from scratch: the upstream project carries **no licence**, so none of its code
  can be reused here.
