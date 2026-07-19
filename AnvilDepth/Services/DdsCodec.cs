
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Pfim;

namespace AnvilDepth.Services
{
    public static class DdsCodec
    {
        const uint DDS_MAGIC = 0x20534444;
        const uint FOURCC_DXT1 = 0x31545844;
        const uint FOURCC_DXT3 = 0x33545844;
        const uint FOURCC_DXT5 = 0x35545844;
        const uint FOURCC_ATI1 = 0x31495441;
        const uint FOURCC_BC4U = 0x55344342;
        const uint FOURCC_ATI2 = 0x32495441;
        const uint FOURCC_BC5U = 0x55354342;
        const uint FOURCC_DX10 = 0x30315844;

        // NEW: Try Pfim first - handles BC7 which old decoder doesn't (geralt_b68.dds)
        private static bool TryPfim(string path, out byte[] bgra, out int w, out int h)
        {
            bgra = null!; w=0; h=0;
            try
            {
                using var image = Pfimage.FromFile(path);
                w = image.Width; h = image.Height;
                bgra = new byte[w*h*4];
                switch(image.Format)
                {
                    case ImageFormat.Rgba32:
                        for(int i=0;i<w*h;i++){ bgra[i*4+0]=image.Data[i*4+2]; bgra[i*4+1]=image.Data[i*4+1]; bgra[i*4+2]=image.Data[i*4+0]; bgra[i*4+3]=image.Data[i*4+3]; }
                        return true;
                    case ImageFormat.Rgb24:
                        for(int i=0;i<w*h;i++){ bgra[i*4+0]=image.Data[i*3+2]; bgra[i*4+1]=image.Data[i*3+1]; bgra[i*4+2]=image.Data[i*3+0]; bgra[i*4+3]=255; }
                        return true;
                    default:
                        if(image.Data.Length >= w*h*4){
                            for(int i=0;i<w*h;i++){ bgra[i*4+0]=image.Data[i*4+2]; bgra[i*4+1]=image.Data[i*4+1]; bgra[i*4+2]=image.Data[i*4+0]; bgra[i*4+3]=255; }
                            return true;
                        }
                        else if(image.Data.Length >= w*h*3){
                            for(int i=0;i<w*h;i++){ bgra[i*4+0]=image.Data[i*3+2]; bgra[i*4+1]=image.Data[i*3+1]; bgra[i*4+2]=image.Data[i*3+0]; bgra[i*4+3]=255; }
                            return true;
                        }
                        break;
                }
            }
            catch{}
            return false;
        }

        public static (byte[] bgra, int width, int height) Decode(string path)
        {
            // 1) Pfim - handles BC7 / BC6H / DXT1-5 / uncompressed - this fixes geralt_b68.dds
            if(TryPfim(path, out var bgraPfim, out int wPfim, out int hPfim))
                return (bgraPfim, wPfim, hPfim);

            // 2) Your original custom decoder (kept for full mip chain support and edge cases)
            try { return DecodeCustom(path); }
            catch{}

            // 3) OpenCV fallback
            try
            {
                using var mat = OpenCvSharp.Cv2.ImRead(path, OpenCvSharp.ImreadModes.Unchanged);
                if(!mat.Empty())
                {
                    int w = mat.Width, h = mat.Height;
                    OpenCvSharp.Mat bgraMat;
                    if(mat.Channels()==4) bgraMat = mat.Clone();
                    else if(mat.Channels()==3){ bgraMat = new OpenCvSharp.Mat(); OpenCvSharp.Cv2.CvtColor(mat, bgraMat, OpenCvSharp.ColorConversionCodes.BGR2BGRA); }
                    else if(mat.Channels()==1){ bgraMat = new OpenCvSharp.Mat(); OpenCvSharp.Cv2.CvtColor(mat, bgraMat, OpenCvSharp.ColorConversionCodes.GRAY2BGRA); }
                    else bgraMat = mat.Clone();
                    byte[] bgra = new byte[w*h*4];
                    Marshal.Copy(bgraMat.Data, bgra, 0, bgra.Length);
                    bgraMat.Dispose();
                    return (bgra,w,h);
                }
            }
            catch{}

            throw new Exception($"Failed to decode DDS {path} - file may be corrupted. Try converting to PNG first.");
        }

        private static (byte[] bgra, int width, int height) DecodeCustom(string path)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt32() != DDS_MAGIC) throw new Exception("Not a valid DDS file (bad magic).");
            br.ReadUInt32(); br.ReadUInt32();
            int height = (int)br.ReadUInt32();
            int width = (int)br.ReadUInt32();
            br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
            br.ReadBytes(4 * 11);
            br.ReadUInt32();
            uint pfFlags = br.ReadUInt32();
            uint fourCC = br.ReadUInt32();
            uint rgbBitCount = br.ReadUInt32();
            uint rMask = br.ReadUInt32();
            uint gMask = br.ReadUInt32();
            uint bMask = br.ReadUInt32();
            uint aMask = br.ReadUInt32();
            br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
            bool fourCCSet = (pfFlags & 0x4) != 0;
            uint dxgiFormat = 0;
            bool isDx10 = fourCCSet && fourCC == FOURCC_DX10;
            if (isDx10)
            {
                dxgiFormat = br.ReadUInt32();
                br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32(); br.ReadUInt32();
            }
            byte[] bgra = new byte[width * height * 4];
            if (isDx10)
            {
                switch (dxgiFormat)
                {
                    case 71: case 72: DecodeBC1(br, width, height, bgra); break;
                    case 74: case 75: DecodeBC2(br, width, height, bgra); break;
                    case 77: case 78: DecodeBC3(br, width, height, bgra); break;
                    case 80: case 81: DecodeBC4(br, width, height, bgra); break;
                    case 83: case 84: DecodeBC5(br, width, height, bgra); break;
                    case 28: case 29: DecodeRGBA8(br, width, height, bgra, true); break;
                    case 87: case 88: DecodeRGBA8(br, width, height, bgra, false); break;
                    case 61: DecodeR8(br, width, height, bgra); break;
                    default: throw new NotSupportedException($"DDS DXGI format {dxgiFormat} - try PNG");
                }
            }
            else if (fourCCSet)
            {
                if (fourCC == FOURCC_DXT1) DecodeBC1(br, width, height, bgra);
                else if (fourCC == FOURCC_DXT3) DecodeBC2(br, width, height, bgra);
                else if (fourCC == FOURCC_DXT5) DecodeBC3(br, width, height, bgra);
                else if (fourCC == FOURCC_ATI1 || fourCC == FOURCC_BC4U) DecodeBC4(br, width, height, bgra);
                else if (fourCC == FOURCC_ATI2 || fourCC == FOURCC_BC5U) DecodeBC5(br, width, height, bgra);
                else throw new NotSupportedException($"FourCC 0x{fourCC:X8}");
            }
            else if ((pfFlags & 0x40) != 0) DecodeUncompressedRGB(br, width, height, bgra, (int)rgbBitCount, rMask, gMask, bMask, aMask, (pfFlags & 0x1) != 0);
            else if ((pfFlags & 0x20000) != 0 || (pfFlags & 0x2) != 0) DecodeR8(br, width, height, bgra);
            else throw new NotSupportedException("Unsupported DDS pixel format.");
            return (bgra, width, height);
        }

        static (byte r, byte g, byte b) Rgb565(ushort c)
        {
            int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
            return ((byte)((r5 * 255 + 15) / 31), (byte)((g6 * 255 + 31) / 63), (byte)((b5 * 255 + 15) / 31));
        }
        static void DecodeBC1(BinaryReader br, int w, int h, byte[] bgra)
        {
            int bx = (w + 3) / 4, by = (h + 3) / 4;
            for (int j = 0; j < by; j++) for (int i = 0; i < bx; i++)
            {
                ushort c0 = br.ReadUInt16(), c1 = br.ReadUInt16(); uint idx = br.ReadUInt32();
                var (r0,g0,b0)=Rgb565(c0); var (r1,g1,b1)=Rgb565(c1);
                byte[] rC=new byte[4], gC=new byte[4], bC=new byte[4], aC=new byte[4]{255,255,255,255};
                rC[0]=r0; gC[0]=g0; bC[0]=b0; rC[1]=r1; gC[1]=g1; bC[1]=b1;
                if(c0>c1){ rC[2]=(byte)((2*r0+r1)/3); gC[2]=(byte)((2*g0+g1)/3); bC[2]=(byte)((2*b0+b1)/3); rC[3]=(byte)((r0+2*r1)/3); gC[3]=(byte)((g0+2*g1)/3); bC[3]=(byte)((b0+2*b1)/3); }
                else{ rC[2]=(byte)((r0+r1)/2); gC[2]=(byte)((g0+g1)/2); bC[2]=(byte)((b0+b1)/2); rC[3]=0; gC[3]=0; bC[3]=0; aC[3]=0; }
                WriteBlock(bgra,w,h,i,j,(px,py)=>{int k=(int)((idx>>(2*(py*4+px)))&3); return (bC[k],gC[k],rC[k],aC[k]);});
            }
        }
        static void DecodeBC2(BinaryReader br, int w, int h, byte[] bgra)
        {
            int bx=(w+3)/4, by=(h+3)/4;
            for(int j=0;j<by;j++) for(int i=0;i<bx;i++)
            {
                ulong alpha = br.ReadUInt64(); ushort c0=br.ReadUInt16(), c1=br.ReadUInt16(); uint idx=br.ReadUInt32();
                var (r0,g0,b0)=Rgb565(c0); var (r1,g1,b1)=Rgb565(c1);
                byte[] rC=new byte[4]{r0,r1,(byte)((2*r0+r1)/3),(byte)((r0+2*r1)/3)}, gC=new byte[4]{g0,g1,(byte)((2*g0+g1)/3),(byte)((g0+2*g1)/3)}, bC=new byte[4]{b0,b1,(byte)((2*b0+b1)/3),(byte)((b0+2*b1)/3)};
                WriteBlock(bgra,w,h,i,j,(px,py)=>{int aIdx=py*4+px; byte a=(byte)(((alpha>>(aIdx*4))&0xF)*17); int cIdx=(int)((idx>>(2*(py*4+px)))&3); return (bC[cIdx],gC[cIdx],rC[cIdx],a);});
            }
        }
        static void DecodeBC3(BinaryReader br, int w, int h, byte[] bgra)
        {
            int bx=(w+3)/4, by=(h+3)/4;
            for(int j=0;j<by;j++) for(int i=0;i<bx;i++)
            {
                byte a0=br.ReadByte(), a1=br.ReadByte(); byte[] aIdxBytes=br.ReadBytes(6); ulong aIdx=0; for(int k=0;k<6;k++) aIdx|=(ulong)aIdxBytes[k]<<(8*k);
                ushort c0=br.ReadUInt16(), c1=br.ReadUInt16(); uint cIdx=br.ReadUInt32();
                var (r0,g0,b0)=Rgb565(c0); var (r1,g1,b1)=Rgb565(c1);
                byte[] rC=new byte[4]{r0,r1,(byte)((2*r0+r1)/3),(byte)((r0+2*r1)/3)}, gC=new byte[4]{g0,g1,(byte)((2*g0+g1)/3),(byte)((g0+2*g1)/3)}, bC=new byte[4]{b0,b1,(byte)((2*b0+b1)/3),(byte)((b0+2*b1)/3)};
                byte[] aTab=new byte[8]; aTab[0]=a0; aTab[1]=a1;
                if(a0>a1){ aTab[2]=(byte)((6*a0+1*a1)/7); aTab[3]=(byte)((5*a0+2*a1)/7); aTab[4]=(byte)((4*a0+3*a1)/7); aTab[5]=(byte)((3*a0+4*a1)/7); aTab[6]=(byte)((2*a0+5*a1)/7); aTab[7]=(byte)((1*a0+6*a1)/7); }
                else{ aTab[2]=(byte)((4*a0+1*a1)/5); aTab[3]=(byte)((3*a0+2*a1)/5); aTab[4]=(byte)((2*a0+3*a1)/5); aTab[5]=(byte)((1*a0+4*a1)/5); aTab[6]=0; aTab[7]=255; }
                WriteBlock(bgra,w,h,i,j,(px,py)=>{int p=py*4+px; int ai=(int)((aIdx>>(3*p))&7); int ci=(int)((cIdx>>(2*p))&3); return (bC[ci],gC[ci],rC[ci],aTab[ai]);});
            }
        }
        static void DecodeBC4(BinaryReader br, int w, int h, byte[] bgra){ int bx=(w+3)/4, by=(h+3)/4; for(int j=0;j<by;j++) for(int i=0;i<bx;i++){ byte r0=br.ReadByte(), r1=br.ReadByte(); byte[] idxB=br.ReadBytes(6); ulong idx=0; for(int k=0;k<6;k++) idx|=(ulong)idxB[k]<<(8*k); byte[] tab=new byte[8]; tab[0]=r0; tab[1]=r1; if(r0>r1){ tab[2]=(byte)((6*r0+r1)/7); tab[3]=(byte)((5*r0+2*r1)/7); tab[4]=(byte)((4*r0+3*r1)/7); tab[5]=(byte)((3*r0+4*r1)/7); tab[6]=(byte)((2*r0+5*r1)/7); tab[7]=(byte)((r0+6*r1)/7);} else{ tab[2]=(byte)((4*r0+r1)/5); tab[3]=(byte)((3*r0+2*r1)/5); tab[4]=(byte)((2*r0+3*r1)/5); tab[5]=(byte)((r0+4*r1)/5); tab[6]=0; tab[7]=255;} WriteBlock(bgra,w,h,i,j,(px,py)=>{int p=py*4+px; int ai=(int)((idx>>(3*p))&7); byte g=tab[ai]; return (g,g,g,255);}); } }
        static void DecodeBC5(BinaryReader br, int w, int h, byte[] bgra){ int bx=(w+3)/4, by=(h+3)/4; for(int j=0;j<by;j++) for(int i=0;i<bx;i++){ byte r0=br.ReadByte(), r1=br.ReadByte(); byte[] rb=br.ReadBytes(6); ulong rIdx=0; for(int k=0;k<6;k++) rIdx|=(ulong)rb[k]<<(8*k); byte g0=br.ReadByte(), g1=br.ReadByte(); byte[] gb=br.ReadBytes(6); ulong gIdx=0; for(int k=0;k<6;k++) gIdx|=(ulong)gb[k]<<(8*k); byte[] rTab=new byte[8], gTab=new byte[8]; rTab[0]=r0; rTab[1]=r1; gTab[0]=g0; gTab[1]=g1; if(r0>r1){ rTab[2]=(byte)((6*r0+r1)/7); rTab[3]=(byte)((5*r0+2*r1)/7); rTab[4]=(byte)((4*r0+3*r1)/7); rTab[5]=(byte)((3*r0+4*r1)/7); rTab[6]=(byte)((2*r0+5*r1)/7); rTab[7]=(byte)((r0+6*r1)/7);} else{ rTab[2]=(byte)((4*r0+r1)/5); rTab[3]=(byte)((3*r0+2*r1)/5); rTab[4]=(byte)((2*r0+3*r1)/5); rTab[5]=(byte)((r0+4*r1)/5); rTab[6]=0; rTab[7]=255;} if(g0>g1){ gTab[2]=(byte)((6*g0+g1)/7); gTab[3]=(byte)((5*g0+2*g1)/7); gTab[4]=(byte)((4*g0+3*g1)/7); gTab[5]=(byte)((3*g0+4*g1)/7); gTab[6]=(byte)((2*g0+5*g1)/7); gTab[7]=(byte)((g0+6*g1)/7);} else{ gTab[2]=(byte)((4*g0+g1)/5); gTab[3]=(byte)((3*g0+2*g1)/5); gTab[4]=(byte)((2*g0+3*g1)/5); gTab[5]=(byte)((g0+4*g1)/5); gTab[6]=0; gTab[7]=255;} WriteBlock(bgra,w,h,i,j,(px,py)=>{int p=py*4+px; byte r=rTab[(rIdx>>(3*p))&7]; byte g=gTab[(gIdx>>(3*p))&7]; return (g,g,r,255);}); } }
        static void DecodeRGBA8(BinaryReader br, int w, int h, byte[] bgra, bool swapRB){ for(int y=0;y<h;y++) for(int x=0;x<w;x++){ byte r=br.ReadByte(), g=br.ReadByte(), b=br.ReadByte(), a=br.ReadByte(); int o=(y*w+x)*4; if(swapRB){ bgra[o]=b; bgra[o+1]=g; bgra[o+2]=r; bgra[o+3]=a;} else{ bgra[o]=r; bgra[o+1]=g; bgra[o+2]=b; bgra[o+3]=a;} } }
        static void DecodeR8(BinaryReader br, int w, int h, byte[] bgra){ for(int y=0;y<h;y++) for(int x=0;x<w;x++){ byte g=br.ReadByte(); int o=(y*w+x)*4; bgra[o]=g; bgra[o+1]=g; bgra[o+2]=g; bgra[o+3]=255; } }
        static void DecodeUncompressedRGB(BinaryReader br, int w, int h, byte[] bgra, int bitCount, uint rMask, uint gMask, uint bMask, uint aMask, bool hasAlpha){ int bytesPerPixel=bitCount/8; for(int y=0;y<h;y++) for(int x=0;x<w;x++){ uint pixel=0; if(bytesPerPixel==4) pixel=br.ReadUInt32(); else if(bytesPerPixel==3){ pixel=(uint)(br.ReadByte() | (br.ReadByte()<<8) | (br.ReadByte()<<16)); } else if(bytesPerPixel==2) pixel=br.ReadUInt16(); else pixel=br.ReadByte(); byte r=Extract(pixel,rMask), g=Extract(pixel,gMask), b=Extract(pixel,bMask), a= hasAlpha? Extract(pixel,aMask): (byte)255; int o=(y*w+x)*4; bgra[o]=b; bgra[o+1]=g; bgra[o+2]=r; bgra[o+3]=a; } }
        static byte Extract(uint pixel, uint mask){ if(mask==0) return 0; int shift=0; while(((mask>>shift)&1)==0) shift++; uint v=(pixel & mask)>>shift; int bits=0; uint m=mask>>shift; while(m!=0){ bits++; m>>=1;} if(bits==8) return (byte)v; return (byte)(v*255/((1<<bits)-1)); }
        static void WriteBlock(byte[] bgra,int w,int h,int bx,int by, Func<int,int,(byte b,byte g,byte r,byte a)> sample){ for(int py=0;py<4;py++){ int y=by*4+py; if(y>=h) continue; for(int px=0;px<4;px++){ int x=bx*4+px; if(x>=w) continue; var (b,g,r,a)=sample(px,py); int o=(y*w+x)*4; bgra[o]=b; bgra[o+1]=g; bgra[o+2]=r; bgra[o+3]=a; } } }

        static List<(int w,int h,byte[] gray)> BuildMipChain(float[] depth,int w,int h)
        {
            var levels=new List<(int,int,byte[])>(); byte[] top=new byte[w*h]; for(int i=0;i<w*h;i++) top[i]=(byte)(Math.Clamp(depth[i],0,1)*255); levels.Add((w,h,top));
            int cw=w,ch=h; byte[] cur=top; while(cw>1||ch>1){ int nw=Math.Max(1,cw/2), nh=Math.Max(1,ch/2); byte[] next=new byte[nw*nh]; for(int y=0;y<nh;y++) for(int x=0;x<nw;x++){ int x0=Math.Min(x*2,cw-1), x1=Math.Min(x*2+1,cw-1), y0=Math.Min(y*2,ch-1), y1=Math.Min(y*2+1,ch-1); int sum=cur[y0*cw+x0]+cur[y0*cw+x1]+cur[y1*cw+x0]+cur[y1*cw+x1]; next[y*nw+x]=(byte)(sum/4);} levels.Add((nw,nh,next)); cw=nw; ch=nh; cur=next; } return levels;
        }
        static void WriteHeader(BinaryWriter bw,int w,int h,int mipCount,bool compressed,uint pitchOrLinear)
        {
            bw.Write(DDS_MAGIC); bw.Write((uint)124);
            uint flags=0x1|0x2|0x4|0x1000 | (uint)(compressed?0x80000:0x8) | (mipCount>1?0x20000u:0);
            bw.Write(flags); bw.Write((uint)h); bw.Write((uint)w); bw.Write(pitchOrLinear); bw.Write((uint)0); bw.Write((uint)mipCount); for(int i=0;i<11;i++) bw.Write((uint)0);
            bw.Write((uint)32); if(compressed){ bw.Write((uint)0x4); bw.Write(FOURCC_DXT1); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);} else{ bw.Write((uint)0x20000); bw.Write((uint)0); bw.Write((uint)8); bw.Write((uint)0xFF); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); }
            uint caps=0x1000 | (mipCount>1?(0x8u|0x400000u):0); bw.Write(caps); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0); bw.Write((uint)0);
        }
        public static void SaveGrayscaleDxt1(float[] depth,int w,int h,string path)
        {
            var levels=BuildMipChain(depth,w,h);
            using var fs=File.Create(path); using var bw=new BinaryWriter(fs);
            int blocksW=Math.Max(1,(w+3)/4), blocksH=Math.Max(1,(h+3)/4);
            WriteHeader(bw,w,h,levels.Count,true,(uint)(blocksW*blocksH*8));
            foreach(var (lw,lh,gray) in levels) WriteBC1Grayscale(bw,gray,lw,lh);
        }
        static void WriteBC1Grayscale(BinaryWriter bw,byte[] gray,int w,int h)
        {
            int bx=Math.Max(1,(w+3)/4), by=Math.Max(1,(h+3)/4);
            for(int j=0;j<by;j++) for(int i=0;i<bx;i++)
            {
                byte gMin=255,gMax=0; byte[] block=new byte[16];
                for(int py=0;py<4;py++) for(int px=0;px<4;px++){ int x=Math.Min(i*4+px,w-1), y=Math.Min(j*4+py,h-1); byte v=gray[y*w+x]; block[py*4+px]=v; if(v<gMin) gMin=v; if(v>gMax) gMax=v; }
                ushort p0=Pack565(gMax), p1=Pack565(gMin); if(p0<=p1){ if(p0<0xFFFF) p0++; else p1--; }
                var (r0,g0,b0)=Rgb565(p0); var (r1,g1,b1)=Rgb565(p1); int[] pal=new int[4]{g0,g1,(2*g0+g1)/3,(g0+2*g1)/3};
                bw.Write(p0); bw.Write(p1); uint idxBits=0; for(int p=0;p<16;p++){ int v=block[p]; int best=0,bestDiff=int.MaxValue; for(int k=0;k<4;k++){ int d=Math.Abs(pal[k]-v); if(d<bestDiff){bestDiff=d; best=k;}} idxBits|=(uint)best<<(2*p); } bw.Write(idxBits);
            }
        }
        static ushort Pack565(byte gray){ int r5=gray*31/255, g6=gray*63/255, b5=gray*31/255; return (ushort)((r5<<11)|(g6<<5)|b5); }
        public static void SaveGrayscaleUncompressed(float[] depth,int w,int h,string path)
        {
            var levels=BuildMipChain(depth,w,h);
            using var fs=File.Create(path); using var bw=new BinaryWriter(fs);
            WriteHeader(bw,w,h,levels.Count,false,(uint)((w*8+7)/8));
            foreach(var (lw,lh,gray) in levels) bw.Write(gray);
        }
    }
}
