
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace AnvilDepth.Services
{
    public class DepthResult{ public float[] Depth = Array.Empty<float>(); public int Width; public int Height; }
    public class DepthEngine
    {
        private InferenceSession? session;
        private string modelPath = "";
        private int inputSize = 518;
        public async Task<string> InitializeAsync()
        {
            return await Task.Run(() =>
            {
                try{
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string[] search = new[] { Path.Combine(baseDir, "Models"), Path.Combine(baseDir, "..", "..", "..", "Models"), "Models" };
                    string? found = null;
                    foreach(var d in search){
                        if(!Directory.Exists(d)) continue;
                        var files = Directory.GetFiles(d, "*.onnx", SearchOption.AllDirectories);
                        if(files.Length>0){ found = files.OrderByDescending(f=> new FileInfo(f).Length).First(); break; }
                    }
                    if(found==null){
                        var here = Directory.GetFiles(baseDir, "*.onnx");
                        if(here.Length>0) found = here[0];
                    }
                    if(found==null) return "No ONNX model - Scene mode won't work, but Texture mode will";
                    modelPath = found;
                    try{ var opts = new SessionOptions(); opts.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL; opts.AppendExecutionProvider("CUDA"); session = new InferenceSession(modelPath, opts); return $"OK RTX CUDA - {Path.GetFileName(modelPath)}"; }catch{}
                    try{ var opts2 = new SessionOptions(); opts2.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL; opts2.AppendExecutionProvider("DML"); session = new InferenceSession(modelPath, opts2); return $"OK RTX DirectML - {Path.GetFileName(modelPath)}"; }catch{}
                    try{ var opts3 = new SessionOptions(); opts3.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL; session = new InferenceSession(modelPath, opts3); return $"OK CPU - {Path.GetFileName(modelPath)}"; }catch(Exception ex){ return $"Model found but failed to load: {ex.Message}"; }
                }catch(Exception ex){ return $"Init error: {ex.Message}"; }
            });
        }

        private Mat LoadMatWithDdsSupport(string path)
        {
            if(Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var (bgra, w, h) = DdsCodec.Decode(path);
                    var mat = new Mat(h, w, MatType.CV_8UC4);
                    Marshal.Copy(bgra, 0, mat.Data, bgra.Length);
                    var bgr = new Mat();
                    Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
                    mat.Dispose();
                    return bgr;
                }
                catch{}
            }
            var src = Cv2.ImRead(path, ImreadModes.Color);
            if(src.Empty()) throw new Exception($"Failed to load image {path}");
            return src;
        }

        public async Task<DepthResult> EstimateDepthAsync(string imagePath, bool highQuality)
        {
            return await Task.Run(()=>{
                if(session==null) throw new Exception("Model not initialized - check Models folder has .onnx");
                using var src = LoadMatWithDdsSupport(imagePath);
                if(src.Empty()) throw new Exception($"Failed to load image {imagePath}");
                int origW = src.Width; int origH = src.Height;
                int size = highQuality ? 1024 : inputSize;
                using var resized = new Mat(); Cv2.Resize(src, resized, new Size(size, size));
                float[] input = new float[1*3*size*size];
                for(int y=0;y<size;y++){ for(int x=0;x<size;x++){ var vec = resized.At<Vec3b>(y,x); float b = vec.Item0 / 255f; float g = vec.Item1 / 255f; float r = vec.Item2 / 255f; r = (r - 0.5f) / 0.5f; g = (g - 0.5f) / 0.5f; b = (b - 0.5f) / 0.5f; int idx = y*size + x; input[0*size*size + idx] = r; input[1*size*size + idx] = g; input[2*size*size + idx] = b; } }
                var inputTensor = new DenseTensor<float>(input, new[] {1,3,size,size});
                var inputs = new[] { NamedOnnxValue.CreateFromTensor("pixel_values", inputTensor) };
                float[] depthData; int dW, dH;
                using(var results = session.Run(inputs)){
                    var output = results.First(); var tensor = output.AsTensor<float>(); var dims = tensor.Dimensions.ToArray();
                    if(dims.Length==4){ dH = dims[2]; dW = dims[3]; depthData = new float[dH*dW]; int c=0; for(int y=0;y<dH;y++) for(int x=0;x<dW;x++) depthData[c++] = tensor[0,0,y,x]; }
                    else if(dims.Length==3){ dH = dims[1]; dW = dims[2]; depthData = new float[dH*dW]; int c=0; for(int y=0;y<dH;y++) for(int x=0;x<dW;x++) depthData[c++] = tensor[0,y,x]; }
                    else{ throw new Exception($"Unexpected output dims {string.Join("x", dims)}"); }
                }
                float[,] depthMat = new float[dH, dW]; for(int y=0;y<dH;y++) for(int x=0;x<dW;x++) depthMat[y,x]=depthData[y*dW+x];
                var mat = Mat.FromArray(depthMat); var resizedDepth = new Mat(); Cv2.Resize(mat, resizedDepth, new Size(origW, origH), interpolation: InterpolationFlags.Linear);
                float[] finalDepth = new float[origW*origH]; for(int y=0;y<origH;y++) for(int x=0;x<origW;x++) finalDepth[y*origW+x]=resizedDepth.At<float>(y,x);
                return new DepthResult{ Depth=finalDepth, Width=origW, Height=origH };
            });
        }
    }
}
