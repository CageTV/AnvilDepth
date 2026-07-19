# AnvilDepth RTX

![AnvilDepth RTX](https://raw.githubusercontent.com/CageTV/AnvilDepth/main/anvildepth_rtx_logo.jpg)

Turn any image into a clean, CNC-ready depth map — locally, on your own GPU. No cloud, no uploads, no subscription.

Built with Skyrim texture modding in mind (bas-relief armor, ornament, and prop textures), but it works on any photo, texture, or AI render you want turned into a height/depth map.

---

## What it does

- **Two generation modes**
  - **Relief** — a pure algorithmic pipeline (de-lighting + frequency-band separation + tone mapping). No AI model required, works instantly on any image.
  - **AI Depth** — local AI depth estimation via a Depth-Anything V2 ONNX model, running entirely on your own machine. Includes an **HQ Tiled** option that runs large textures through the model in overlapping tiles near its native resolution, so big texture atlases keep their fine detail instead of getting squashed down.
- **Drag-and-drop** — drop an image straight onto the window, or click to browse.
- **Live sliders** for flatten/de-light strength, frequency bands (low/mid/high/detail), gamma, shadows/midtones/highlights, invert, background removal, and seamless tiling — with instant preview in Relief mode.
- **Export formats**: 16-bit PNG, 32-bit EXR, 32-bit TIFF, and a downsampled STL mesh (ready for a slicer or CNC/CAM package).
- **100% local and offline** — nothing leaves your machine.

---

## Requirements

**Hardware**
- Windows 10/11, 64-bit
- A DirectX 12–capable GPU (any modern NVIDIA, AMD, or Intel GPU works via DirectML — an NVIDIA RTX card is not required, though it will run faster). CPU-only also works, just slower.
- 16 GB RAM recommended

**To use AI Depth mode**
- Download the `onnx-community/depth-anything-v2-small` ONNX model from Hugging Face, rename it `model.onnx`, and place it in the app's `Models` folder. Relief mode works out of the box without this.

No CUDA Toolkit, cuDNN, or separate NVIDIA driver install is required — GPU acceleration goes through DirectML, which ships with Windows.

---

## Quick start

1. Launch AnvilDepth.
2. Drag an image onto the window (or click the drop zone to browse for one).
3. Leave **Use AI** unchecked for the fast algorithmic Relief pipeline, or check it to use AI Depth mode (needs `model.onnx` installed — see above).
4. Adjust the sliders until the preview looks right for your material and depth. Relief mode updates live as you drag; AI mode needs a click of **Generate** to re-run.
5. Export as PNG, EXR, TIFF, or STL.

## Tips

- **EXR export** depends on your OpenCV build including OpenEXR support. If it fails to save, use 32-bit TIFF or 16-bit PNG instead — same precision, more reliable.
- **STL export** is capped at a 256×256 grid to keep triangle counts sane for slicers/CAM tools. If you need more resolution, that cap can be raised (ask whoever built your copy of the app).
- **Large textures (2048px+)** will feel slower to process — this is expected, it's a one-shot "click Generate and wait" tool, not real-time.
- **Seamless blend** is meant as a tiling aid for repeating textures; it isn't pixel-perfect at the corners.

---

## Support

This is a small, local tool — there's no online service or account behind it. If something looks wrong or crashes, check the on-screen status message first; most failures (missing model file, bad EXR support) show a plain-English reason.

---

Credits

With thanks to Mazze45 for guidance in refining and creating this tool.
