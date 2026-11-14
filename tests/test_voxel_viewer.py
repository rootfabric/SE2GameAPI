"""Protocol/visualization fixtures; no claim that synthetic data came from SE2."""
import base64
import importlib.util
from pathlib import Path

import numpy as np
import pytest

spec = importlib.util.spec_from_file_location("voxel_viewer", Path(__file__).parents[1] / "examples/voxel_viewer.py")
viewer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(viewer)


def tile(raw=bytes([0, 255, 0, 0, 0, 0, 128, 0])):
    return {"index": 0, "storageId": "fixture", "size": [2, 2, 2], "min": [0, 0, 0],
            "origin": [1e9, -2e9, 3e9], "axisX": [0, 1, 0], "axisY": [-1, 0, 0], "axisZ": [0, 0, 1],
            "content": base64.b64encode(raw).decode(), "measuredAtMs": 1, "cached": False}


def document(tiles=None):
    return {"status": {"requestId": "fixture", "state": "partial", "epoch": "e", "gridId": "g",
                       "grid": {"gridName": "Fixture, NOT game evidence", "position": [1e9, -2e9, 3e9]},
                       "radius": 8, "publishedTiles": 1, "plannedTiles": 2, "issues": ["fixture_unknown_tile"]},
            "tiles": [tile()] if tiles is None else tiles}


def test_x_fastest_decode():
    arr = viewer.decode_tile(tile())
    assert arr.shape == (2, 2, 2)
    assert arr[0, 0, 1] == 255
    assert arr[1, 1, 0] == 128


def test_rotated_double_precision_relative_coordinates():
    points, total = viewer.point_cloud(document())
    assert total == 2
    np.testing.assert_allclose(points, [[0, 1, 0], [-1, 0, 1]])


def test_threshold_is_view_only():
    doc = document()
    points, total = viewer.point_cloud(doc, threshold=200)
    assert total == len(points) == 1
    assert viewer.decode_tile(doc["tiles"][0])[1, 1, 0] == 128


def test_empty_samples_different_from_missing_tile():
    assert viewer.point_cloud(document([tile(bytes(8))]))[1] == 0
    with pytest.raises(ValueError):
        viewer.decode_tile({**tile(), "content": ""})


@pytest.mark.parametrize("change", [
    {"content": "????"}, {"size": [16, 16, 17]}, {"size": [True, 2, 2]},
    {"axisX": [float("nan"), 0, 0]}, {"origin": [1, 2]}, {"content": "A" * 6000},
])
def test_invalid_payload_is_not_empty_space(change):
    with pytest.raises(ValueError):
        viewer.decode_tile({**tile(), **change})


def test_renderer_bound_and_no_server_data_mutation():
    doc = document([tile(bytes([255]) * 8)])
    points, total = viewer.point_cloud(doc, max_points=2)
    assert total == 8 and len(points) <= 2
    assert len(viewer.decode_tile(doc["tiles"][0]).ravel()) == 8


def test_unique_grid_selection():
    catalog = {"a": "Scout", "b": "Scout"}
    with pytest.raises(ValueError):
        viewer.select_grid(catalog, None, "Scout")
    assert viewer.select_grid(catalog, "b", None) == "b"
    with pytest.raises(ValueError):
        viewer.select_grid(catalog, "missing", None)


def test_render_to_offscreen_png(tmp_path):
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    fig = plt.figure()
    ax = fig.add_subplot(projection="3d")
    viewer.draw(ax, document(), 1, 50000)
    assert "не проверка" in ax.get_title().lower()
    path = tmp_path / "fixture.png"
    fig.savefig(path)
    assert path.stat().st_size > 1000
    plt.close(fig)


def test_missing_grid_pose_is_unknown_not_origin():
    doc = document()
    doc["status"]["grid"] = None
    with pytest.raises(ValueError):
        viewer.point_cloud(doc)
