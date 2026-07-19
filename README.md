AnvilDepth RTX
Turn any image into a clean, CNC-ready depth map using your RTX GPU. Local, fast, no cloud.

AnvilDepth takes a photo, texture, or AI render and generates a high-contrast depth map with a pure black background, ready for CNC carving, 3D printing, and bas-relief.

What it does
AI depth estimation using Depth Anything V2 (Small for testing, Large for production)
Pure black background masking — no more washed-out gray backgrounds
Percentile normalization + detail boost for crisp ornaments and edges
16-bit PNG, 32-bit EXR, and STL relief export
DDS support (Skyrim SE/AE)
4 gamma variants to quickly pick the best contrast for your material
100% local — runs on your RTX card
Requirements
Hardware

Windows 10/11 64-bit
NVIDIA RTX GPU (3060 or better, 8GB+ VRAM recommended for Large model)
16GB RAM
Software
-.NET 8 SDK

Visual Studio 2026 Preview with.NET Desktop + C++ Desktop
NVIDIA Driver 555.85+
CUDA Toolkit 12.4
cuDNN 9 for CUDA 12
Optional: TensorRT 10 for max speed (~15ms vs ~35ms)

Models
Place in AnvilDepth/Models/:
