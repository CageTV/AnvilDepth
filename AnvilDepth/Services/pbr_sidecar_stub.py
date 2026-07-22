#!/usr/bin/env python3
"""
AnvilDepth PBR Sidecar -- STUB / PLACEHOLDER, NOT PBRFusion4
=============================================================
This script exists to prove the C#<->Python bridge works: AnvilDepth spawns this process, hands
it an image path, and this script writes back processed images plus a JSON line describing
where to find them. It is deliberately NOT running the real PBRFusion4 model.

Only dependency: Pillow (`pip install pillow`). No torch, no diffusers, no GPU, no multi-GB
model download -- that's the point of a stub: the whole bridge can be tested today, before
committing to setting up a real Python/ML environment.

--- WHERE THE REAL PBRFusion4 CALL WOULD GO ---
Replace fake_second_pass() below with real inference: load PBRFusion4's actual model code
(from its ComfyUI custom node repo) and its checkpoint, run it on `image`, and return real
refined depth/normal outputs instead of this placeholder blur+edge-detect. That requires a
Python environment with torch + diffusers + the model's own dependencies + the downloaded
checkpoint -- none of which this stub needs, which is exactly why it's useful as a first test.
"""
import sys
import os
import json

try:
    from PIL import Image, ImageFilter
except ImportError:
    print(json.dumps({"error": "Pillow not installed. Run: pip install pillow"}), file=sys.stderr)
    sys.exit(1)


def fake_second_pass(image):
    """
    PLACEHOLDER for a real PBRFusion4 pass. Real PBRFusion4 would run a diffusion-based
    depth/normal refinement on the source image. This stub applies a plain smoothing filter and
    a crude edge-detect as a stand-in, purely to prove data can flow through the bridge and come
    back out the other side as usable images -- NOT to demonstrate real quality.
    """
    gray = image.convert("L")
    refined_depth = gray.filter(ImageFilter.SMOOTH_MORE)
    edges = refined_depth.filter(ImageFilter.FIND_EDGES).convert("RGB")
    return refined_depth, edges


def main():
    if len(sys.argv) < 3:
        print(json.dumps({"error": "Usage: pbr_sidecar_stub.py <input_image> <output_dir>"}), file=sys.stderr)
        sys.exit(1)

    input_path, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    try:
        image = Image.open(input_path).convert("RGB")
    except Exception as ex:
        print(json.dumps({"error": "Could not open image: %s" % ex}), file=sys.stderr)
        sys.exit(1)

    refined_depth, edges = fake_second_pass(image)

    depth_path = os.path.join(out_dir, "pass2_depth.png")
    normal_path = os.path.join(out_dir, "pass2_normal.png")
    refined_depth.save(depth_path)
    edges.save(normal_path)

    result = {
        "RefinedDepthPath": depth_path,
        "RefinedNormalPath": normal_path,
        "Note": "STUB result -- not real PBRFusion4 output. See script header for what a real integration would replace."
    }
    print(json.dumps(result))


if __name__ == "__main__":
    main()
