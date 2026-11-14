#!/usr/bin/env python3
"""Read-only voxel scan and 3D viewer. See docs/VOXEL-SCANNER.md.

Live:   python examples/voxel_viewer.py --grid-name Scout --radius 32
List:   python examples/voxel_viewer.py --list-grids
Replay: python examples/voxel_viewer.py --replay scan.json
No engine raycasts, ship commands, game modifications or synthetic fallback.
"""
from __future__ import annotations

import argparse
import base64
import json
import math
import queue
import threading
import time
import uuid
from pathlib import Path
from typing import Any, Iterator

PREFIX = "se2:local:voxels"
TERMINAL = {"complete", "partial", "rejected", "cancelled", "failed"}
ENQUEUE = """
if redis.call('LLEN', KEYS[1]) >= 8 then return 0 end
redis.call('RPUSH', KEYS[1], ARGV[1])
redis.call('EXPIRE', KEYS[1], 15)
return 1
"""


def grid_catalog(snapshot: dict[str, Any]) -> dict[str, str]:
    return {str(d["gridId"]): str(d.get("gridName", d["gridId"]))
            for d in snapshot.get("devices", []) if d.get("gridId")}


def select_grid(catalog: dict[str, str], grid_id: str | None, name: str | None) -> str:
    if grid_id:
        if grid_id not in catalog:
            raise ValueError(f"Грид {grid_id!r} не найден. Сначала выполните --list-grids.")
        return grid_id
    matches = [key for key, value in catalog.items() if value == name] if name else list(catalog)
    if len(matches) != 1:
        raise ValueError("Нужно однозначно выбрать грид через --grid-id или --grid-name.")
    return matches[0]


def decode_tile(tile: dict[str, Any]):
    """Return raw uint8 samples as [z,y,x]. Missing/invalid data is never empty space."""
    import numpy as np
    size = tile.get("size")
    if not isinstance(size, list) or len(size) != 3 or any(type(v) is not int or not 1 <= v <= 16 for v in size):
        raise ValueError("Некорректный размер плитки")
    content = tile.get("content")
    if not isinstance(content, str) or len(content) > 5500:
        raise ValueError("Некорректная длина закодированной плитки")
    raw = base64.b64decode(content, validate=True)
    if len(raw) != math.prod(size):
        raise ValueError("Длина Content не соответствует размеру плитки")
    for field in ("origin", "axisX", "axisY", "axisZ"):
        v = tile.get(field)
        if not isinstance(v, list) or len(v) != 3 or any(not isinstance(x, (int, float)) or not math.isfinite(x) for x in v):
            raise ValueError(f"Некорректное преобразование {field}")
    return np.frombuffer(raw, dtype=np.uint8).reshape(size[2], size[1], size[0])


def point_cloud(document: dict[str, Any], threshold: int = 1, max_points: int = 50000):
    """Grid-relative coordinates; float64 subtraction happens before rendering."""
    import numpy as np
    if not 1 <= threshold <= 255 or max_points < 1:
        raise ValueError("threshold=1..255, max_points>0")
    status = document["status"]
    grid = status.get("grid")
    if not isinstance(grid, dict) or "position" not in grid:
        raise ValueError("Нет измеренной позиции грида; координаты скана неизвестны")
    origin = np.asarray(grid["position"], dtype=np.float64)
    if origin.shape != (3,) or not np.all(np.isfinite(origin)):
        raise ValueError("Некорректная позиция грида")
    radius = float(status["radius"])
    if not math.isfinite(radius) or not 1 <= radius <= 128:
        raise ValueError("Некорректный радиус")
    tiles = document.get("tiles", [])
    if len(tiles) > 512:
        raise ValueError("Слишком много плиток")
    chunks = []
    total = 0
    # Bound renderer memory independently of scan size. Server data is not discarded/modified.
    quota = max(1, max_points // max(1, len(tiles)))
    for tile in tiles:
        values = decode_tile(tile)
        z, y, x = np.nonzero(values >= threshold)
        axes = np.asarray([tile["axisX"], tile["axisY"], tile["axisZ"]], dtype=np.float64)
        local_origin = np.asarray(tile["origin"], dtype=np.float64) - origin
        points = np.column_stack((x, y, z)) @ axes + local_origin
        points = points[np.all(np.abs(points) <= radius, axis=1)]
        total += len(points)
        if len(points):
            step = max(1, math.ceil(len(points) / quota))
            chunks.append(points[::step])
    points = np.concatenate(chunks) if chunks else np.empty((0, 3))
    return points[:max_points], total


def draw(ax, document: dict[str, Any], threshold: int, max_points: int) -> None:
    status = document["status"]
    if not status.get("grid"):
        ax.clear()
        ax.set_title(f"{status['state']}: {status.get('error') or 'Ожидание данных'}\nНет данных — не означает пустое пространство.")
        return
    points, total = point_cloud(document, threshold, max_points)
    ax.clear()
    if len(points):
        ax.scatter(points[:, 0], points[:, 1], points[:, 2], s=7, marker="s", alpha=0.65)
    ax.scatter([0], [0], [0], s=130, marker="x", label="Грид (начало координат)")
    radius = float(status["radius"])
    ax.set(xlim=(-radius, radius), ylim=(-radius, radius), zlim=(-radius, radius),
           xlabel="X относительно грида, м", ylabel="Y относительно грида, м", zlabel="Z относительно грида, м")
    ax.set_box_aspect((1, 1, 1))
    name = (status.get("grid") or {}).get("gridName", status.get("gridId", ""))
    times = [t.get("measuredAtMs", 0) for t in document.get("tiles", [])]
    age = max(0, int(time.time()*1000) - min(times)) / 1000 if times else 0
    ax.set_title(f"{name}: {status['state']} | плиток {status['publishedTiles']}/{status['plannedTiles']}\n"
                 f"Content ≥ {threshold}: {total:,}; показано {len(points):,}; возраст ≤ {age:.1f} с\n"
                 "Непрочитанные области неизвестны. Это не проверка безопасности маршрута.")
    ax.legend(loc="upper right")
    issues = status.get("issues") or []
    if status.get("error") or issues:
        ax.text2D(0.01, 0.01, str(status.get("error") or issues[0])[:160], transform=ax.transAxes)


class VoxelClient:
    def __init__(self, url: str):
        try:
            import redis
        except ImportError as exc:
            raise RuntimeError("Установите зависимости: python -m pip install -r examples/requirements-voxel.txt") from exc
        self.db = redis.Redis.from_url(url, socket_timeout=3, socket_connect_timeout=3)
        self.active: dict[str, Any] | None = None

    def snapshot(self) -> dict[str, Any]:
        raw = self.db.get("se2:local:state")
        if not raw:
            raise RuntimeError("Нет свежего se2:local:state. Запустите плагин и загрузите мир.")
        state = json.loads(raw)
        if state.get("state") != "READY" or int(time.time()*1000) - state.get("sampledAtMs", 0) > 5000:
            raise RuntimeError("Мир ещё не готов либо телеметрия устарела.")
        return state

    def cancel(self) -> None:
        if self.active is not None:
            self.db.eval(ENQUEUE, 1, f"{PREFIX}:requests", json.dumps({**self.active, "action": "cancel"}))

    def scan(self, grid_id: str, radius: float, fresh: bool, stop: threading.Event) -> Iterator[dict[str, Any]]:
        epoch = self.snapshot()["epoch"]
        request = {"requestId": uuid.uuid4().hex, "epoch": epoch, "gridId": grid_id,
                   "expiresAtMs": int(time.time()*1000) + 90000, "radius": radius, "fresh": fresh}
        if not self.db.eval(ENQUEUE, 1, f"{PREFIX}:requests", json.dumps(request)):
            raise RuntimeError("Очередь сканирования заполнена")
        self.active = request
        deadline = time.monotonic() + 95
        tiles: list[dict[str, Any]] = []
        last = None
        try:
            while not stop.is_set() and time.monotonic() < deadline:
                if self.snapshot()["epoch"] != epoch:
                    raise RuntimeError("Сессия изменилась. Старые данные больше не относятся к текущему миру.")
                raw = self.db.get(f"{PREFIX}:scan:{request['requestId']}")
                if raw:
                    status = json.loads(raw)
                    if status.get("epoch") != epoch or status.get("requestId") != request["requestId"]:
                        raise RuntimeError("Ответ относится к другой сессии/запросу")
                    count = status.get("publishedTiles", 0)
                    if type(count) is not int or not 0 <= count <= 512:
                        raise RuntimeError("Некорректное число плиток")
                    if count > len(tiles):
                        keys = [f"{PREFIX}:tile:{request['requestId']}:{i}" for i in range(len(tiles), count)]
                        values = self.db.mget(keys)
                        for index, value in enumerate(values, start=len(tiles)):
                            if value is None:
                                raise RuntimeError("Плитка отсутствует/истекла. Область остаётся неизвестной.")
                            tile = json.loads(value)
                            if tile.get("index") != index:
                                raise RuntimeError("Нарушена нумерация плиток")
                            decode_tile(tile)
                            tiles.append(tile)
                    signature = (status["state"], count, status.get("updatedAtMs"))
                    if signature != last:
                        yield {"status": status, "tiles": list(tiles)}
                        last = signature
                    if status["state"] in TERMINAL:
                        if status["state"] in {"failed", "rejected"}:
                            raise RuntimeError(f"{status['state']}: {status.get('error')}; {status.get('issues')}")
                        return
                stop.wait(0.2)
            if not stop.is_set():
                raise TimeoutError("Сканирование не завершилось. Отсутствие ответа не означает пустое пространство.")
        finally:
            if stop.is_set() or time.monotonic() >= deadline:
                self.cancel()
            self.active = None


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--redis-url", default="redis://127.0.0.1:6379/0")
    parser.add_argument("--list-grids", action="store_true")
    group = parser.add_mutually_exclusive_group()
    group.add_argument("--grid-id")
    group.add_argument("--grid-name")
    parser.add_argument("--radius", type=float, default=32)
    parser.add_argument("--fresh", action="store_true", help="Не использовать кэш плиток")
    parser.add_argument("--watch", action="store_true", help="Повторять завершённые сканы; не запускать параллельные")
    parser.add_argument("--interval", type=float, default=2)
    parser.add_argument("--threshold", type=int, default=1)
    parser.add_argument("--max-points", type=int, default=50000)
    parser.add_argument("--record", type=Path)
    parser.add_argument("--replay", type=Path)
    parser.add_argument("--no-gui", action="store_true", help="Только запись/вывод статуса")
    args = parser.parse_args()
    if not math.isfinite(args.radius) or not 1 <= args.radius <= 128 or not 1 <= args.threshold <= 255 or not 1 <= args.max_points <= 200000 or not math.isfinite(args.interval) or args.interval < 1:
        parser.error("radius: 1..128; threshold: 1..255; max-points: 1..200000; interval ≥ 1")
    if args.replay:
        if args.replay.stat().st_size > 10_000_000:
            raise ValueError("Файл скана слишком большой")
        document = json.loads(args.replay.read_text(encoding="utf-8"))
        if args.no_gui:
            points, total = point_cloud(document, args.threshold, args.max_points)
            print(f"Сохранённый скан: {total} занятых отсчётов, {len(points)} для отображения")
            return
        import matplotlib.pyplot as plt
        ax = plt.figure(figsize=(11, 8)).add_subplot(projection="3d")
        draw(ax, document, args.threshold, args.max_points)
        plt.show()
        return
    client = VoxelClient(args.redis_url)
    catalog = grid_catalog(client.snapshot())
    if args.list_grids:
        for key, value in catalog.items():
            print(f"{key}\t{value}")
        return
    grid_id = select_grid(catalog, args.grid_id, args.grid_name)
    stop = threading.Event()
    updates: queue.Queue[Any] = queue.Queue(maxsize=2)

    def put_latest(value: Any) -> None:
        try:
            updates.put_nowait(value)
        except queue.Full:
            try:
                updates.get_nowait()
            except queue.Empty:
                pass
            updates.put_nowait(value)

    def collect() -> None:
        try:
            while not stop.is_set():
                for document in client.scan(grid_id, args.radius, args.fresh, stop):
                    s = document["status"]
                    print(f"{s['state']}: {s['publishedTiles']}/{s['plannedTiles']}; reads={s['reads']}, cache={s['cacheHits']}")
                    if args.record and s["state"] in TERMINAL:
                        args.record.parent.mkdir(parents=True, exist_ok=True)
                        args.record.write_text(json.dumps(document, ensure_ascii=False), encoding="utf-8")
                    put_latest(document)
                if not args.watch or stop.wait(args.interval):
                    break
        except Exception as exc:
            put_latest(exc)

    if args.no_gui:
        collect()
        while not updates.empty():
            value = updates.get_nowait()
            if isinstance(value, Exception):
                raise value
        return
    import matplotlib.pyplot as plt
    from matplotlib.animation import FuncAnimation
    fig = plt.figure(figsize=(11, 8))
    ax = fig.add_subplot(projection="3d")
    ax.set_title("Ожидание данных SE2. Пустое окно НЕ означает отсутствие препятствий.")
    thread = threading.Thread(target=collect, daemon=True)
    thread.start()

    def refresh(_):
        value = None
        while True:
            try:
                value = updates.get_nowait()
            except queue.Empty:
                break
        if isinstance(value, Exception):
            ax.set_title(f"Ошибка: {value}")
            print(value)
            stop.set()
        elif value is not None:
            draw(ax, value, args.threshold, args.max_points)

    animation = FuncAnimation(fig, refresh, interval=500, cache_frame_data=False)
    try:
        plt.show()
    finally:
        stop.set()
        thread.join(timeout=4)
        _ = animation  # Keep animation alive until the window is closed.


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, TimeoutError) as exc:
        raise SystemExit(str(exc)) from exc
