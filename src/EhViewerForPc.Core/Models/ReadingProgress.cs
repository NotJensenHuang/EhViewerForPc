namespace EhViewerForPc.Core.Models;

public sealed class ReadingProgress
{
    public int PageNumber { get; set; } = 1;
    public int PageIndex { get; set; }
    public double PageOffset { get; set; }
    public int PageCountAtSave { get; set; }
    public DateTime LastReadUtc { get; set; }
    public bool Completed { get; set; }
    public bool Favorite { get; set; }
    public bool HiddenFromHistory { get; set; }

    public ReadingProgress Clone() => (ReadingProgress)MemberwiseClone();
}
