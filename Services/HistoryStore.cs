using System.Text.Json;

namespace ReCall.Services;

/// <summary>Persists clipboard history to disk under
/// %LocalAppData%\ReCall, so it survives app restarts (closes the
/// README's "No persistence" gap). Text lives inline in a small JSON
/// manifest (history.json); each image item's raw bytes are written once to
/// their own file under an Images subfolder (named by the item's Id) and
/// referenced from the manifest by filename -- so a save doesn't rewrite
/// multi-megabyte image bytes to disk every time the list changes, only the
/// (cheap) manifest does.
///
/// All I/O here is best-effort: a missing/corrupt manifest yields an empty
/// history instead of blocking startup, and a failed save is swallowed
/// rather than crashing the app -- persistence is a convenience, not
/// something that should ever take the panel down.</summary>
public sealed class HistoryStore
{
    private readonly string _rootDir;
    private readonly string _imagesDir;
    private readonly string _manifestPath;
    private readonly string _foldersManifestPath;

    public HistoryStore()
    {
        _rootDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReCall");
        _imagesDir = Path.Combine(_rootDir, "Images");
        _manifestPath = Path.Combine(_rootDir, "history.json");
        _foldersManifestPath = Path.Combine(_rootDir, "folders.json");

        try { Directory.CreateDirectory(_imagesDir); } catch { /* best effort */ }
    }

    /// <summary>Manifest row. Type is stored as text ("Text"/"Image",
    /// matching ClipboardItemType.ToString()) rather than the enum itself so
    /// the on-disk format doesn't silently break if the enum is ever
    /// reordered.</summary>
    public sealed class Record
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "Text";
        public string? Text { get; set; }
        public string? ImageFile { get; set; }
        public DateTime Timestamp { get; set; }
        public bool IsPinned { get; set; }
    }

    /// <summary>Folder manifest row, saved to its own small file
    /// (folders.json) separate from history.json -- folders change far less
    /// often than clipboard entries, and keeping them apart means renaming a
    /// folder never has to rewrite the (potentially much larger) item
    /// manifest, and vice versa. Items is the folder's own independent
    /// content (each entry either moved or copied in via the right-click
    /// menu -- see ClipboardPanelWindow.MoveItemToFolder/AddCopyToFolder),
    /// using the same Record shape as history.json's rows since a folder
    /// item is a full ClipboardItem in its own right, not just a reference.</summary>
    public sealed class FolderRecord
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";

        /// <summary>Hex tint for the folder's glyph (see
        /// Models/ClipboardFolder.cs Color/Palette). Nullable/omittable so
        /// manifests written before this field existed still deserialize
        /// fine -- the load site in ClipboardPanelWindow falls back to
        /// ClipboardFolder.DefaultColor when this is null or empty.</summary>
        public string? Color { get; set; }

        public List<Record> Items { get; set; } = new();
    }

    /// <summary>Reads the folders manifest, in stored order. Same
    /// best-effort contract as Load() below -- a missing/corrupt file just
    /// yields no folders instead of blocking startup.</summary>
    public List<FolderRecord> LoadFolders()
    {
        try
        {
            if (!File.Exists(_foldersManifestPath)) return new();
            var json = File.ReadAllText(_foldersManifestPath);
            return JsonSerializer.Deserialize<List<FolderRecord>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>Reads the manifest in stored (newest-first) order.</summary>
    public List<Record> Load()
    {
        try
        {
            if (!File.Exists(_manifestPath)) return new();
            var json = File.ReadAllText(_manifestPath);
            return JsonSerializer.Deserialize<List<Record>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    public byte[]? ReadImageBytes(string imageFile)
    {
        try
        {
            var path = Path.Combine(_imagesDir, imageFile);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes one image's bytes to disk under its item Id. Called
    /// once, the first time an image item is added (or loaded from a
    /// manifest that already points at a file, in which case this is
    /// skipped) -- returns the filename to store in that item's manifest
    /// record.</summary>
    public string SaveImageBytes(string itemId, byte[] bytes)
    {
        var fileName = itemId + ".bin";
        try
        {
            File.WriteAllBytes(Path.Combine(_imagesDir, fileName), bytes);
        }
        catch
        {
            // If this fails the item just won't survive a restart -- not
            // worth taking the app down over.
        }
        return fileName;
    }

    /// <summary>Overwrites both manifests (history.json and folders.json --
    /// caller is responsible for having already trimmed/ordered the history
    /// records to match the in-memory list) and deletes any image file
    /// under Images\ that isn't referenced by either one, in a single pass.
    /// History and folder items are saved together (rather than as two
    /// independent Save/SaveFolders calls, as this used to be split) so the
    /// image-cleanup sweep always sees the true, current union of both --
    /// saving them separately could otherwise have one save's cleanup
    /// delete an image the other file still points at, e.g. right after
    /// "Add to folder" clones an item into a folder that hasn't been
    /// persisted yet.</summary>
    public void SaveAll(IReadOnlyList<Record> historyRecords, IReadOnlyList<FolderRecord> folderRecords)
    {
        try
        {
            Directory.CreateDirectory(_rootDir);
            File.WriteAllText(_manifestPath, JsonSerializer.Serialize(historyRecords));
            File.WriteAllText(_foldersManifestPath, JsonSerializer.Serialize(folderRecords));

            var keep = historyRecords
                .Where(r => r.ImageFile is not null)
                .Select(r => r.ImageFile!)
                .Concat(folderRecords
                    .SelectMany(f => f.Items)
                    .Where(r => r.ImageFile is not null)
                    .Select(r => r.ImageFile!))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(_imagesDir))
            {
                if (!keep.Contains(Path.GetFileName(file)))
                    File.Delete(file);
            }
        }
        catch
        {
            // Best-effort persistence -- a save failure shouldn't crash the app.
        }
    }
}
