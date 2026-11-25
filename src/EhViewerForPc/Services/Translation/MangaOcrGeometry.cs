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

    internal static IReadOnlyList<MangaOcrRegion> ArrangeDialogue(List<MangaOcrRegion> regions,
        ReadOnlySpan<byte> pixels, int width, int height, int stride, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (regions.Count < 2) return regions;
        var source = new ImagePixels(pixels, width, height, stride, 4);
        var result = new List<MangaOcrRegion>(regions.Count);
        var vertical = new List<MangaOcrRegion>();
        var horizontal = new List<MangaOcrRegion>();
        foreach (MangaOcrRegion region in regions)
        {
            if (region.Text.Length >= 2 && region.Height >= 1.65 * region.Width) vertical.Add(region);
            else if (region.Text.Length >= 2 && region.Width >= 1.65 * region.Height) horizontal.Add(region);
            else result.Add(region);
        }
        AppendDialogue(vertical, result, source, vertical: true, token);
        AppendDialogue(horizontal, result, source, vertical: false, token);

        result.Sort(static (first, second) => first.Y == second.Y ? second.X.CompareTo(first.X) : first.Y.CompareTo(second.Y));
        var rightToLeft = Comparer<MangaOcrRegion>.Create(static (first, second) =>
            first.X == second.X ? first.Y.CompareTo(second.Y) : second.X.CompareTo(first.X));
        for (int start = 0; start < result.Count;)
        {
            MangaOcrRegion anchor = result[start];
            int end = start + 1;
            while (end < result.Count && result[end].Y - anchor.Y <= Math.Max(12, Math.Min(result[end].Height, anchor.Height) * 0.5)) end++;
            result.Sort(start, end - start, rightToLeft);
            start = end;
        }
        return result;
    }

    private static void AppendDialogue(List<MangaOcrRegion> candidates, List<MangaOcrRegion> output,
        ImagePixels source, bool vertical, CancellationToken token)
    {
        if (candidates.Count < 2)
        {
            if (candidates.Count == 1) output.Add(candidates[0]);
            return;
        }
        if (vertical)
            candidates.Sort(static (first, second) => CenterX(second).CompareTo(CenterX(first)));
        else
            candidates.Sort(static (first, second) => CenterY(first).CompareTo(CenterY(second)));

        // 用书写方向上的最近邻组成一条链。页内排序时穿插的音效不会截断对话。
        int[] next = new int[candidates.Count];
        int[] previous = new int[candidates.Count];
        Array.Fill(next, -1);
        Array.Fill(previous, -1);
        for (int i = 0; i < candidates.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            MangaOcrRegion first = candidates[i];
            for (int j = i + 1; j < candidates.Count; j++)
            {
                MangaOcrRegion second = candidates[j];
                double distance = vertical ? CenterX(first) - CenterX(second) : CenterY(second) - CenterY(first);
                if (distance > (vertical ? first.Width * 1.55 : first.Height * 1.60)) break;
                bool adjacent = vertical ? AdjacentColumns(first, second, distance, source) : AdjacentLines(first, second, distance, source);
                if (!adjacent) continue;
                // 已占用时，当前候选更靠近第二行或列。
                if (previous[j] >= 0) next[previous[j]] = -1;
                previous[j] = i;
                next[i] = j;
                break;
            }
        }

        for (int i = 0; i < candidates.Count; i++)
            if (previous[i] < 0) output.Add(next[i] < 0 ? candidates[i] : JoinDialogue(candidates, next, i, vertical));
    }

    private static double CenterX(MangaOcrRegion region) => region.X + region.Width * 0.5;

    private static double CenterY(MangaOcrRegion region) => region.Y + region.Height * 0.5;

    private static bool AdjacentColumns(MangaOcrRegion right, MangaOcrRegion left, double distance, ImagePixels source)
    {
        int width = Math.Min(right.Width, left.Width);
        int gap = right.X - left.X - left.Width;
        if (distance < 0.42 * width || distance > 1.55 * width || gap > 0.40 * width ||
            Math.Max(right.Width, left.Width) > 1.65 * width) return false;

        int top = Math.Max(right.Y, left.Y);
        int bottom = Math.Min(right.Y + right.Height, left.Y + left.Height);
        if (bottom - top < 0.72 * Math.Min(right.Height, left.Height) || Math.Abs(right.Y - left.Y) > 0.55 * width)
            return false;

        int middle = (int)Math.Round((CenterX(right) + CenterX(left)) / 2);
        return ClearDialogueSeam(source, middle, width, top, bottom, vertical: true);
    }

    private static bool AdjacentLines(MangaOcrRegion upper, MangaOcrRegion lower, double distance, ImagePixels source)
    {
        int height = Math.Min(upper.Height, lower.Height);
        int gap = lower.Y - upper.Y - upper.Height;
        if (distance < 0.65 * height || distance > 1.60 * height || gap > 0.50 * height ||
            Math.Max(upper.Height, lower.Height) > 1.45 * height) return false;

        int left = Math.Max(upper.X, lower.X);
        int right = Math.Min(upper.X + upper.Width, lower.X + lower.Width);
        if (right - left < 0.84 * Math.Min(upper.Width, lower.Width)) return false;
        if (Math.Abs(CenterX(upper) - CenterX(lower)) > 0.90 * height && Math.Abs(upper.X - lower.X) > 0.55 * height)
            return false;

        // 横排行长常有差异。间隙采用实际边缘位置，避免中间扫描线穿过较高一行的文字。
        int middle = (upper.Y + upper.Height + lower.Y) / 2;
        return ClearDialogueSeam(source, middle, height, left, right, vertical: false);
    }

    private static bool ClearDialogueSeam(ImagePixels source, int middle, int thickness,
        int begin, int finish, bool vertical)
    {
        int margin = Math.Max(1, (int)Math.Round(thickness * 0.10));
        int limit = vertical ? source.Width : source.Height;
        int start = Math.Max(0, middle - margin);
        int end = Math.Min(limit, middle + margin);
        // 检测框带外扩留白，边框可能在间隙两侧，需在比亮度采样更宽的范围内检查。
        int borderMargin = Math.Max(margin, (int)Math.Ceiling(thickness * 0.25));
        int borderStart = Math.Max(0, middle - borderMargin);
        int borderEnd = Math.Min(limit, middle + borderMargin + 1);
        int length = finish - begin;
        int open = 0;
        // 竖列允许浅灰底纹。横排换行采用更严格的浅色间隙，以免跨连上下两个气泡。
        int light = vertical ? 115 : 195;
        for (int across = borderStart; across < borderEnd; across++)
        {
            bool inCenter = across >= start && across < end;
            int dark = 0;
            int darkRun = 0;
            int longestDarkRun = 0;
            for (int along = begin; along < finish; along++)
            {
                int gray = vertical ? source.Get(across, along).Gray : source.Get(along, across).Gray;
                if (inCenter && gray >= light) open++;
                if (gray < 90)
                {
                    dark++;
                    longestDarkRun = Math.Max(longestDarkRun, ++darkRun);
                }
                else darkRun = 0;
            }
            // 竖排检测框外扩后会覆盖字边，断开的笔画也可能累计成较高的深色占比。
            if (dark >= length * 0.45 && (!vertical || longestDarkRun >= Math.Min(thickness * 0.75, length * 0.40)))
                return false;
        }
        return open >= (end - start) * length * (vertical ? 0.72 : 0.85);
    }

    private static MangaOcrRegion JoinDialogue(List<MangaOcrRegion> regions, int[] next, int start, bool vertical)
    {
        int x = int.MaxValue;
        int y = int.MaxValue;
        int right = 0;
        int bottom = 0;
        var text = new StringBuilder();
        for (int i = start; i >= 0; i = next[i])
        {
            MangaOcrRegion region = regions[i];
            x = Math.Min(x, region.X);
            y = Math.Min(y, region.Y);
            right = Math.Max(right, region.X + region.Width);
            bottom = Math.Max(bottom, region.Y + region.Height);
            if (!vertical && i != start) text.Append('\n');
            text.Append(region.Text);
        }
        return new MangaOcrRegion(x, y, right - x, bottom - y, text.ToString());
    }
}
