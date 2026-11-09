"""Inspect an SE2 grid autopilot, start a mission, or request stop."""
import argparse
import json
from secontrol.se2 import Client

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--grid", required=True)
parser.add_argument("--target", nargs=3, type=float, metavar=("X", "Y", "Z"))
parser.add_argument("--forward", nargs=3, type=float)
parser.add_argument("--up", nargs=3, type=float)
parser.add_argument("--mode", choices=("flight", "ground"), default="flight")
parser.add_argument("--speed", type=float, default=3)
parser.add_argument("--timeout", type=float, default=120)
parser.add_argument("--stop", action="store_true")
args = parser.parse_args()
if args.stop and args.target:
    parser.error("Choose --stop or --target")
with Client(timeout=5) as client:
    pilots = client.devices(device_type="autopilot", grid_name=args.grid)
    if len(pilots) != 1:
        raise SystemExit(f"Expected one grid, found {len(pilots)}")
    pilot = pilots[0]
    if args.stop:
        print(json.dumps(pilot.stop().actual_state, indent=2))
    elif args.target:
        receipt = pilot.goto(args.target, forward=args.forward, up=args.up, mode=args.mode,
                             max_speed=args.speed, timeout=args.timeout)
        print(f"Mission accepted: {receipt.request_id}", flush=True)
        try:
            print(json.dumps(pilot.wait(receipt.request_id, timeout=args.timeout+5), indent=2))
        except KeyboardInterrupt:
            pilot.stop()
            print("Stop confirmed")
    else:
        print(json.dumps(pilot.refresh(), indent=2))
