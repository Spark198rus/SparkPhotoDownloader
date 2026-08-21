using Microsoft.Data.Sqlite;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// The persistent record of every file this program has downloaded.
/// </summary>
/// <remarks>
/// A file is identified the way Rapid Photo Downloader identifies it: by the triple
/// (original file name, modification time, size). That triple survives renaming at the
/// destination and re-inserting the same card, and it does not require reading the whole
/// file. Rows are kept forever unless the user forgets them explicitly, so a file stays
/// recognised even after its downloaded copy is deleted -- which is exactly the case this
/// program flags rather than silently re-downloading.
/// </remarks>
public sealed class DownloadHistoryService : IDisposable
{
    private readonly string _databasePath;
    private readonly SqliteConnection _connection;
    private readonly Lock _gate = new();

    /// <summary>
    /// In-memory index over the whole history, keyed by name+size. Scanning a card asks
    /// thousands of questions in a row, so the answers come from RAM, not from SQLite.
    /// </summary>
    private readonly Dictionary<(string Name, long Size), List<DownloadRecord>> _index =
        new(NameSizeComparer.Instance);

    /// <summary>
    /// Clock offset discovered between the source file system and the stored times, in
    /// seconds. Cameras and cards regularly disagree with the host by whole time zones.
    /// </summary>
    private double _foundOffset;

    /// <summary>Cache of volume-root availability, so each scan stats a root only once.</summary>
    private readonly Dictionary<string, bool> _volumeOnline = new(StringComparer.OrdinalIgnoreCase);

    public DownloadHistoryService(string databasePath)
    {
        _databasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,

            // One long-lived connection gains nothing from pooling, and pooling would keep
            // the file handle open after Dispose, leaving the database locked and its
            // write-ahead log un-checkpointed.
            Pooling = false,
        }.ToString());

        _connection.Open();
        CreateSchema();
        LoadIndex();
    }

    public string DatabasePath => _databasePath;

    public int Count
    {
        get { lock (_gate) return _index.Values.Sum(v => v.Count); }
    }

    private void CreateSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;

            CREATE TABLE IF NOT EXISTS downloaded (
                file_name         TEXT    NOT NULL,
                mtime             REAL    NOT NULL,
                size              INTEGER NOT NULL,
                download_path     TEXT    NOT NULL,
                download_datetime TEXT    NOT NULL,
                source_name       TEXT,
                source_path       TEXT,
                job_code          TEXT,
                kind              INTEGER NOT NULL DEFAULT 0,
                backup_paths      TEXT,
                PRIMARY KEY (file_name, mtime, size)
            );

            CREATE INDEX IF NOT EXISTS idx_downloaded_path ON downloaded (download_path);
            CREATE INDEX IF NOT EXISTS idx_downloaded_when ON downloaded (download_datetime);
            """;
        cmd.ExecuteNonQuery();
    }

    private const string SelectColumns =
        "SELECT file_name, mtime, size, download_path, download_datetime, " +
        "source_name, source_path, job_code, kind, backup_paths";

    private void LoadIndex()
    {
        lock (_gate)
        {
            _index.Clear();

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = SelectColumns + " FROM downloaded";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                AddToIndex(Read(reader));
        }
    }

    private static DownloadRecord Read(SqliteDataReader r) => new()
    {
        FileName = r.GetString(0),
        MTime = r.GetDouble(1),
        Size = r.GetInt64(2),
        DownloadPath = r.GetString(3),
        DownloadedAtUtc = DateTime.Parse(r.GetString(4), null,
            System.Globalization.DateTimeStyles.RoundtripKind),
        SourceName = r.IsDBNull(5) ? null : r.GetString(5),
        SourcePath = r.IsDBNull(6) ? null : r.GetString(6),
        JobCode = r.IsDBNull(7) ? null : r.GetString(7),
        Kind = (FileKind)r.GetInt32(8),
        BackupPaths = r.IsDBNull(9) ? null : r.GetString(9),
    };

    private void AddToIndex(DownloadRecord record)
    {
        var key = (record.FileName, record.Size);
        if (!_index.TryGetValue(key, out var list))
            _index[key] = list = [];

        list.RemoveAll(r => Math.Abs(r.MTime - record.MTime) < 0.001);
        list.Add(record);
    }

    // -------------------------------------------------------------------------------------
    // Lookup
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Ask whether <paramref name="identity"/> has been downloaded before and, if so,
    /// whether the copy is still at the destination.
    /// </summary>
    public HistoryLookup Lookup(FileIdentity identity)
    {
        var record = FindRecord(identity);
        return record is null
            ? HistoryLookup.NotFound
            : new HistoryLookup(record, CheckDestination(record.DownloadPath));
    }

    private static readonly double[] TimeZoneResolutions = [3600, 1800, 900];

    /// <summary>Find the history row for a file, tolerating a whole-timezone clock difference.</summary>
    public DownloadRecord? FindRecord(FileIdentity identity)
    {
        lock (_gate)
        {
            if (!_index.TryGetValue((identity.FileName, identity.Size), out var candidates))
                return null;

            var mtime = identity.MTime;

            // 1. Exact match, allowing for file systems that store whole seconds only.
            var exact = candidates.FirstOrDefault(r => Math.Abs(r.MTime - mtime) <= 1.0);
            if (exact is not null)
                return exact;

            // 2. An offset was already worked out during this session: try it first.
            if (_foundOffset != 0)
            {
                var shifted = candidates.FirstOrDefault(
                    r => Math.Abs(r.MTime - (mtime - _foundOffset)) <= 1.0);
                if (shifted is not null)
                    return shifted;
            }

            // 3. Look for a candidate within a day and check that it lands on a plausible
            //    time zone offset. Zones exist at 60, 30 and 15 minute resolutions.
            foreach (var candidate in candidates)
            {
                var delta = mtime - candidate.MTime;
                if (Math.Abs(delta) > 86_400)
                    continue;

                foreach (var resolution in TimeZoneResolutions)
                {
                    var units = delta / resolution;
                    if (Math.Abs(units - Math.Round(units)) * resolution <= 1.0)
                    {
                        _foundOffset = Math.Round(units) * resolution;
                        return candidate;
                    }
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Decide whether a previously downloaded copy is still present. An unmounted volume
    /// reports <see cref="DestinationStatus.Offline"/> rather than Missing, so unplugging an
    /// archive drive does not make a whole library look deleted.
    /// </summary>
    public DestinationStatus CheckDestination(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return DestinationStatus.Missing;

        try
        {
            if (File.Exists(path))
                return DestinationStatus.Present;

            return IsVolumeOnline(path) ? DestinationStatus.Missing : DestinationStatus.Offline;
        }
        catch (IOException)
        {
            return DestinationStatus.Offline;
        }
        catch (UnauthorizedAccessException)
        {
            return DestinationStatus.Offline;
        }
    }

    private bool IsVolumeOnline(string path)
    {
        string root;
        try
        {
            root = Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
        }
        catch (ArgumentException)
        {
            return true;
        }

        if (string.IsNullOrEmpty(root))
            return true;

        lock (_gate)
        {
            if (_volumeOnline.TryGetValue(root, out var cached))
                return cached;

            bool online;
            try
            {
                online = root.StartsWith(@"\\", StringComparison.Ordinal)
                    ? Directory.Exists(root)
                    : new DriveInfo(root).IsReady;
            }
            catch (Exception)
            {
                online = false;
            }

            _volumeOnline[root] = online;
            return online;
        }
    }

    /// <summary>Drop cached volume availability so the next scan re-checks mounts.</summary>
    public void InvalidateVolumeCache()
    {
        lock (_gate) _volumeOnline.Clear();
    }

    // -------------------------------------------------------------------------------------
    // Mutation
    // -------------------------------------------------------------------------------------

    public void Add(DownloadRecord record) => AddRange([record]);

    public void AddRange(IReadOnlyCollection<DownloadRecord> records)
    {
        if (records.Count == 0)
            return;

        lock (_gate)
        {
            using var tx = _connection.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR REPLACE INTO downloaded
                    (file_name, mtime, size, download_path, download_datetime,
                     source_name, source_path, job_code, kind, backup_paths)
                VALUES ($name, $mtime, $size, $path, $when, $source, $srcpath, $job, $kind, $backup)
                """;

            var pName = cmd.Parameters.Add("$name", SqliteType.Text);
            var pMtime = cmd.Parameters.Add("$mtime", SqliteType.Real);
            var pSize = cmd.Parameters.Add("$size", SqliteType.Integer);
            var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
            var pWhen = cmd.Parameters.Add("$when", SqliteType.Text);
            var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
            var pSrcPath = cmd.Parameters.Add("$srcpath", SqliteType.Text);
            var pJob = cmd.Parameters.Add("$job", SqliteType.Text);
            var pKind = cmd.Parameters.Add("$kind", SqliteType.Integer);
            var pBackup = cmd.Parameters.Add("$backup", SqliteType.Text);

            foreach (var record in records)
            {
                pName.Value = record.FileName;
                pMtime.Value = record.MTime;
                pSize.Value = record.Size;
                pPath.Value = record.DownloadPath;
                pWhen.Value = record.DownloadedAtUtc.ToString("O");
                pSource.Value = (object?)record.SourceName ?? DBNull.Value;
                pSrcPath.Value = (object?)record.SourcePath ?? DBNull.Value;
                pJob.Value = (object?)record.JobCode ?? DBNull.Value;
                pKind.Value = (int)record.Kind;
                pBackup.Value = (object?)record.BackupPaths ?? DBNull.Value;
                cmd.ExecuteNonQuery();

                AddToIndex(record);
            }

            tx.Commit();
        }
    }

    /// <summary>
    /// Forget a file, so the next scan treats it as new. This is the deliberate escape hatch
    /// for "I really do want this one again".
    /// </summary>
    public bool Forget(FileIdentity identity)
    {
        var record = FindRecord(identity);
        if (record is null)
            return false;

        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "DELETE FROM downloaded WHERE file_name = $n AND mtime = $m AND size = $s";
            cmd.Parameters.AddWithValue("$n", record.FileName);
            cmd.Parameters.AddWithValue("$m", record.MTime);
            cmd.Parameters.AddWithValue("$s", record.Size);
            cmd.ExecuteNonQuery();

            var key = (record.FileName, record.Size);
            if (_index.TryGetValue(key, out var list))
            {
                list.RemoveAll(r => Math.Abs(r.MTime - record.MTime) < 0.001);
                if (list.Count == 0)
                    _index.Remove(key);
            }
        }

        return true;
    }

    public int ForgetMany(IEnumerable<FileIdentity> identities) => identities.Count(Forget);

    /// <summary>Remove every history row whose downloaded copy no longer exists.</summary>
    public int ForgetMissing()
    {
        InvalidateVolumeCache();
        var missing = All()
            .Where(r => CheckDestination(r.DownloadPath) == DestinationStatus.Missing)
            .Select(r => r.Identity)
            .ToList();
        return ForgetMany(missing);
    }

    public void Clear()
    {
        lock (_gate)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM downloaded";
            cmd.ExecuteNonQuery();
            _index.Clear();
        }
    }

    // -------------------------------------------------------------------------------------
    // Reporting
    // -------------------------------------------------------------------------------------

    public IReadOnlyList<DownloadRecord> All()
    {
        lock (_gate)
            return _index.Values.SelectMany(v => v).ToList();
    }

    /// <summary>History rows, newest first, for the history window.</summary>
    public IReadOnlyList<DownloadRecord> Recent(int limit = 5000)
    {
        lock (_gate)
            return _index.Values
                .SelectMany(v => v)
                .OrderByDescending(r => r.DownloadedAtUtc)
                .Take(limit)
                .ToList();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                // Fold the write-ahead log back into the database file, so what is on disk is
                // the whole history even if the machine is powered off next.
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
                cmd.ExecuteNonQuery();
            }
            catch (Exception)
            {
                // Closing the connection checkpoints anyway; this is belt and braces.
            }

            _connection.Close();
            _connection.Dispose();
        }
    }

    private sealed class NameSizeComparer : IEqualityComparer<(string Name, long Size)>
    {
        public static readonly NameSizeComparer Instance = new();

        public bool Equals((string Name, long Size) x, (string Name, long Size) y) =>
            x.Size == y.Size && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Name, long Size) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Name), obj.Size);
    }
}
