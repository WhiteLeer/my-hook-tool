"""Locate a pixel in the measured captured vertex output, without identity inference."""
import argparse
import json
from pathlib import Path

import numpy as np

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--sample", type=Path, required=True)
parser.add_argument("--x", type=int, required=True)
parser.add_argument("--y", type=int, required=True)
args = parser.parse_args()
sample = json.loads(args.sample.read_text(encoding="utf-8"))
directory = args.sample.parent
values = np.fromfile(directory / "unity-postvs.bin", dtype="<f4").reshape(-1, 12)
position = values[:, :4].astype(np.float64)
ndc = position[:, :3] / position[:, 3:4]
screen = (ndc[:, :2] + 1) * [sample["viewportWidth"] / 2, sample["viewportHeight"] / 2]
triangles = np.array(sample["mesh"]["triangles"]).reshape(-1, 3)
a, b, c = (screen[triangles[:, index]] for index in range(3))
point = np.array([args.x + .5, args.y + .5])
cross = lambda u, v: u[:, 0] * v[:, 1] - u[:, 1] * v[:, 0]
area = cross(b - a, c - a)
with np.errstate(divide="ignore", invalid="ignore"):
    beta = cross(np.broadcast_to(point, a.shape) - a, c - a) / area
    gamma = cross(b - a, np.broadcast_to(point, a.shape) - a) / area
    weights = np.stack([1 - beta - gamma, beta, gamma], axis=1)
valid = (area != 0) & np.all(weights >= 0, axis=1) & np.all(position[triangles, 3] > 0, axis=1)
depth = np.fromfile(Path(sample["referenceDirectory"]) / "before-depth.rgba32f", dtype="<f4")
depth = depth.reshape(sample["viewportHeight"], sample["viewportWidth"], 4)
print(json.dumps({"pixel": [args.x, args.y], "beforeDepth": float(depth[-1-args.y, args.x, 0]),
    "triangles": [{"triangle": int(index), "vertices": triangles[index].tolist(),
        "screen": screen[triangles[index]].tolist(),
        "depth": float(np.sum(weights[index] * (ndc[triangles[index], 2] + 1) / 2))}
        for index in np.where(valid)[0]]}))
