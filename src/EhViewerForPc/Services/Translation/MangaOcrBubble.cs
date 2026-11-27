using System.Buffers;

namespace EhViewerForPc.Services;

internal static partial class MangaOcr
{
    private const int BubblePaperBrightness = 195;
    private const int MaximumBubbleSearchPixels = 160_000;
    private const byte Paper = 2;
    private const byte Exterior = 3;

    private readonly record struct BubbleInteriors(MangaOcrLayoutBounds Wide, MangaOcrLayoutBounds? Balanced);

    private static bool InSameClosedBubble(ImagePixels image, MangaOcrRegion right, MangaOcrRegion left,
        int thickness, int seedX, int seedY, CancellationToken token)
    {
        Bounds window = BubbleSearchWindow(image, left.X, Math.Min(left.Y, right.Y),
            Math.Max(left.X + left.Width, right.X + right.Width), Math.Max(left.Y + left.Height, right.Y + right.Height), thickness * 4);
        if (!IsValidBubbleWindow(window)) return false;
        int area = window.Width * window.Height;
        byte[] visited = ArrayPool<byte>.Shared.Rent(area);
        try
        {
            return FillClosedPaper(image, window, seedX, seedY, thickness, visited, token) &&
                HasPaperCoverage(visited, window, left) && HasPaperCoverage(visited, window, right);
        }
        finally { ArrayPool<byte>.Shared.Return(visited); }
    }

    private static BubbleInteriors? MeasureBubbleInterior(ImagePixels image, MangaOcrRegion dialogue,
        CancellationToken token)
    {
        int x = dialogue.X + dialogue.Width / 2;
        int y = dialogue.Y + dialogue.Height / 2;
        if (!TryFindPaperSeed(image, dialogue, x, y, out int seedX, out int seedY)) return null;

        int padding = Math.Clamp(Math.Min(dialogue.Width, dialogue.Height) * 2, 20, 60);
        Bounds previous = default;
        for (int attempt = 0; attempt < 4; attempt++, padding *= 2)
        {
            Bounds window = BubbleSearchWindow(image, dialogue.X, dialogue.Y, dialogue.X + dialogue.Width,
                dialogue.Y + dialogue.Height, padding);
            if (window == previous) return null;
            if (!IsValidBubbleWindow(window))
            {
                // 大图上也把最后一次搜索扩到预算允许的最大范围，避免跳过介于两档之间的泡。
                int minimum = padding / 2;
                int maximum = padding;
                while (maximum - minimum > 1)
                {
                    int middle = minimum + (maximum - minimum) / 2;
                    Bounds probe = BubbleSearchWindow(image, dialogue.X, dialogue.Y, dialogue.X + dialogue.Width,
                        dialogue.Y + dialogue.Height, middle);
                    if (IsValidBubbleWindow(probe)) minimum = middle;
                    else maximum = middle;
                }
                window = BubbleSearchWindow(image, dialogue.X, dialogue.Y, dialogue.X + dialogue.Width,
                    dialogue.Y + dialogue.Height, minimum);
                if (!IsValidBubbleWindow(window) || window == previous) return null;
                attempt = 3;
            }
            previous = window;

            byte[] visited = ArrayPool<byte>.Shared.Rent(window.Width * window.Height);
            try
            {
                if (!FillClosedPaper(image, window, seedX, seedY, 1, visited, token)) continue;
                if (!HasPaperCoverage(visited, window, dialogue)) return null;
                MarkPaperExterior(visited, window, token);
                return FindInteriorRectangle(visited, window, dialogue, x, y, token);
            }
            finally { ArrayPool<byte>.Shared.Return(visited); }
        }
        return null;
    }

    private static bool TryFindPaperSeed(ImagePixels image, MangaOcrRegion region, int x, int y, out int seedX, out int seedY)
    {
        seedX = x;
        seedY = y;
        int best = int.MaxValue;
        for (int row = Math.Max(region.Y, y - 8); row < Math.Min(region.Y + region.Height, y + 9); row++)
        {
            for (int column = Math.Max(region.X, x - 8); column < Math.Min(region.X + region.Width, x + 9); column++)
            {
                int distance = (column - x) * (column - x) + (row - y) * (row - y);
                if (distance >= best || image.Get(column, row).Gray < BubblePaperBrightness) continue;
                best = distance;
                seedX = column;
                seedY = row;
            }
        }
        return best != int.MaxValue;
    }

    private static Bounds BubbleSearchWindow(ImagePixels image, int x0, int y0, int x1, int y1, int padding)
    {
        int x = Math.Max(0, x0 - padding);
        int y = Math.Max(0, y0 - padding);
        return new Bounds(x, y, Math.Min(image.Width, x1 + padding) - x, Math.Min(image.Height, y1 + padding) - y);
    }

    private static bool IsValidBubbleWindow(Bounds window) =>
        window.Width >= 3 && window.Height >= 3 && (long)window.Width * window.Height <= MaximumBubbleSearchPixels;

    private static bool FillClosedPaper(ImagePixels image, Bounds window, int seedX, int seedY, int thickness,
        byte[] visited, CancellationToken token)
    {
        int seedStart = Math.Max(window.X + 1, seedX - thickness / 4);
        int seedEnd = Math.Min(window.Right - 2, seedX + thickness / 4);
        // 接缝中心可能落在笔画上，选择最近的明亮纸底作为起点。
        int xSeed = seedStart;
        while (xSeed <= seedEnd && image.Get(xSeed, seedY).Gray < BubblePaperBrightness) xSeed++;
        if (xSeed > seedEnd) return false;

        int width = window.Width;
        int area = width * window.Height;
        int[] pending = ArrayPool<int>.Shared.Rent(area);
        try
        {
            Array.Clear(visited, 0, area);
            int seed = (seedY - window.Y) * width + xSeed - window.X;
            visited[seed] = Paper;
            pending[0] = seed;
            int count = 1;
            for (int index = 0; index < count; index++)
            {
                if ((index & 0x1fff) == 0) token.ThrowIfCancellationRequested();
                int cell = pending[index];
                int column = cell % width;
                int row = cell / width;
                // 外泄到搜索边界时无法证实气泡闭合。
                if (column == 0 || row == 0 || column == width - 1 || row == window.Height - 1) return false;

                int x = column + window.X;
                int y = row + window.Y;
                EnqueueBubblePixel(image, visited, pending, ref count, cell - 1, x - 1, y);
                EnqueueBubblePixel(image, visited, pending, ref count, cell + 1, x + 1, y);
                EnqueueBubblePixel(image, visited, pending, ref count, cell - width, x, y - 1);
                EnqueueBubblePixel(image, visited, pending, ref count, cell + width, x, y + 1);
            }
            return true;
        }
        finally { ArrayPool<int>.Shared.Return(pending); }
    }

    private static void EnqueueBubblePixel(ImagePixels image, byte[] visited, int[] pending, ref int count,
        int cell, int x, int y)
    {
        if (visited[cell] != 0) return;
        if (image.Get(x, y).Gray >= BubblePaperBrightness)
        {
            visited[cell] = Paper;
            pending[count++] = cell;
        }
        else visited[cell] = 1;
    }

    private static bool HasPaperCoverage(byte[] visited, Bounds window, MangaOcrRegion region)
    {
        int covered = 0;
        for (int y = region.Y; y < region.Y + region.Height; y++)
        {
            int start = (y - window.Y) * window.Width + region.X - window.X;
            for (int x = 0; x < region.Width; x++)
                if (visited[start + x] == Paper) covered++;
        }

        // 允许检测外扩及原字留下的暗像素，但纸底必须覆盖大部分区域。
        return covered >= region.Width * region.Height * 0.55;
    }

    private static void MarkPaperExterior(byte[] visited, Bounds window, CancellationToken token)
    {
        int width = window.Width;
        int[] pending = ArrayPool<int>.Shared.Rent(width * window.Height);
        try
        {
            // 已确认纸底未触边，搜索框的所有外边缘都可由左上角到达。
            visited[0] = Exterior;
            pending[0] = 0;
            int count = 1;
            for (int index = 0; index < count; index++)
            {
                if ((index & 0x1fff) == 0) token.ThrowIfCancellationRequested();
                int column = pending[index] % width;
                int row = pending[index] / width;
                for (int y = Math.Max(0, row - 1); y <= Math.Min(window.Height - 1, row + 1); y++)
                for (int x = Math.Max(0, column - 1); x <= Math.Min(width - 1, column + 1); x++)
                {
                    int cell = y * width + x;
                    if (visited[cell] is Paper or Exterior) continue;
                    visited[cell] = Exterior;
                    pending[count++] = cell;
                }
            }
        }
        finally { ArrayPool<int>.Shared.Return(pending); }
    }

    private static BubbleInteriors? FindInteriorRectangle(byte[] visited, Bounds window, MangaOcrRegion dialogue,
        int centerX, int centerY, CancellationToken token)
    {
        int width = window.Width;
        int[] runs = ArrayPool<int>.Shared.Rent(width);
        int[] stack = ArrayPool<int>.Shared.Rent(width);
        try
        {
            Array.Clear(runs, 0, width);
            Bounds best = default;
            Bounds balanced = default;
            long balancedScore = 0;
            long bestScore = (long)dialogue.Width * dialogue.Width * dialogue.Height;
            int minimumArea = dialogue.Width * dialogue.Height * 85 / 100;
            int inkLeft = dialogue.X - window.X;
            int inkRight = inkLeft + dialogue.Width;
            for (int row = 0; row < window.Height; row++)
            {
                token.ThrowIfCancellationRequested();
                int y = window.Y + row;
                ReadOnlySpan<byte> pixels = visited.AsSpan(row * width, width);
                bool mayContainInk = y >= dialogue.Y && y < dialogue.Y + dialogue.Height;
                int stackCount = 0;
                for (int column = 0; column <= width; column++)
                {
                    if (column < width)
                    {
                        // 只允许原字范围内、与气泡外部不连通的笔画空洞。
                        bool ink = mayContainInk && column >= inkLeft && column < inkRight &&
                            pixels[column] != Exterior;
                        runs[column] = pixels[column] == Paper || ink ? runs[column] + 1 : 0;
                    }

                    int height = column == width ? 0 : runs[column];
                    while (stackCount > 0 && runs[stack[stackCount - 1]] > height)
                    {
                        int runHeight = runs[stack[--stackCount]];
                        int left = stackCount == 0 ? 0 : stack[stackCount - 1] + 1;
                        // 在气泡边缘保留两像素间距。
                        int x0 = window.X + left + 2;
                        int y0 = y - runHeight + 3;
                        int w = column - left - 4;
                        int h = runHeight - 4;
                        if (w < 3 || h < 3 || w * h < minimumArea ||
                            centerX < x0 || centerX >= x0 + w || centerY < y0 || centerY >= y0 + h)
                            continue;
                        long score = (long)w * w * h;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = new Bounds(x0, y0, w, h);
                        }
                        // 宽版便于横排。保留短边也够长的安全候选，以便实际译文决定换行。
                        long compactness = (long)w * h * Math.Min(w, h);
                        if (compactness > balancedScore)
                        {
                            balancedScore = compactness;
                            balanced = new Bounds(x0, y0, w, h);
                        }
                    }
                    if (column < width) stack[stackCount++] = column;
                }
            }
            if (best.Width == 0) return null;
            return new BubbleInteriors(new(best.X, best.Y, best.Width, best.Height),
                balanced.Width == 0 || best == balanced ? null : new(balanced.X, balanced.Y, balanced.Width, balanced.Height));
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stack);
            ArrayPool<int>.Shared.Return(runs);
        }
    }
}
