# PoE Rumours

An Atlas overlay for **Path of Exile 2** that tells you what an Uncharted Waters tile *actually* holds — above
all, **how many Grand Expeditions** are on it.

## Why it exists

The Uncharted Waters tooltip lists **three** rumours. The tile holds more than three. Which three you see is
drawn at random, and **toggling any Saga re-rolls the draw** — for free, since a Saga is only consumed when a
Logbook is charted.

So a single reading is a **sample, not the tile**. The only way to learn what a tile really has is to re-roll
and take the union of everything it has ever shown. That is what this does: hover a tile, toggle a Saga a few
times, and watch the list stop growing.

```
Fallen stars...        Moor of Fallen Skies    grand
Somethin' fishy...     Barren Atoll            grand
Endless cliffs...      Craggy Peninsula        grand
Cold as ice...         Frigid Bluffs           boss
──────────────────────────────────────────────────────
Found: 3 grand · 1 boss · 0 unique    4 distinct · 8 samples
```

It reports counts, never verdicts. It will not tell you a tile is "good", and it will not tell you when to stop
toggling — it does not know the pool size, and pretending to would be inventing a fact about the game.

## Using it

Run `PoeRumours.exe`. It lives in the tray; closing the overlay does not close the app, only **Exit** does.

1. Open the Atlas and hover an Uncharted Waters tile. The plate appears after a second.
2. **Pin the tooltip** (the game's own pin button), so the cursor is free.
3. Toggle a Saga in the inventory, hover the ship icon again — a new triple is drawn.
4. Repeat. The list grows, then stops. What it stops at is the tile's pool.

Drag the plate by its header; it stays where you put it.

| Button | |
|---|---|
| ↺ | Reset the pool. **Use it when you move to another tile** — see below. |
| 🔒 | Keep the plate on screen for the whole Atlas session, even before anything is found. |
| ✕ | Get it out of the way now. It comes back when rumours are on screen again. |

The pool resets automatically on exactly one event: **closing the Atlas**.

> **The one thing that can silently lie to you.** The pool is not tied to a tile — nothing in the tooltip
> identifies which tile it belongs to. Move to a neighbouring tile without closing the Atlas and its rumours
> are quietly added to the previous tile's, giving one merged list and a Grand count belonging to neither tile.
> Press **↺** when you change tiles. This is the tool's sharpest edge, and it is not yet solved.

## Language

The game's language is a **setting** (tray → *Settings…*), defaulting to English. It is never auto-detected: a
wrong guess would not fail loudly, it would just match nothing and show an empty list — indistinguishable from
a tile with no rumours. Changing it restarts the app.

Only `en` and `ru` are in the data so far, and Windows needs the matching OCR language pack installed
(*Settings → Time & language → Language*). If it is missing, the app says so on startup and stops rather than
running blind.

## What it needs

Windows 10 2004 (build 19041) or later, and the game in **windowed or borderless** mode — not
fullscreen-exclusive, whose screen nothing can read.

The overlay is deliberately **invisible to screen capture** (`WDA_EXCLUDEFROMCAPTURE`), because the scanner
OCRs the whole screen and would otherwise read its own plate back, feeding the rumour names it drew into the
pool as if the game had shown them. Side effect: it does not appear in screenshots or OBS. That is expected.
Tray → *Screenshot mode* turns the exclusion off for debugging, and pauses scanning in the same breath, because
the two cannot be separated.

`%LocalAppData%\PoeRumours\scan.log` records every sample, including lines the OCR could not resolve
(`?<raw text>`). If the app ever seems to be missing a rumour, that file is the place to look.

## Building

```
dotnet build                                        # needs .NET 10 SDK
dotnet test tests/PoeRumours.Tests
dotnet publish src/PoeRumours -c Release -o dist    # self-contained exe + data\
```

`tools/IconGen` rebuilds `media/app.ico` from the source SVG. Its 16/20/24px entries are deliberately not the
artwork — downscaled, that line art is mush at the size the tray actually draws — they are a hand-drawn
silhouette.

The rumour data lives in `data/*.json`, hand-edited, keyed by locale-invariant ids. Adding a language means
adding its tooltip lines there and its UI anchors to `ui-strings.json`, and nothing else.
