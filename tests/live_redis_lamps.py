"""Opt-in integration test against the running creative world; restores selected lamps."""
import concurrent.futures
import json
from pathlib import Path
import statistics
import subprocess
import time
import uuid

from secontrol.se2 import Client, BridgeError

report = {}
destination = Path(__file__).resolve().parents[1] / "artifacts" / "redis-lamp-test.json"


def wait_ready(client, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            snapshot = client.snapshot(fresh=True)
            if snapshot["lamps"]:
                return snapshot
        except Exception:
            pass
        time.sleep(.25)
    raise AssertionError("No ready bridge")


def raw_command(client, lamp, cmd="enable", **changes):
    payload = dict(requestId=uuid.uuid4().hex, epoch=lamp.epoch, gridId=lamp.grid_id,
                   deviceId=lamp.device_id, cmd=cmd, expiresAtMs=int(time.time()*1000)+3000)
    payload.update(changes)
    client.redis.rpush("se2:local:commands", json.dumps(payload))
    return payload


def reply(client, payload):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        data = client.redis.get("se2:local:reply:" + payload["requestId"])
        if data:
            return json.loads(data)
        time.sleep(.01)
    raise AssertionError("No response: " + payload["requestId"])


with Client(timeout=9) as client:
    initial = wait_ready(client)
    lamps = client.lamps(grid_name="Concordia Research Station", name="Interior Light 0.5 m")
    assert len(lamps) == 3, f"Expected exactly 3 test lamps, found {len(lamps)}"
    originals = {lamp.device_id: lamp.is_enabled() for lamp in lamps}
    other_enabled = {s["id"]: s["enabled"] for s in initial["lamps"] if s["id"] not in originals}
    try:
        latencies = []
        for enabled in (False, True, False, True):
            for lamp in lamps:
                result = lamp.set_enabled(enabled)
                assert result.actual_state["enabled"] == enabled
                assert result.actual_state["working"] == enabled
                latencies.append(result.latency_ms)
        report["functional"] = "12 commands confirmed; Enabled and Working match both phases"
        print(report["functional"], flush=True)

        target = lamps[0]
        baseline = target.enable().actual_state["enabled"]
        payload = raw_command(client, target, "toggle")
        client.redis.rpush("se2:local:commands", json.dumps(payload))
        first = reply(client, payload)
        assert first["status"] == "confirmed" and first["actualState"]["enabled"] is not baseline
        time.sleep(.3)
        client.redis.rpush("se2:local:commands", json.dumps(payload))
        time.sleep(.3)
        assert target.refresh()["enabled"] is not baseline
        report["deduplication"] = "same toggle submitted 3 times, executed once"

        for changes, expected in [({"epoch": "old:0"}, "stale_session"),
                                  ({"expiresAtMs": 1}, "invalid_deadline"),
                                  ({"deviceId": "missing"}, "not_found"),
                                  ({"gridId": "wrong"}, "target_changed"),
                                  ({"cmd": "arbitrary_code"}, "unsupported_command"),
                                  ({"expectedRevision": -1}, "revision_conflict")]:
            result = reply(client, raw_command(client, target, **changes))
            assert result["status"] == "rejected" and result["error"] == expected, result
        report["validation"] = "old session, expired, missing, wrong grid, unsupported operation and revision conflict rejected"
        for lamp in lamps:
            lamp.set_enabled(originals[lamp.device_id])
        time.sleep(.3)
        after = client.snapshot(fresh=True)
        assert all(item["enabled"] == other_enabled[item["id"]] for item in after["lamps"] if item["id"] in other_enabled)
        report["isolation"] = f"{len(other_enabled)} other lamps unchanged"
        print(report["deduplication"], report["validation"], report["isolation"], sep="\n", flush=True)

        # Idle reference and load interval use ALL tick histogram counters, not sparsely sampled timings.
        idle_start = client.snapshot(fresh=True)
        time.sleep(5)
        idle_end = client.snapshot(fresh=True)

        def load_worker(index):
            with Client(timeout=9) as c:
                group = c.lamps(grid_name="Concordia Research Station", name="Interior Light 0.5 m")
                lamp = group[index % 3]
                return [lamp.set_enabled(originals[lamp.device_id]).latency_ms for _ in range(8)]

        load_start = client.snapshot(fresh=True)
        started = time.monotonic()
        with concurrent.futures.ThreadPoolExecutor(max_workers=12) as pool:
            load_latencies = [value for values in pool.map(load_worker, range(12)) for value in values]
        duration = time.monotonic() - started
        time.sleep(.3)
        load_end = client.snapshot(fresh=True)

        def interval(a, b):
            count = b["tickCount"] - a["tickCount"]
            histogram = [y-x for x,y in zip(a["tickHistogram"], b["tickHistogram"])]
            threshold = count * .95
            cumulative = 0
            p95 = None
            for bound, n in zip([.1,.25,.5,1,2,5,10,20,">20"], histogram):
                cumulative += n
                if cumulative >= threshold:
                    p95 = bound
                    break
            return dict(ticks=count, average_scene_ms=(b["averageTickMs"]*b["tickCount"]-a["averageTickMs"]*a["tickCount"])/count,
                        p95_scene_ms_upper_bound=p95, tick_histogram=histogram)

        report["idle"] = interval(idle_start, idle_end)
        report["load"] = dict(clients=12, commands=len(load_latencies), seconds=duration,
                              ack_median_ms=statistics.median(load_latencies), ack_p95_ms=sorted(load_latencies)[int(len(load_latencies)*.95)-1],
                              **interval(load_start, load_end))
        report["single_client_ack_ms"] = dict(median=statistics.median(latencies), maximum=max(latencies))
        print(json.dumps({"idle": report["idle"], "load": report["load"]}), flush=True)

        before_outage = client.snapshot(fresh=True)
        subprocess.run(["docker", "stop", "se2-redis-bridge"], check=True, capture_output=True)
        try:
            time.sleep(3.5)
            try:
                Client().snapshot(fresh=True)
            except Exception:
                report["offline_client"] = "fails explicitly"
            else:
                raise AssertionError("Offline bridge incorrectly reported live")
        finally:
            subprocess.run(["docker", "start", "se2-redis-bridge"], check=True, capture_output=True)
        recovered = wait_ready(client, 20)
        assert recovered["epoch"] == before_outage["epoch"]
        assert recovered["tickCount"] > before_outage["tickCount"]
        assert target.enable().status == "confirmed"
        report["redis_recovery"] = "same game session kept ticking during outage; command confirmed after reconnect"
        report["epoch"] = recovered["epoch"]
        report["startup_inclusive_max_scene_ms"] = recovered["maxTickMs"]
        report["total_lamps"] = len(recovered["lamps"])
        print(report["redis_recovery"], flush=True)
    finally:
        for lamp in lamps:
            lamp.set_enabled(originals[lamp.device_id])
        destination.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print("Restored original lamp states. Report:", destination, flush=True)
