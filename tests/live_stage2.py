"""Read current world and check connector/cargo refusals and common switch."""
import json
from pathlib import Path
from secontrol.se2 import BridgeError, Client


def run():
    with Client(timeout=3) as client:
        state = client.snapshot(fresh=True)
        cargo = client.containers()
        connectors = client.connectors()
        batteries = client.devices(device_type="Battery")
        assert len(cargo) == 55 and len(connectors) == 7 and len(batteries) == 10
        source, = [c for c in cargo if c.items]
        assert source.items[0]["amount"] and source.telemetry["gridName"] == "H2 Prototype"
        other = next(c for c in cargo if c.grid_id != source.grid_id)
        try:
            source.transfer_item(other, 0, 1)
            raise AssertionError("Cross-grid transfer accepted")
        except BridgeError as e:
            assert "same grid" in str(e)
        connector = next(c for c in connectors if not c.connected and not c.telemetry["properties"]["readyToConnect"])
        try:
            connector.connect()
            raise AssertionError("No-neighbor connector accepted")
        except BridgeError as e:
            assert "no_nearby_connector" in str(e)
        assert connector.refresh()["status"] == "Disconnected"
        battery = batteries[0]
        original = battery.is_enabled()
        try:
            assert battery.set_enabled(not original).actual_state["enabled"] is not original
        finally:
            battery.set_enabled(original)
        assert battery.refresh()["enabled"] is original
        result = {"cargo": len(cargo), "connectors": len(connectors), "batteries": len(batteries),
                  "source_items": source.items, "connector_refusal": "no_nearby_connector",
                  "battery_restored": True, "scene_max_ms": client.snapshot(fresh=True)["maxTickMs"]}
        Path(__file__).resolve().parents[1].joinpath("docs", "stage2-live.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
        print(json.dumps(result))


if __name__ == "__main__":
    run()
