# AnvilDepth

Merged project: your AnvilDepth UI (drag-and-drop, Relief sliders, multi-format export)
+ DepthMapper's ONNX depth engine, under one name. DepthMapper is retired as a separate
project — everything lives here now.

Two generation modes, toggled by the **Use AI** checkbox:

- **Relief mode (default, "Use AI" unchecked)** — pure algorithmic pipeline: de-light →
  low/mid/high/detail frequency-band separation → tone mapping. This is what your
  reference PNG was close to, and it doesn't need any model file.
- **AI mode ("Use AI" checked)** — local Depth-Anything V2 (ONNX, offline). "HQ Tiled"
  splits the source into overlapping tiles run through the model near its native
  resolution and feather-blends them, since a single global pass squashes a large
  texture atlas down to 518x518 and loses most of the detail.

**Honesty check:** I don't have your original `ImageProcessor`/`DepthEngine`/`StlExporter`
— those weren't in what you uploaded, so this is a from-scratch reconstruction based on
the slider names and defaults in your XAML. The overall structure (de-light → frequency
bands → tone curve) should behave the way the UI implies, but exact constants (blur
sigmas, de-light formula, tone-curve shape) are my best judgment, not a match to
whatever the earlier project actually did. Compare against your reference and tune the
values marked in `ImageProcessor.cs` if it's off.

## Setup

1. Open `AnvilDepth.sln` in Visual Studio 2022+.
2. NuGet restores: `Microsoft.ML.OnnxRuntime.DirectML`, `OpenCvSharp4`, `OpenCvSharp4.runtime.win`.
   - I removed `Microsoft.ML.OnnxRuntime.Gpu` from the csproj — it and `.DirectML` both
     ship a native `onnxruntime.dll`, and having both in one project causes the wrong
     one to load unpredictably. DirectML covers any DX12 GPU (NVIDIA/AMD/Intel) without
     needing the separate CUDA/cuDNN toolkit `.Gpu` requires. If you specifically want
     CUDA, swap the package back and drop DirectML instead — don't run both.
3. For AI mode: download `onnx-community/depth-anything-v2-small` (`onnx/model.onnx`)
   from Hugging Face, rename to `model.onnx`, place in `AnvilDepth/Models/model.onnx`.
   Relief mode works without this.

## Known rough edges to watch for

- **EXR export** depends on your local OpenCV build including OpenEXR support — not
  guaranteed in all prebuilt `OpenCvSharp4.runtime.win` versions. If `Save 32-bit EXR`
  throws, 32-bit TIFF or 16-bit PNG are safer fallbacks with the same precision options.
- **Performance**: the tone-mapping and seamless-blend passes use per-pixel `Mat`
  indexers for clarity, which is fine for a one-shot "Generate" click but will feel slow
  on very large atlases (2048px+). `AllowUnsafeBlocks` is already on in the csproj if you
  want to swap those loops for raw pointer access later.
- **Seamless blend** cross-contaminates slightly where the column-blend and row-blend
  zones overlap in the corners — minor artifact, fine for tiling aid, not pixel-perfect.
- **STL export** downsamples to a 256x256 grid max (full-res would be tens of millions
  of triangles). Change `MaxGridSize` in `StlExporter.cs` if you want more/less detail.

## File map

- `MainWindow.xaml(.cs)`, `App.xaml(.cs)` — your existing UI, unchanged
- `Services/ImageProcessor.cs` — Relief pipeline + AI post-processing + all save formats
- `Services/DepthEngine.cs` — ONNX inference, global + tiled HQ passes
- `Services/StlExporter.cs` — heightmap → binary STL mesh
