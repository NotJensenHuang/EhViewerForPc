namespace EhViewerReader.ImageSearch;

/// <summary>Creates reproducible line-art fixtures without external or private images.</summary>
public static class SyntheticImages
{
    /// <summary>Creates a distinct grayscale page with lines, frames, circles and local textures.</summary>
    public static GrayImage Create(int seed, int width = 320, int height = 448)
    {
        if (width < 64 || height < 64) throw new ArgumentOutOfRangeException(nameof(width));
        var random = new Random(seed); byte[] pixels = Enumerable.Repeat((byte)250, checked(width * height)).ToArray();
        void Dot(int x, int y, byte value) { if (x >= 0 && x < width && y >= 0 && y < height) pixels[y * width + x] = value; }
        void Line(int x0, int y0, int x1, int y1, byte color)
        {
            int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1, dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1, error = dx + dy;
            while (true)
            {
                Dot(x0, y0, color); Dot(x0 + 1, y0, color);
                if (x0 == x1 && y0 == y1) break;
                int e = 2 * error; if (e >= dy) { error += dy; x0 += sx; } if (e <= dx) { error += dx; y0 += sy; }
            }
        }
        for (int i = 0; i < 90; i++)
        {
            int x = random.Next(5, width - 5), y = random.Next(5, height - 5), size = random.Next(5, 35);
            byte color = (byte)random.Next(0, 160);
            if (i % 3 == 0)
            {
                Line(x, y, x + size, y, color); Line(x + size, y, x + size, y + size, color);
                Line(x, y, x, y + size, color); Line(x, y + size, x + size, y + size, color);
            }
            else if (i % 3 == 1)
                for (int angle = 0; angle < 360; angle++) Dot(x + (int)(Math.Cos(angle * Math.PI / 180) * size), y + (int)(Math.Sin(angle * Math.PI / 180) * size), color);
            else Line(x, y, x + random.Next(-35, 36), y + random.Next(-45, 46), color);
        }
        return new(width, height, pixels);
    }
}
