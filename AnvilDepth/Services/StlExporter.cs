using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AnvilDepth.Services;

/// <summary>
/// Converts a depth/height map into a binary STL mesh. Full-resolution textures (e.g. 2048x2048)
/// would produce tens of millions of triangles, so the grid is downsampled to a manageable cap.
/// </summary>
public static class StlExporter
{
    private const int MaxGridSize = 256;

    public static void SaveAsStl(float[] depth, int width, int height, string path, float heightScale)
    {
        int gridW = Math.Min(width, MaxGridSize);
        int gridH = Math.Min(height, MaxGridSize);

        var heights = new float[gridW, gridH];
        for (int gy = 0; gy < gridH; gy++)
        {
            int sy = (int)((long)gy * (height - 1) / Math.Max(1, gridH - 1));
            for (int gx = 0; gx < gridW; gx++)
            {
                int sx = (int)((long)gx * (width - 1) / Math.Max(1, gridW - 1));
                heights[gx, gy] = depth[sy * width + sx];
            }
        }

        var triangles = new List<(Vec3 a, Vec3 b, Vec3 c)>();
        float sizeX = gridW, sizeY = gridH;

        for (int gy = 0; gy < gridH - 1; gy++)
        {
            for (int gx = 0; gx < gridW - 1; gx++)
            {
                var p00 = new Vec3(gx, gy, heights[gx, gy] * heightScale);
                var p10 = new Vec3(gx + 1, gy, heights[gx + 1, gy] * heightScale);
                var p01 = new Vec3(gx, gy + 1, heights[gx, gy + 1] * heightScale);
                var p11 = new Vec3(gx + 1, gy + 1, heights[gx + 1, gy + 1] * heightScale);

                triangles.Add((p00, p10, p11));
                triangles.Add((p00, p11, p01));
            }
        }

        WriteBinaryStl(path, triangles);
    }

    private static void WriteBinaryStl(string path, List<(Vec3 a, Vec3 b, Vec3 c)> triangles)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, Encoding.ASCII);

        var header = new byte[80];
        Encoding.ASCII.GetBytes("AnvilDepth STL export".PadRight(80)[..80]).CopyTo(header, 0);
        bw.Write(header);
        bw.Write((uint)triangles.Count);

        foreach (var (a, b, c) in triangles)
        {
            var normal = Normal(a, b, c);
            bw.Write(normal.X); bw.Write(normal.Y); bw.Write(normal.Z);
            bw.Write(a.X); bw.Write(a.Y); bw.Write(a.Z);
            bw.Write(b.X); bw.Write(b.Y); bw.Write(b.Z);
            bw.Write(c.X); bw.Write(c.Y); bw.Write(c.Z);
            bw.Write((ushort)0);
        }
    }

    private static Vec3 Normal(Vec3 a, Vec3 b, Vec3 c)
    {
        var u = new Vec3(b.X - a.X, b.Y - a.Y, b.Z - a.Z);
        var v = new Vec3(c.X - a.X, c.Y - a.Y, c.Z - a.Z);
        var n = new Vec3(
            u.Y * v.Z - u.Z * v.Y,
            u.Z * v.X - u.X * v.Z,
            u.X * v.Y - u.Y * v.X);
        float len = MathF.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        return len > 1e-8f ? new Vec3(n.X / len, n.Y / len, n.Z / len) : new Vec3(0, 0, 1);
    }

    private readonly struct Vec3
    {
        public readonly float X, Y, Z;
        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }
    }
}
