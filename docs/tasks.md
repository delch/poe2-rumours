# Task breakdown

Derived from `epic-app.md` (R1–R7, Q1–Q7). Ordered so that the things everything else rests on are proved
first, and so that as much as possible is testable **without launching the game**.

---

## M0 — Skeleton

**T0.1 — Solution and projects**
`PoeRumours` (WinExe, WinForms, net10.0-windows10.0.19041.0) + `PoeRumours.Tests` (xunit).
*Done when:* both build, one trivial test passes.

---

## M0.5 — Spike — **DONE, all checks pass**

Run on 2026-07-14 against the live game (`spikes/OverlaySpike`):

```
A. capture exclusion .... PASS — plate visible on screen, absent from the capture
B1. body click-through .. PASS — a click on the body reaches the window underneath
B2. header grabbable .... PASS — the header strip catches the mouse
C. game capture ......... PASS — 93.9% of sampled pixels non-black (3440x1440)
```

**The big one is C.** PoE 2 renders on Vulkan and plain GDI capture returns black for some fullscreen
renderers — which is presumably why the predecessor carries a D3D11 stack (Vortice.Direct3D11 + Vortice.DXGI
and a backend abstraction around them). It turns out `Graphics.CopyFromScreen` sees the game fine, so
**the entire capture layer collapses into a few lines of `System.Drawing`** — no D3D, no extra packages, no
backend switching. (True for borderless; exclusive fullscreen would break it, and that is already a
non-goal.)

B1/B2 were verified with `WindowFromPoint`, which performs the same hit-test the mouse does — strong
evidence, but not identical to a human clicking. Confirm by hand once the real overlay exists (M3).

*Original brief, kept for context:*

The whole overlay design rests on two beliefs about Windows. If either is false, R7 collapses and we would
rather find out in an afternoon than at the end.

**T0.5.1 — Capture exclusion**
A stub layered window with `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`.
*Done when:* the window is visible on screen, and a capture of the desktop (the same path our scanner will
use) **does not contain it**. This is what stops the overlay from being OCR'd back into its own pool.

**T0.5.2 — Click-through and drag, plate-sized**
A stub window the size of a plate, `WS_EX_NOACTIVATE`, hit-test returning `HTTRANSPARENT` on the body and
`HTCAPTION` on a header strip.
*Done when:* clicks on the body reach the app underneath (verify against a real window, not a mock), the
game never loses focus, and dragging the header moves the window.

*Risk being retired:* this was the single most dangerous part of the predecessor — a full-screen overlay
that hand-rolled click-through across the entire desktop. A plate-sized window removes the risk by
construction, but the primitives still have to behave as documented.

---

## M1 — Core: pure logic, no screen

Everything here is deterministic and unit-tested against **real OCR output captured from the game**, not
invented strings. No game needed to run these tests.

**T1.1 — Data loading and validation**
Load `rumours.json` + `ui-strings.json` into typed models.
*Done when:* the loader **refuses to start** on bad data — duplicate ids, unknown `kind`, a locale missing a
rumour string, a `ratingScale` violation. Bad data must fail loudly at startup, not silently produce a wrong
count later.

**T1.2 — Name resolution**
Normalise an OCR line (case-fold, strip the trailing ellipsis, collapse whitespace, tolerate mangled
apostrophes) and match it against the closed 20-string vocabulary of the selected locale.
*Done when:* the real, observed garbling resolves — `"now' to drink.."` → `Nothin' to drink`,
`"Waru but risklå..."` → `Warm but risky`, `"Lt's at least..."` → `It's dry at least` — and an unknown line
resolves to *nothing* rather than to the nearest wrong answer.

**T1.3 — Panel model**
From a set of OCR lines, produce the 1–3 rumour rows.
*Done when:* boilerplate is filtered (`Consumes:`, the logbook hint, the title, the section header, the item
name), **duplicate lines are collapsed** (OCR reads a line twice: once clean, once garbled), and a 1- or
2-row panel is accepted as legitimate rather than treated as a failed read.
*Note:* the predecessor never filtered `Consumes:`, which left its "unknown rumour" indicator lit on every
tile forever.

**T1.4 — Tile pool**
Accumulate rumours across samples. A sample counts **when the displayed set changes** (R4). Track per-rumour
`Seen`. Expose counts by `kind`. Explicit reset.
*Done when:* the real seven-toggle observation reproduces — 7 samples, 3 shown each, **5 distinct** found —
and re-reading an unchanged panel does not inflate anything.

---

## M2 — Sensing

**T2.1 — Screen capture** behind an interface, so the core stays testable without a screen.

**T2.2 — OCR**
`Windows.Media.Ocr` with the language from settings.
*Done when:* a missing recognizer for the selected language is reported **out loud** (R3), never as an empty
result. Enumerate `AvailableRecognizerLanguages` at startup.

**T2.3 — Atlas gate**
Several plain-text anchors OR'd together (search box, act tabs, banner) — **not** the stylised
`World` / `Мир` word alone.
*Done when:* the gate still holds when the rumour panel **covers the search box** (observed: it does).

**T2.4 — Scan loop**
Gate → detect → sample → publish. Throttled. Diagnostic log whose header carries the app version (R2).
*Done when:* a pinned panel toggled repeatedly produces one sample per re-roll — the case that would have
silently produced exactly one sample forever under a "sample per panel opening" rule.

---

## M3 — Overlay

**T3.1 — The window**
Plate-sized, layered, `WS_EX_NOACTIVATE`, capture-excluded. Builds directly on M0.5.

**T3.2 — Rendering**
Rows: name · map · kind · `Seen`. Footer: counts by kind — *"2 boss · 3 expedition · 1 unique"*. Sorted by
`rating` (ours), not by `kind`. **Counts only — no verdicts** (Q2).

**T3.3 — Placement**
Drag by the header (`HTCAPTION`); position persisted. Never moves on its own.

**T3.4 — Buttons**
✕ close · 🔒 lock (hold while the Atlas is open) · ↺ reset.
Pool auto-resets **only** when the Atlas closes. A reading that overlaps nothing **asks** — it never resets
silently (R6).

---

## M4 — Shell

**T4.1 — Tray** — icon, menu, close/minimise → tray, explicit exit (R1).
**T4.2 — Settings** — language, overlay position, lock default.
**T4.3 — Version** — read from the assembly; shown in the UI and in the log header. Never hand-typed (R2).
**T4.4 — Icon** — multi-size `.ico`; a simplified silhouette at 16/24 or it is a smudge in the tray (R5).

---

## Ordering rationale

- **M0.5 first** because M3 and the pool's integrity both depend on OS behaviour we have assumed but not
  proven on this machine.
- **M1 before M2** because the core is where correctness lives and it can be fully tested without the game.
  Every real bug we hit in a day on the predecessor was at a **seam** — the gate misreading its own band, the
  overlay being read by its own OCR, the data having holes. The logic was fine. So: build the logic on solid
  tests, then approach the seams carefully, one at a time.
- **M3 after M2** so the overlay renders something real rather than a mock.
