# Commonwealth Link

Commonwealth Link feeds the **Commonwealth Dashboard** widget on a CORSAIR XENEON EDGE (in iCUE) from **Fallout 4**'s own companion app server, when you don't have a Stream Deck. It's a small tray program: no window, no installer, no administrator rights, and it only talks to programs on your own PC.

The Stream Deck plugin **Commonwealth Deck** writes exactly the same feed and answers the widget the same way. Use whichever suits you; if you have both, see [Running it with Commonwealth Deck](#running-it-with-commonwealth-deck).

## What you need

- Fallout 4 on a Windows 10 or 11 PC (64-bit), the same PC as iCUE.
- **F4SE** (the Fallout 4 Script Extender, f4se.silverlock.org or its Steam page) and the **Pip-Boy Companion App Enabler (AE)** mod (Nexus Mods 108133, an F4SE plugin: `Data\F4SE\Plugins\PipboyAppFix.dll`). The game has a server built in for its Pip-Boy phone app; the mod turns it back on. Both are other people's work, from their own pages.
- iCUE 5.51 or later with the Commonwealth Dashboard widget on a XENEON EDGE.

## What it does

- Connects to the game's companion app server on this PC (TCP port **27000**, the one the official Pip-Boy phone app used), only while `Fallout4.exe` runs, and reconnects gently when the game closes and comes back.
- Writes `%APPDATA%\CommonwealthDeck\feed.json` once a second, and at once when something changes: health, AP, level, caps, carry weight, limbs, weapon and ammo, chems, S.P.E.C.I.A.L., perks, aid and ammo lists, quests, time, effects, power armor piece by piece, the world map with its markers, and the radio. The format is in [docs/STATE-FILE.md](docs/STATE-FILE.md).
- Serves the widget's touch controls on **localhost:52827**: the radio, a Stimpak, RadAway or Rad-X, and the custom map marker (set where you stand, or clear it). Each tap is one command to the game, the same commands the phone app sent.
- Makes the widget's map picture from your own copy of the game: the world map texture the game's Pip-Boy map uses, read from your Fallout 4 folder (read only, once per game version) and kept as `worldmap-<world>.png` beside the feed, plus the game's live local map while the widget's map is open.

The game serves one companion app at a time. If your phone's Pip-Boy app is connected, the tray says "another app has the game's one connection" and the Link asks again every 15 seconds; it never pushes the other app off.

## Install

1. Download `CommonwealthLink-1.0.0.zip` and unzip it anywhere.
2. Run `CommonwealthLink.exe` once before you place the widget in iCUE: it makes the `CommonwealthDeck` folder the widget reads. A tray icon appears; hover it to see the status ("waiting for Fallout 4", "in game").
3. Start Fallout 4 through F4SE (`f4se_loader.exe`, or your mod manager's F4SE tool). If Windows asks, allow Fallout 4 on **Private networks**.
4. Load a save and **open the Pip-Boy once**: the game starts sending its data then, and the widget fills in.

**Windows SmartScreen:** Commonwealth Link is a new program that is not code-signed yet, so Windows may show "Windows protected your PC" the first time. Choose **More info**, then **Run anyway**. You can read every line of it here first.

**Uninstall:** untick Start with Windows if you ticked it, quit it from the tray, and delete its folder. Its settings and files are in `%APPDATA%\CommonwealthDeck` (shared with Commonwealth Deck, so leave the folder if you use the plugin too).

## The tray menu

- The status line.
- **Touch controls on the widget** (on by default): untick it and the Link closes its endpoint, so the widget only shows the game.
- **Map picture from your game** (on by default): untick it and the widget draws its own grid instead.
- **Show the character's name** (off by default): only then does the feed carry your character's name.
- **Open the folder**, **Start with Windows** (a per-user startup entry, no admin) and **Quit**.

## Settings

`%APPDATA%\CommonwealthDeck\link-settings.json`, written with the defaults on first start (the tray menu changes the first three):

```json
{
  "showName": false,
  "touchControls": true,
  "mapPicture": true,
  "gameDir": "",
  "pipboyHost": "",
  "pipboyPort": 27000
}
```

- `gameDir`: your Fallout 4 folder, when the Link does not find it by itself (it looks at the game's registry key, then every Steam library).
- `pipboyHost`, `pipboyPort`: the game on another PC. Left empty, the Link connects to this PC only while Fallout 4 runs here; an address typed in is always dialled.

Restart Commonwealth Link after editing the file. Command line, for testing: `CommonwealthLink.exe [--no-tray] [--dir PATH] [--pipboy-host HOST] [--pipboy-port N] [--widget-port N] [--game-dir PATH] [--show-name] [--no-controls] [--no-map-picture] [--assume-running 0|1]`. `--dir` uses another folder instead of `%APPDATA%\CommonwealthDeck`; `--widget-port` moves the endpoint (the widget itself only knows 52827); `--assume-running` skips looking for `Fallout4.exe`. A log of what it did is kept in `link.log` beside the feed.

## Running it with Commonwealth Deck

The game has one companion app connection and the widget has one endpoint port, so only one of the two feeds the widget at a time, and Commonwealth Link always leaves it to Commonwealth Deck. Whenever Commonwealth Deck writes `feed.json` (its **Edge feed** option, on by default), the Link drops its game connection, closes its endpoint, stops writing and says "Commonwealth Deck is running; standing by". About 5 seconds after Commonwealth Deck stops writing (you quit Stream Deck), the Link takes over by itself: it connects to the game, opens the endpoint and writes the feed again.

If the Link was running first, Commonwealth Deck finds the game connection and port 52827 taken for a moment; the Link stands back within a second or two, and Commonwealth Deck connects on its next try (within 15 seconds for the game, 30 for the touch controls). Both keep the same map picture cache in the folder, so neither reads the game's archives twice.

With Commonwealth Deck's Edge feed turned off, the Link cannot tell the plugin is there: if Commonwealth Deck's keys hold the game, the Link's tray says another app has the connection. Quit the Link then, or turn Edge feed back on.

## Building

Needs the .NET 10 SDK on Windows. A trimmed self-contained single file (about 11 MB), no Native AOT, so the Windows SDK and Visual Studio's C++ tools are not needed:

`cd src` then `dotnet publish -c Release -o publish`

Run it from a console with `--no-tray --dir <a test folder>` to test without the tray icon (Ctrl+C stops it). `powershell -File tools\pack.ps1` builds and zips `dist\CommonwealthLink-<version>.zip` (the exe, this README and the licence).

The rest of `tools` is how a few files were made: `node tools/make-icon.mjs` draws the icon (`src/commonwealth.ico`), and `node tools/make-tables.mjs` writes `src/Tables.cs` (the name sort order of Node's collator, so lists sort exactly as the plugin sorts them, and the map pictures' palette).

`node tests/conformance.mjs` checks the Link against Commonwealth Deck itself: it runs the plugin's own fake game server twice, the plugin's code against one copy and `CommonwealthLink.exe` against the other, and compares the two `feed.json` files field by field (in game, at the menu, loading, dead, in and out of power armor, no data yet, busy, no app, no game, controls and map picture off), the endpoints' answers (commands, refusals, rate limits, map pictures) and the map picture from made-up game archives. It needs the Commonwealth Deck plugin's source, which is not public (set `COMMONWEALTH_DECK_DIR` to it), and Node 20 or later; without it the test says so and skips. It never touches the real game, Stream Deck or `%APPDATA%\CommonwealthDeck`: every folder is a temp folder and every port an unused one.

## Privacy

Commonwealth Link reads only what the game's companion app server sends it, and your Fallout 4 folder (read only) for the map picture. It writes only in `%APPDATA%\CommonwealthDeck`, listens only on this PC's loopback addresses, and opens no connection to the internet. The feed has no host address, save name or Windows user name; the character's name only when you tick it. The touch controls' key is new each time the Link starts and only works for the widget's four commands on this PC.

## Licence

MIT (see LICENSE). Commonwealth Link is an unofficial fan project, not affiliated with or endorsed by Bethesda Softworks, Bethesda Game Studios or ZeniMax Media. Fallout and Pip-Boy are trademarks of ZeniMax Media Inc. It contains no art or files from the game: the map pictures are made on your PC from your own copy of Fallout 4.
