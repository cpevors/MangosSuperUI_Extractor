using System.Text;

namespace MangosSuperUI_Extractor;

/// <summary>
/// Lightweight WDBC parser for reading WoW 1.12.1 client DBC files directly from MPQ archives.
/// Only extracts the fields we need for asset mapping (icons, model paths).
/// </summary>
public class DbcParser
{
    private MpqManager _mpq;

    public DbcParser(MpqManager mpq)
    {
        _mpq = mpq;
    }

    /// <summary>
    /// Parse ItemDisplayInfo.dbc → dictionary of displayId → (iconName, modelFilename)
    /// Fields: ID(0), ModelName0(1), ModelName1(2), ModelTexture0(3), ModelTexture1(4),
    ///         Icon0(5), Icon1(6), ...  (icon at field index 5)
    /// Record: 15 fields × 4 bytes = 60 bytes per record (may vary by build)
    /// </summary>
    public Dictionary<int, ItemDisplayInfoEntry> ParseItemDisplayInfo()
    {
        var result = new Dictionary<int, ItemDisplayInfoEntry>();
        var data = ExtractDbc("DBFilesClient\\ItemDisplayInfo.dbc");
        if (data == null) return result;

        var (records, stringBlock, recordCount, fieldCount, recordSize) = ParseHeader(data);
        if (records == null) return result;

        for (int i = 0; i < recordCount; i++)
        {
            int off = i * recordSize;
            int id = ReadInt(records, off);

            // Field 1: model name 0 (string offset)
            string modelName0 = ReadString(records, off + 4, stringBlock);
            // Field 2: model name 1 (string offset)
            string modelName1 = ReadString(records, off + 8, stringBlock);
            // Field 3: model texture 0
            string modelTex0 = ReadString(records, off + 12, stringBlock);
            // Field 4: model texture 1
            string modelTex1 = ReadString(records, off + 16, stringBlock);
            // Field 5: icon 0 (the inventory icon name, e.g. "INV_Sword_39")
            string icon0 = ReadString(records, off + 20, stringBlock);

            if (id > 0)
            {
                result[id] = new ItemDisplayInfoEntry
                {
                    DisplayId = id,
                    ModelName = modelName0,
                    ModelTexture = modelTex0,
                    IconName = icon0
                };
            }
        }

        return result;
    }

    /// <summary>
    /// Parse SpellIcon.dbc → dictionary of iconId → iconFilePath
    /// Fields: ID(0), IconPath(1)
    /// The icon path is like "Interface\\Icons\\Spell_Fire_Fireball" (no extension)
    /// </summary>
    public Dictionary<int, string> ParseSpellIcon()
    {
        var result = new Dictionary<int, string>();
        var data = ExtractDbc("DBFilesClient\\SpellIcon.dbc");
        if (data == null) return result;

        var (records, stringBlock, recordCount, fieldCount, recordSize) = ParseHeader(data);
        if (records == null) return result;

        for (int i = 0; i < recordCount; i++)
        {
            int off = i * recordSize;
            int id = ReadInt(records, off);
            string iconPath = ReadString(records, off + 4, stringBlock);

            if (id > 0 && !string.IsNullOrEmpty(iconPath))
            {
                // Extract just the filename (strip "Interface\\Icons\\" prefix)
                var fileName = Path.GetFileNameWithoutExtension(iconPath.Replace('/', '\\'));
                if (iconPath.Contains("\\"))
                    fileName = iconPath.Substring(iconPath.LastIndexOf('\\') + 1);

                result[id] = fileName;
            }
        }

        return result;
    }

    /// <summary>
    /// Parse GameObjectDisplayInfo.dbc → dictionary of displayId → modelPath
    /// Fields: ID(0), ModelName(1), Sound0-10(2-12), GeoBoxMinX-MaxZ(13-18)
    /// Record: 12 fields × 4 bytes = 48 bytes per record
    /// The model path is like "World\\Generic\\ActiveDoodads\\Chest02\\Chest02.mdx"
    /// </summary>
    public Dictionary<int, string> ParseGameObjectDisplayInfo()
    {
        var result = new Dictionary<int, string>();
        var data = ExtractDbc("DBFilesClient\\GameObjectDisplayInfo.dbc");
        if (data == null) return result;

        var (records, stringBlock, recordCount, fieldCount, recordSize) = ParseHeader(data);
        if (records == null) return result;

        for (int i = 0; i < recordCount; i++)
        {
            int off = i * recordSize;
            int id = ReadInt(records, off);
            string modelPath = ReadString(records, off + 4, stringBlock);

            if (id > 0 && !string.IsNullOrEmpty(modelPath))
            {
                result[id] = modelPath;
            }
        }

        return result;
    }

    /// <summary>
    /// Parse Spell.dbc → dictionary of spellId → spellIconId
    /// Spell.dbc is huge (26k+ records). We only need field 0 (ID) and field index for SpellIconID.
    /// In 1.12.1 (build 5875), SpellIconID is at field offset 352 (byte offset from record start).
    /// Actually it varies — let's use the known offset: field index 88 → byte offset 352.
    /// </summary>
    public Dictionary<int, int> ParseSpellToIconMap()
    {
        var result = new Dictionary<int, int>();
        var data = ExtractDbc("DBFilesClient\\Spell.dbc");
        if (data == null) return result;

        var (records, stringBlock, recordCount, fieldCount, recordSize) = ParseHeader(data);
        if (records == null) return result;

        // SpellIconID field offset in Spell.dbc for 1.12.1 (build 5875)
        // This is at field index 88 → byte offset 352
        // But let's verify: recordSize / 4 = field count, SpellIconID position is known
        int spellIconOffset = 88 * 4; // 352 bytes from record start

        if (recordSize < spellIconOffset + 4)
        {
            // Fallback: try common alternative offset
            spellIconOffset = 79 * 4; // 316
        }

        for (int i = 0; i < recordCount; i++)
        {
            int off = i * recordSize;
            int spellId = ReadInt(records, off);

            if (off + spellIconOffset + 4 <= records.Length)
            {
                int iconId = ReadInt(records, off + spellIconOffset);
                if (spellId > 0 && iconId > 0)
                {
                    result[spellId] = iconId;
                }
            }
        }

        return result;
    }

    // ── Header parsing ──

    private (byte[]? records, byte[]? stringBlock, int recordCount, int fieldCount, int recordSize) ParseHeader(byte[] data)
    {
        if (data.Length < 20) return (null, null, 0, 0, 0);

        var magic = Encoding.ASCII.GetString(data, 0, 4);
        if (magic != "WDBC")
            return (null, null, 0, 0, 0);

        int recordCount = BitConverter.ToInt32(data, 4);
        int fieldCount = BitConverter.ToInt32(data, 8);
        int recordSize = BitConverter.ToInt32(data, 12);
        int stringBlockSize = BitConverter.ToInt32(data, 16);

        int headerSize = 20;
        int recordsSize = recordCount * recordSize;

        if (data.Length < headerSize + recordsSize + stringBlockSize)
            return (null, null, 0, 0, 0);

        var records = new byte[recordsSize];
        Array.Copy(data, headerSize, records, 0, recordsSize);

        var stringBlock = new byte[stringBlockSize];
        Array.Copy(data, headerSize + recordsSize, stringBlock, 0, stringBlockSize);

        return (records, stringBlock, recordCount, fieldCount, recordSize);
    }

    private int ReadInt(byte[] data, int offset)
    {
        if (offset + 4 > data.Length) return 0;
        return BitConverter.ToInt32(data, offset);
    }

    private string ReadString(byte[] records, int fieldOffset, byte[]? stringBlock)
    {
        if (stringBlock == null || fieldOffset + 4 > records.Length) return "";

        int strOffset = BitConverter.ToInt32(records, fieldOffset);
        if (strOffset <= 0 || strOffset >= stringBlock.Length) return "";

        int end = Array.IndexOf(stringBlock, (byte)0, strOffset);
        if (end < 0) end = stringBlock.Length;

        return Encoding.UTF8.GetString(stringBlock, strOffset, end - strOffset);
    }

    private byte[]? ExtractDbc(string path)
    {
        return _mpq.ExtractFile(path);
    }
}

// ── DBC entry types ──

public class ItemDisplayInfoEntry
{
    public int DisplayId { get; set; }
    public string ModelName { get; set; } = "";
    public string ModelTexture { get; set; } = "";
    public string IconName { get; set; } = "";
}
