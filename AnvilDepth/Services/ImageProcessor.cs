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
    public static float[] ProcessTextureAtlasAdvanced(
        byte[] bgra, int width, int height,
        float detail, float gamma, bool invert,
        float highlights, float midtones, float shadows,
        bool removeBg, bool useLab,
        float flatten, float flattenRadius,
        float lowFreq, float midFreq, float highFreq,
        bool seamless, float seamBlend,
        bool zeroMidGray, float zeroLevel,
        bool percentile, float loPct, float hiPct)
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
            ApplyBackgroundRemoval(toned, srcBgra);
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

    /// <summary>Post-processes an AI-estimated depth map: contrast/strength, real detail injection from the
    /// source texture, tone mapping. sourceBgra may be null (detail injection is skipped if so).</summary>
    public static float[] ProcessForSculptOKQuality(
        float[] depth, int width, int height, byte[]? sourceBgra,
        float strength, float detail, float lowFreq, float midFreq, float highFreq, float gamma, bool invert,
        float highlights, float midtones, float shadows,
        bool zeroMidGray, float zeroLevel)
    {
        using var src = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(depth, 0, src.Data, depth.Length);

        // Contrast around the midpoint
        using var centered = new Mat();
        Cv2.Subtract(src, new Scalar(0.5), centered);
        using var amplified = new Mat();
        Cv2.Add(centered * strength, new Scalar(0.5), amplified);

        // Sobel *magnitude* discards sign/direction, so every edge becomes a thin bright outline
        // regardless of which way the surface curves — that's the "scribble/noise" look. Use the same
        // signed band-pass (difference-of-blurs) the Relief pipeline uses instead: it preserves which
        // side of a bump is locally brighter vs. darker, which is what actually reads as rounded relief
        // instead of an outline.
        using var withDetail = new Mat();
        if (sourceBgra != null && (detail > 0.001f || midFreq > 0.001f || highFreq > 0.001f || lowFreq > 0.001f))
        {
            using var srcBgraMat = new Mat(height, width, MatType.CV_8UC4);
            Marshal.Copy(sourceBgra, 0, srcBgraMat.Data, sourceBgra.Length);
            using var bgr = new Mat();
            Cv2.CvtColor(srcBgraMat, bgr, ColorConversionCodes.BGRA2BGR);
            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
            using var gray32f = new Mat();
            gray.ConvertTo(gray32f, MatType.CV_32FC1, 1.0 / 255.0);

            // Four signed bands, same sigmas as the Relief pipeline:
            //  - low    (24+):   broad shape from the source texture itself, mean-centered so it adds
            //                     contrast rather than fighting the AI's own macro depth as a second vote
            //  - mid    (8->24): rounded mass — a whole buckle or shoulder plate as one soft volume
            //  - high   (2->8):  rounded detail — individual studs, strap edges
            //  - detail (0->2):  fine texture — stitching, engraving, surface grain
            using var b1 = new Mat(); Cv2.GaussianBlur(gray32f, b1, new OpenCvSharp.Size(0, 0), 2.0);
            using var b2 = new Mat(); Cv2.GaussianBlur(gray32f, b2, new OpenCvSharp.Size(0, 0), 8.0);
            using var b3 = new Mat(); Cv2.GaussianBlur(gray32f, b3, new OpenCvSharp.Size(0, 0), 24.0);
            using var lowBand = new Mat();
            Scalar meanB3 = Cv2.Mean(b3);
            Cv2.Subtract(b3, new Scalar(meanB3.Val0), lowBand);
            using var midBand = new Mat(); Cv2.Subtract(b2, b3, midBand);
            using var highBand = new Mat(); Cv2.Subtract(b1, b2, highBand);
            using var detailBand = new Mat(); Cv2.Subtract(gray32f, b1, detailBand);

            using var detailSignal = new Mat();
            Cv2.AddWeighted(lowBand, lowFreq, midBand, midFreq, 0, detailSignal);
            Cv2.AddWeighted(detailSignal, 1.0, highBand, highFreq, 0, detailSignal);
            Cv2.AddWeighted(detailSignal, 1.0, detailBand, detail, 0, detailSignal);

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

    private static void ApplyBackgroundRemoval(Mat toned, Mat srcBgra)
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

    public static void SaveAsTiff32(float[] data, int width, int height, string path)
    {
        using var mat = new Mat(height, width, MatType.CV_32FC1);
        Marshal.Copy(data, 0, mat.Data, data.Length);
        Cv2.ImWrite(path, mat);
    }
}
