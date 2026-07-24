using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace AnvilDepth.Services;

/// <summary>
/// The algorithmic "Relief" pipeline: de-light -> frequency-band separation -> tone mapping.
/// This is a reconstruction from the slider names/defaults in MainWindow.xaml, not a copy of
/// a prior implementation — the exact math (band sigmas, de-light formula, tone curve shape)
/// is my best-guess starting point. Tune the constants marked below if it doesn't match what
/// you remember from the earlier project.
/// </summary>
public static class ImageProcessor
{
    /// <summary>Suggested starting values for the AI-mode sliders, computed from real statistics of
    /// the loaded image (not a learned model — plain OpenCV measurements). Values are clamped to
    /// each slider's actual UI range. Constants below are first-pass heuristics, not calibrated
    /// against a labeled dataset — expect to retune them after testing against real textures.</summary>
    public sealed record SuggestedSettings(
        float Flatten, float FlattenRadius,
        float MacroFreq, float LowFreq, float MidFreq, float HighFreq, float Detail, float MicroFreq,
        float Clarity, float Strength, string Explanation);

    /// <summary>Looks at the source color image's contrast, edge/texture density, and low-frequency
    /// structure to propose a starting point for the frequency-band and de-light sliders — meant as
    /// a "first guess to tune from," not a final answer. Runs entirely on the already-loaded image,
    /// no network, no model file.</summary>
    public static SuggestedSettings AnalyzeAndSuggest(byte[] bgra, int width, int height)
    {
        using var srcBgra = new Mat(height, width, MatType.CV_8UC4);
        Marshal.Copy(bgra, 0, srcBgra.Data, bgra.Length);
        using var bgr = new Mat();
        Cv2.CvtColor(srcBgra, bgr, ColorConversionCodes.BGRA2BGR);
        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        using var gray32f = new Mat();
        gray.ConvertTo(gray32f, MatType.CV_32FC1, 1.0 / 255.0);

        // Global contrast: std-dev of luminance. A flat, evenly-lit texture photo sits low (~0.05-0.1);
        // a high-contrast, strongly side-lit shot sits high (~0.2-0.3+).
        Cv2.MeanStdDev(gray32f, out var lumaMean, out var lumaStd);
        double globalContrast = lumaStd.Val0;

        // Fine-detail density via Laplacian variance (the standard "how much high-frequency content
        // is in this image" measure, also commonly used for blur detection). Scaled by image area so
        // it's roughly comparable across different texture resolutions.
        using var lap = new Mat();
        Cv2.Laplacian(gray32f, lap, MatType.CV_32FC1, ksize: 3);
        Cv2.MeanStdDev(lap, out _, out var lapStd);
        double detailDensity = lapStd.Val0 * 12.0; // empirical scale factor, not derived from data

        // Low-frequency structure: how much large-scale brightness variation survives a heavy blur.
        // High = big soft shapes worth bringing out with Macro; low = the image is mostly flat or
        // mostly fine texture with little large-scale form.
        using var heavyBlur = new Mat();
        Cv2.GaussianBlur(gray32f, heavyBlur, new OpenCvSharp.Size(0, 0), 32.0);
        Cv2.MeanStdDev(heavyBlur, out _, out var blurStd);
        double macroStructure = blurStd.Val0;

        float flatten = (float)Math.Clamp(0.25 + globalContrast * 1.2, 0.1, 0.9);
        float flattenRadius = (float)Math.Clamp(30 + macroStructure * 300, 20, 120);
        float macroFreq = (float)Math.Clamp(macroStructure * 5.5, 0f, 1.2f);
        float lowFreq = 1.0f; // keep the existing default — this band rarely needs to move much
        float midFreq = (float)Math.Clamp(0.8 + detailDensity * 0.3, 0.4f, 1.6f);
        float highFreq = (float)Math.Clamp(0.9 + detailDensity * 0.6, 0.4f, 2.2f);
        float detail = (float)Math.Clamp(0.3 + detailDensity * 0.8, 0.15f, 1.0f);
        float microFreq = (float)Math.Clamp(detailDensity * 0.5, 0f, 0.6f);
        float clarity = (float)Math.Clamp(0.15 + detailDensity * 0.35, 0f, 0.6f);
        float strength = (float)Math.Clamp(0.9 + globalContrast * 0.6, 0.6f, 1.6f);

        string explanation =
            $"Measured: contrast={globalContrast:0.00}, detail density={detailDensity:0.00}, " +
            $"macro structure={macroStructure:0.00}. " +
            (detailDensity > 0.6
                ? "This looks like a busy/high-detail texture — leaned into Detail/Micro/Clarity. "
                : "This looks like a fairly smooth image — kept Detail/Micro modest. ") +
            (macroStructure > 0.15
                ? "Found real large-scale shape variation — raised Macro to bring out big soft volumes."
                : "Not much large-scale shape variation detected — left Macro low.");

        return new SuggestedSettings(flatten, flattenRadius, macroFreq, lowFreq, midFreq, highFreq, detail, microFreq, clarity, strength, explanation);
    }

    public static float[] ProcessTextureAtlasAdvanced(
        byte[] bgra, int width, int height,
        float detail, float gamma, bool invert,
        float highlights, float midtones, float shadows,
        bool removeBg, bool useLab,
        float flatten, float flattenRadius,
        float lowFreq, float midFreq, float highFreq,
        bool seamless, float seamBlend,
        bool zeroMidGray, float zeroLevel,
        bool percentile, float loPct, float hiPct,
        float[]? bgMask = null)
    {
        using var srcBgra = new Mat(height, width, MatType.CV_8UC4);
        Marshal.Copy(bgra, 0, srcBgra.Data, bgra.Length);

        using var bgr = new Mat();
        Cv2.CvtColor(srcBgra, bgr, ColorConversionCodes.BGRA2BGR);

        using var luma = new Mat();
        if (useLab)
        {
            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
            Cv2.ExtractChannel(lab, luma, 0);
        }
        else
        {
            Cv2.CvtColor(bgr, luma, ColorConversionCodes.BGR2GRAY);
        }
        using var luma32f = new Mat();
        luma.ConvertTo(luma32f, MatType.CV_32FC1, 1.0 / 255.0);

        // --- 1. De-light: pull out a large-scale lighting gradient and subtract a fraction of it ---
        double lightSigma = Math.Max(3.0, flattenRadius / 2.5);
        using var lightMap = new Mat();
        Cv2.GaussianBlur(luma32f, lightMap, new OpenCvSharp.Size(0, 0), lightSigma);
        Scalar meanLight = Cv2.Mean(lightMap);
        using var lightDelta = new Mat();
        Cv2.Subtract(lightMap, new Scalar(meanLight.Val0), lightDelta);
        using var flattened = new Mat();
        Cv2.Subtract(luma32f, lightDelta * flatten, flattened);

        // --- 2. Frequency-band separation (fixed sigmas; Low/Mid/High/Detail are gains) ---
        using var b1 = new Mat(); Cv2.GaussianBlur(flattened, b1, new OpenCvSharp.Size(0, 0), 2.0);
        using var b2 = new Mat(); Cv2.GaussianBlur(flattened, b2, new OpenCvSharp.Size(0, 0), 8.0);
        using var b3 = new Mat(); Cv2.GaussianBlur(flattened, b3, new OpenCvSharp.Size(0, 0), 24.0);

        using var midBand = new Mat(); Cv2.Subtract(b2, b3, midBand);
        using var highBand = new Mat(); Cv2.Subtract(b1, b2, highBand);
        using var detailBand = new Mat(); Cv2.Subtract(flattened, b1, detailBand);

        using var combined = new Mat();
        Cv2.AddWeighted(b3, lowFreq, midBand, midFreq, 0, combined);
        Cv2.AddWeighted(combined, 1.0, highBand, highFreq, 0, combined);
        Cv2.AddWeighted(combined, 1.0, detailBand, detail, 0, combined);

        // --- 3. Tone mapping ---
        using var toned = new Mat();
        ApplyToneMapping(combined, toned, gamma, shadows, midtones, highlights, percentile, loPct, hiPct);

        if (invert)
        {
            using var invMat = new Mat();
            Cv2.Subtract(new Scalar(1.0), toned, invMat);
            invMat.CopyTo(toned);
        }

        if (removeBg)
        {
            // Prefer the AI-computed subject mask (works on any image, soft edges) — fall back
            // to the original alpha-channel threshold if no background-removal model is loaded
            // or the mask couldn't be computed, so this behaves exactly as before by default.
            if (bgMask != null)
                ApplyBackgroundMask(toned, bgMask, width, height);
            else
                ApplyBackgroundRemovalFromAlpha(toned, srcBgra);
        }

        if (seamless)
        {
            ApplySeamlessBlend(toned, seamBlend);
        }

        if (zeroMidGray || Math.Abs(zeroLevel) > 0.0001f)
        {
            using var shifted = new Mat();
            Cv2.Add(toned, new Scalar(zeroLevel - 0.5), shifted);
            Cv2.Min(shifted, new Scalar(1.0), shifted);
            Cv2.Max(shifted, new Scalar(0.0), shifted);
            shifted.CopyTo(toned);
        }

        return MatToFloatArray(toned, width, height);
    }

    /// <summary>Post-processes an AI-estimated depth map: de-light (flatten/radius), depth-native
    /// macro/micro frequency recombination (Low/Mid/High/Detail — this is what lets you suppress
    /// the AI's tendency to read dark albedo, like mineral flecks or wood knots, as if it were real
    /// surface depth), contrast/strength, optional detail borrowed from the source texture on top,
    /// tone mapping, optional background removal. sourceBgra may be null (texture detail injection
    /// is skipped if so — the depth-native frequency separation above still applies regardless).
    /// removeBg prefers bgMask (AI segmentation) when supplied, and otherwise falls back to
    /// thresholding sourceBgra's alpha channel — same behavior as the Relief pipeline.</summary>
    public static float[] ProcessForSculptOKQuality(
        float[] depth, int width, int height, byte[]? sourceBgra,
        float strength, float detail, float lowFreq, float midFreq, float highFreq, float gamma, bool invert,
        float highlights, float midtones, float shadows,
        bool zeroMidGray, float zeroLevel,
        bool removeBg = false, float[]? bgMask = null,
        float flatten = 0f, float flattenRadius = 40f, bool injectTextureDetail = true,
        float macroFreq = 0f, float microFreq = 0f, float clarity = 0f)
    {
        using var src = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(depth, 0, src.Data, depth.Length);

        // --- De-Light: same operation as the Relief pipeline's Flatten/Radius, now actually
        // wired into AI mode (previously these two sliders were read from the UI but never
        // reached this method at all — a real bug, not a "too subtle to see" issue). A large
        // flattenRadius here is also the direct fix for the "pillow effect" on tiling textures:
        // it removes the broad brightness gradient across the whole map before anything else
        // sees it, rather than only feathering the seam at the very edge.
        using var delighted = new Mat();
        if (flatten > 0.001f)
        {
            double lightSigma = Math.Max(3.0, flattenRadius / 2.5);
            using var lightMap = new Mat();
            Cv2.GaussianBlur(src, lightMap, new OpenCvSharp.Size(0, 0), lightSigma);
            Scalar meanLight = Cv2.Mean(lightMap);
            using var lightDelta = new Mat();
            Cv2.Subtract(lightMap, new Scalar(meanLight.Val0), lightDelta);
            Cv2.Subtract(src, lightDelta * flatten, delighted);
        }
        else
        {
            src.CopyTo(delighted);
        }

        // --- Depth-native macro/micro frequency separation ---
        // Low/Mid/High/Detail recombine the AI depth map's OWN bands (this is the real fix for
        // "the AI punches dark mineral spots/wood knots in as holes": set Low down to suppress
        // the AI's spurious large-scale shape errors from albedo, and rely on High/Detail for
        // genuine fine surface variation instead).
        using var b1 = new Mat(); Cv2.GaussianBlur(delighted, b1, new OpenCvSharp.Size(0, 0), 2.0);
        using var b2 = new Mat(); Cv2.GaussianBlur(delighted, b2, new OpenCvSharp.Size(0, 0), 8.0);
        using var b3 = new Mat(); Cv2.GaussianBlur(delighted, b3, new OpenCvSharp.Size(0, 0), 24.0);
        using var midBand = new Mat(); Cv2.Subtract(b2, b3, midBand);
        using var highBand = new Mat(); Cv2.Subtract(b1, b2, highBand);
        using var detailBand = new Mat(); Cv2.Subtract(delighted, b1, detailBand);

        using var recombined = new Mat();
        Cv2.AddWeighted(b3, lowFreq, midBand, midFreq, 0, recombined);
        Cv2.AddWeighted(recombined, 1.0, highBand, highFreq, 0, recombined);
        Cv2.AddWeighted(recombined, 1.0, detailBand, detail, 0, recombined);

        // --- Macro octave: broader than Low (sigma 48 vs 24) — huge, soft volume swells (a whole
        // shoulder or torso reading as one rounded mass) that the existing 3-sigma pyramid is too
        // narrow to isolate on its own. Purely additive on top of the existing recombination, and
        // defaults to 0 (off), so it never shifts output for anyone not using it — a genuinely new
        // band, not a restructuring of the existing four.
        if (macroFreq > 0.001f)
        {
            using var b0 = new Mat(); Cv2.GaussianBlur(delighted, b0, new OpenCvSharp.Size(0, 0), 48.0);
            using var macroBand = new Mat();
            Scalar meanB0 = Cv2.Mean(b0);
            Cv2.Subtract(b0, new Scalar(meanB0.Val0), macroBand);
            Cv2.AddWeighted(recombined, 1.0, macroBand, macroFreq, 0, recombined);
        }

        // --- Micro octave: finer than Detail (sigma 0.6 vs the sigma-2 cutoff Detail uses) — the
        // very finest sub-pixel grain: pores, cloth weave, engraved micro-texture. Also additive
        // and off by default for the same reason as Macro above.
        if (microFreq > 0.001f)
        {
            using var b4 = new Mat(); Cv2.GaussianBlur(delighted, b4, new OpenCvSharp.Size(0, 0), 0.6);
            using var microBand = new Mat();
            Cv2.Subtract(delighted, b4, microBand);
            Cv2.AddWeighted(recombined, 1.0, microBand, microFreq, 0, recombined);
        }

        // Contrast around the midpoint (now applied to the recombined depth, not raw AI output)
        using var centered = new Mat();
        Cv2.Subtract(recombined, new Scalar(0.5), centered);
        using var amplified = new Mat();
        Cv2.Add(centered * strength, new Scalar(0.5), amplified);

        // --- Clarity: a large-radius local-contrast pass (the same "clarity" slider concept from
        // photo editing) — distinct from just raising Mid/High, because it's an unsharp-mask style
        // boost applied AFTER contrast/strength amplification as a final polish, punching up how
        // "defined" every surface reads without touching the fine Detail/Micro grain or the overall
        // brightness curve. This is the single highest-leverage addition for the "crunchy, sculpted"
        // look in game-asset AO bakes — it's an local-contrast operator, not another additive band.
        if (clarity > 0.001f)
        {
            using var clarityBase = new Mat();
            Cv2.GaussianBlur(amplified, clarityBase, new OpenCvSharp.Size(0, 0), 16.0);
            using var clarityHighPass = new Mat();
            Cv2.Subtract(amplified, clarityBase, clarityHighPass);
            Cv2.AddWeighted(amplified, 1.0, clarityHighPass, clarity, 0, amplified);
        }

        // Optional additional layer: real surface detail borrowed from the source color texture
        // (stitching, engraving, surface grain that the AI depth map wouldn't otherwise capture).
        // This is on top of the depth-native separation above, not a replacement for it — kept
        // as its own toggle so existing setups that relied on this don't change unexpectedly.
        using var withDetail = new Mat();
        if (injectTextureDetail && sourceBgra != null && (detail > 0.001f || midFreq > 0.001f || highFreq > 0.001f || lowFreq > 0.001f))
        {
            using var srcBgraMat = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(sourceBgra, 0, srcBgraMat.Data, sourceBgra.Length);
            using var bgr = new Mat();
            Cv2.CvtColor(srcBgraMat, bgr, ColorConversionCodes.BGRA2BGR);
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            using var gray32f = new Mat();
            gray.ConvertTo(gray32f, MatType.CV_32FC1, 1.0 / 255.0);

            using var tb1 = new Mat(); Cv2.GaussianBlur(gray32f, tb1, new OpenCvSharp.Size(0, 0), 2.0);
            using var tb2 = new Mat(); Cv2.GaussianBlur(gray32f, tb2, new OpenCvSharp.Size(0, 0), 8.0);
            using var tb3 = new Mat(); Cv2.GaussianBlur(gray32f, tb3, new OpenCvSharp.Size(0, 0), 24.0);
            using var lowBand = new Mat();
            Scalar meanTB3 = Cv2.Mean(tb3);
            Cv2.Subtract(tb3, new Scalar(meanTB3.Val0), lowBand);
            using var texMidBand = new Mat(); Cv2.Subtract(tb2, tb3, texMidBand);
            using var texHighBand = new Mat(); Cv2.Subtract(tb1, tb2, texHighBand);
            using var texDetailBand = new Mat(); Cv2.Subtract(gray32f, tb1, texDetailBand);

            using var detailSignal = new Mat();
            Cv2.AddWeighted(lowBand, lowFreq, texMidBand, midFreq, 0, detailSignal);
            Cv2.AddWeighted(detailSignal, 1.0, texHighBand, highFreq, 0, detailSignal);
            Cv2.AddWeighted(detailSignal, 1.0, texDetailBand, detail, 0, detailSignal);

            Cv2.AddWeighted(amplified, 1.0, detailSignal, 1.5, 0, withDetail);
        }
        else
        {
            amplified.CopyTo(withDetail);
        }

        using var toned = new Mat();
        ApplyToneMapping(withDetail, toned, gamma, shadows, midtones, highlights, percentile: false, 0.02f, 0.98f);

        if (invert)
        {
            using var invMat = new Mat();
            Cv2.Subtract(new Scalar(1.0), toned, invMat);
            invMat.CopyTo(toned);
        }

        if (removeBg)
        {
            if (bgMask != null)
            {
                ApplyBackgroundMask(toned, bgMask, width, height);
            }
            else if (sourceBgra != null)
            {
                using var bgAlphaMat = new Mat(height, width, MatType.CV_8UC4);
                Marshal.Copy(sourceBgra, 0, bgAlphaMat.Data, sourceBgra.Length);
                ApplyBackgroundRemovalFromAlpha(toned, bgAlphaMat);
            }
        }

        if (zeroMidGray || Math.Abs(zeroLevel) > 0.0001f)
        {
            using var shifted = new Mat();
            Cv2.Add(toned, new Scalar(zeroLevel - 0.5), shifted);
            Cv2.Min(shifted, new Scalar(1.0), shifted);
            Cv2.Max(shifted, new Scalar(0.0), shifted);
            shifted.CopyTo(toned);
        }

        return MatToFloatArray(toned, width, height);
    }

    private static void ApplyToneMapping(Mat src, Mat dst, float gamma, float shadows, float midtones, float highlights,
        bool percentile, float loPct, float hiPct)
    {
        using var normalized = new Mat();
        if (percentile)
        {
            var (lo, hi) = ComputePercentiles(src, loPct, hiPct);
            double range = Math.Max(hi - lo, 1e-6);
            Cv2.Subtract(src, new Scalar(lo), normalized);
            normalized.ConvertTo(normalized, -1, 1.0 / range, 0);
        }
        else
        {
            Cv2.Normalize(src, normalized, 0, 1, NormTypes.MinMax);
        }
        Cv2.Min(normalized, new Scalar(1.0), normalized);
        Cv2.Max(normalized, new Scalar(0.0), normalized);

        using var gammaCorrected = new Mat();
        Cv2.Pow(normalized, 1.0 / Math.Max(0.01, gamma), gammaCorrected);

        // Three-zone level adjustment: shadows/midtones/highlights each scale their own weighted region.
        dst.Create(src.Size(), MatType.CV_32FC1);
        for (int y = 0; y < src.Rows; y++)
        {
            for (int x = 0; x < src.Cols; x++)
            {
                float v = gammaCorrected.At<float>(y, x);
                float shadowW = Math.Clamp(1f - 2f * v, 0f, 1f);
                float highlightW = Math.Clamp(2f * v - 1f, 0f, 1f);
                float midW = 1f - shadowW - highlightW;
                float adjusted = v * (shadowW * shadows + midW * midtones + highlightW * highlights);
                dst.Set(y, x, Math.Clamp(adjusted, 0f, 1f));
            }
        }
    }

    private static (float lo, float hi) ComputePercentiles(Mat src, float loPct, float hiPct)
    {
        var values = MatToFloatArray(src, src.Cols, src.Rows);
        Array.Sort(values);
        int loIdx = Math.Clamp((int)(values.Length * loPct), 0, values.Length - 1);
        int hiIdx = Math.Clamp((int)(values.Length * hiPct), 0, values.Length - 1);
        return (values[loIdx], values[hiIdx]);
    }

    /// <summary>Fallback background removal for when no AI segmentation mask is available: reads
    /// whatever alpha channel the source image already has and hard-thresholds it. Only works if
    /// the source was already a PNG with real transparency — kept for backward compatibility with
    /// installs that don't have Models\bg_remove.onnx.</summary>
    private static void ApplyBackgroundRemovalFromAlpha(Mat toned, Mat srcBgra)
    {
        using var alpha = new Mat();
        Cv2.ExtractChannel(srcBgra, alpha, 3);
        using var mask = new Mat();
        Cv2.Threshold(alpha, mask, 10, 255, ThresholdTypes.Binary);
        using var maskF = new Mat();
        mask.ConvertTo(maskF, MatType.CV_32FC1, 1.0 / 255.0);

        using var fg = new Mat();
        Cv2.Multiply(toned, maskF, fg);
        using var invMaskF = new Mat();
        Cv2.Subtract(new Scalar(1.0), maskF, invMaskF);
        using var bgPart = new Mat();
        Cv2.Multiply(new Mat(toned.Size(), MatType.CV_32FC1, new Scalar(0.5)), invMaskF, bgPart);
        Cv2.Add(fg, bgPart, toned);
    }

    /// <summary>Preferred background removal: blends to neutral gray outside the subject using a
    /// soft AI-predicted probability mask (from SegmentationEngine) instead of a hard alpha
    /// threshold — preserves natural edge falloff (hair, fur, semi-transparent trim) and works on
    /// any image, not just ones that already had transparency.</summary>
    private static void ApplyBackgroundMask(Mat toned, float[] mask, int width, int height)
    {
        using var maskMat = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(mask, 0, maskMat.Data, mask.Length);

        using var fg = new Mat();
        Cv2.Multiply(toned, maskMat, fg);
        using var invMask = new Mat();
        Cv2.Subtract(new Scalar(1.0), maskMat, invMask);
        using var bgPart = new Mat();
        Cv2.Multiply(new Mat(toned.Size(), MatType.CV_32FC1, new Scalar(0.5)), invMask, bgPart);
        Cv2.Add(fg, bgPart, toned);
    }

    /// <summary>Feather-blends opposite edges so the map tiles more cleanly.</summary>
    private static void ApplySeamlessBlend(Mat toned, float seamBlend)
    {
        int width = toned.Cols, height = toned.Rows;
        int blendX = Math.Clamp((int)(seamBlend * width * 0.1), 2, width / 4);
        int blendY = Math.Clamp((int)(seamBlend * height * 0.1), 2, height / 4);

        using var original = toned.Clone();
        for (int y = 0; y < height; y++)
        {
            for (int i = 0; i < blendX; i++)
            {
                float t = (i + 1f) / (blendX + 1f);
                float left = original.At<float>(y, i);
                float right = original.At<float>(y, width - 1 - i);
                toned.Set(y, i, left * t + right * (1 - t));
                toned.Set(y, width - 1 - i, right * t + left * (1 - t));
            }
        }
        using var afterX = toned.Clone();
        for (int x = 0; x < width; x++)
        {
            for (int i = 0; i < blendY; i++)
            {
                float t = (i + 1f) / (blendY + 1f);
                float top = afterX.At<float>(i, x);
                float bottom = afterX.At<float>(height - 1 - i, x);
                toned.Set(i, x, top * t + bottom * (1 - t));
                toned.Set(height - 1 - i, x, bottom * t + top * (1 - t));
            }
        }
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

    public static BitmapSource FloatArrayToBitmapSource(float[] data, int width, int height)
    {
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(Math.Clamp(data[i], 0f, 1f) * 255f);

        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), pixels, width, 0);
        bmp.Freeze();
        return bmp;
    }

    public static void SaveAs8Bit(float[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC1);
        var pixels = new byte[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (byte)(Math.Clamp(data[i], 0f, 1f) * 255f);
        Marshal.Copy(pixels, 0, mat.Data, pixels.Length);
        Cv2.ImWrite(path, mat);
    }

    public static void SaveAs16Bit(float[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_16UC1);
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = (ushort)(Math.Clamp(data[i], 0f, 1f) * 65535f);
        var bytes = new byte[pixels.Length * sizeof(ushort)];
        Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
        Marshal.Copy(bytes, 0, mat.Data, bytes.Length);
        Cv2.ImWrite(path, mat);
    }

    public static void SaveAsEXR(float[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        Cv2.ImWrite(path, mat);
    }

    /// <summary>
    /// Derives a cheap cavity/ambient-occlusion-style map from the depth data: recesses (areas
    /// lower than their local surroundings) come out dark, raised/flat areas stay light. This is
    /// the classic "difference of blurs" cavity trick — not a physically-based AO render, but a
    /// fast, good-enough approximation for texture work. Like the normal map, derived from depth
    /// already computed — no separate model.
    /// </summary>
    public static byte[] ComputeCavityMap(float[] depth, int width, int height, float strength, int blurRadius)
    {
        using var src = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(depth, 0, src.Data, depth.Length);

        using var blurred = new Mat();
        Cv2.GaussianBlur(src, blurred, new OpenCvSharp.Size(0, 0), Math.Max(1.0, blurRadius));

        // Positive where the surface dips below its local neighborhood average (a recess);
        // negative where it rises above it (a ridge). Scale, invert, center at mid-gray.
        using var cavity = new Mat();
        Cv2.Subtract(blurred, src, cavity); // blurred - src: positive in recesses
        using var scaled = new Mat();
        cavity.ConvertTo(scaled, MatType.CV_32FC1, strength, 0.5); // *strength, +0.5 to center

        var result = new byte[width * height];
        var vals = MatToFloatArray(scaled, width, height);
        for (int i = 0; i < vals.Length; i++)
            result[i] = (byte)(Math.Clamp(vals[i], 0f, 1f) * 255f);
        return result;
    }

    /// <summary>Wraps an 8-bit single-channel byte array (e.g. from ComputeCavityMap) as a
    /// displayable grayscale BitmapSource.</summary>
    public static BitmapSource GrayArrayToBitmapSource(byte[] gray, int width, int height)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Gray8, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), gray, width, 0);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Saves a cavity/AO map as an 8-bit grayscale PNG.</summary>
    public static void SaveCavityMapPng(byte[] gray, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC1);
        Marshal.Copy(gray, 0, mat.Data, gray.Length);
        Cv2.ImWrite(path, mat);
    }

    public static void SaveAsTiff32(float[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        Cv2.ImWrite(path, mat);
    }

    /// <summary>
    /// Derives a tangent-space normal map directly from an already-generated depth/height map via
    /// Sobel gradients. This is NOT a separate AI model — there's no ONNX network in this app that
    /// outputs normals; Depth-Anything only estimates depth. Treating the depth field as a height
    /// map, the surface gradient at each pixel gives a normal vector: N = normalize(-dx*strength,
    /// -dy*strength, 1), encoded to RGB the standard way (each axis mapped from [-1,1] to [0,255]).
    /// invertY flips the green channel — OpenGL-style engines (Unity, most game engines) expect Y+
    /// pointing "up" the slope; DirectX-style engines (Unreal) expect the opposite.
    /// </summary>
    public static byte[] ComputeNormalMap(float[] depth, int width, int height, float strength, bool invertY, bool useEdgePreservingSmooth = false)
    {
        using var src = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(depth, 0, src.Data, depth.Length);

        using var smoothed = new Mat();
        if (useEdgePreservingSmooth)
        {
            // Bilateral filter: smooths flat/noisy regions while preserving real edges, unlike a
            // plain Gaussian blur which softens edges right along with noise. Same general
            // technique used by PBRFusion4 (a purpose-built PBR depth/normal diffusion model) for
            // its own depth cleanup step before normal generation. d=9 neighborhood; sigmas tuned
            // for depth's 0..1 float range rather than 0..255 pixel values.
            Cv2.BilateralFilter(src, smoothed, 9, 0.1, 9);
        }
        else
        {
            // Light blur before differentiating — raw per-pixel Sobel on noisy/quantized depth data
            // produces a speckled normal map; a small blur trades a little fine detail for a much
            // cleaner result, which is what you want for a bakeable normal map rather than raw noise.
            Cv2.GaussianBlur(src, smoothed, new OpenCvSharp.Size(0, 0), 1.0);
        }

        using var gx = new Mat();
        using var gy = new Mat();
        Cv2.Sobel(smoothed, gx, MatType.CV_32F, 1, 0, ksize: 3);
        Cv2.Sobel(smoothed, gy, MatType.CV_32F, 0, 1, ksize: 3);

        float ySign = invertY ? -1f : 1f;
        var result = new byte[width * height * 4]; // BGRA, matches WriteableBitmap's Bgra32 format
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float dx = gx.At<float>(y, x) * strength;
                float dy = gy.At<float>(y, x) * strength * ySign;

                float nx = -dx, ny = -dy, nz = 1f;
                float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                nx /= len; ny /= len; nz /= len;

                int i = (y * width + x) * 4;
                result[i + 0] = (byte)Math.Clamp((nz * 0.5f + 0.5f) * 255f, 0, 255); // B <- Z
                result[i + 1] = (byte)Math.Clamp((ny * 0.5f + 0.5f) * 255f, 0, 255); // G <- Y
                result[i + 2] = (byte)Math.Clamp((nx * 0.5f + 0.5f) * 255f, 0, 255); // R <- X
                result[i + 3] = 255; // A
            }
        }
        return result;
    }

    /// <summary>Wraps an already-BGRA byte array (e.g. from ComputeNormalMap) as a displayable
    /// BitmapSource — simpler than FloatArrayToBitmapSource since there's no float->byte mapping
    /// to do, just a direct pixel copy.</summary>
    public static BitmapSource BgraArrayToBitmapSource(byte[] bgra, int width, int height)
    {
        var bmp = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Saves a normal map as an 8-bit RGB PNG — the standard format normal maps are
    /// consumed in (game engines, sculpting tools); alpha is dropped since it's unused here.</summary>
    public static void SaveNormalMapPng(byte[] bgra, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_8UC4);
        Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
        using var bgr = new Mat();
        Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
        Cv2.ImWrite(path, bgr);
    }
}

