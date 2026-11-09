"""python examples/redis_lamp.py --cycles 3 --grid 'Concordia Research Station'

Install the local updated secontrol checkout first: pip install -e C:\secontrol
"""
import argparse
import time
from secontrol.se2 import Client

parser = argparse.ArgumentParser()
parser.add_argument("--grid", default="Concordia Research Station")
parser.add_argument("--name", default="Interior Light 0.5 m")
parser.add_argument("--cycles", type=int, default=3)
args = parser.parse_args()
with Client() as client:
    lamps = client.lamps(grid_name=args.grid, name=args.name)
    if not lamps:
        raise SystemExit("No matching lamps")
    print([(lamp.device_id, lamp.name) for lamp in lamps])
    original = [(lamp, lamp.is_enabled()) for lamp in lamps]
    try:
        for _ in range(args.cycles):
            for enabled in (False, True):
                for lamp in lamps:
                    ack = lamp.set_enabled(enabled)
                    print(lamp.device_id, ack.status, ack.actual_state["enabled"], round(ack.latency_ms, 1), "ms")
                time.sleep(1)
    finally:
        for lamp, enabled in original:
            lamp.set_enabled(enabled)
