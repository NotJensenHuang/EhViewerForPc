using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EhViewerForPc.ImageSearch;

/// <summary>Defines the versioned feature extraction configuration.</summary>
public sealed record FeatureProfile
{
    /// <summary>Gets the requested number of features.</summary>
    public int Features { get; init; } = 500;
    /// <summary>Gets the pyramid scale factor.</summary>
    public float ScaleFactor { get; init; } = 1.2f;
    /// <summary>Gets the pyramid level count.</summary>
    public int Levels { get; init; } = 8;
    /// <summary>Gets the initial FAST threshold.</summary>
    public int FastThreshold { get; init; } = 20;
    /// <summary>Gets the fallback FAST threshold.</summary>
    public int MinFastThreshold { get; init; } = 7;
    /// <summary>Gets whether intensity centroid orientation is enabled.</summary>
    public bool Oriented { get; init; } = true;
    /// <summary>Gets the internal resize height.</summary>
    public int MaxHeight { get; init; } = 1080;
    /// <summary>Gets the internal resize width.</summary>
    public int MaxWidth { get; init; } = 768;
    /// <summary>Gets the aspect ratio above which extra features are requested.</summary>
    public float MaxAspectRatio { get; init; } = 5;
    /// <summary>Gets the feature limit for long images.</summary>
    public int MaxFeatures { get; init; } = 1000;
    /// <summary>Gets the pyramid interpolation kernel.</summary>
    public ImageInterpolation Interpolation { get; init; } = ImageInterpolation.Area;
    /// <summary>Gets the extraction implementation version.</summary>
    public const string ExtractorVersion = "managed-slam-orb-v1";
    /// <summary>Gets the normalization implementation version.</summary>
    public const string NormalizerVersion = "canonical-png-gray8-v1";
    /// <summary>Gets the stable fingerprint of every extraction parameter.</summary>
    public string Id => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"normalizer={NormalizerVersion}\nextractor={ExtractorVersion}\nmaxSide=2048\nfeatures={Features}\nscale={ScaleFactor:R}\nlevels={Levels}\nfast={FastThreshold}\nminFast={MinFastThreshold}\noriented={Oriented}\nsize={MaxHeight}x{MaxWidth}\naspect={MaxAspectRatio:R}\nmaxFeatures={MaxFeatures}\ninterpolation={Interpolation.ToString().ToLowerInvariant()}\n"))));

    /// <summary>Rejects unsupported or excessive extraction parameters.</summary>
    public void Validate()
    {
        if (Features is < 1 or > 10000 || MaxFeatures < Features || MaxFeatures > 10000 || Levels is < 1 or > 16
            || !float.IsFinite(ScaleFactor) || ScaleFactor is <= 1 or > 2 || FastThreshold is < 1 or > 255
            || MinFastThreshold < 1 || MinFastThreshold > FastThreshold || MaxWidth is < 32 or > 4096
            || MaxHeight is < 32 or > 4096 || !float.IsFinite(MaxAspectRatio) || MaxAspectRatio < 1 || !Enum.IsDefined(Interpolation))
            throw new ArgumentOutOfRangeException(nameof(FeatureProfile), "Invalid feature profile.");
    }
}

/// <summary>Defines search limits independent of extraction parameters.</summary>
public sealed record SearchOptions
{
    public static int DefaultReadThreads => Math.Clamp(Environment.ProcessorCount, 1, 8);
    /// <summary>Gets the number of probed inverted lists.</summary>
    public int NProbe { get; init; } = 16;
    /// <summary>Gets the neighbors kept per query descriptor.</summary>
    public int K { get; init; } = 3;
    /// <summary>Gets the maximum accepted Hamming distance.</summary>
    public int MaxDistance { get; init; } = 64;
    /// <summary>Gets the maximum number of returned images.</summary>
    public int Count { get; init; } = 100;
    /// <summary>Gets whether raw match count replaces Wilson scoring.</summary>
    public bool CountScore { get; init; }
    /// <summary>Gets the maximum simultaneous bucket readers.</summary>
    public int ReadThreads { get; init; } = DefaultReadThreads;
    internal void Validate(int centers)
    {
        if (NProbe < 1 || NProbe > centers || K is < 1 or > 64 || Count is < 1 or > 1000 || MaxDistance is < 0 or > 255 || ReadThreads is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(SearchOptions), "Invalid search limits.");
    }
}

/// <summary>Identifies a scored backend image and its logical aliases.</summary>
public sealed record SearchHit(long ImageId, float Score, string[] Aliases);

/// <summary>Identifies a crop in display-oriented source pixels.</summary>
public readonly record struct ImageCrop(int X, int Y, int Width, int Height);
