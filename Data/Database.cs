using Microsoft.Data.Sqlite;
using MyMusic.Models;

namespace MyMusic.Data;

public sealed class Database
{
    private readonly string _connectionString;

    public Database(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = $"Data Source={databasePath}";
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS Tracks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Title TEXT NOT NULL,
                Artist TEXT NOT NULL,
                Album TEXT NOT NULL,
                AlbumArtist TEXT NOT NULL,
                TrackNumber INTEGER NOT NULL DEFAULT 0,
                DiscNumber INTEGER NOT NULL DEFAULT 0,
                Genre TEXT NOT NULL DEFAULT '',
                Year INTEGER NULL,
                FilePath TEXT NOT NULL UNIQUE,
                OriginalPath TEXT NOT NULL,
                ArtworkPath TEXT NULL,
                FileHash TEXT NOT NULL,
                FileSize INTEGER NOT NULL,
                DurationSeconds INTEGER NOT NULL DEFAULT 0,
                ImportedAtUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Tracks_Title ON Tracks(Title);
            CREATE INDEX IF NOT EXISTS IX_Tracks_Artist ON Tracks(Artist);
            CREATE INDEX IF NOT EXISTS IX_Tracks_Album ON Tracks(Album);
            CREATE INDEX IF NOT EXISTS IX_Tracks_Hash ON Tracks(FileHash);
            CREATE TABLE IF NOT EXISTS Playlists (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                CreatedAtUtc TEXT NOT NULL,
                CoverPath TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS PlaylistTracks (
                PlaylistId INTEGER NOT NULL,
                TrackId INTEGER NOT NULL,
                Position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (PlaylistId, TrackId),
                FOREIGN KEY (PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE
            );
            """;
        await command.ExecuteNonQueryAsync();
        try
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE Tracks ADD COLUMN DurationSeconds INTEGER NOT NULL DEFAULT 0";
            await migration.ExecuteNonQueryAsync();
        }
        catch (SqliteException) { }
        try
        {
            var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE Playlists ADD COLUMN CoverPath TEXT NULL";
            await migration.ExecuteNonQueryAsync();
        }
        catch (SqliteException) { }
    }

    public async Task<bool> ContainsHashAsync(string hash)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM Tracks WHERE FileHash = $hash LIMIT 1";
        command.Parameters.AddWithValue("$hash", hash);
        return await command.ExecuteScalarAsync() is not null;
    }

    public async Task<long> InsertTrackAsync(Track track)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Tracks
            (Title, Artist, Album, AlbumArtist, TrackNumber, DiscNumber, Genre, Year,
             FilePath, OriginalPath, ArtworkPath, FileHash, FileSize, DurationSeconds, ImportedAtUtc)
            VALUES ($title, $artist, $album, $albumArtist, $trackNumber, $discNumber, $genre, $year,
                    $filePath, $originalPath, $artworkPath, $fileHash, $fileSize, $durationSeconds, $importedAtUtc);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$artist", track.Artist);
        command.Parameters.AddWithValue("$album", track.Album);
        command.Parameters.AddWithValue("$albumArtist", track.AlbumArtist);
        command.Parameters.AddWithValue("$trackNumber", track.TrackNumber);
        command.Parameters.AddWithValue("$discNumber", track.DiscNumber);
        command.Parameters.AddWithValue("$genre", track.Genre);
        command.Parameters.AddWithValue("$year", (object?)track.Year ?? DBNull.Value);
        command.Parameters.AddWithValue("$filePath", track.FilePath);
        command.Parameters.AddWithValue("$originalPath", track.OriginalPath);
        command.Parameters.AddWithValue("$artworkPath", (object?)track.ArtworkPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$fileHash", track.FileHash);
        command.Parameters.AddWithValue("$fileSize", track.FileSize);
        command.Parameters.AddWithValue("$durationSeconds", track.DurationSeconds);
        command.Parameters.AddWithValue("$importedAtUtc", track.ImportedAtUtc.ToString("O"));
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task UpdateTrackArtworkPathAsync(long trackId, string artworkPath)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tracks SET ArtworkPath = $path WHERE Id = $id";
        command.Parameters.AddWithValue("$path", artworkPath);
        command.Parameters.AddWithValue("$id", trackId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateTrackInfoAsync(Track track)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE Tracks
            SET Title = $title, Artist = $artist, Album = $album, AlbumArtist = $albumArtist, ArtworkPath = $artworkPath
            WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$title", track.Title);
        command.Parameters.AddWithValue("$artist", track.Artist);
        command.Parameters.AddWithValue("$album", track.Album);
        command.Parameters.AddWithValue("$albumArtist", track.AlbumArtist);
        command.Parameters.AddWithValue("$artworkPath", (object?)track.ArtworkPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", track.Id);
        await command.ExecuteNonQueryAsync();
    }

    public async Task UpdateTrackDurationAsync(long trackId, long durationSeconds)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Tracks SET DurationSeconds = $duration WHERE Id = $id";
        command.Parameters.AddWithValue("$duration", durationSeconds);
        command.Parameters.AddWithValue("$id", trackId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<int> RemoveMissingTracksAsync()
    {
        var missingIds = new List<long>();
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, FilePath FROM Tracks";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (!File.Exists(reader.GetString(1)))
                    missingIds.Add(reader.GetInt64(0));
            }
        }

        if (missingIds.Count == 0) return 0;

        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync();
            await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync();
            foreach (var id in missingIds)
            {
                var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM Tracks WHERE Id = $id";
                command.Parameters.AddWithValue("$id", id);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        return missingIds.Count;
    }

    public async Task<List<Track>> GetTracksAsync(string? search = null)
    {
        var tracks = new List<Track>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, Title, Artist, Album, AlbumArtist, TrackNumber, DiscNumber, Genre, Year,
                   FilePath, OriginalPath, ArtworkPath, FileHash, FileSize, DurationSeconds, ImportedAtUtc
            FROM Tracks
            WHERE $search = '' OR Title LIKE $pattern OR Artist LIKE $pattern OR Album LIKE $pattern
            ORDER BY Artist COLLATE NOCASE, Album COLLATE NOCASE, DiscNumber, TrackNumber, Title COLLATE NOCASE;
            """;
        var value = search?.Trim() ?? "";
        command.Parameters.AddWithValue("$search", value);
        command.Parameters.AddWithValue("$pattern", $"%{value}%");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(new Track
            {
                Id = reader.GetInt64(0), Title = reader.GetString(1), Artist = reader.GetString(2),
                Album = reader.GetString(3), AlbumArtist = reader.GetString(4), TrackNumber = reader.GetInt32(5),
                DiscNumber = reader.GetInt32(6), Genre = reader.GetString(7), Year = reader.IsDBNull(8) ? null : reader.GetInt32(8),
                FilePath = reader.GetString(9), OriginalPath = reader.GetString(10), ArtworkPath = reader.IsDBNull(11) ? null : reader.GetString(11),
                FileHash = reader.GetString(12), FileSize = reader.GetInt64(13), DurationSeconds = reader.GetInt64(14), ImportedAtUtc = DateTime.Parse(reader.GetString(15))
            });
        }
        return tracks;
    }

    public async Task<List<string>> GetPlaylistNamesAsync()
    {
        var result = new List<string>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Playlists ORDER BY Name COLLATE NOCASE";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<DateTime?> GetPlaylistCreatedAtUtcAsync(string playlistName)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT CreatedAtUtc FROM Playlists WHERE Name = $name COLLATE NOCASE LIMIT 1";
        command.Parameters.AddWithValue("$name", playlistName);
        var value = await command.ExecuteScalarAsync();
        if (value is null || value is DBNull) return null;
        return DateTime.TryParse(Convert.ToString(value), out var created) ? DateTime.SpecifyKind(created, DateTimeKind.Utc) : null;
    }

    public async Task CreatePlaylistAsync(string name)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO Playlists(Name, CreatedAtUtc) VALUES($name, $created)";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task AddTrackToPlaylistAsync(string playlistName, long trackId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO PlaylistTracks(PlaylistId, TrackId, Position)
            SELECT p.Id, $trackId, COALESCE((SELECT MAX(Position) + 1 FROM PlaylistTracks WHERE PlaylistId = p.Id), 0)
            FROM Playlists p WHERE p.Name = $name;
            """;
        command.Parameters.AddWithValue("$name", playlistName);
        command.Parameters.AddWithValue("$trackId", trackId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task RemoveTrackFromPlaylistAsync(string playlistName, long trackId)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PlaylistTracks WHERE PlaylistId = (SELECT Id FROM Playlists WHERE Name = $name) AND TrackId = $trackId";
        command.Parameters.AddWithValue("$name", playlistName);
        command.Parameters.AddWithValue("$trackId", trackId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<string?> GetPlaylistCoverPathAsync(string playlistName)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT CoverPath FROM Playlists WHERE Name = $name";
        command.Parameters.AddWithValue("$name", playlistName);
        return (await command.ExecuteScalarAsync()) as string;
    }

    public async Task SetPlaylistCoverPathAsync(string playlistName, string? path)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE Playlists SET CoverPath = $path WHERE Name = $name";
        command.Parameters.AddWithValue("$name", playlistName);
        command.Parameters.AddWithValue("$path", (object?)path ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task SavePlaylistOrderAsync(string playlistName, IReadOnlyList<long> trackIds)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        var position = 0;
        foreach (var trackId in trackIds)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE PlaylistTracks SET Position = $position WHERE PlaylistId = (SELECT Id FROM Playlists WHERE Name = $name) AND TrackId = $trackId";
            command.Parameters.AddWithValue("$name", playlistName);
            command.Parameters.AddWithValue("$trackId", trackId);
            command.Parameters.AddWithValue("$position", position++);
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<List<Track>> GetPlaylistTracksAsync(string playlistName)
    {
        var tracks = new List<Track>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.Id, t.Title, t.Artist, t.Album, t.AlbumArtist, t.TrackNumber, t.DiscNumber, t.Genre, t.Year,
                   t.FilePath, t.OriginalPath, t.ArtworkPath, t.FileHash, t.FileSize, t.DurationSeconds, t.ImportedAtUtc
            FROM PlaylistTracks pt
            JOIN Playlists p ON p.Id = pt.PlaylistId
            JOIN Tracks t ON t.Id = pt.TrackId
            WHERE p.Name = $name
            ORDER BY pt.Position;
            """;
        command.Parameters.AddWithValue("$name", playlistName);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tracks.Add(new Track
            {
                Id = reader.GetInt64(0), Title = reader.GetString(1), Artist = reader.GetString(2), Album = reader.GetString(3),
                AlbumArtist = reader.GetString(4), TrackNumber = reader.GetInt32(5), DiscNumber = reader.GetInt32(6), Genre = reader.GetString(7),
                Year = reader.IsDBNull(8) ? null : reader.GetInt32(8), FilePath = reader.GetString(9), OriginalPath = reader.GetString(10),
                ArtworkPath = reader.IsDBNull(11) ? null : reader.GetString(11), FileHash = reader.GetString(12), FileSize = reader.GetInt64(13), DurationSeconds = reader.GetInt64(14),
                ImportedAtUtc = DateTime.Parse(reader.GetString(15))
            });
        }
        return tracks;
    }
    public async Task RemapManagedPathsAsync(string oldRoot, string newRoot)
    {
        oldRoot = Path.GetFullPath(oldRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        newRoot = Path.GetFullPath(newRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();

        var tracks = connection.CreateCommand();
        tracks.Transaction = transaction;
        tracks.CommandText = """
            UPDATE Tracks SET FilePath = $new || substr(FilePath, $start)
            WHERE FilePath = $old OR FilePath LIKE $prefix;
            UPDATE Tracks SET ArtworkPath = $new || substr(ArtworkPath, $start)
            WHERE ArtworkPath IS NOT NULL AND (ArtworkPath = $old OR ArtworkPath LIKE $prefix);
            """;
        tracks.Parameters.AddWithValue("$old", oldRoot);
        tracks.Parameters.AddWithValue("$new", newRoot);
        tracks.Parameters.AddWithValue("$prefix", oldRoot + Path.DirectorySeparatorChar + "%");
        tracks.Parameters.AddWithValue("$start", oldRoot.Length + 1);
        await tracks.ExecuteNonQueryAsync();

        var playlists = connection.CreateCommand();
        playlists.Transaction = transaction;
        playlists.CommandText = """
            UPDATE Playlists SET CoverPath = $new || substr(CoverPath, $start)
            WHERE CoverPath IS NOT NULL AND (CoverPath = $old OR CoverPath LIKE $prefix);
            """;
        playlists.Parameters.AddWithValue("$old", oldRoot);
        playlists.Parameters.AddWithValue("$new", newRoot);
        playlists.Parameters.AddWithValue("$prefix", oldRoot + Path.DirectorySeparatorChar + "%");
        playlists.Parameters.AddWithValue("$start", oldRoot.Length + 1);
        await playlists.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

}
