# The feed file

`%APPDATA%\CommonwealthDeck\feed.json`: what Commonwealth Link (or the Commonwealth Deck Stream Deck plugin) knows about the game, for the Commonwealth Dashboard widget in CORSAIR iCUE on a XENEON EDGE. iCUE widgets can read files in a folder under the user's home, and the folder must exist before the widget is added, so the Link creates it as soon as it starts.

## One format, two writers

The format is Commonwealth Deck's Edge feed, format number `1` (`"feed": 1`), described field by field in the plugin's `docs/STATE-FILE.md`. The Link writes the same fields, statuses and values, in the same order, for the same game: `tests/conformance.mjs` runs the plugin's own code and the Link against the same made-up game and compares the two files field by field (and the JSON text byte for byte, once the fields below are set alike). A reader written for Commonwealth Deck's feed reads the Link's unchanged.

What differs, and only this:

| field | Commonwealth Link | Commonwealth Deck |
|---|---|---|
| `app` | `"Commonwealth Link"` | `"Commonwealth Deck"` |
| `version` | the Link's version, `"1.0.0"` | the plugin's version |
| `seq`, `at` | its own counter and write time | its own |
| `controls.key` | its own session key, new each start | its own |
| `sources.bridge` | always `{ "state": "off", "version": null }`: the Link reads only the game's companion app server, never the optional F4SE bridge | the bridge's state |

So the Link's statuses are the plugin's, less the bridge's: `ok`, `menu`, `loading`, `dead`, `nodata`, `no-game`, `no-app`, `busy`; `source` is `"pipboy"` or `null`. A reader that needs to know the writer looks at `app`; one that needs a feature looks for its field, as the Commonwealth Dashboard widget does (`controls` for the touch controls, `mapPicture` for the map pictures, `flags.powerArmor` and `armor` for the power armor page), never at a version number.

## How it is written

- Whole file, UTF-8 without a BOM, one JSON object, the last key always `"end": true`. A reader skips a file that does not parse or lacks `"end": true` and keeps the last whole copy.
- Atomic: written to `feed.json.tmp` and renamed over `feed.json` (a plain rename: `File.Replace` can leave `feed.json~RF*.TMP` files behind when a reader holds the file); if a reader holds it, two more tries 5 ms apart, then the file is rewritten in place in one write.
- Once a second, and at once (at most four times a second) when something changes. A reader that sees no write for 10 s knows neither writer is running.
- Not at all while Commonwealth Deck writes it: the Link stands by (README.md, "Running it with Commonwealth Deck").

## The endpoint

`controls` says how the widget reaches the touch controls and the map pictures: `localhost:52827` (127.0.0.1 and ::1), `GET /hello`, `POST /command` with the header `X-Commonwealth-Deck: <key>`, `GET /map/world.png` and `GET /map/local.png`, with the same checks, limits and answers as the plugin's (also checked by `tests/conformance.mjs`). `GET /hello` answers `"app": "Commonwealth Link"`.

## Beside it

- `link-settings.json`: the Link's settings (README.md, "Settings").
- `link.log`: what the Link did (connections, the map picture, standing by), kept under 512 KB.
- `worldmap.json` and `worldmap-<world>.png`: the map picture cache, the same files the plugin keeps; either can use the other's.
- `state.json` and `command.json`: the optional F4SE bridge's own files, which the Link never reads or writes.
