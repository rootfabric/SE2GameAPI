"""Opt-in flight test: Blue Miner, short clear-space translation and return."""
import json
import time
import math
from pathlib import Path
from secontrol.se2 import Client


def run():
    with Client(timeout=5) as client:
        pilot, = client.devices(device_type="autopilot", grid_name="Blue Miner")
        initial = pilot.refresh()["properties"]
        origin, up, forward = initial["position"], initial["up"], initial["forward"]
        target = [p+u*5 for p,u in zip(origin,up)]
        report = {"initial": initial, "legs": []}
        def leg(point, direction):
            ack = pilot.goto(point, forward=direction, up=up, max_speed=1,
                             position_tolerance=.5, heading_tolerance_deg=3, timeout=60)
            samples = []
            deadline = time.monotonic()+65
            while time.monotonic()<deadline:
                state=pilot.refresh(); p=state["properties"]
                if p["missionId"] == ack.request_id:
                    samples.append({"status":state["status"],"position":p["position"],"distance":p["distance"],
                                    "headingErrorDeg":p["headingErrorDeg"],"speed":math.dist(p["velocity"],[0,0,0]),"error":p["error"]})
                    print(json.dumps(samples[-1]),flush=True)
                    if state["status"]!="Navigating":
                        report["legs"].append(samples)
                        assert state["status"]=="Arrived",p
                        return state
                time.sleep(1)
            raise TimeoutError("Test mission timed out")
        try:
            leg(target,forward)
            # Rotate 15 degrees in the plane of the original up direction.
            right=[forward[1]*up[2]-forward[2]*up[1],forward[2]*up[0]-forward[0]*up[2],forward[0]*up[1]-forward[1]*up[0]]
            turned=[f*math.cos(math.pi/12)+r*math.sin(math.pi/12) for f,r in zip(forward,right)]
            leg(target,turned)
            leg(origin,forward)
            report["passed"]=True
        finally:
            pilot.stop()
            report["final"]=pilot.refresh()
            Path("docs/autopilot-live-test.json").write_text(json.dumps(report,indent=2),encoding="utf-8")


if __name__=="__main__":
    run()
