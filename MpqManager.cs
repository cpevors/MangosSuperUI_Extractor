using War3Net.IO.Mpq;

namespace MangosSuperUI_Extractor;

/// <summary>
/// Wraps War3Net.IO.Mpq to open WoW 1.12.1 MPQ archives and enumerate/extract files.
/// WoW 1.12.1 uses these MPQs in the Data/ folder (load order matters — patches override base):
///   patch.MPQ, patch-2.MPQ (if exists), terrain.MPQ, texture.MPQ, wmo.MPQ, model.MPQ,
///   interface.MPQ, sound.MPQ, speech.MPQ, misc.MPQ, dbc.MPQ
/// </summary>
public class MpqManager : IDisposable
{
    private readonly List<(string Name, MpqArchive Archive, List<string> Files)> _archives = new();

    public event Action<string>? Log;

    /// <summary>
    /// Scan a WoW client Data/ folder for all MPQ files and open them.
    /// </summary>
    public int OpenClientFolder(string dataFolderPath)
    {
        _archives.Clear();

        if (!Directory.Exists(dataFolderPath))
            throw new DirectoryNotFoundException($"Data folder not found: {dataFolderPath}");

        // Find all MPQ files
        var mpqFiles = Directory.GetFiles(dataFolderPath, "*.MPQ", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetFiles(dataFolderPath, "*.mpq", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log?.Invoke($"Found {mpqFiles.Count} MPQ files in {dataFolderPath}");

        foreach (var mpqPath in mpqFiles)
        {
            try
            {
                var archive = MpqArchive.Open(mpqPath, true);
                var name = Path.GetFileName(mpqPath);

                // Try to get file list from (listfile)
                var files = new List<string>();
                try
                {
                    if (archive.TryOpenFile("(listfile)", out var listfileStream))
                    {
                        using var reader = new StreamReader(listfileStream!);
                        var content = reader.ReadToEnd();
                        files = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(f => f.Trim())
                            .Where(f => !string.IsNullOrEmpty(f))
                            .ToList();
                    }
                }
                catch
                {
                    // Some MPQs may not have a listfile
                }

                _archives.Add((name, archive, files));
                Log?.Invoke($"  Opened {name}: {files.Count:N0} files in listfile");
            }
            catch (Exception ex)
            {
                Log?.Invoke($"  SKIP {Path.GetFileName(mpqPath)}: {ex.Message}");
            }
        }

        return _archives.Count;
    }

    /// <summary>
    /// Get all files across all opened MPQs matching a path filter.
    /// Returns (mpqName, filePath) tuples.
    /// </summary>
    public List<(string MpqName, string FilePath)> FindFiles(Func<string, bool> filter)
    {
        var results = new List<(string, string)>();
        foreach (var (name, archive, files) in _archives)
        {
            foreach (var file in files)
            {
                if (filter(file))
                    results.Add((name, file));
            }
        }
        return results;
    }

    /// <summary>
    /// Find files matching a path prefix (case-insensitive).
    /// </summary>
    public List<(string MpqName, string FilePath)> FindFilesByPrefix(string prefix)
    {
        var lowerPrefix = prefix.ToLowerInvariant().Replace('/', '\\');
        return FindFiles(f => f.ToLowerInvariant().Replace('/', '\\').StartsWith(lowerPrefix));
    }

    /// <summary>
    /// Find files matching an extension (case-insensitive).
    /// </summary>
    public List<(string MpqName, string FilePath)> FindFilesByExtension(string extension)
    {
        var ext = extension.StartsWith('.') ? extension : "." + extension;
        return FindFiles(f => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Extract a file from the MPQ archives. Searches in reverse order (patches first).
    /// Returns the raw bytes, or null if not found.
    /// </summary>
    public byte[]? ExtractFile(string filePath)
    {
        // Search archives in reverse order so patches override base files
        for (int i = _archives.Count - 1; i >= 0; i--)
        {
            var (name, archive, files) = _archives[i];
            try
            {
                if (archive.TryOpenFile(filePath, out var stream))
                {
                    using var ms = new MemoryStream();
                    stream!.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Extract a file and return it as a Stream. Caller must dispose.
    /// </summary>
    public Stream? ExtractFileStream(string filePath)
    {
        var bytes = ExtractFile(filePath);
        if (bytes == null) return null;
        return new MemoryStream(bytes);
    }

    /// <summary>
    /// Get summary of all opened archives.
    /// </summary>
    public List<(string Name, int FileCount)> GetArchiveSummary()
    {
        return _archives.Select(a => (a.Name, a.Files.Count)).ToList();
    }

    /// <summary>
    /// Get total unique file count across all archives.
    /// </summary>
    public int TotalFileCount => _archives.Sum(a => a.Files.Count);

    public void Dispose()
    {
        foreach (var (_, archive, _) in _archives)
        {
            try { archive.Dispose(); } catch { }
        }
        _archives.Clear();
    }
}
