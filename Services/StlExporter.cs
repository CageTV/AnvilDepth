using System.IO;
using OpenCvSharp;

namespace AnvilDepth.Services
{
    public static class StlExporter
    {
        public static void SaveStl(float[] depth, int w, int h, string path, float heightMm=10f)
        {
            int maxSize=1024;
            if(w>maxSize || h>maxSize){
                float scale=maxSize/(float)System.Math.Max(w,h);
                int nw=(int)(w*scale), nh=(int)(h*scale);
                float[,] srcArr = new float[h,w];
                for(int y=0;y<h;y++) for(int x=0;x<w;x++) srcArr[y,x]=depth[y*w+x];
                var srcMat = Mat.FromArray(srcArr);
                var dstMat = new Mat();
                Cv2.Resize(srcMat, dstMat, new Size(nw,nh), 0,0, InterpolationFlags.Area);
                depth = new float[nw*nh];
                for(int y=0;y<nh;y++) for(int x=0;x<nw;x++) depth[y*nw+x]=dstMat.At<float>(y,x);
                w=nw; h=nh;
            }
            using var writer = new BinaryWriter(File.Open(path, FileMode.Create));
            writer.Write(new byte[80]);
            int triCount=(w-1)*(h-1)*2;
            writer.Write(triCount);
            for(int y=0;y<h-1;y++) for(int x=0;x<w-1;x++){
                float z00=depth[y*w+x]*heightMm, z10=depth[y*w+x+1]*heightMm, z01=depth[(y+1)*w+x]*heightMm, z11=depth[(y+1)*w+x+1]*heightMm;
                WriteTri(writer, x,y,z00, x+1,y,z10, x,y+1,z01);
                WriteTri(writer, x+1,y,z10, x+1,y+1,z11, x,y+1,z01);
            }
        }
        static void WriteTri(BinaryWriter w, float x1,float y1,float z1,float x2,float y2,float z2,float x3,float y3,float z3){
            w.Write(0f); w.Write(0f); w.Write(1f);
            w.Write(x1); w.Write(y1); w.Write(z1);
            w.Write(x2); w.Write(y2); w.Write(z2);
            w.Write(x3); w.Write(y3); w.Write(z3);
            w.Write((ushort)0);
        }
    }
}
