namespace EhViewerReader.Core.Models;

public enum FilterTarget
{
    Tag = 0,
    Title = 1,
    Uploader = 2,
}

/// <summary>
/// One list filter rule. <see cref="Include"/> rules act as a whitelist; the
/// rest hide matching galleries.
/// </summary>
public sealed class FilterRule
{
    public string Text { get; set; } = string.Empty;
    public FilterTarget Target { get; set; } = FilterTarget.Tag;
    public bool Include { get; set; }
    public bool Enabled { get; set; } = true;

    public FilterRule Clone() => new()
    {
        Text = Text,
        Target = Target,
        Include = Include,
        Enabled = Enabled,
    };
}
