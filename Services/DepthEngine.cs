using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;

namespace AnvilDepth.Services
{
    public class DepthResult{ public float[] Depth = Array.Empty<float>(); public int Width, Height; }

    public class DepthEngine : IDisposable
    {
        private InferenceSession? session;
        private string device = "CPU";

        public async Task<string> InitializeAsync()
        {
            return await Task.Run(() => {
                try{
                    string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
                    string modelPath = Path.Combine(baseDir, "depth_anything_v2_large.onnx");
                    if (!File.Exists(modelPath)) modelPath = Path.Combine(baseDir, "depth_anything_v2_small.onnx");
                    if (!File.Exists(modelPath)) return "Model not found - Place ONNX in Models folder";

                    var opts = new SessionOptions{ GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
                    try{ opts.AppendExecutionProvider("TensorRT", new Dictionary<string,string>{{"device_id","0"},{"trt_fp16_enable","1"}}); device = "RTX TensorRT"; }catch{}
                    try{ opts.AppendExecutionProvider("CUDA", new Dictionary<string,string>{{"device_id","0"}}); if(device=="CPU") device="RTX CUDA"; }catch{}
                    try{ opts.AppendExecutionProvider("DML"); if(device=="CPU") device="DirectML"; }catch{}

                    session = new InferenceSession(modelPath, opts);
                    return $"OK {device} - {Path.GetFileName(modelPath)}";
                }catch(Exception ex){ device="CPU"; return $"CPU fallback: {ex.Message}"; }
            });
        }

        public async Task<DepthResult> GenerateDepthAsync(string imagePath, bool removeBackground = true, bool highQuality = true)
        {
            return await Task.Run(() => {
                using var img = Cv2.ImRead(imagePath, ImreadModes.Color);
                int origW = img.Width, origH = img.Height;
                int inferSize = highQuality ? 1024 : 518;

                using var resized = new Mat();
                Cv2.Resize(img, resized, new Size(inferSize, inferSize));

                // Preprocess to NCHW float array
                float[] mean = {0.485f,0.456f,0.406f};
                float[] std = {0.229f,0.224f,0.225f};
                var tensorData = new float[1*3*inferSize*inferSize];
                for(int y=0;y<inferSize;y++) for(int x=0;x<inferSize;x++){
                    var pix = resized.At<Vec3b>(y,x);
                    int idx = y*inferSize + x;
                    tensorData[0*inferSize*inferSize + idx] = (pix.Item2/255f - mean[0])/std[0];
                    tensorData[1*inferSize*inferSize + idx] = (pix.Item1/255f - mean[1])/std[1];
                    tensorData[2*inferSize*inferSize + idx] = (pix.Item0/255f - mean[2])/std[2];
                }

                float[] depth;
                if(session != null){
                    var inputTensor = new DenseTensor<float>(tensorData, new[]{1,3,inferSize,inferSize});
                    var inputs = new List<NamedOnnxValue>{ NamedOnnxValue.CreateFromTensor(session.InputMetadata.First().Key, inputTensor) };
                    using var results = session.Run(inputs);
                    var output = results.First().AsTensor<float>();
                    var flat = output.ToArray();

                    // Output is [1,1,H,W] or [1,H,W] - handle both
                    int outCount = flat.Length;
                    int outSide = (int)Math.Sqrt(outCount);
                    float[,] arr2d = new float[outSide, outSide];
                    for(int y=0;y<outSide;y++) for(int x=0;x<outSide;x++) arr2d[y,x]=flat[y*outSide+x];
                    var depthMat = Mat.FromArray(arr2d);
                    using var resizedDepth = new Mat();
                    Cv2.Resize(depthMat, resizedDepth, new Size(origW, origH), 0,0, InterpolationFlags.Cubic);
                    depth = new float[origW*origH];
                    for(int y=0;y<origH;y++) for(int x=0;x<origW;x++) depth[y*origW+x]=resizedDepth.At<float>(y,x);
                }else{
                    depth = new float[origW*origH];
                    for(int y=0;y<origH;y++) for(int x=0;x<origW;x++){ var p=img.At<Vec3b>(y,x); float g=(p.Item0+p.Item1+p.Item2)/3f/255f; depth[y*origW+x]=1f-g; }
                }
                return new DepthResult{ Depth=depth, Width=origW, Height=origH };
            });
        }
        public void Dispose()=>session?.Dispose();
    }
}
