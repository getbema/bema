namespace Bema.Core;

/// <summary>A slide as Bema knows it. <see cref="Id"/> is 1-based, as PowerPoint numbers slides.</summary>
public sealed record SlideInfo(int Id, string Title, string Notes, bool Hidden, bool Draft = false)
{
    public SlideInfo WithNotes(string notes, bool draft) => this with { Notes = notes, Draft = draft };
}
