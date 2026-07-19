
using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
namespace AnvilDepth.Services
{
    public static class ImageProcessor
    {
        public static float[] ProcessForSculptOKQuality(float[] depth, int w, int h, float strength, float detail, float gamma, bool invert, float highlights = 1f, float midtones = 1f, float shadows = 1f)
        {
            try{
            var sorted = depth.OrderBy(v=>v).ToArray();
            float dMin = sorted[(int)(sorted.Length*0.02)];
            float dMax = sorted[(int)(sorted.Length*0.98)];
            float range = Math.Max(dMax-dMin, 0.001f);
            float[] norm = new float[depth.Length];
            for(int i=0;i<depth.Length;i++){ float v=(depth[i]-dMin)/range; v=Math.Clamp(v,0,1); if(invert) v=1f-v; norm[i]=v; }
            if(detail>0.001f){
                try{
                    float[,] arr = new float[h,w];
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++) arr[y,x]=norm[y*w+x];
                    var mat = Mat.FromArray(arr);
                    var blurred = new Mat();
                    Cv2.BilateralFilter(mat, blurred, 9, 75, 75);
                    for(int y=0;y<h;y++){ for(int x=0;x<w;x++){ int idx = y*w+x; float low = blurred.At<float>(y,x); float high = norm[idx] - low; norm[idx] = norm[idx] + high * detail * 3.0f; } }
                }catch(Exception ex){ System.Diagnostics.Debug.WriteLine($"Detail fail: {ex.Message}"); }
            }
            for(int i=0;i<norm.Length;i++){ float v=norm[i]*strength; v=(float)Math.Pow(Math.Clamp(v,0,1), gamma); norm[i]=v; }
            norm = ApplyToneControls(norm, highlights, midtones, shadows);
            for(int i=0;i<norm.Length;i++) norm[i]=Math.Clamp(norm[i],0,1);
            return norm;
            }catch(Exception ex){ throw new Exception($"ProcessForSculptOKQuality failed: {ex.Message}", ex); }
        }
        public static float[] ProcessTextureAtlas(byte[] bgraPixels, int w, int h, float detail, float gamma, bool invert, float highlights, float midtones, float shadows, bool removeBg)
        {
            try{
            if(bgraPixels==null) throw new ArgumentNullException(nameof(bgraPixels));
            if(bgraPixels.Length < w*h*4) throw new Exception($"BGRA buffer too small: {bgraPixels.Length} vs {w*h*4}");
            int n = w*h;
            float[] gray = new float[n];
            float[] low = new float[n];
            for(int i=0;i<n;i++){ int o=i*4; float b=bgraPixels[o]/255f; float g=bgraPixels[o+1]/255f; float r=bgraPixels[o+2]/255f; gray[i] = r*0.299f + g*0.587f + b*0.114f; }
            try{
                float[,] arr = new float[h,w];
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) arr[y,x]=gray[y*w+x];
                var mat = Mat.FromArray(arr);
                var blurred = new Mat();
                Cv2.BilateralFilter(mat, blurred, 15, 90, 90);
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) low[y*w+x]=blurred.At<float>(y,x);
            }catch{ Array.Copy(gray, low, n); }
            float[] combined = new float[n];
            for(int i=0;i<n;i++){ float g = gray[i]; float l = low[i]; float high = g - l; float v = l - high * detail * 3.0f; v = v * 0.85f + l * 0.15f; combined[i]=v; }
            var sorted = combined.OrderBy(v=>v).ToArray();
            float dMin = sorted[(int)(sorted.Length*0.02)];
            float dMax = sorted[(int)(sorted.Length*0.98)];
            float range = Math.Max(dMax-dMin, 0.001f);
            for(int i=0;i<n;i++){ float v=(combined[i]-dMin)/range; v=Math.Clamp(v,0,1); if(invert) v=1f-v; v=(float)Math.Pow(v, gamma); combined[i]=v; }
            combined = ApplyToneControls(combined, highlights, midtones, shadows);
            if(removeBg){ for(int i=0;i<n;i++){ float l = low[i]; float mask; if(l < 0.08f) mask=0f; else if(l < 0.22f) mask=(l-0.08f)/0.14f; else mask=1f; combined[i] *= mask; if(mask < 0.02f) combined[i]=0f; } }
            for(int i=0;i<n;i++) combined[i]=Math.Clamp(combined[i],0,1);
            return combined;
            }catch(Exception ex){ throw new Exception($"TextureAtlas failed: {ex.Message}", ex); }
        }
        public static float[] ApplyToneControls(float[] depth, float highlights, float midtones, float shadows)
        {
            float[] outArr = new float[depth.Length];
            for(int i=0;i<depth.Length;i++){
                float v = depth[i];
                float shadowMask = 1f - Math.Clamp(v / 0.5f, 0f, 1f); shadowMask *= shadowMask;
                float highlightMask = Math.Clamp((v - 0.5f) / 0.5f, 0f, 1f); highlightMask *= highlightMask;
                float midMask = 1f - Math.Abs(v - 0.5f) * 2f; midMask = Math.Max(0f, midMask); midMask *= midMask;
                float shadowAdj = v + (shadows - 1f) * shadowMask * 0.6f;
                float highlightAdj = shadowAdj + (highlights - 1f) * highlightMask * 0.6f;
                float midAdj = highlightAdj;
                if (Math.Abs(midtones - 1f) > 0.001f){ float centered = midAdj - 0.5f; float contrasted = centered * midtones; float target = contrasted + 0.5f; midAdj = Lerp(highlightAdj, target, midMask * 0.9f); }
                outArr[i] = Math.Clamp(midAdj, 0f, 1f);
            }
            return outArr;
        }
        static float Lerp(float a, float b, float t) => a + (b - a) * t;
        public static BitmapSource FloatArrayToBitmapSource(float[] depth, int w, int h){
            byte[] pixels = new byte[w*h];
            for(int i=0;i<depth.Length;i++) pixels[i]=(byte)(Math.Clamp(depth[i],0,1)*255);
            return BitmapSource.Create(w,h,96,96, PixelFormats.Gray8, null, pixels, w);
        }
        public static void SaveAs16Bit(float[] depth, int w, int h, string path){
            ushort[,] arr = new ushort[h,w];
            for(int y=0;y<h;y++) for(int x=0;x<w;x++) arr[y,x]=(ushort)(Math.Clamp(depth[y*w+x],0,1)*65535);
            var mat = Mat.FromArray(arr);
            Cv2.ImWrite(path, mat);
        }
        public static void SaveAsEXR(float[] depth, int w, int h, string path){
            try{
                float[,] arr = new float[h,w];
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) arr[y,x]=depth[y*w+x];
                var mat = Mat.FromArray(arr);
                if(!Cv2.ImWrite(path, mat)) SaveAs16Bit(depth,w,h, System.IO.Path.ChangeExtension(path,".png"));
            }catch{ SaveAs16Bit(depth,w,h, System.IO.Path.ChangeExtension(path,".png")); }
        }
    }
}
