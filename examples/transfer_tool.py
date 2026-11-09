"""Park a ship at a base connector and transfer items from the ship connector to the dock.

Prerequisites (see README):
  1. Redis running on 127.0.0.1:6379 (docker run -d --name se2-game-api-redis -p 127.0.0.1:6379:6379 redis:7)
  2. Game started with the SE2GameAPI plugin (.\start-smoke.ps1 -ContinueGame) and state READY
  3. secontrol cloned and on PYTHONPATH:
        $env:PYTHONPATH = 'C:\tools\secontrol\src'

Usage:
    python examples\transfer_tool.py --grid "Blue Fighter" --station "Concordia Research Station"
"""

import argparse
import math
import sys
import time

try:
    from secontrol.se2 import BridgeError, Client
    from secontrol.se2_parking import park
except ImportError:
    sys.exit("secontrol not importable: clone https://github.com/rootfabric/secontrol and set PYTHONPATH to its src/")

ap = argparse.ArgumentParser()
ap.add_argument("--grid", required=True, help="ship grid name, e.g. 'Blue Fighter'")
ap.add_argument("--station", required=True, help="station grid name that owns the dock connector")
ap.add_argument("--backoff", type=float, default=40.0, help="undock distance before the docking approach")
args = ap.parse_args()


def items_of(device):
    inv = device.refresh().get("inventories") or [{}]
    return inv[0].get("items") or []


def main():
    with Client(timeout=9) as c:
        own, = [d for d in c.connectors(grid_name=args.grid)]
        own_props = own.refresh()["properties"]

        # 1. Park (skip if already docked)
        if not own_props["isConnected"]:
            docks = [d for d in c.connectors(grid_name=args.station)
                     if (d._state.get("properties") or {}).get("pivotPosition")]
            if not docks:
                sys.exit(f"no connector with pivot telemetry found on {args.station!r}")
            dock = min(docks, key=lambda d: math.dist(d._state["properties"]["pivotPosition"],
                                                      own_props["pivotPosition"]))
            print(f"parking {args.grid} at {dock._state['id']} ...")
            print("PARK", park(c, args.grid, dock_id=dock._state["id"],
                              standoff=0.05, approach=15.0, max_speed=2.0, timeout=240))
            own, = [d for d in c.connectors(grid_name=args.grid)]
        dock_id = own.refresh()["properties"]["counterpartId"]
        dock, = [d for d in c.connectors() if d._state["id"] == dock_id]
        print(f"docked at {dock_id}")

        # 2. Transfer every item from the ship connector to the base connector
        time.sleep(1)  # let inventory telemetry settle after docking
        for item in list(items_of(own)):
            receipt = own.transfer_item(dock, item["index"], float(item["amount"]))
            print(f"transfer {item['definition'].split(chr(92))[-2]} x{item['amount']}: {receipt.status}")

        # 3. Verify with a fresh read (inventory telemetry may lag one tick)
        time.sleep(1)
        ship_items, base_items = items_of(own), items_of(dock)
        print("ship connector:", [i["definition"].split(chr(92))[-2] for i in ship_items])
        print("base connector:", [i["definition"].split(chr(92))[-2] for i in base_items])
        print("VERDICT", "OK" if not ship_items and base_items else "CHECK")


if __name__ == "__main__":
    main()
