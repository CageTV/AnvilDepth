using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AnvilDepth.Services;

public sealed record DepthResult(float[] Depth, int Width, int Height);

/// <summary>
/// Runs Depth-Anything V2 (local ONNX, offline) to produce a relative depth map.
///
/// "HQ Tiled" is implemented literally: the model's native input resolution (518x518 for
/// the standard Small/Base/Large exports, or whatever InitializeAsync detects for others)
/// is far smaller than a large texture atlas (e.g. 2048x2048), so a single global pass
/// throws away most detail. When HighQuality is on, the source is additionally split into
/// overlapping tiles that are each run through the model near-native-resolution, feathered
/// back together, and blended as a *local* detail layer on top of the coherent global pass
/// (so tile seams don't show up as large-scale depth drift).
/// </summary>
public sealed class DepthEngine : IDisposable
{
    // Default net size for models with dynamic (unconstrained) input dims — this is what
    // Depth-Anything V2 Small/Base/Large are normally exported as. If a given model.onnx
    // instead has a *fixed* input shape baked in (some third-party re-exports do this),
    // InitializeAsync overrides this from the model's own metadata so tiling/resizing
    // always matches what the model actually expects instead of assuming 518.
    private const int DefaultNetSize = 518;
    private static readonly float[] Mean = { 0.485f, 0.456f, 0.406f };
    private static readonly float[] Std = { 0.229f, 0.224f, 0.225f };

    private InferenceSession? _session;
    private int _netW = DefaultNetSize;
    private int _netH = DefaultNetSize;

    // Guards every read/dispose/swap of _session. This is the actual fix for the crash-on-model-
    // switch bug: LoadModelAsync used to do `_session?.Dispose(); _session = newSession;` with no
    // synchronization against EstimateDepthAsync, which reads _session on a background thread and
    // calls .Run() on it — possibly multiple times across HQ Tiled's tiles. Disposing an
    // InferenceSession while another thread is mid-Run() is a use-after-dispose on a native ONNX
    // Runtime handle, which typically doesn't throw a catchable .NET exception; it can hard-crash
    // the process (access violation), bypassing AppDomain.UnhandledException entirely. Holding
    // this semaphore for the full duration of both LoadModelAsync's swap and EstimateDepthAsync's
    // entire body makes the two mutually exclusive: a model switch requested mid-generation now
    // just waits for the generation to finish, instead of crashing.
    private readonly SemaphoreSlim _sessionLock = new(1, 1);

    /// <summary>Loads Models\model.onnx (the default/Small model) at startup — kept for backward compatibility
    /// with existing installs that only have a single model.onnx.</summary>
    public Task<string> InitializeAsync() => LoadModelAsync("model.onnx");

    /// <summary>
    /// Loads (or swaps to) a specific model file from the Models folder, e.g. "model.onnx" (Small),
    /// "model_base.onnx", or "model_large.onnx". Safe to call after a model is already loaded —
    /// the new session only replaces the old one on success, so a failed swap (missing file,
    /// incompatible export) leaves the previously working model active instead of leaving the
    /// engine in a broken state.
    /// </summary>
    public Task<string> LoadModelAsync(string modelFileName)
    {
        return Task.Run(() =>
        {
            Logger.Log($"DepthEngine.LoadModelAsync: requested '{modelFileName}'");
            string modelPath = Path.Combine(AppContext.BaseDirectory, "Models", modelFileName);
            if (!File.Exists(modelPath))
            {
                Logger.Log($"DepthEngine.LoadModelAsync: file not found at '{modelPath}'");
                return $"Model not found: Models\\{modelFileName}";
            }

            var options = new SessionOptions();
            bool gpu = false;
            try
            {
                // DirectML EP requirements (per Microsoft's ONNX Runtime docs): memory pattern
                // optimization must be disabled and execution must be sequential. Without these,
                // ORT can silently fall back to CPU for part or all of the graph — no exception,
                // no warning, just quietly slower — which is exactly the symptom of "GPU Ready"
                // showing while a GPU (e.g. RTX 5070) sits idle and CPU usage spikes instead.
                options.EnableMemoryPattern = false;
                options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
                options.AppendExecutionProvider_DML();
                gpu = true;
            }
            catch
            {
                // No DX12-capable GPU, or DirectML unavailable — falls back to CPU automatically.
            }

            try
            {
                // Session *construction* doesn't touch the shared _session field yet, so it can
                // happen outside the lock — only the swap below needs to be exclusive.
                var newSession = new InferenceSession(modelPath, options);

                // Depth-Anything V2 models normally export with dynamic height/width (dim -1),
                // in which case we keep DefaultNetSize. But if this particular .onnx has a fixed
                // shape baked in (dim > 0), honor it — otherwise Run() would throw a shape
                // mismatch the first time a differently-sized model (e.g. a Base/Large variant
                // exported without dynamic axes) gets loaded.
                int netH = DefaultNetSize, netW = DefaultNetSize;
                var inputDims = newSession.InputMetadata.Values.First().Dimensions;
                if (inputDims.Length == 4)
                {
                    if (inputDims[2] > 0) netH = inputDims[2];
                    if (inputDims[3] > 0) netW = inputDims[3];
                }

                // Only swap over now that the new session loaded successfully — old model
                // stays active if anything above throws. Waits here if a generation using the
                // OLD session is still running (see _sessionLock doc comment above).
                Logger.Log($"DepthEngine.LoadModelAsync: '{modelFileName}' loaded OK ({(gpu ? "GPU/DirectML" : "CPU")}), waiting for session lock to swap in...");
                _sessionLock.Wait();
                try
                {
                    _session?.Dispose();
                    _session = newSession;
                    _netH = netH;
                    _netW = netW;
                }
                finally
                {
                    _sessionLock.Release();
                }
                Logger.Log($"DepthEngine.LoadModelAsync: '{modelFileName}' is now the active session ({netW}x{netH}).");

                return gpu ? $"AI Ready — {modelFileName} (GPU/DirectML)" : $"AI Ready — {modelFileName} (CPU)";
            }
            catch (Exception ex)
            {
                Logger.LogException($"DepthEngine.LoadModelAsync('{modelFileName}')", ex);
                return $"Failed to load {modelFileName}: {ex.Message}";
            }
        });
    }

    public Task<DepthResult> EstimateDepthAsync(string path, bool highQuality, IProgress<double>? progress = null)
    {
        return Task.Run(() =>
        {
            Logger.Log($"DepthEngine.EstimateDepthAsync: starting, path='{path}', highQuality={highQuality}, waiting for session lock...");
            _sessionLock.Wait();
            Logger.Log("DepthEngine.EstimateDepthAsync: got session lock, running.");
            try
            {
                if (_session == null)
                    throw new InvalidOperationException("AI model isn't loaded. Uncheck 'Use AI' for Relief mode, or add Models\\model.onnx.");

                using var src = Cv2.ImRead(path, ImreadModes.Color);
                if (src.Empty())
                    throw new InvalidOperationException($"Could not read image: {path}");

                int outW = src.Width, outH = src.Height;
                progress?.Report(0.05); // image loaded

                Mat globalDepth;
                try
                {
                    globalDepth = RunSingleTile(src, new Rect(0, 0, src.Width, src.Height));
                }
                catch (OnnxRuntimeException ex)
                {
                    throw new InvalidOperationException(
                        $"The loaded model (Models\\model.onnx) rejected the input it was given ({_netW}x{_netH}, 3-channel). " +
                        $"This usually means it's a different Depth-Anything V2 export than expected (wrong input layout, " +
                        $"or a fixed shape that doesn't match {_netW}x{_netH}). Original error: {ex.Message}", ex);
                }
                using var _globalDepthDisposeGuard = globalDepth;
                using var globalFullRes = new Mat();
                Cv2.Resize(globalDepth, globalFullRes, new OpenCvSharp.Size(outW, outH), 0, 0, InterpolationFlags.Cubic);
                // Without HQ Tiled, the global pass IS the whole job — done. With it, the global pass
                // is just the coarse base layer and most of the time is about to be spent tiling.
                progress?.Report(highQuality ? 0.15 : 0.95);

                Mat finalDepth = globalFullRes;
                Mat? tiledDetail = null;

                if (highQuality)
                {
                    tiledDetail = RunTiledDetail(src, outW, outH, progress);
                    // Inject only the *local* structure from the tiled pass (high-pass it first)
                    // so per-tile brightness differences don't create visible seams.
                    using var tiledLowPass = new Mat();
                    Cv2.GaussianBlur(tiledDetail, tiledLowPass, new OpenCvSharp.Size(0, 0), 24.0);
                    using var tiledHighPass = new Mat();
                    Cv2.Subtract(tiledDetail, tiledLowPass, tiledHighPass);

                    using var blended = new Mat();
                    Cv2.AddWeighted(globalFullRes, 1.0, tiledHighPass, 0.6, 0, blended);
                    Cv2.Min(blended, new Scalar(1.0), blended);
                    Cv2.Max(blended, new Scalar(0.0), blended);
                    finalDepth = blended.Clone();
                }

                var result = MatToFloatArray(finalDepth, outW, outH);
                tiledDetail?.Dispose();
                if (highQuality) finalDepth.Dispose();
                progress?.Report(1.0);
                Logger.Log("DepthEngine.EstimateDepthAsync: completed successfully.");

                return new DepthResult(result, outW, outH);
            }
            catch (Exception ex)
            {
                Logger.LogException("DepthEngine.EstimateDepthAsync", ex);
                throw;
            }
            finally
            {
                _sessionLock.Release();
                Logger.Log("DepthEngine.EstimateDepthAsync: released session lock.");
            }
        });
    }

    /// <summary>Runs one region of the source image through the model, returns a depth Mat sized to whatever the model outputs (0..1, white = near).</summary>
    ///
    /// Pillow/mattress-effect fix: a plain crop hands the model a hard, information-less boundary
    /// at every edge, and depth models tend to read "nothing beyond this edge" as "receding" —
    /// which is exactly the inflated-mattress look on tiling textures. Before resizing to the
    /// model's input size, we pad the crop (mirrored content, not real neighboring pixels — see
    /// below) so the model sees a continuation instead of a cliff, then crop the corresponding
    /// inner window back out of its output afterward so the returned Mat still represents only
    /// the requested region.
    private Mat RunSingleTile(Mat src, Rect region)
    {
        using var cropped = new Mat(src, region);

        int padPx = Math.Max(8, (int)(Math.Min(region.Width, region.Height) * 0.12));
        using var padded = new Mat();
        Cv2.CopyMakeBorder(cropped, padded, padPx, padPx, padPx, padPx, BorderTypes.Reflect101);

        using var resized = new Mat();
        Cv2.Resize(padded, resized, new OpenCvSharp.Size(_netW, _netH), 0, 0, InterpolationFlags.Cubic);
        using var rgb = new Mat();
        Cv2.CvtColor(resized, rgb, ColorConversionCodes.BGR2RGB);

        var input = new DenseTensor<float>(new[] { 1, 3, _netH, _netW });
        for (int y = 0; y < _netH; y++)
        {
            for (int x = 0; x < _netW; x++)
            {
                var px = rgb.At<Vec3b>(y, x);
                input[0, 0, y, x] = (px.Item0 / 255f - Mean[0]) / Std[0];
                input[0, 1, y, x] = (px.Item1 / 255f - Mean[1]) / Std[1];
                input[0, 2, y, x] = (px.Item2 / 255f - Mean[2]) / Std[2];
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_session!.InputMetadata.Keys.First(), input)
        };

        using var results = _session.Run(inputs);

        // Some Depth-Anything V2 exports (esp. third-party Base/Large conversions) emit more
        // than one output — e.g. intermediate features alongside the depth map. Prefer an
        // output literally named "predicted_depth" (the standard HF export name); otherwise
        // fall back to the highest-rank tensor, since a depth map is rank-3/4 while auxiliary
        // outputs are often lower-rank; only if that's still ambiguous do we take the first.
        var depthResult =
            results.FirstOrDefault(r => r.Name.Equals("predicted_depth", StringComparison.OrdinalIgnoreCase))
            ?? results.OrderByDescending(r => r.AsTensor<float>().Dimensions.Length).First();

        var outTensor = depthResult.AsTensor<float>();
        var dims = outTensor.Dimensions;
        int h = dims.Length == 4 ? dims[2] : dims[1];
        int w = dims.Length == 4 ? dims[3] : dims[2];
        var raw = outTensor.ToArray();

        float min = raw.Min(), max = raw.Max();
        float range = Math.Max(max - min, 1e-6f);

        using var depthPadded = new Mat(h, w, MatType.CV_32FC1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // Depth-Anything outputs disparity (higher = closer); invert to white = near.
                depthPadded.Set(y, x, 1f - (raw[y * w + x] - min) / range);
            }
        }

        // depthPadded represents the padded field of view (region + mirrored margin), in the
        // model's own output resolution — crop out the inner window matching the true (unpadded)
        // region so the returned Mat represents only what the caller asked for.
        double padFracX = (double)padPx / padded.Width;
        double padFracY = (double)padPx / padded.Height;
        int innerX0 = Math.Clamp((int)Math.Round(w * padFracX), 0, w - 1);
        int innerY0 = Math.Clamp((int)Math.Round(h * padFracY), 0, h - 1);
        int innerW = Math.Max(1, w - innerX0 * 2);
        int innerH = Math.Max(1, h - innerY0 * 2);

        return depthPadded[new Rect(innerX0, innerY0, innerW, innerH)].Clone();
    }

    private Mat RunTiledDetail(Mat src, int outW, int outH, IProgress<double>? progress = null)
    {
        int tilesPerAxis = 3;
        int overlapPx = Math.Max(24, (int)(Math.Min(src.Width, src.Height) * 0.08));
        var xRanges = TileRanges(src.Width, tilesPerAxis, overlapPx);
        var yRanges = TileRanges(src.Height, tilesPerAxis, overlapPx);

        using var accum = new Mat(outH, outW, MatType.CV_32FC1, new Scalar(0));
        using var weightSum = new Mat(outH, outW, MatType.CV_32FC1, new Scalar(0));

        int totalTiles = xRanges.Count * yRanges.Count;
        int tilesDone = 0;

        foreach (var (x0, x1) in xRanges)
        {
            foreach (var (y0, y1) in yRanges)
            {
                var region = new Rect(x0, y0, x1 - x0, y1 - y0);
                using var tileDepth = RunSingleTile(src, region);
                using var tileResized = new Mat();
                Cv2.Resize(tileDepth, tileResized, new OpenCvSharp.Size(region.Width, region.Height), 0, 0, InterpolationFlags.Cubic);
                using var weight = FeatherWindow(region.Width, region.Height, overlapPx);

                using var accumRoi = new Mat(accum, region);
                using var weightRoi = new Mat(weightSum, region);
                using var weighted = new Mat();
                Cv2.Multiply(tileResized, weight, weighted);
                Cv2.Add(accumRoi, weighted, accumRoi);
                Cv2.Add(weightRoi, weight, weightRoi);

                tilesDone++;
                Logger.Log($"DepthEngine.RunTiledDetail: tile {tilesDone}/{totalTiles} done (region {region.X},{region.Y} {region.Width}x{region.Height}).");
                // Tiling spans 0.15..0.95 of the overall job — the global pass already reported 0.15,
                // and 0.95..1.0 is reserved for the final blend/array-copy after this returns.
                progress?.Report(0.15 + 0.80 * tilesDone / totalTiles);
            }
        }

        using var safeWeight = new Mat();
        Cv2.Max(weightSum, new Scalar(1e-4), safeWeight);
        var result = new Mat();
        Cv2.Divide(accum, safeWeight, result);
        return result;
    }

    private static List<(int start, int end)> TileRanges(int total, int count, int overlapPx)
    {
        var ranges = new List<(int, int)>();
        if (count <= 1 || total <= overlapPx * 2)
        {
            ranges.Add((0, total));
            return ranges;
        }

        int tileSize = (total + overlapPx * (count - 1)) / count;
        int step = tileSize - overlapPx;
        for (int i = 0; i < count; i++)
        {
            int start = Math.Min(i * step, Math.Max(0, total - tileSize));
            int end = Math.Min(start + tileSize, total);
            ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>Linear feather ramp near tile edges so overlapping tiles blend smoothly instead of seaming.</summary>
    private static Mat FeatherWindow(int w, int h, int featherPx)
    {
        var window = new Mat(h, w, MatType.CV_32FC1);
        int fx = Math.Min(featherPx, w / 2);
        int fy = Math.Min(featherPx, h / 2);
        for (int y = 0; y < h; y++)
        {
            float wy = fy > 0 ? Math.Min(1f, Math.Min((y + 1f) / fy, (h - y) / (float)fy)) : 1f;
            for (int x = 0; x < w; x++)
            {
                float wx = fx > 0 ? Math.Min(1f, Math.Min((x + 1f) / fx, (w - x) / (float)fx)) : 1f;
                window.Set(y, x, Math.Max(0.05f, wx * wy));
            }
        }
        return window;
    }

    private static float[] MatToFloatArray(Mat mat, int w, int h)
    {
        bool cloned = !mat.IsContinuous();
        Mat contiguous = cloned ? mat.Clone() : mat;
        var result = new float[w * h];
        Marshal.Copy(contiguous.Data, result, 0, w * h);
        if (cloned) contiguous.Dispose();
        return result;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _sessionLock.Dispose();
    }
}
