
using System.IO;
namespace AnvilDepth.Services
{
    public static class StlExporter
    {
        public static void SaveAsStl(float[] depth, int w, int h, string path, float heightScale = 10f)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var bw = new BinaryWriter(fs);
            bw.Write(new byte[80]); int triCount = (w-1)*(h-1)*2; bw.Write((uint)triCount);
            for(int y=0;y<h-1;y++){ for(int x=0;x<w-1;x++){
                int i00 = y*w+x; int i10 = y*w+(x+1); int i01 = (y+1)*w+x; int i11 = (y+1)*w+(x+1);
                float z00 = depth[i00]*heightScale; float z10 = depth[i10]*heightScale; float z01 = depth[i01]*heightScale; float z11 = depth[i11]*heightScale;
                WriteTri(bw, x, y, z00, x+1, y, z10, x, y+1, z01);
                WriteTri(bw, x+1, y, z10, x+1, y+1, z11, x, y+1, z01);
            }}
        }
        static void WriteTri(BinaryWriter bw, float x1,float y1,float z1, float x2,float y2,float z2, float x3,float y3,float z3){
            bw.Write(0f); bw.Write(0f); bw.Write(1f);
            bw.Write(x1); bw.Write(y1); bw.Write(z1);
            bw.Write(x2); bw.Write(y2); bw.Write(z2);
            bw.Write(x3); bw.Write(y3); bw.Write(z3);
            bw.Write((ushort)0);
        }
    }
}
