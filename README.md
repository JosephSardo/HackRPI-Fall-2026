# PathFinder

**Race your friends across real city streets.** PathFinder is a LAN party game for Windows. Each round
drops everyone on the same real map from OpenStreetMap, with a start and a destination, and gives you a
ticking clock. Click your way from street to street, and the shortest route wins. No accounts and no
server: one laptop hosts and everyone else types a short game code.

Built with C#, .NET 10 and Avalonia for HackRPI Fall 2026.

## Running it

### The ready-made .exe (no .NET needed)

Build it once on any machine with the .NET 10 SDK:

```powershell
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

This produces a single self-contained `publish\PathFinder.exe` (about 100 MB). Copy that one file to any
64-bit Windows laptop and double-click it. (`publish/` is git-ignored.)

### From source

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then:

```powershell
dotnet run --project src/PathFinder
```

If you work in WSL, use the Windows `dotnet.exe`, since the app is a Windows desktop app.

## Hosting and joining

1. **Host:** enter your name and click **Host a game**. The lobby shows a **game code** such as
   `K3QZ-7TB`. Read it out or click **Copy**.
2. **Join:** everyone else enters their name, types the code into the join box, and clicks **Join**.
   The code is just the host's IPv4 address in disguise, so all laptops must be on the same network.
   The (i) button next to the code shows the raw IP addresses, which also work in the join box.
3. **Firewall:** the first time you host, Windows asks whether to allow PathFinder on the network.
   Click **Allow** (private networks are enough). If other players can't connect, this is almost always the
   cause. The game uses TCP port 5050.
4. **Pick a place:** click a preset (**RPI campus**, **Downtown Troy**, **Downtown Albany**), type
   `latitude, longitude`, or use **Pick on map**. Set the rounds, seconds per round and radius, then click
   **Start match**.

**Testing on one laptop:** start the app twice. Host in one window, then in the other type `127.0.0.1`
into the join box.

**Playing alone:** tick **Practice (solo allowed)** in the lobby and the host can start a competitive
match without anyone else. This is handy for demos. It's off by default.

## Game modes

- **Competitive (default):** every player gets the same start and destination. Build your route by
  clicking intersections, and submit once before the timer runs out. The shortest route scores 100 and longer ones score less, and
  players are ranked by score each round and across the match. If you don't submit in time, you score 0
  for that round.
- **Cloud Navigator (co-op):** tick *Cloud Navigator mode* in the lobby. One player is the
  **navigator**, who sees the whole map from above but can't drive. The other is the **driver**, who
  sees only the street they're on and the exits at each intersection. The navigator talks the driver
  to the destination before the clock runs out. Players choose with *I'll navigate* or *I'll drive*.
  This mode needs two players.

## Where the maps come from

Maps are real streets from **OpenStreetMap**, downloaded by the host when a match starts:

1. **Saved copies:** every map the host downloads is saved to `%LOCALAPPDATA%\PathFinder\maps`, so the
   same place and radius load instantly next time, even offline. Delete that folder to force fresh
   downloads.
2. **Download:** for small areas the app asks the OpenStreetMap API first, which takes about a second.
   For larger areas, or if that fails, it tries several public Overpass servers.
3. **Bundled demo maps:** RPI campus, downtown Troy and downtown Albany ship inside the app
   (`src/PathFinder/Assets/Maps/`). The lobby presets use them directly, so they need no internet. If a
   download fails anywhere within about 1 km of one of them, the host uses that map and says so in the
   lobby.
4. **Test grid:** if all of that fails, the game is played on a built-in grid map, so a match can
   always start.

Only the host downloads. It sends a snapshot of the map to every player, so everyone plays on an
identical map.

### Attribution

OpenStreetMap data is © OpenStreetMap contributors, available under the
[Open Database Licence](https://www.openstreetmap.org/copyright). The licence requires that credit to be
visible wherever the map is shown. The credit travels with every real map (`CityMap.Attribution` and
`MapData.Attribution`, including the bundled maps and saved copies) and is drawn on the game screen.
Keep it there. The satellite imagery in the map picker shows its own credit too.

## Architecture (brief)

```
src/PathFinder/
  Network/     GameHost (authoritative), GameClient, messages, match rules, map loading/caching
  Loading/     OpenStreetMap download (OSM API / Overpass) and street graph building
  Services/    MapBuilder (the entry point for real maps), pathfinding, scoring
  Models/      CityMap, Intersection, Street (a QuikGraph graph)
  ViewModels/  MVVM view models (CommunityToolkit.Mvvm)
  Views/       Avalonia views
tools/         NetCheck.cs (LAN connectivity check), publish.ps1
```

- **The host owns the state.** The host laptop runs a `GameHost` TCP server and joins it with its own
  `GameClient`, just like everyone else. Clients only send requests (start the match, submit a route,
  drive to an intersection). The host validates everything against its own map, scores the routes, runs
  the timers and broadcasts the results.
- **One event loop.** Everything that happens to the host goes through a single queue: messages,
  connects and disconnects, timers and map changes. The match rules (`Match`, `NavigatorMatch`) need no
  locks or threads. They take an event and return the messages to broadcast.
- **The wire format:** one JSON object per line over TCP, each with a `type` field. `Protocol.Version`
  is checked when a player joins, so mismatched builds are turned away with a clear message.
- **`MapData` snapshots.** When a match starts, the host sends its canonical map as a plain `MapData`
  snapshot of intersections, streets, lengths and attribution. Each round then only sends its start and
  end IDs. The same format is used for saved maps and for the bundled demo maps.

## Team workflow

- Branch from `main` with a prefix: `Feature/short-name`, `Bugfix/short-name`, and so on.
- Open a pull request into `main`. **CI auto-merges every non-draft PR** once it's clean and builds.
  The workflow merges `main` into the PR, builds it in Release (the app plus `tools/NetCheck.cs`), and
  squash-merges it.
- **To hold a PR back,** open it as a **draft** or add the **`do-not-merge`** label. It still gets synced
  and built, but not merged. PRs that conflict or fail to build get a `merge-conflict` or `build-failed`
  label and a comment.
- No test projects (hackathon rule). Verify changes by running the app.
