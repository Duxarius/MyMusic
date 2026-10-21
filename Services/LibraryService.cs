using System.Security.Cryptography;
using MyMusic.Data;
using MyMusic.Models;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace MyMusic.Services;

public sealed class ImportResult
{
    public int Imported { get; set; }
    public int SkippedDuplicates { get; set; }
    public int SkippedUnsupported { get; set; }
    public List<string> Errors { get; } = [];
}

public sealed class LibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".mp3", ".flac", ".wav", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".ape", ".wv", ".mpc" };

    private readonly Database _database;
    public string LibraryRoot { get; }

    public LibraryService(Database database, string libraryRoot)
    {
        _database = database;
        LibraryRoot = libraryRoot;
        Directory.CreateDirectory(LibraryRoot);
    }

    public async Task<ImportResult> ImportAsync(IEnumerable<string> paths)
    {
        var result = new ImportResult();
        foreach (var path in ExpandPaths(paths))
        {
            if (!SupportedExtensions.Contains(Path.GetExtension(path))) { result.SkippedUnsupported++; continue; }
            try
            {
                var hash = await ComputeHashAsync(path);
                if (await _database.ContainsHashAsync(hash)) { result.SkippedDuplicates++; continue; }

                var metadata = await ReadMetadataAsync(path);
                var artist = Clean(metadata.Artist, "Unknown Artist");
                var album = Clean(metadata.Album, "Unknown Album");
                var title = Clean(metadata.Title, Path.GetFileNameWithoutExtension(path));
                var extension = Path.GetExtension(path).ToLowerInvariant();
                var folder = Path.Combine(LibraryRoot, CleanPathPart(artist), CleanPathPart(album));
                Directory.CreateDirectory(folder);
                var destination = GetUniqueDestination(folder, $"{metadata.TrackNumber:00} - {title}{extension}");
                File.Copy(path, destination, false);

                // Read embedded ID3/Vorbis/MP4 pictures directly. Windows shell thumbnails
                // are not reliable for unpackaged desktop apps and may omit embedded covers.
                var artworkPath = await TryExtractEmbeddedArtworkAsync(destination);

                var track = new Track
                {
                    Title = title,
                    Artist = artist,
                    Album = album,
                    AlbumArtist = Clean(metadata.AlbumArtist, artist),
                    TrackNumber = (int)metadata.TrackNumber,
                    DiscNumber = (int)metadata.DiscNumber,
                    Genre = metadata.Genre,
                    Year = metadata.Year > 0 ? (int)metadata.Year : null,
                    FilePath = destination,
                    OriginalPath = Path.GetFullPath(path),
                    ArtworkPath = artworkPath,
                    FileHash = hash,
                    FileSize = new FileInfo(destination).Length,
                    DurationSeconds = metadata.DurationSeconds,
                    ImportedAtUtc = DateTime.UtcNow
                };
                await _database.InsertTrackAsync(track);
                result.Imported++;
            }
            catch (Exception ex) { result.Errors.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        return result;
    }

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(path)) yield return path;
            else if (Directory.Exists(path))
                foreach (var file in Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories)) yield return file;
        }
    }

    private static async Task<string> ComputeHashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task<TagMetadata> ReadMetadataAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var props = await file.Properties.GetMusicPropertiesAsync();
        return new TagMetadata(
            props.Title,
            props.Artist,
            props.Album,
            props.AlbumArtist,
            props.TrackNumber,
            0,
            props.Genre.Count > 0 ? string.Join(", ", props.Genre) : "",
            props.Year,
            props.Duration.TotalSeconds > 0 ? (long)props.Duration.TotalSeconds : 0);
    }

    public async Task RepairMissingArtworkAsync(IReadOnlyCollection<Track> tracks)
    {
        foreach (var track in tracks)
        {
            if (!File.Exists(track.FilePath)) continue;
            // New-style .cover files are already extracted from the embedded tag. Revisit
            // legacy .jpg thumbnails once so previously imported tracks get real cover art.
            if (!string.IsNullOrWhiteSpace(track.ArtworkPath) && File.Exists(track.ArtworkPath) &&
                Path.GetFileName(track.ArtworkPath).Contains(".cover.square.", StringComparison.OrdinalIgnoreCase)) continue;

            try
            {
                var artwork = await TryExtractEmbeddedArtworkAsync(track.FilePath);
                if (artwork is null) continue;
                track.ArtworkPath = artwork;
                await _database.UpdateTrackArtworkPathAsync(track.Id, artwork);
            }
            catch
            {
                // A malformed or unsupported tag should not prevent the library loading.
            }
        }
    }

    private static async Task<string?> TryExtractEmbeddedArtworkAsync(string audioPath)
    {
        try
        {
            using var taggedFile = TagLib.File.Create(audioPath);
            var picture = taggedFile.Tag.Pictures?.FirstOrDefault(p => p.Data is not null && p.Data.Count > 0);
            if (picture is null) return null;

            var extension = picture.MimeType?.ToLowerInvariant() switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                "image/gif" => ".gif",
                "image/bmp" => ".bmp",
                "image/jpeg" or "image/jpg" => ".jpg",
                _ => Path.GetExtension(picture.Filename ?? string.Empty) is { Length: > 0 } ext ? ext : ".jpg"
            };
            var artworkPath = Path.Combine(Path.GetDirectoryName(audioPath)!, $"{Path.GetFileNameWithoutExtension(audioPath)}.cover{extension}");
            var bytes = picture.Data.Data;
            if (bytes.Length == 0) return null;
            File.WriteAllBytes(artworkPath, bytes);

            // Create a square display copy. Rectangular embedded artwork often includes
            // a decorative gradient/frame around a square cover; center-cropping it gives
            // every player surface consistent cover-art framing. Already-square images are
            // preserved without cropping.
            var sourceFile = await StorageFile.GetFileFromPathAsync(artworkPath);
            using var sourceStream = await sourceFile.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(sourceStream);
            uint side = Math.Min(decoder.PixelWidth, decoder.PixelHeight);
            if (side == 0) return artworkPath;

            var crop = new BitmapTransform
            {
                Bounds = new BitmapBounds
                {
                    X = (decoder.PixelWidth - side) / 2,
                    Y = (decoder.PixelHeight - side) / 2,
                    Width = side,
                    Height = side
                }
            };
            var pixels = (await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Ignore,
                crop,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage)).DetachPixelData();

            var squarePath = Path.Combine(Path.GetDirectoryName(audioPath)!, $"{Path.GetFileNameWithoutExtension(audioPath)}.cover.square.jpg");
            if (File.Exists(squarePath)) File.Delete(squarePath);
            var outputFolder = await StorageFolder.GetFolderFromPathAsync(Directory.GetParent(squarePath)!.FullName);
            var outputFile = await outputFolder.CreateFileAsync(Path.GetFileName(squarePath), CreationCollisionOption.ReplaceExisting);
            using var outputStream = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, side, side,
                decoder.DpiX, decoder.DpiY, pixels);
            await encoder.FlushAsync();
            return squarePath;
        }
        catch
        {
            // If the image format cannot be decoded by Windows Imaging, retain the original
            // embedded image so that supported formats still display instead of disappearing.
            try
            {
                using var taggedFile = TagLib.File.Create(audioPath);
                var picture = taggedFile.Tag.Pictures?.FirstOrDefault(p => p.Data is not null && p.Data.Count > 0);
                if (picture is null) return null;
                var extension = picture.MimeType?.ToLowerInvariant() switch
                {
                    "image/png" => ".png", "image/webp" => ".webp", "image/gif" => ".gif",
                    "image/bmp" => ".bmp", "image/jpeg" or "image/jpg" => ".jpg",
                    _ => Path.GetExtension(picture.Filename ?? string.Empty) is { Length: > 0 } ext ? ext : ".jpg"
                };
                var artworkPath = Path.Combine(Path.GetDirectoryName(audioPath)!, $"{Path.GetFileNameWithoutExtension(audioPath)}.cover{extension}");
                if (!File.Exists(artworkPath)) File.WriteAllBytes(artworkPath, picture.Data.Data);
                return artworkPath;
            }
            catch { return null; }
        }
    }

    private static string GetUniqueDestination(string folder, string filename)
    {
        var safe = CleanPathPart(filename);
        var path = Path.Combine(folder, safe);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(safe);
        var ext = Path.GetExtension(safe);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string Clean(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    private static string CleanPathPart(string value)
    {
        var invalid = new string(Path.GetInvalidFileNameChars());
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars).Trim().TrimEnd('.');
    }

    private sealed record TagMetadata(
        string? Title,
        string? Artist,
        string? Album,
        string? AlbumArtist,
        uint TrackNumber,
        uint DiscNumber,
        string Genre,
        uint Year,
        long DurationSeconds);
}
