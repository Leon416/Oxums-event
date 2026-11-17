# Oxum Event

A Rust server plugin that runs an event at **Oxum's Gas Station**. Patrol helicopters and/or Bradley APCs guard the
monument until players destroy them all or the time limit runs out. How many of each spawn is set per difficulty
in the config, and the plugin keeps the vehicles in the area around the gas station.

Works on [Oxide/uMod](https://umod.org/) and [Carbon](https://carbonmod.gg/). No other plugins are required.


## Contents

- [What the event does](#what-the-event-does)
- [Installation](#installation)
- [Permissions](#permissions)
- [Commands](#commands)
- [Configuration](#configuration)
- [Difficulties](#difficulties)
- [Common changes](#common-changes)
- [Changing the chat texts](#changing-the-chat-texts)
- [Good to know](#good-to-know)


## What the event does

1. **Announcement.** An automatic event is announced in chat 5 minutes before it starts, and a red circle
   appears on the map at the gas station. A shop icon in the middle of the circle shows the time: hover over
   it to read "starts in 4 minutes" and, once the event runs, "47 minutes left".
2. **Start.** The Bradleys spawn on the road next to the gas station and the helicopters fly in.
3. **Fight.** Only players inside the event zone (150 m around the gas station) are attacked.
   - A Bradley drives back and forth over a short stretch of road (60 m to each side), waits 6 to 18 seconds at
     each end and deploys scientists when it is attacked. Several Bradleys each get their own stretch.
   - A helicopter stays around the gas station. It does not fly off after its normal lifetime and does not flee
     when damaged.
4. **End.** The event ends in one of three ways:
   - **Won:** everything is destroyed. Each vehicle drops its normal loot crates.
   - **Time is up:** the remaining vehicles disappear without an explosion and without loot. A warning is sent
     5 minutes before.
   - **Stopped:** an admin stops it, or the plugin is unloaded. Everything the plugin spawned is removed.

Only one event runs at a time. After an event ends, the timer for the next automatic event starts again.

## Installation

1. Copy **only** `OxumEvent.cs` to the plugins folder of your server:
   - Oxide: `oxide/plugins/`
   - Carbon: `carbon/plugins/`
2. The plugin loads by itself. Check the server console for a line like
   `Found 1 Oxum's Gas Station(s) on this map.`
3. The config file is created on first load:
   - Oxide: `oxide/config/OxumEvent.json`
   - Carbon: `carbon/configs/OxumEvent.json`


If the console says no gas station was found, the map has no Oxum's Gas Station. Stand where you want the event
and use `/oxum setpos` (see [Commands](#commands)).

## Permissions

| Permission | What it allows |
| --- | --- |
| `oxumevent.admin` | Starting and stopping events, setting or clearing the custom event position, and the full status output. |

Players with admin rights on the server (`ownerid` or `moderatorid`) can use all commands without this
permission. The server console and RCON can always use the console commands.

Granting the permission:

| | Oxide | Carbon |
| --- | --- | --- |
| To one player | `oxide.grant user <name or SteamID> oxumevent.admin` | `c.grant user <name or SteamID> oxumevent.admin` |
| To a group | `oxide.grant group <group> oxumevent.admin` | `c.grant group <group> oxumevent.admin` |
| Taking it away | `oxide.revoke user <name or SteamID> oxumevent.admin` | `c.revoke user <name or SteamID> oxumevent.admin` |

On Carbon, run `c.grant` without arguments to see the exact syntax.

Normal players need no permission. Everyone can take part in the event and use `/oxum status`.

## Commands

### Chat

| Command | Who | What it does |
| --- | --- | --- |
| `/oxum` or `/oxum status` | Everyone | Shows whether an event is running, what is still alive and how much time is left. Admins also see the number of gas stations found, the time until the next automatic event and the command list. |
| `/oxum start` | Admin | Starts an event right away with a random difficulty (chosen by chance weight). No countdown. |
| `/oxum start <difficulty>` | Admin | Starts an event right away with that difficulty, for example `/oxum start hard`. Names are not case-sensitive. |
| `/oxum stop` | Admin | Stops the event (or cancels an announced one) and removes everything the plugin spawned. No loot. |
| `/oxum setpos` | Admin | Saves the spot where you stand as the event position. It replaces the gas station search until you clear it. |
| `/oxum clearpos` | Admin | Removes the custom position. Events take place at the gas station again. |

If an automatic event has been announced, `/oxum start` without a difficulty starts that event early, with the
difficulty and location that were announced.

### Console (server console, RCON or F1)

| Command | What it does |
| --- | --- |
| `oxum.start [difficulty]` | Same as `/oxum start`. |
| `oxum.stop` | Same as `/oxum stop`. |
| `oxum.status` | Same as `/oxum status`, always with the admin details. |

There are no console commands for `setpos` and `clearpos`, because they use the position of your character.

## Configuration

Edit the config file, save it, then reload the plugin:

- Oxide: `oxide.reload OxumEvent`
- Carbon: `c.reload OxumEvent`

Reloading stops a running event and removes everything it spawned. Settings added by a newer plugin version are
written into your existing file with their default value; your own values are kept.

### Default config

```json
{
  "Automatic start enabled": true,
  "Automatic start: minimum minutes between events": 120.0,
  "Automatic start: maximum minutes between events": 180.0,
  "Automatic start: minimum online players": 1,
  "Announce an automatic event this many minutes before it starts (0 = start at once)": 5.0,
  "Warn this many minutes before the time limit (0 = no warning)": 5.0,
  "Event zone radius in metres (only players inside are attacked)": 150.0,
  "Helicopter leash radius in metres (sent back when further away)": 300.0,
  "Bradley road length in metres to each side of the gas station": 60.0,
  "Maximum distance in metres between the gas station and a road": 80.0,
  "Bradleys deploy scientists": true,
  "Block event damage to player bases": false,
  "Show a marker on the map": true,
  "Map marker radius (map units, not metres)": 0.5,
  "Show the time left on the map (shop icon in the middle of the circle)": true,
  "Chat prefix": "<color=#ff6b35>[Oxum's Event]</color> ",
  "Custom event position (set with /oxum setpos; replaces the gas station search)": null,
  "Difficulties": [
    {
      "Name": "Easy",
      "Bradleys": 1,
      "Helicopters": 0,
      "Chance weight (0 = only with the admin command)": 40.0,
      "Time limit in minutes": 30.0,
      "Loot crates per Bradley": 3,
      "Loot crates per helicopter": 4
    },
    {
      "Name": "Medium",
      "Bradleys": 1,
      "Helicopters": 1,
      "Chance weight (0 = only with the admin command)": 30.0,
      "Time limit in minutes": 40.0,
      "Loot crates per Bradley": 3,
      "Loot crates per helicopter": 4
    },
    {
      "Name": "Hard",
      "Bradleys": 2,
      "Helicopters": 2,
      "Chance weight (0 = only with the admin command)": 20.0,
      "Time limit in minutes": 50.0,
      "Loot crates per Bradley": 3,
      "Loot crates per helicopter": 4
    },
    {
      "Name": "Extreme",
      "Bradleys": 3,
      "Helicopters": 3,
      "Chance weight (0 = only with the admin command)": 10.0,
      "Time limit in minutes": 60.0,
      "Loot crates per Bradley": 3,
      "Loot crates per helicopter": 4
    }
  ]
}
```

### Settings

**Automatic start**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Automatic start enabled` | `true` | `false` = events only start with the admin command. |
| `Automatic start: minimum minutes between events` | `120` | Shortest wait before the next automatic event. Lowest allowed value: 1. |
| `Automatic start: maximum minutes between events` | `180` | Longest wait. The plugin picks a random time between minimum and maximum. Set both to the same number for a fixed interval. |
| `Automatic start: minimum online players` | `1` | With fewer players online the event is postponed and the plugin checks again every 5 minutes. `0` = also start on an empty server. |
| `Announce an automatic event this many minutes before it starts` | `5` | Countdown between the chat announcement and the start. `0` = start at once. Events started by an admin never have a countdown. |
| `Warn this many minutes before the time limit` | `5` | Chat warning before the time runs out. `0` = no warning. |

**Area**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Event zone radius in metres` | `150` | Helicopters and Bradleys of the event only attack players inside this circle around the gas station. Lowest allowed value: 30. |
| `Helicopter leash radius in metres` | `300` | A helicopter heading further away than this is sent back. Cannot be smaller than the event zone. Keep it well above the zone radius: a helicopter starts a rocket run almost 200 m from its target. |
| `Bradley road length in metres to each side of the gas station` | `60` | How far a Bradley drives along the road in each direction. With several Bradleys this stretch is divided between them. Lowest allowed value: 10. |
| `Maximum distance in metres between the gas station and a road` | `80` | How far from the event position the plugin looks for a road. If there is no road within this distance, the Bradleys stay where they spawn and act as turrets. |

**Behaviour**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Bradleys deploy scientists` | `true` | `false` = event Bradleys never deploy scientists. |
| `Block event damage to player bases` | `false` | `false` = bases near the gas station take normal damage from the event. `true` = during an event and for 2 minutes after it, damage that does not come from a player is blocked for player-built or player-placed objects within the leash radius + 50 m. Decay and damage by players are never blocked. |

**Display**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Show a marker on the map` | `true` | Red circle on the map from the announcement until the end. |
| `Map marker radius (map units, not metres)` | `0.5` | Size of the circle. Change it if the circle looks too small or too large on your map. |
| `Show the time left on the map (shop icon in the middle of the circle)` | `true` | Adds a shop icon in the middle of the circle. Hovering over it shows the difficulty and the time until the start or until the time limit. It updates once a minute, and every 2 seconds in the last minute. Needs `Show a marker on the map` to be on. |
| `Chat prefix` | `<color=#ff6b35>[Oxum's Event]</color> ` | Text in front of every chat message of the plugin. Rust colour tags are allowed. |

**Position**

| Setting | Default | Meaning |
| --- | --- | --- |
| `Custom event position` | `null` | Filled in by `/oxum setpos` and emptied by `/oxum clearpos`. Use the commands rather than typing coordinates. |

## Difficulties

Each entry in `Difficulties` is one difficulty. You can change the four default ones, remove some or add your own.

| Field | Meaning | Allowed |
| --- | --- | --- |
| `Name` | Name shown in chat and used in `/oxum start <name>`. Use one word without spaces. | Any text |
| `Bradleys` | Number of Bradley APCs. | 0 to 10 |
| `Helicopters` | Number of patrol helicopters. | 0 to 10 |
| `Chance weight` | How often an automatic event picks this difficulty, compared to the others. | 0 or higher |
| `Time limit in minutes` | Time players get to destroy everything. | 1 or higher |
| `Loot crates per Bradley` | Crates a destroyed Bradley drops. | 0 to 12 |
| `Loot crates per helicopter` | Crates a shot-down helicopter drops. | 0 to 12 |

**How the chance weight works.** The chance of a difficulty is its weight divided by the sum of all weights. The
defaults (40, 30, 20, 10) add up to 100, so they read as percentages: Easy 40%, Medium 30%, Hard 20%,
Extreme 10%. The weights do not have to add up to 100. A difficulty with weight `0` is never picked
automatically and can only be started by name with the admin command.

A difficulty with 0 Bradleys and 0 helicopters cannot be started. If the list is empty or missing, the plugin
falls back to the four default difficulties.

Example of an extra difficulty that only admins can start:

```json
{
  "Name": "Nightmare",
  "Bradleys": 10,
  "Helicopters": 10,
  "Chance weight (0 = only with the admin command)": 0.0,
  "Time limit in minutes": 90.0,
  "Loot crates per Bradley": 5,
  "Loot crates per helicopter": 6
}
```

Add it inside the `[ ]` of `Difficulties`, with a comma after the entry before it, and start it with
`/oxum start boss`.

## Common changes

| I want… | Change |
| --- | --- |
| Events only when I start them | `Automatic start enabled` → `false` |
| An event every hour | Minimum and maximum minutes both `60` |
| No events on a nearly empty server | `Automatic start: minimum online players` → for example `4` |
| Only helicopters, no Bradleys | Set `Bradleys` to `0` in every difficulty |
| No scientists | `Bradleys deploy scientists` → `false` |
| Bases near the gas station kept safe | `Block event damage to player bases` → `true` |
| The Bradley to cover more road | Raise `Bradley road length in metres to each side of the gas station` |
| The event somewhere else | Stand there and use `/oxum setpos` |
| No circle on the map | `Show a marker on the map` → `false` |
| The circle but no shop icon with the time | `Show the time left on the map (shop icon in the middle of the circle)` → `false` |

## Changing the chat texts

All texts players see are in the language file, created on first load:

- Oxide: `oxide/lang/en/OxumEvent.json`
- Carbon: `carbon/lang/en/OxumEvent.json`

Edit the texts and reload the plugin. For another language, copy the file to a folder with that language code
(for example `oxide/lang/nl/OxumEvent.json`) and translate it; players with that game language then get those
texts. Keep the placeholders `{0}`, `{1}` and `{2}` in place: the plugin fills in names, numbers and times there.

The text on the shop icon (`Marker.Announced` and `Marker.Running`) is the same for every player, so it always
comes from the language file of the server's default language.

## Good to know

- **Loot** is only the normal crates of each destroyed vehicle. There is no bonus crate.
- **The regular patrol helicopter and the Bradley at Launch Site** are not touched by this plugin. The crate
  settings only apply to event vehicles: with the setting at 1 an event Bradley dropped 1 crate on the test
  server, while the Launch Site Bradley still dropped its normal 3.
- **At night** a helicopter only sees players within 40 m of the gas station. That is behaviour of the game itself.
- **Several gas stations** on the map: each event picks one at random.
- **Nothing is left behind.** Event vehicles are not written to the server save, so after a restart during an
  event nothing of it remains. Unloading the plugin removes Bradleys, helicopters, scientists and the map marker.
  A helicopter that is already crashing is left alone, so it still drops its crates.
- **Bradleys do not chase players.** They only drive their stretch of road.
- **A helicopter can leave the area for a moment** when it is shot from far away. It comes back by itself.