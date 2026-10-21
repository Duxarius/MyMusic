namespace MyMusic.Models;

public sealed class Track
{
    public long Id { get; set; }
    public string Title { get; set; } = "Unknown Title";
    public string Artist { get; set; } = "Unknown Artist";
    public string Album { get; set; } = "Unknown Album";
    public string AlbumArtist { get; set; } = "Unknown Artist";
    public int TrackNumber { get; set; }
    public int DiscNumber { get; set; }
    public string Genre { get; set; } = "";
    public int? Year { get; set; }
    public string FilePath { get; set; } = "";
    public string OriginalPath { get; set; } = "";
    public string? ArtworkPath { get; set; }
    public string? ArtworkUri => string.IsNullOrWhiteSpace(ArtworkPath) || !File.Exists(ArtworkPath) ? null : new Uri(Path.GetFullPath(ArtworkPath)).AbsoluteUri;
    public string FileHash { get; set; } = "";
    public long FileSize { get; set; }
    public long DurationSeconds { get; set; }
    public string DurationText => TimeSpan.FromSeconds(DurationSeconds).ToString(DurationSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss");
    public DateTime ImportedAtUtc { get; set; }
}
