using System;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace AnvilDepth.Services
{
    public static class ImageProcessor
    {
        public static float[] ProcessForSculptOKQuality(float[] depth, int w, int h, float strength, float detail, float gamma, bool invert)
        {
            var sorted = depth.OrderBy(v=>v).ToArray();
            float dMin = sorted[(int)(sorted.Length*0.02)];
            float dMax = sorted[(int)(sorted.Length*0.98)];
            float range = Math.Max(dMax-dMin, 0.001f);
            float[] norm = new float[depth.Length];
            for(int i=0;i<depth.Length;i++){ float v=(depth[i]-dMin)/range; v=Math.Clamp(v,0,1); if(invert) v=1f-v; norm[i]=v; }

            if(detail>0.01f){
                try{
                    float[,] arr = new float[h,w];
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++) arr[y,x]=norm[y*w+x];
                    var mat = Mat.FromArray(arr);
                    var filtered = new Mat();
                    Cv2.BilateralFilter(mat, filtered, 9, 75, 75);
                    float[,] fArr = new float[h,w];
                    // Get data via indexer
                    for(int y=0;y<h;y++) for(int x=0;x<w;x++) fArr[y,x]=filtered.At<float>(y,x);
                    for(int i=0;i<norm.Length;i++){ int y=i/w, x=i%w; norm[i]=norm[i]*(1-detail)+fArr[y,x]*detail; }
                }catch{}
            }
            for(int i=0;i<norm.Length;i++){ float v=norm[i]*strength; v=(float)Math.Pow(Math.Clamp(v,0,1), gamma); norm[i]=v; }
            return norm;
        }

        public static float[] ApplyGamma(float[] depth, float gamma){ var o=new float[depth.Length]; for(int i=0;i<depth.Length;i++) o[i]=(float)Math.Pow(depth[i], gamma); return o; }

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
