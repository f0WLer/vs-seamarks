# Seamarks: Beacons & Lighthouses

A Vintage Story mod that adds buildable lighthouse beacons for marking coastlines, harbors and
landmarks over long distances, including well beyond normal render and simulation distance.

## Beacons

**Fire beacon**: a stone-and-metal brazier, lit with a torch or firestarter. It burns any
combustible item (firewood, coal, charcoal) for fuel, throws light in every direction, and goes out
on its own if left standing in heavy rain.

**Oil lamp beacon**: a single-block Argand lamp fed with oil from any container and lit with rmb. It faces the way it was placed and throws a directional beam. Fit it with an
Optics Housing and drive it with mechanical power and it becomes a rotating lighthouse beacon.

## The Optics Housing

The housing is a separate block that attaches onto an oil lamp beacon and decides what its light
actually does. It holds up to three **Lens Panels**, and each fitted panel becomes one beam, spaced
evenly around the optic so a driven lamp flashes a viewer once per beam per revolution.

A panel's type sets its beam's angular width, which is also how long each flash lasts:

| Lens Panel | Beam width |
|---|---|
| Type 1 | 4° |
| Type 2 | 10° |
| Type 3 | 16° |
| Type 4 | 20° |

Panels come in clear, red, green and amber glass, and a colored panel tints its beam. Fitting
any panel also focuses the lamp's sideways glare into the beam, which extends its range and stops it
blinding anyone standing next to it.

Right-clicking a placed housing with a wrench opens its slots. Ctrl+Shift+right-click picks it back up with its panels and tuning intact, and it keeps that configuration through breaking,
dropping and replacing.

### Reflector plates

Right-click an oil lamp with a gold, silver or electrum plate to attach up to four of them. They all
have to be the same metal. Electrum reflects best and gold worst, with diminishing returns per plate.
Shift+right-click removes one.

## Mechanical power

An oil lamp senses a turning shaft directly beneath it (i.e. from a windmill) and
sweeps its beams while the shaft turns. The sweep rate follows the shaft's speed.

The **Governor** is a flyball speed regulator that sits in the shaft line and caps the speed passed
upward, so a gusting windmill still drives the light at a steady, chosen rate. Right-click it to set
the ceiling. Its head spins with the incoming shaft and its weights ride further out the faster it
turns.

## Glassmaking integration (optional)

With [Glassmaking](https://mods.vintagestory.at/glassmaking) installed, Lens Panels get a full
survival production chain: melt optical glass, cast a lens blank, anneal it, and finish it on the
glassworking bench. Without that mod the chain's intermediate items are hidden and a simpler grid
recipe for the panels takes over instead, so the mod is fully playable either way.

## The far-render blip

A beacon's light shows from much farther away than the game normally simulates, drawn as a small
glowing marker that fades in near the edge of its range instead of popping in and out. Range follows
a rough lighthouse-geometry formula (mounting height above sea level, plus whatever the fixture,
plates and housing add), and is configurable per tier.

The server only ever sends each player the beacons currently inside their own maximum possible range,
diffed as they move, so a map with many beacons doesn't have every client tracking all of them.

Lit beacons also appear on the world map under a "Beacons" layer, flashing in step with their beams.

## Commands

| Command | Privilege | What it does |
|---|---|---|
| `/seamarkseternal [on\|off]` | `controlserver` | Pins the beacon you're looking at to burn forever, taking no fuel. With no argument it toggles. |
| `/seamarksbeamcolor <beam> <color>` | `chat` | Recolors one beam of the Optics Housing you're looking at, placed or fitted. Beam 0 sets them all. Takes a name, a hex code like `ff8800`, or `reset`. |

## Configuration

Server admins can tune the mod through `ModConfig/seamarksbeacons.json`, written on first run. With
[ConfigLib](https://mods.vintagestory.at/configlib) installed, most of it is editable in-game and
takes effect immediately; otherwise edits apply on the next server start.

Both tiers share a range model:

- `BaseRangeBlocks`: reach with the beacon sitting at sea level.
- `RangePerElevationBlock` and `ElevationBonusStart`: extra reach per block of tower above a given
  height, so a taller lighthouse sees further.
- `FalloffStartFraction`: how much of the max range stays full brightness before it starts fading.
- `RequiresFuel`: turn it off and the beacon lights empty and never burns down.
- `BlipSizeScale`: multiplier on how large the distant marker draws.

Fire adds `FuelBurnTimeMultiplier`, `MaxFuelHours` and `RainDouseEnabled`. The oil lamp adds
`MaxFuelLitres`, `FuelHoursPerLitre`, `HousingRangeBonus`, `PlateRangeBonusPerMetal` and the two
`VisualBeamLength` values.

A few settings sit outside the tiers:

- `SpinTracksWorldTime`: whether a driven optic turns on world time. On, it stops when the world
  clock does and races when that clock is sped up. Off, it holds the rate a default world would give
  it, so a frozen or fast-forwarded world still has working lighthouses. Fuel always burns on world
  time either way.
- `ShowBeaconsOnMap`: whether lit beacons are sent to the map layer.
- `ExtraOilCodes` and `BlockedOilCodes`: the lamp burns any liquid whose code contains `oilportion`,
  which already covers vanilla, Vintage Engineering and Expanded Foods. These name the exceptions.
  Wildcards work, and blocked wins over everything.
- `FuelHoursPerLitreOverrides`: burn hours per litre for specific oils, overriding the flat rate.
- `OilLampItemFuelHours`: solid items the lamp will accept as fuel, mapped to the hours one item
  adds. Empty by default, so out of the box the lamp drinks only oil. `"game:fat-rendered": 3.0` is
  the case it was built for.

The client never reads this file. It receives the effective values over the network, so client and
server can't disagree about range or beam length even on separate installs.

## Building from source

Requires a local Vintage Story install for the game API assemblies, and the .NET SDK matching the
game's target framework.

```
cp .env.example .env                            # fill in VINTAGE_STORY_PATH, MODS_PATH, DEBUG_DATA_PATH
dotnet build Seamarks/seamarks.csproj -c Debug   # builds, zips, and copies into MODS_PATH for testing
./build.ps1                                      # release build, zipped into Releases/
```

Opening the folder in VS Code with `.env` already filled in is enough for F5 debugging: a task
regenerates `.vscode/launch.json` from `.env` automatically on open (VS Code may ask once to allow
it), so the game paths never need editing by hand. Rerun the "sync launch.json from .env" task after
changing `.env` later.

## Project layout

```
Seamarks/Beacon/          block, block entity, and shared tuning/visibility logic
Seamarks/Beacon/Client/   rendering, client-side beacon registry, map layer, gaze effect
Seamarks/Beacon/Server/   server registry, per-player visibility tracking, config
Seamarks/Mechanics/       the Governor and its mechanical-power node
Seamarks/Glassmaking/     optional Glassmaking compatibility shim
Seamarks/assets/seamarks/ block and item definitions, recipes, shaders, sounds, lang
CakeBuild/                the release packaging build
```

## Compatibility note

The far-render blip raises the camera's far clip plane, which needs one type from the game's
`Vintagestory.Client.NoObf` namespace. That is the mod's most version-sensitive line, so a game
update that moves it will show up here first.

## License

MIT. See [LICENSE](LICENSE).
