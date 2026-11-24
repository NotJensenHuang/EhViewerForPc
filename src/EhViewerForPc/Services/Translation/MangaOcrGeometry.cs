using System.Buffers;
using System.Numerics;
using System.Text;

namespace EhViewerForPc.Services;

internal static partial class MangaOcr
{
    private readonly record struct Quad(Vector2 A, Vector2 B, Vector2 C, Vector2 D)
    {
        internal Quad FromTopLeft()
        {
            Span<Vector2> points = stackalloc Vector2[] { A, B, C, D };
            int first = 0;
            for (int i = 1; i < points.Length; i++)
                if (points[i].X + points[i].Y < points[first].X + points[first].Y) first = i;
            return new(points[first], points[(first + 1) % 4], points[(first + 2) % 4], points[(first + 3) % 4]);
        }

        internal Quad ScaleAndClamp(int width, int height, int mapWidth, int mapHeight)
        {
            Vector2 scale = new((float)width / mapWidth, (float)height / mapHeight);
            Vector2 upper = new(width - 1, height - 1);
            return new(Vector2.Clamp(A * scale, Vector2.Zero, upper), Vector2.Clamp(B * scale, Vector2.Zero, upper),
                Vector2.Clamp(C * scale, Vector2.Zero, upper), Vector2.Clamp(D * scale, Vector2.Zero, upper));
        }

        internal Bounds GetBounds()
        {
            Vector2 lower = Vector2.Min(Vector2.Min(A, B), Vector2.Min(C, D));
            Vector2 upper = Vector2.Max(Vector2.Max(A, B), Vector2.Max(C, D));
            int x = (int)MathF.Floor(lower.X);
            int y = (int)MathF.Floor(lower.Y);
            return new Bounds(x, y, (int)MathF.Floor(upper.X) + 1 - x, (int)MathF.Floor(upper.Y) + 1 - y);
        }
    }

    private readonly record struct Bounds(int X, int Y, int Width, int Height)
    {
        internal int Right => X + Width;
        internal int Bottom => Y + Height;
    }

    private readonly record struct RotatedRectangle(Vector2 Along, Vector2 Across, float Left, float Top, float Width, float Height)
    {
        internal Quad Expand(float amount)
        {
            Vector2 first = Along * (Left - amount / 2) + Across * (Top - amount / 2);
            Vector2 horizontal = Along * (Width + amount);
            Vector2 vertical = Across * (Height + amount);
            return new(first, first + horizontal, first + horizontal + vertical, first + vertical);
        }
    }

    private static List<Quad> DetectBoxes(ReadOnlySpan<float> probability, ReadOnlySpan<long> shape,
        int width, int height, CancellationToken token)
    {
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 1 || shape[2] < 1 || shape[3] < 1 ||
            shape[2] > MaxDetectionDimension || shape[3] > MaxDetectionDimension || shape[2] * shape[3] != probability.Length)
            throw new MangaTranslationException("文字检测模型输出尺寸不匹配。");
        int mapWidth = (int)shape[3];
        int mapHeight = (int)shape[2];
        int[] queue = ArrayPool<int>.Shared.Rent(probability.Length);
        byte[] visited = ArrayPool<byte>.Shared.Rent(probability.Length);
        Array.Clear(visited, 0, probability.Length);
        int[] rowStamp = new int[mapHeight];
        int[] rowLeft = new int[mapHeight];
        int[] rowRight = new int[mapHeight];
        var hullPoints = new List<Vector2>(mapHeight * 2);
        var boxes = new List<Quad>();
        try
        {
            for (int seed = 0; seed < probability.Length; seed++)
            {
                if (seed % mapWidth == 0) token.ThrowIfCancellationRequested();
                if (visited[seed] != 0 || !(probability[seed] > 0.30f)) continue;
                visited[seed] = 1;
                queue[0] = seed;
                int head = 0;
                int tail = 1;
                int minX = mapWidth;
                int minY = mapHeight;
                int maxX = 0;
                int maxY = 0;
                while (head < tail)
                {
                    if ((head & 0xffff) == 0) token.ThrowIfCancellationRequested();
                    int position = queue[head++];
                    int x = position % mapWidth;
                    int y = position / mapWidth;
                    minX = Math.Min(minX, x);
                    maxX = Math.Max(maxX, x);
                    minY = Math.Min(minY, y);
                    maxY = Math.Max(maxY, y);
                    if (rowStamp[y] != seed + 1)
                    {
                        rowStamp[y] = seed + 1;
                        rowLeft[y] = rowRight[y] = x;
                    }
                    else
                    {
                        rowLeft[y] = Math.Min(rowLeft[y], x);
                        rowRight[y] = Math.Max(rowRight[y], x);
                    }

                    for (int nearY = Math.Max(0, y - 1); nearY <= Math.Min(mapHeight - 1, y + 1); nearY++)
                    {
                        for (int nearX = Math.Max(0, x - 1); nearX <= Math.Min(mapWidth - 1, x + 1); nearX++)
                        {
                            int next = nearY * mapWidth + nearX;
                            if (visited[next] != 0 || !(probability[next] > 0.30f)) continue;
                            visited[next] = 1;
                            queue[tail++] = next;
                        }
                    }
                }
                if (maxX - minX < 3 || maxY - minY < 3) continue;

                hullPoints.Clear();
                for (int y = minY; y <= maxY; y++)
                {
                    if (rowStamp[y] != seed + 1) continue;
                    hullPoints.Add(new Vector2(rowLeft[y], y));
                    if (rowLeft[y] != rowRight[y]) hullPoints.Add(new Vector2(rowRight[y], y));
                }

                RotatedRectangle rectangle = MinimumRectangle(hullPoints);
                if (Math.Min(rectangle.Width, rectangle.Height) < 3) continue;
                Bounds limits = new(minX, minY, maxX - minX + 1, maxY - minY + 1);
                if (FilledComponentScore(probability, mapWidth, queue, tail, limits, token) < 0.50) continue;
                float padding = 1.4f * rectangle.Width * rectangle.Height / (rectangle.Width + rectangle.Height);
                boxes.Add(rectangle.Expand(padding).ScaleAndClamp(width, height, mapWidth, mapHeight));
                if (boxes.Count > 120) throw new MangaTranslationException("页面文字区域过多。");
            }
            return MergeVerticalPunctuation(boxes);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(queue);
            ArrayPool<byte>.Shared.Return(visited);
        }
    }

    private static double FilledComponentScore(ReadOnlySpan<float> probability, int stride, int[] queue, int count,
        Bounds limits, CancellationToken token)
    {
        // 八连通前景对应四连通外部空白。填充内部空洞后计算轮廓内概率均值。
        int area = limits.Width * limits.Height;
        byte[] mask = ArrayPool<byte>.Shared.Rent(area);
        Array.Clear(mask, 0, area);
        try
        {
            for (int i = 0; i < count; i++)
            {
                int x = queue[i] % stride - limits.X;
                int y = queue[i] / stride - limits.Y;
                mask[y * limits.Width + x] = 1;
            }

            int head = 0;
            int tail = 0;
            for (int x = 0; x < limits.Width; x++)
            {
                MarkExterior(mask, queue, ref tail, x);
                MarkExterior(mask, queue, ref tail, (limits.Height - 1) * limits.Width + x);
            }
            for (int y = 0; y < limits.Height; y++)
            {
                MarkExterior(mask, queue, ref tail, y * limits.Width);
                MarkExterior(mask, queue, ref tail, y * limits.Width + limits.Width - 1);
            }
            while (head < tail)
            {
                if ((head & 0xffff) == 0) token.ThrowIfCancellationRequested();
                int position = queue[head++];
                int x = position % limits.Width;
                int y = position / limits.Width;
                if (x > 0) MarkExterior(mask, queue, ref tail, position - 1);
                if (x + 1 < limits.Width) MarkExterior(mask, queue, ref tail, position + 1);
                if (y > 0) MarkExterior(mask, queue, ref tail, position - limits.Width);
                if (y + 1 < limits.Height) MarkExterior(mask, queue, ref tail, position + limits.Width);
            }

            double sum = 0;
            for (int y = 0; y < limits.Height; y++)
            {
                int mapIndex = (y + limits.Y) * stride + limits.X;
                for (int x = 0; x < limits.Width; x++)
                {
                    if (mask[y * limits.Width + x] == 2) continue;
                    float value = probability[mapIndex + x];
                    if (float.IsFinite(value)) sum += value;
                }
            }
            return sum / (area - tail);
        }
        finally { ArrayPool<byte>.Shared.Return(mask); }
    }

    private static void MarkExterior(byte[] mask, int[] queue, ref int tail, int index)
    {
        if (mask[index] != 0) return;
        mask[index] = 2;
        queue[tail++] = index;
    }

    private static float Cross(Vector2 first, Vector2 second) => first.X * second.Y - first.Y * second.X;

    private static RotatedRectangle MinimumRectangle(List<Vector2> input)
    {
        input.Sort(static (first, second) => first.X == second.X ? first.Y.CompareTo(second.Y) : first.X.CompareTo(second.X));
        Vector2[] hull = ArrayPool<Vector2>.Shared.Rent(input.Count * 2);
        try
        {
            int count = 0;
            foreach (Vector2 point in input) AddHullPoint(hull, ref count, 0, point);
            int forward = count;
            for (int i = input.Count - 2; i >= 0; i--) AddHullPoint(hull, ref count, forward - 1, input[i]);
            count--; // 最后一个点重复首点。
            double bestArea = double.MaxValue;
            RotatedRectangle best = default;
            for (int i = 0; i < count; i++)
            {
                Vector2 edge = hull[(i + 1) % count] - hull[i];
                if (edge == Vector2.Zero) continue;
                Vector2 along = Vector2.Normalize(edge);
                Vector2 across = new(-along.Y, along.X);
                float left = float.PositiveInfinity;
                float top = float.PositiveInfinity;
                float right = float.NegativeInfinity;
                float bottom = float.NegativeInfinity;
                for (int j = 0; j < count; j++)
                {
                    float x = Vector2.Dot(hull[j], along);
                    float y = Vector2.Dot(hull[j], across);
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
                double area = (right - left) * (bottom - top);
                if (area >= bestArea) continue;
                bestArea = area;
                best = new RotatedRectangle(along, across, left, top, right - left, bottom - top);
            }
            return best;
        }
        finally { ArrayPool<Vector2>.Shared.Return(hull); }
    }

    private static void AddHullPoint(Vector2[] hull, ref int count, int lowerBound, Vector2 point)
    {
        while (count >= lowerBound + 2 && Cross(hull[count - 1] - hull[count - 2], point - hull[count - 1]) <= 0) count--;
        hull[count++] = point;
    }

    private static List<Quad> MergeVerticalPunctuation(List<Quad> boxes)
    {
        Bounds[] bounds = boxes.Select(static box => box.GetBounds()).ToArray();
        bool[] consumed = new bool[boxes.Count];
        var corners = new List<Vector2>(8);
        for (int i = 0; i < bounds.Length; i++)
        {
            Bounds fragment = bounds[i];
            int best = -1;
            int distance = int.MaxValue;
            for (int j = 0; j < bounds.Length; j++)
            {
                Bounds column = bounds[j];
                int gap = fragment.Y - column.Bottom;
                double center = fragment.X + fragment.Width * 0.5;
                if (column.Height <= column.Width * 1.6 || Math.Max(fragment.Width, fragment.Height) >= column.Width * 0.65 ||
                    center < column.X || center > column.Right || gap <= -fragment.Height || gap >= column.Width * 0.60 ||
                    Math.Abs(gap) >= distance) continue;
                best = j;
                distance = Math.Abs(gap);
            }
            if (best < 0) continue;
            Quad columnBox = boxes[best];
            Quad fragmentBox = boxes[i];
            corners.Clear();
            corners.Add(columnBox.A);
            corners.Add(columnBox.B);
            corners.Add(columnBox.C);
            corners.Add(columnBox.D);
            corners.Add(fragmentBox.A);
            corners.Add(fragmentBox.B);
            corners.Add(fragmentBox.C);
            corners.Add(fragmentBox.D);
            boxes[best] = MinimumRectangle(corners).Expand(0);
            consumed[i] = true;
        }
        var merged = new List<Quad>(boxes.Count);
        for (int i = 0; i < boxes.Count; i++) if (!consumed[i]) merged.Add(boxes[i]);
        return merged;
    }

    private static IReadOnlyList<MangaOcrRegion> ArrangeDialogue(List<MangaOcrRegion> regions, ImagePixels source)
    {
        regions.Sort(static (first, second) => first.Y == second.Y ? second.X.CompareTo(first.X) : first.Y.CompareTo(second.Y));
        var rightToLeft = Comparer<MangaOcrRegion>.Create(static (first, second) =>
            first.X == second.X ? first.Y.CompareTo(second.Y) : second.X.CompareTo(first.X));
        for (int start = 0; start < regions.Count;)
        {
            MangaOcrRegion anchor = regions[start];
            int end = start + 1;
            while (end < regions.Count && regions[end].Y - anchor.Y <= Math.Max(12, Math.Min(regions[end].Height, anchor.Height) * 0.5)) end++;
            regions.Sort(start, end - start, rightToLeft);
            start = end;
        }

        var result = new List<MangaOcrRegion>(regions.Count);
        for (int start = 0; start < regions.Count;)
        {
            MangaOcrRegion anchor = regions[start];
            int end = start + 1;
            if (IsVertical(anchor))
            {
                while (end < regions.Count && end - start < 6 && IsVertical(regions[end]) &&
                       SameVerticalRow(anchor, regions[end]) && SameVerticalRow(regions[end - 1], regions[end]) &&
                       ClearVerticalSeam(regions[end - 1], regions[end], source)) end++;
            }
            result.Add(end == start + 1 ? anchor : JoinDialogue(regions, start, end));
            start = end;
        }
        return result;
    }

    private static bool IsVertical(MangaOcrRegion box) => box.Height >= 1.65 * box.Width && box.Text.Length >= 2;

    private static bool SameVerticalRow(MangaOcrRegion first, MangaOcrRegion second)
    {
        int overlap = Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y);
        return overlap >= 0.82 * Math.Max(first.Height, second.Height) &&
            Math.Abs(first.Y - second.Y) <= 0.45 * Math.Min(first.Width, second.Width);
    }

    private static bool ClearVerticalSeam(MangaOcrRegion right, MangaOcrRegion left, ImagePixels source)
    {
        int width = Math.Min(right.Width, left.Width);
        double rightCenter = right.X + right.Width * 0.5;
        double leftCenter = left.X + left.Width * 0.5;
        double centers = rightCenter - leftCenter;
        int gap = right.X - left.X - left.Width;
        if (centers < 0.42 * width || centers > 1.2 * width || gap > 0.22 * width) return false;

        int middle = (int)Math.Round((rightCenter + leftCenter) / 2);
        int margin = Math.Max(1, (int)Math.Round(width * 0.10));
        int top = Math.Max(right.Y, left.Y);
        int bottom = Math.Min(right.Y + right.Height, left.Y + left.Height);
        int start = Math.Max(0, middle - margin);
        int end = Math.Min(source.Width, middle + margin);
        int brightness = 0;
        for (int x = start; x < end; x++)
        {
            int dark = 0;
            for (int y = top; y < bottom; y++)
            {
                int gray = source.Get(x, y).Gray;
                if (gray >= 195) brightness++;
                if (gray < 90) dark++;
            }
            if (dark >= (bottom - top) * 0.45) return false;
        }
        return brightness >= (end - start) * (bottom - top) * 0.84;
    }

    private static MangaOcrRegion JoinDialogue(List<MangaOcrRegion> regions, int start, int end)
    {
        int x = int.MaxValue;
        int y = int.MaxValue;
        int right = 0;
        int bottom = 0;
        var text = new StringBuilder();
        for (int i = start; i < end; i++)
        {
            MangaOcrRegion region = regions[i];
            x = Math.Min(x, region.X);
            y = Math.Min(y, region.Y);
            right = Math.Max(right, region.X + region.Width);
            bottom = Math.Max(bottom, region.Y + region.Height);
            text.Append(region.Text);
        }
        return new MangaOcrRegion(x, y, right - x, bottom - y, text.ToString());
    }
}
