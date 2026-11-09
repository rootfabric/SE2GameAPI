"""Opt-in live test. Restores selected lamp and door even on assertion failure."""
import concurrent.futures
import json
import statistics
import time
from pathlib import Path
from secontrol.se2 import Client


def run():
    report = {}
    with Client(timeout=9) as client:
        lamps = client.lamps(grid_name="Concordia Research Station", name="Interior Light 0.5 m")
        assert len(lamps) == 3, len(lamps)
        doors = [d for d in client.doors(grid_name="Concordia Research Station")
                 if d.telemetry["enabled"] and d.telemetry["working"] and d.telemetry["status"] == "Closed"]
        assert doors, "No working closed station door"
        lamp, door = lamps[0], doors[0]
        original_lamp, original_door = dict(lamp.telemetry), dict(door.telemetry)
        before = client.snapshot(fresh=True)
        report["counts"] = {kind: sum(d["type"] == kind for d in before["devices"]) for kind in ("lamp", "door")}
        report["targets"] = [original_lamp, original_door]
        results = []
        try:
            # Shared implementation on both subclasses.
            for device in (lamp, door):
                assert device.disable().actual_state["enabled"] is False
                assert device.enable().actual_state["enabled"] is True
            time.sleep(.3)
            # A long operation must not hold the other device's queue.
            with concurrent.futures.ThreadPoolExecutor(2) as pool:
                opening = pool.submit(door.open)
                time.sleep(.05)
                light = lamp.disable()
                opened = opening.result()
                assert opened.actual_state["status"] == "Open"
                assert light.actual_state["enabled"] is False
                report["parallel"] = {"lamp_ms": light.latency_ms, "door_ms": opened.latency_ms}
            assert door.close().actual_state["status"] == "Closed"
            # Repeated same-value switch commands must not invent state revisions.
            first = lamp.disable().actual_state["revision"]
            assert lamp.disable().actual_state["revision"] == first
            time.sleep(1)
            start = client.snapshot(fresh=True)
            def worker(index):
                with Client(timeout=9) as c:
                    device = c.devices(device_type="lamp" if index % 2 == 0 else "door",
                                       grid_name="Concordia Research Station")[index // 2 % 3]
                    return [device.set_enabled(device.telemetry["enabled"]).latency_ms for _ in range(8)]
            with concurrent.futures.ThreadPoolExecutor(6) as pool:
                for samples in pool.map(worker, range(6)):
                    results.extend(samples)
            time.sleep(.5)
            end = client.snapshot(fresh=True)
            count = end["tickCount"] - start["tickCount"]
            report["load"] = {"commands": len(results), "ack_median_ms": statistics.median(results),
                              "ack_p95_ms": sorted(results)[int(len(results) * .95)],
                              "scene_average_ms": (end["averageTickMs"] * end["tickCount"] - start["averageTickMs"] * start["tickCount"]) / count,
                              "tick_histogram": [b-a for a,b in zip(start["tickHistogram"], end["tickHistogram"])],
                              "tick_count": count, "lifetime_max_ms": end["maxTickMs"]}
            report["passed"] = True
        finally:
            door.enable()
            (door.open if original_door["status"] == "Open" else door.close)()
            door.set_enabled(original_door["enabled"])
            lamp.set_enabled(original_lamp["enabled"])
            time.sleep(.5)
            actual = client.snapshot(fresh=True)
            original = {d["id"]: d for d in before["devices"]}
            changes = [d["id"] for d in actual["devices"] if d["id"] in original
                       and (d["enabled"], d["status"]) != (original[d["id"]]["enabled"], original[d["id"]]["status"])]
            report["remaining_changes"] = changes
            path = Path(__file__).resolve().parents[1] / "docs" / "devices-test-2026-11-09.json"
            path.write_text(json.dumps(report, indent=2), encoding="utf-8")
            assert not changes, changes
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    run()
