# SE2GameAPI

**SE2GameAPI** is an open-source Space Engineers 2 plugin that exposes the game world —
grids, devices, inventories and a virtual autopilot — to external automation: AI agents,
bots, scripts and dashboards. The plugin runs inside the game, publishes telemetry to a
local message broker and executes confirmed device commands. Many external clients
("robots") can play the game through one shared API.

```
┌─────────────┐   Redis (localhost)   ┌──────────────────────────┐
│  SE2 game   │◄─────────────────────►│  external clients        │
│  + plugin   │   state / commands    │  (python agents, tests,  │
│  (in-proc)  │   replies (receipts)  │   dashboards, bots, ...) │
└─────────────┘                       └──────────────────────────┘
```

Transport today is a trusted **localhost Redis** (key prefix `se2:local`). The transport is
an implementation detail of the gateway — the client-facing contract is the command/receipt
protocol, not Redis itself.

## Features

- **World catalog**: every grid and 20+ device types (lamps, doors, connectors, cargo,
  thrusters, gyroscopes, batteries, tanks, reactors, assemblers, refineries, cockpits,
  landing gear, gravity generators, antennas, conveyors, ...) discovered and streamed as
  typed telemetry with per-device freshness (`measuredAtMs`).
- **Confirmed commands**: enable/disable, door open/close, connector connect/disconnect,
  thrust override, gravity, broadcast, landing-gear lock, battery/tank modes. Every command
  returns a receipt with the actual game state — never a transport-level "subscriber count".
- **Virtual autopilot** (`autopilot` device per grid): fly to a world position with a final
  orientation, speed/braking planner, gyro control, mission timeout, `stop()`.
  - `alignCourse` — keep the nose pointed along the course to the target for the whole
    flight (measured in-game: 0.0–0.5° deviation).
  - `keepControlOnPlayer` (default true) — a player entering a seat does not cancel the
    mission.
  - Precise arrival: position tolerance down to 0.05 m (verified: return to a captured
    pose within 0.12–0.14 m and 0.03°).
- **Connector docking**: world-space mating pivot telemetry
  (`pivotPosition/pivotForward/pivotUp`), engine alignment flag, synchronous `TryConnect`.
  The companion Python module performs full park/unpark cycles (verified in-game: undock,
  40 m back-off, return, dock `connected: true`).
- **Safety model**: per-session epochs (devices must be rediscovered after restart), stale
  telemetry rejection, command dedup by request id, optimistic conflict check
  (`expectedRevision`), honest "unknown outcome" timeouts.

The Python client lives in the [`secontrol`](https://github.com/rootfabric/secontrol)
repository (`secontrol.se2`, `secontrol.se2_navigation`, `secontrol.se2_parking`) — but any
client speaking the Redis protocol below works.

> **The reference client is [`secontrol`](https://github.com/rootfabric/secontrol).**
> It is consumed **directly from the git repository — there is no pip package and none is
> required.** secontrol also contains the full operator playbook, device skills and mission
> scripts this API was built for (see the section *Python client (secontrol)* below).

## Requirements

- Windows with [Space Engineers 2](https://store.steampowered.com/app/1068430/) installed.
- [Pulsar 2.4.3+](https://github.com/SpaceGT/Pulsar/releases) mod loader (Windows x64).
- .NET SDK 10 (`dotnet` in PATH or portable under `artifacts/tools/dotnet`).
- A local Redis 6+ (Docker is the easiest way).
- Python 3.11+ only if you use the `secontrol` client (cloned from its git repository;
  no pip package).

## Deployment

### 1. Get the code

```powershell
git clone https://github.com/rootfabric/SE2GameAPI.git
cd SE2GameAPI
```

### 2. Install portable tools (optional but recommended)

`build.ps1` falls back to `dotnet` from PATH if the portable SDK is absent.

```powershell
# Pulsar loader: download the Windows x64 zip from the releases page and unpack to:
#   artifacts\tools\Pulsar\Modern.dll (+ loader files next to it)

# Portable .NET 10 SDK:
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile dotnet-install.ps1
.\dotnet-install.ps1 -Channel 10.0 -InstallDir "$PWD\artifacts\tools\dotnet" -NoPath
```

### 3. Build the plugin

```powershell
.\build.ps1 -Game2 "C:\Path\To\SteamLibrary\steamapps\common\SpaceEngineers2\Game2"
```

Output: `artifacts\plugin\SE2GameAPI.dll` (plus dependency DLLs).

### 4. Run Redis

The plugin needs a Redis 6+ reachable at `redis://127.0.0.1:6379/0` (localhost only).
Pick any option:

**Option A — Docker Desktop** (easiest if you already have it):

```powershell
docker run -d --name se2-game-api-redis -p 127.0.0.1:6379:6379 redis:7
```

**Option B — native Windows build** (no Docker; e.g.
[redis-windows](https://github.com/redis-windows/redis-windows) or Memurai):

```powershell
# download a Windows redis-server binary, then:
.\redis-server.exe --port 6379 --bind 127.0.0.1 --protected-mode yes
```

**Option C — WSL2**:

```powershell
wsl --install -d Ubuntu   # if WSL is not installed yet
wsl sudo apt-get update && wsl sudo apt-get install -y redis-server
wsl sudo service redis-server start
```

Check the port is free first (`Test-NetConnection 127.0.0.1 -Port 6379` should fail
before you start) and verify after:

```powershell
docker exec se2-game-api-redis redis-cli PING   # -> PONG  (docker option)
```

Redis must be running before you launch the game; the plugin writes state with a short
TTL, so the game simply reconnects if you restart Redis between sessions.

### 5. Launch the game with the plugin

Close the game first (a DLL cannot be redeployed while running), then:

```powershell
.\start-smoke.ps1 -Game2 "C:\Path\To\...\Game2" -ContinueGame
```

The script deploys the DLL into the portable Pulsar plugin folder
(`artifacts\tools\Pulsar\Modern\Local\SE2GameAPI\plugin.dll`) and starts the loader with
`smoke-profile.xml` (only this plugin; loader auto-update, Discord RPC and plugin stats are
disabled). `-ContinueGame` loads your latest save.

### 6. Verify

```powershell
docker exec se2-game-api-redis redis-cli GET se2:local:state
```

`"state":"READY"` with a fresh `sampledAtMs` means the world is up. Plugin-side state and
transport log: `%LOCALAPPDATA%\SE2GameAPI\` (`bridge-state.json`, `transport.log`).

### 7. Python client (secontrol)

The plugin API is transport-level; **secontrol** is the reference Python client and
operator toolkit. It is **installed from the git repository, without pip**:

```powershell
# clone secontrol anywhere (no pip install, no build step)
git clone https://github.com/rootfabric/secontrol.git C:\tools\secontrol

# point Python at its source tree — that is the whole "installation"
$env:PYTHONPATH = 'C:\tools\secontrol\src'
python -c "import secontrol, secontrol.se2; print('ok')"
```

Keep `$env:PYTHONPATH` set in your working shell (or add it to your user environment
variables). If you use the client inside your own code, an alternative to PYTHONPATH is
adding the path in Python:

```python
import sys
sys.path.insert(0, r'C:\tools\secontrol\src')
from secontrol.se2 import Client
```

First call against a running game (see steps 1–6 above):

```powershell
$env:PYTHONPATH = 'C:\tools\secontrol\src'
python -c "from secontrol.se2 import Client; c = Client(); print(len(c.snapshot()['devices']), 'devices')"
```

The only Python dependency is `redis` (`pip install redis` — the client library, not
secontrol itself). secontrol's SE2 modules (`secontrol.se2`, `secontrol.se2_navigation`,
`secontrol.se2_parking`) live on the `main` branch together with the operator playbook,
device skills and mission scripts.

## Protocol (Redis keys, prefix `se2:local`)

| Key / channel | Type | Purpose |
|---|---|---|
| `se2:local:state` | string (JSON) | full world snapshot: bridge state, epoch, all devices with telemetry; `protocolVersion: 2` |
| `se2:local:commands` | list | command queue (Lua-enqueued, max 256, 15 s TTL) |
| `se2:local:reply:{requestId}` | string (JSON) | command receipt: `confirmed`/`rejected`/`failed` + actual state |
| `se2:local:state` (channel) | pub/sub | every snapshot is **published** (~5 Hz) — subscribe instead of polling |
| `se2:local:replies` (channel) | pub/sub | every command receipt is **pushed** to subscribers |

Pub/sub uses plain Redis `PUBLISH`/`SUBSCRIBE` — **no server configuration required**
(no `notify-keyspace-events` or config-file changes; the default `redis:7` Docker image and
any native build work as-is). Subscribe-only clients never poll; the Python client exposes
this as `Client.subscribe()` / `Client.listen(callback)`.

Commands carry `requestId`, `epoch`, `gridId`, `deviceId`, `cmd`, typed parameters and an
expiry; replies are per-request receipts with the post-command device state. Devices list
their `capabilities`; unsupported commands are rejected before enqueueing. See
`src/Redis/Protocol.cs` for the authoritative record definitions and
[docs/AUTOPILOT.md](docs/AUTOPILOT.md) for the navigation contract.

## Safety notes

- **Localhost prototype**: no authentication, no per-client ownership leases. Do not expose
  the Redis port publicly; one trusted operator machine per game session.
- Commands change real game state. Live tests move ships — check free space around a grid
  before re-running them.
- The autopilot has **no obstacle avoidance**: routes are straight lines.
- A game/session restart changes the `epoch`; rediscover devices before writing.

## Development

```powershell
.\build.ps1                                                        # build plugin
artifacts\tools\dotnet\dotnet.exe run --project tests/NavigationTests -c Release   # math/model tests
artifacts\tools\dotnet\dotnet.exe run --project tests/SchedulerTests -c Release    # router tests
python tests/live_devices.py                                       # in-game device checks (needs running game)
```

Docs: [docs/AUTOPILOT.md](docs/AUTOPILOT.md) (navigation contract),
[docs/DEVICE-ARCHITECTURE.md](docs/DEVICE-ARCHITECTURE.md) (adding device types),
[docs/ENVIRONMENT.md](docs/ENVIRONMENT.md) (toolchain details),
[docs/DIAGNOSTICS-0.0.3.md](docs/DIAGNOSTICS-0.0.3.md) (loader behaviour).
Python client + parking module: the `secontrol` repository, docs `SE2_BACKEND.md` and
`SE2_NAVIGATION.md`.

## Versioning

The plugin and the Python client evolve in lockstep (protocol v2). This repository follows
the plugin version (0.1.0 = first open-source release: `alignCourse`, `keepControlOnPlayer`,
connector pivot telemetry + `TryConnect`, 0.05 m autopilot tolerance).

## License

[MIT](LICENSE) © rootfabric. Space Engineers 2 is a product of Keen Software House; this
project is an independent community plugin and is not affiliated with or endorsed by them.
