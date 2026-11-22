// Managed port of imsearch/orb.rs and ORB-SLAM3/ORBextractor.cc (GPL-3.0).
// Original copyright and BSD notices: tools/imsearch/ORB-SLAM3-LICENSE.txt.
using System.Buffers;
using System.Numerics;

namespace EhViewerForPc.ImageSearch;

/// <summary>Stores a detected feature and its oriented binary descriptor.</summary>
public readonly record struct OrbFeature(float X, float Y, int Level, float Angle, int Response, BinaryDescriptor Descriptor);

/// <summary>Extracts multiscale, spatially distributed FAST features and rotated ORB descriptors.</summary>
public sealed class OrbExtractor
{
    private readonly FeatureProfile _profile;
    private readonly bool _useSimd;
    private readonly IImageComputeAccelerator? _accelerator;
    private static readonly (int X, int Y)[] Circle = [(0,-3),(1,-3),(2,-2),(3,-1),(3,0),(3,1),(2,2),(1,3),(0,3),(-1,3),(-2,2),(-3,1),(-3,0),(-3,-1),(-2,-2),(-1,-3)];
    private readonly record struct Corner(int X, int Y, int Score);
    private sealed record Region(int X0, int Y0, int X1, int Y1, List<Corner> Points);
    private static readonly int[] UMax = MakeUMax();

    /// <summary>Initializes an extractor using a validated, immutable profile.</summary>
    public OrbExtractor(FeatureProfile? profile = null, bool useSimd = true, IImageComputeAccelerator? accelerator = null) { _profile = profile ?? new(); _profile.Validate(); _useSimd = useSimd; _accelerator = accelerator; }

    /// <summary>Extracts features without retaining image or pyramid buffers.</summary>
    public OrbFeature[] Extract(GrayImage input, CancellationToken cancellationToken = default)
    {
        double ratio = Math.Max((double)_profile.MaxWidth / input.Width, (double)_profile.MaxHeight / input.Height);
        GrayImage image = ratio >= 1 ? input : input.Resize(Math.Max(1, (int)Math.Round(input.Width * ratio)), Math.Max(1, (int)Math.Round(input.Height * ratio)));
        int featureCount = _profile.Features;
        if (!(image.Width < _profile.MaxHeight && image.Height < _profile.MaxWidth))
        {
            float aspect = (float)Math.Max(image.Width, image.Height) / Math.Min(image.Width, image.Height);
            if (aspect > _profile.MaxAspectRatio)
                featureCount = Math.Min(_profile.MaxFeatures, featureCount + (int)MathF.Round(featureCount * (aspect / _profile.MaxAspectRatio - 1) / 100, MidpointRounding.AwayFromZero) * 100);
        }
        double inverse = 1.0 / _profile.ScaleFactor;
        double desired = featureCount * (1 - inverse) / (1 - Math.Pow(inverse, _profile.Levels));
        int allocated = 0;
        var features = new List<OrbFeature>(featureCount + _profile.Levels * 3);
        GrayImage pyramid = image;
        for (int level = 0; level < _profile.Levels; level++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double scale = Math.Pow(_profile.ScaleFactor, level);
            if (level > 0) pyramid = pyramid.Resize(Math.Max(1, (int)Math.Round(image.Width / scale)), Math.Max(1, (int)Math.Round(image.Height / scale)), _profile.Interpolation);
            int n = level == _profile.Levels - 1 ? Math.Max(0, featureCount - allocated) : (int)Math.Round(desired);
            allocated += n; desired *= inverse;
            if (pyramid.Width < 40 || pyramid.Height < 40 || n == 0) continue;
            var corners = DetectCells(pyramid, cancellationToken);
            var selected = Distribute(corners, pyramid.Width, pyramid.Height, n);
            GrayImage blurred = ImageKernels.GaussianBlur(pyramid, _useSimd);
            foreach (var point in selected)
            {
                float angle = _profile.Oriented ? Orientation(pyramid, point.X, point.Y) : 0;
                features.Add(new((float)(point.X * scale), (float)(point.Y * scale), level, angle, point.Score, Describe(blurred, point.X, point.Y, angle)));
            }
        }
        return features.ToArray();
    }

    private List<Corner> DetectCells(GrayImage image, CancellationToken ct)
    {
        int[]? rented = _accelerator is null ? null : ArrayPool<int>.Shared.Rent(image.Pixels.Length);
        try
        {
            int[]? scores = rented is not null && _accelerator!.TryGetFastScores(image, _profile.MinFastThreshold, rented, ct) ? rented : null;
            const int border = 16;
            int width = image.Width - 2 * border, height = image.Height - 2 * border;
            int columns = Math.Max(1, width / 35), rows = Math.Max(1, height / 35);
            int cellWidth = (int)Math.Ceiling((double)width / columns), cellHeight = (int)Math.Ceiling((double)height / rows);
            // 单元有效区域不超过 69 × 69，重用同一块栈缓冲。分数范围为 0–254。
            Span<byte> cellScores = stackalloc byte[scores is null ? cellWidth * cellHeight : 0];
            Span<int> offsets = stackalloc int[16];
            if (scores is null)
            {
                for (int i = 0; i < offsets.Length; i++)
                    offsets[i] = Circle[i].Y * image.Width + Circle[i].X;
            }
            var points = new List<Corner>();
            for (int row = 0; row < rows; row++)
            {
                ct.ThrowIfCancellationRequested();
                for (int column = 0; column < columns; column++)
                {
                    int x0 = border + column * cellWidth, y0 = border + row * cellHeight;
                    int x1 = Math.Min(image.Width - border, x0 + cellWidth + 6), y1 = Math.Min(image.Height - border, y0 + cellHeight + 6);
                    if (scores is null)
                    {
                        AppendFastCorners(image, x0 + 3, y0 + 3, x1 - 3, y1 - 3, offsets, cellScores,
                            _profile.FastThreshold, _profile.MinFastThreshold, points);
                    }
                    else
                    {
                        int start = points.Count;
                        AppendFastCornersFromScores(image.Width, scores, x0 + 3, y0 + 3, x1 - 3, y1 - 3,
                            _profile.FastThreshold, points);
                        if (points.Count == start && _profile.FastThreshold != _profile.MinFastThreshold)
                            AppendFastCornersFromScores(image.Width, scores, x0 + 3, y0 + 3, x1 - 3, y1 - 3,
                                _profile.MinFastThreshold, points);
                    }
                }
            }

            // 剔除各边 3 像素后，相邻单元的有效范围互不重叠。
            return points;
        }
        finally { if (rented is not null) ArrayPool<int>.Shared.Return(rented); }
    }

    private static void AppendFastCornersFromScores(int width, int[] scores, int x0, int y0, int x1, int y1,
        int threshold, List<Corner> output)
    {
        for (int y = y0; y < y1; y++)
        for (int x = x0; x < x1; x++)
        {
            int score = scores[y * width + x];
            if (score < threshold) continue;
            bool maximum = true;
            for (int dy = -1; dy <= 1 && maximum; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                // 原实现的非极大值抑制只比较同一 cell 的候选，不比较跨 cell 像素。
                if ((dx != 0 || dy != 0) && nx >= x0 && nx < x1 && ny >= y0 && ny < y1 && scores[ny * width + nx] >= score)
                { maximum = false; break; }
            }
            if (maximum) output.Add(new(x, y, score));
        }
    }

    private static void AppendFastCorners(GrayImage image, int x0, int y0, int x1, int y1,
        ReadOnlySpan<int> offsets, Span<byte> scores, int threshold, int minimum, List<Corner> output)
    {
        int width = Math.Max(0, x1 - x0), height = Math.Max(0, y1 - y0);
        scores = scores[..(width * height)];
        scores.Clear();
        Span<int> differences = stackalloc int[24];
        byte[] pixels = image.Pixels;
        for (int y = 0; y < height; y++)
        {
            int start = (y0 + y) * image.Width + x0;
            for (int x = 0; x < width; x++)
            {
                int index = start + x;
                int center = pixels[index];
                int top = pixels[index + offsets[0]] - center;
                int right = pixels[index + offsets[4]] - center;
                int bottom = pixels[index + offsets[8]] - center;
                int left = pixels[index + offsets[12]] - center;
                int bright = (top > minimum ? 1 : 0) + (right > minimum ? 1 : 0)
                    + (bottom > minimum ? 1 : 0) + (left > minimum ? 1 : 0);
                int dark = (top < -minimum ? 1 : 0) + (right < -minimum ? 1 : 0)
                    + (bottom < -minimum ? 1 : 0) + (left < -minimum ? 1 : 0);
                if (bright < 2 && dark < 2) continue;

                uint brighter = 0, darker = 0;
                for (int i = 0; i < offsets.Length; i++)
                {
                    int difference = pixels[index + offsets[i]] - center;
                    differences[i] = difference;
                    brighter |= (difference > minimum ? 1U : 0U) << i;
                    darker |= (difference < -minimum ? 1U : 0U) << i;
                }
                uint positive = ArcStarts(brighter), negative = ArcStarts(darker);
                if ((positive | negative) == 0) continue;

                differences[..8].CopyTo(differences[16..]);
                // 只有整个弧段超过最低阈值，才可能产生有效分数。
                scores[y * width + x] = (byte)(Math.Max(ArcScore(positive, differences, 1),
                    ArcScore(negative, differences, -1)) - 1);
            }
        }

        int cellStart = output.Count;
        bool hasStrongCorner = false;
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int score = scores[y * width + x];
            if (score < (hasStrongCorner ? threshold : minimum)) continue;
            if (!IsLocalMaximum(scores, width, height, x, y, score)) continue;
            // 弱角点无法抑制强角点。若存在强极大值，只保留强阈值下的结果。
            if (!hasStrongCorner && score >= threshold)
            {
                output.RemoveRange(cellStart, output.Count - cellStart);
                hasStrongCorner = true;
            }
            output.Add(new(x0 + x, y0 + y, score));
        }
    }

    private static uint ArcStarts(uint ring)
    {
        // 将环复制一周，逐步得到每个长度为 9 的连续弧段的起点。
        uint repeated = ring | (ring << 16);
        uint pairs = repeated & (repeated >> 1);
        uint quads = pairs & (pairs >> 2);
        return quads & (quads >> 4) & (repeated >> 8) & 0xffff;
    }

    private static int ArcScore(uint starts, ReadOnlySpan<int> differences, int sign)
    {
        int best = 0;
        while (starts != 0)
        {
            int start = BitOperations.TrailingZeroCount(starts);
            int weakest = differences[start] * sign;
            for (int i = start + 1; i < start + 9 && weakest > best; i++)
                weakest = Math.Min(weakest, differences[i] * sign);
            best = Math.Max(best, weakest);
            starts &= starts - 1;
        }
        return best;
    }

    private static bool IsLocalMaximum(ReadOnlySpan<byte> scores, int width, int height, int x, int y, int score)
    {
        for (int ny = Math.Max(0, y - 1); ny <= Math.Min(height - 1, y + 1); ny++)
        for (int nx = Math.Max(0, x - 1); nx <= Math.Min(width - 1, x + 1); nx++)
        {
            if ((nx != x || ny != y) && scores[ny * width + nx] >= score) return false;
        }
        return true;
    }

    private static List<Corner> Distribute(List<Corner> points, int width, int height, int target)
    {
        if (points.Count <= target) return points;
        int count = Math.Max(1, (int)Math.Round((double)(width - 32) / (height - 32), MidpointRounding.AwayFromZero));
        var regions = new List<Region>();
        for (int i = 0; i < count; i++)
        {
            int x0 = 16 + (width - 32) * i / count, x1 = 16 + (width - 32) * (i + 1) / count;
            var inside = points.Where(p => p.X >= x0 && p.X < x1).ToList();
            if (inside.Count > 0) regions.Add(new(x0, 16, x1, height - 16, inside));
        }
        while (regions.Count < target)
        {
            var expandable = regions.Where(r => r.Points.Count > 1 && (r.X1 - r.X0 > 1 || r.Y1 - r.Y0 > 1)).ToArray();
            if (expandable.Length == 0) break;
            if (regions.Count + expandable.Length * 3 > target) expandable = expandable.OrderByDescending(r => r.Points.Count).ThenBy(r => r.Y0).ThenBy(r => r.X0).ToArray();
            int before = regions.Count;
            foreach (var region in expandable)
            {
                int mx = (region.X0 + region.X1 + 1) / 2, my = (region.Y0 + region.Y1 + 1) / 2;
                var children = new List<Region>();
                for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++)
                {
                    int x0 = x == 0 ? region.X0 : mx, x1 = x == 0 ? mx : region.X1;
                    int y0 = y == 0 ? region.Y0 : my, y1 = y == 0 ? my : region.Y1;
                    var inside = region.Points.Where(p => p.X >= x0 && p.X < x1 && p.Y >= y0 && p.Y < y1).ToList();
                    if (inside.Count > 0) children.Add(new(x0, y0, x1, y1, inside));
                }
                regions.Remove(region); regions.AddRange(children);
                if (regions.Count >= target) break;
            }
            if (regions.Count == before) break;
        }
        return regions.Select(r => r.Points.OrderByDescending(p => p.Score).ThenBy(p => p.Y).ThenBy(p => p.X).First()).ToList();
    }

    private static int[] MakeUMax()
    {
        var result = new int[16];
        int vmax = (int)Math.Floor(15 * Math.Sqrt(2) / 2 + 1), vmin = (int)Math.Ceiling(15 * Math.Sqrt(2) / 2);
        for (int v = 0; v <= vmax; v++) result[v] = (int)Math.Round(Math.Sqrt(225 - v * v));
        for (int v = 15, v0 = 0; v >= vmin; v--) { while (result[v0] == result[v0 + 1]) v0++; result[v] = v0++; }
        return result;
    }

    private static float Orientation(GrayImage image, int x, int y)
    {
        int m01 = 0, m10 = 0;
        for (int u = -15; u <= 15; u++) m10 += u * image.At(x + u, y);
        for (int v = 1; v <= 15; v++)
        {
            int sum = 0;
            for (int u = -UMax[v]; u <= UMax[v]; u++)
            {
                int plus = image.At(x + u, y + v), minus = image.At(x + u, y - v);
                sum += plus - minus; m10 += u * (plus + minus);
            }
            m01 += v * sum;
        }
        float angle = MathF.Atan2(m01, m10);
        return angle < 0 ? angle + 2 * MathF.PI : angle;
    }

    private static BinaryDescriptor Describe(GrayImage image, int x, int y, float angle)
    {
        Span<byte> bytes = stackalloc byte[32]; bytes.Clear();
        float a = MathF.Cos(angle), b = MathF.Sin(angle);
        for (int bit = 0; bit < 256; bit++)
        {
            int i = bit * 4;
            int x1 = (int)MathF.Round(OrbPattern.Values[i] * a - OrbPattern.Values[i + 1] * b);
            int y1 = (int)MathF.Round(OrbPattern.Values[i] * b + OrbPattern.Values[i + 1] * a);
            int x2 = (int)MathF.Round(OrbPattern.Values[i + 2] * a - OrbPattern.Values[i + 3] * b);
            int y2 = (int)MathF.Round(OrbPattern.Values[i + 2] * b + OrbPattern.Values[i + 3] * a);
            if (image.At(x + x1, y + y1) < image.At(x + x2, y + y2)) bytes[bit / 8] |= (byte)(1 << (bit % 8));
        }
        return BinaryDescriptor.Read(bytes);
    }

}
