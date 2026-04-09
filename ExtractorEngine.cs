using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using War3Net.Drawing.Blp;

namespace MangosSuperUI_Extractor;

public class AssetCategory
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public int FileCount => Files.Count;
    public List<AssetFile> Files { get; set; } = new();
    public bool RequiresTrsRename { get; set; }

    /// <summary>If true, this category runs custom logic instead of normal file extraction.</summary>
    public bool IsCustomExtraction { get; set; }

    /// <summary>Custom extraction delegate (for manifest generation, model extraction, etc.)</summary>
    public Func<string, CancellationToken, Task>? CustomExtractAction { get; set; }

    public long TotalSizeEstimate => Files.Count * 30_000L;
}

public class AssetFile
{
    public string MpqName { get; set; } = "";
    public string MpqPath { get; set; } = "";
    public string OutputName { get; set; } = "";
    public bool ConvertBlpToPng { get; set; }
}

public class ExtractionProgress
{
    public int Total { get; set; }
    public int Current { get; set; }
    public string CurrentFile { get; set; } = "";
    public string Status { get; set; } = "";
    public int Errors { get; set; }
}

public class TrsEntry
{
    public string RealPath { get; set; } = "";
    public string HashName { get; set; } = "";
}

public class ExtractorEngine
{
    private MpqManager _mpq;
    private DbcParser _dbc;
    private Dictionary<string, TrsEntry> _trsMap = new();

    // Cached DBC data
    private Dictionary<int, ItemDisplayInfoEntry>? _itemDisplayInfo;
    private Dictionary<int, string>? _spellIcons;
    private Dictionary<int, int>? _spellToIcon;
    private Dictionary<int, string>? _goDisplayInfo;

    // Cached MPQ index: lowercase bare filename (no ext) → full MPQ path for item M2s
    private Dictionary<string, string>? _itemModelIndex;

    public event Action<ExtractionProgress>? ProgressChanged;
    public event Action<string>? LogMessage;

    public MpqManager Mpq => _mpq;

    public ExtractorEngine(MpqManager mpq)
    {
        _mpq = mpq;
        _dbc = new DbcParser(mpq);
    }

    // ═══════════════════════════════════════════════════════════════
    // TRS Loading
    // ═══════════════════════════════════════════════════════════════

    public int LoadTrs()
    {
        _trsMap.Clear();

        var trsData = _mpq.ExtractFile(@"Textures\Minimap\md5translate.trs")
                  ?? _mpq.ExtractFile(@"textures\minimap\md5translate.trs")
                  ?? _mpq.ExtractFile(@"Textures\minimap\md5translate.trs");

        if (trsData == null)
        {
            Log("WARNING: md5translate.trs not found in any MPQ");
            return 0;
        }

        var content = Encoding.UTF8.GetString(trsData);
        foreach (var line in content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var parts = trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            var realPath = parts[0].Replace(".blp", "", StringComparison.OrdinalIgnoreCase);
            var hashFile = parts[1].Replace(".blp", "", StringComparison.OrdinalIgnoreCase);

            _trsMap[hashFile.ToLowerInvariant()] = new TrsEntry
            {
                RealPath = realPath,
                HashName = hashFile
            };
        }

        Log($"Loaded {_trsMap.Count} TRS hash→name mappings");
        return _trsMap.Count;
    }

    // ═══════════════════════════════════════════════════════════════
    // DBC Loading (lazy, cached)
    // ═══════════════════════════════════════════════════════════════

    private void EnsureDbcLoaded()
    {
        if (_itemDisplayInfo == null)
        {
            Log("Parsing ItemDisplayInfo.dbc...");
            _itemDisplayInfo = _dbc.ParseItemDisplayInfo();
            Log($"  → {_itemDisplayInfo.Count} item display entries");
        }

        if (_spellIcons == null)
        {
            Log("Parsing SpellIcon.dbc...");
            _spellIcons = _dbc.ParseSpellIcon();
            Log($"  → {_spellIcons.Count} spell icon entries");
        }

        if (_spellToIcon == null)
        {
            Log("Parsing Spell.dbc (spellId → iconId mapping)...");
            _spellToIcon = _dbc.ParseSpellToIconMap();
            Log($"  → {_spellToIcon.Count} spell→icon mappings");
        }

        if (_goDisplayInfo == null)
        {
            Log("Parsing GameObjectDisplayInfo.dbc...");
            _goDisplayInfo = _dbc.ParseGameObjectDisplayInfo();
            Log($"  → {_goDisplayInfo.Count} game object display entries");
        }

        if (_itemModelIndex == null)
        {
            Log("Building item model path index from MPQ...");
            _itemModelIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Scan Item\ObjectComponents\ for M2/MDX files
            var itemModelFiles = _mpq.FindFilesByPrefix(@"Item\ObjectComponents\");
            foreach (var (mpqName, filePath) in itemModelFiles)
            {
                if (!filePath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase) &&
                    !filePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase))
                    continue;

                var bare = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
                // First one wins (patch override is handled by MpqManager.ExtractFile reverse order)
                if (!_itemModelIndex.ContainsKey(bare))
                    _itemModelIndex[bare] = filePath;
            }

            Log($"  → {_itemModelIndex.Count} item model files indexed under Item\\ObjectComponents\\");
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Category Scanning
    // ═══════════════════════════════════════════════════════════════

    public List<AssetCategory> ScanCategories()
    {
        EnsureDbcLoaded();

        var categories = new List<AssetCategory>();

        categories.Add(ScanMinimapTiles());
        categories.Add(ScanIcons());
        categories.Add(ScanWorldMaps());
        categories.Add(ScanDbcFiles());
        categories.Add(ScanCreatureTextures());
        categories.Add(ScanItemTextures());

        // ── NEW: DBC-driven categories ──
        categories.Add(ScanItemIconManifest());
        categories.Add(ScanSpellIconManifest());
        categories.Add(ScanGameObjectModels());
        categories.Add(ScanItemModels());

        categories.RemoveAll(c => c.FileCount == 0 && !c.IsCustomExtraction);
        return categories;
    }

    // ═══════════════════════════════════════════════════════════════
    // Original categories (unchanged)
    // ═══════════════════════════════════════════════════════════════

    private AssetCategory ScanMinimapTiles()
    {
        var cat = new AssetCategory
        {
            Name = "Minimap Tiles",
            Description = "256×256 top-down terrain tiles for Leaflet.js world map.\nAuto-renamed from md5 hashes via md5translate.trs.\nCovers Azeroth, Kalimdor, and all instance/dungeon maps.",
            OutputFolder = "minimap",
            RequiresTrsRename = true
        };

        var minimapFiles = _mpq.FindFilesByPrefix(@"Textures\Minimap\");

        foreach (var (mpqName, filePath) in minimapFiles)
        {
            if (!filePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;
            if (filePath.EndsWith("md5translate.trs", StringComparison.OrdinalIgnoreCase)) continue;

            var fileName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();

            string outputName;
            if (_trsMap.TryGetValue(fileName, out var trs))
            {
                outputName = trs.RealPath.Replace('\\', '/');
            }
            else if (Regex.IsMatch(fileName, @"^map\d+_\d+$"))
            {
                outputName = fileName;
            }
            else
            {
                continue;
            }

            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = outputName.ToLowerInvariant() + ".png",
                ConvertBlpToPng = true
            });
        }

        Log($"Minimap tiles: {cat.FileCount} files (via TRS rename)");
        return cat;
    }

    private AssetCategory ScanIcons()
    {
        var cat = new AssetCategory
        {
            Name = "Interface Icons",
            Description = "Item, spell, and ability icons from Interface\\Icons.\n64×64 BLP textures converted to PNG.",
            OutputFolder = "icons"
        };

        var files = _mpq.FindFilesByPrefix(@"Interface\Icons\");
        foreach (var (mpqName, filePath) in files)
        {
            if (!filePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;

            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant() + ".png",
                ConvertBlpToPng = true
            });
        }

        Log($"Interface icons: {cat.FileCount} files");
        return cat;
    }

    private AssetCategory ScanWorldMaps()
    {
        var cat = new AssetCategory
        {
            Name = "World Maps",
            Description = "Hand-painted zone maps (the M-key maps).\nOrganized by zone, includes overlay pieces.",
            OutputFolder = "worldmaps"
        };

        var files = _mpq.FindFilesByPrefix(@"Interface\WorldMap\");
        foreach (var (mpqName, filePath) in files)
        {
            if (!filePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase)) continue;

            var relative = filePath;
            if (relative.StartsWith(@"Interface\WorldMap\", StringComparison.OrdinalIgnoreCase))
                relative = relative.Substring(@"Interface\WorldMap\".Length);

            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = Path.ChangeExtension(relative.Replace('\\', '/'), ".png").ToLowerInvariant(),
                ConvertBlpToPng = true
            });
        }

        Log($"World maps: {cat.FileCount} files");
        return cat;
    }

    private AssetCategory ScanDbcFiles()
    {
        var cat = new AssetCategory
        {
            Name = "DBC Files",
            Description = "Client database files (Spell.dbc, Item.dbc, AreaTable.dbc, etc.).\nBinary format, used by DbcService in MangosSuperUI.",
            OutputFolder = "dbc"
        };

        var files = _mpq.FindFilesByPrefix(@"DBFilesClient\");
        foreach (var (mpqName, filePath) in files)
        {
            if (!filePath.EndsWith(".dbc", StringComparison.OrdinalIgnoreCase)) continue;

            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = Path.GetFileName(filePath).ToLowerInvariant(),
                ConvertBlpToPng = false
            });
        }

        Log($"DBC files: {cat.FileCount} files");
        return cat;
    }

    private AssetCategory ScanCreatureTextures()
    {
        var cat = new AssetCategory
        {
            Name = "Creature Textures",
            Description = "Creature/NPC skin textures.\nUseful for reference or custom creature work.",
            OutputFolder = "creature_textures"
        };

        var files = _mpq.FindFilesByPrefix(@"Character\")
            .Concat(_mpq.FindFilesByPrefix(@"Creature\"))
            .Where(f => f.FilePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var (mpqName, filePath) in files)
        {
            var relative = filePath.Replace('\\', '/');
            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = Path.ChangeExtension(relative, ".png").ToLowerInvariant(),
                ConvertBlpToPng = true
            });
        }

        Log($"Creature textures: {cat.FileCount} files");
        return cat;
    }

    private AssetCategory ScanItemTextures()
    {
        var cat = new AssetCategory
        {
            Name = "Item Textures",
            Description = "Item model textures and object textures.",
            OutputFolder = "item_textures"
        };

        var files = _mpq.FindFilesByPrefix(@"Item\")
            .Where(f => f.FilePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var (mpqName, filePath) in files)
        {
            var relative = filePath.Replace('\\', '/');
            cat.Files.Add(new AssetFile
            {
                MpqName = mpqName,
                MpqPath = filePath,
                OutputName = Path.ChangeExtension(relative, ".png").ToLowerInvariant(),
                ConvertBlpToPng = true
            });
        }

        Log($"Item textures: {cat.FileCount} files");
        return cat;
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW: Item Icon Manifest (DBC-driven)
    // ═══════════════════════════════════════════════════════════════

    private AssetCategory ScanItemIconManifest()
    {
        int count = _itemDisplayInfo?.Count(e => !string.IsNullOrEmpty(e.Value.IconName)) ?? 0;

        var cat = new AssetCategory
        {
            Name = "Item Icon Manifest (JSON)",
            Description = $"JSON lookup: displayId → icon filename.\nParsed from ItemDisplayInfo.dbc ({count} entries with icons).\n\nOutputs:\n  item_display_info.json — full entry (icon + model + texture)\n  item_icon_lookup.json  — flat displayId → iconName",
            OutputFolder = "manifests",
            IsCustomExtraction = true,
        };

        // Dummy entry so FileCount > 0 and it shows in the UI
        if (count > 0)
            cat.Files.Add(new AssetFile { OutputName = $"item_display_info.json ({count} entries)" });

        cat.CustomExtractAction = async (outputRoot, ct) =>
        {
            await ExtractItemIconManifestAsync(outputRoot, ct);
        };

        Log($"Item icon manifest: {count} display entries with icons");
        return cat;
    }

    private async Task ExtractItemIconManifestAsync(string outputRoot, CancellationToken ct)
    {
        if (_itemDisplayInfo == null) return;

        var outDir = Path.Combine(outputRoot, "manifests");
        Directory.CreateDirectory(outDir);

        // Full manifest: displayId → { icon, model, texture }
        var manifest = new Dictionary<string, object>();
        foreach (var (id, entry) in _itemDisplayInfo)
        {
            if (string.IsNullOrEmpty(entry.IconName)) continue;

            manifest[id.ToString()] = new
            {
                icon = entry.IconName.ToLowerInvariant(),
                model = string.IsNullOrEmpty(entry.ModelName) ? null : entry.ModelName,
                texture = string.IsNullOrEmpty(entry.ModelTexture) ? null : entry.ModelTexture
            };
        }

        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outDir, "item_display_info.json"), json, ct);
        Log($"Wrote item_display_info.json — {manifest.Count} entries");

        // Flat lookup: displayId → iconName
        var flatLookup = _itemDisplayInfo
            .Where(e => !string.IsNullOrEmpty(e.Value.IconName))
            .ToDictionary(e => e.Key.ToString(), e => e.Value.IconName.ToLowerInvariant());

        var flatJson = JsonSerializer.Serialize(flatLookup, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(outDir, "item_icon_lookup.json"), flatJson, ct);
        Log($"Wrote item_icon_lookup.json — {flatLookup.Count} entries");

        ReportProgress("Item Icon Manifest", 1, 1, "item_display_info.json");
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW: Spell Icon Manifest (DBC-driven)
    // ═══════════════════════════════════════════════════════════════

    private AssetCategory ScanSpellIconManifest()
    {
        int iconCount = _spellIcons?.Count ?? 0;
        int spellCount = _spellToIcon?.Count ?? 0;

        var cat = new AssetCategory
        {
            Name = "Spell Icon Manifest (JSON)",
            Description = $"JSON lookups parsed from SpellIcon.dbc + Spell.dbc.\n\nOutputs:\n  spell_icon_lookup.json         — iconId → iconName ({iconCount} entries)\n  spell_to_icon.json             — spellId → iconId ({spellCount} entries)\n  spell_direct_icon_lookup.json  — spellId → iconName (combined)",
            OutputFolder = "manifests",
            IsCustomExtraction = true,
        };

        if (iconCount > 0)
            cat.Files.Add(new AssetFile { OutputName = $"spell_icon_lookup.json ({iconCount} icons, {spellCount} spells)" });

        cat.CustomExtractAction = async (outputRoot, ct) =>
        {
            await ExtractSpellIconManifestAsync(outputRoot, ct);
        };

        Log($"Spell icon manifest: {iconCount} icon entries, {spellCount} spell→icon mappings");
        return cat;
    }

    private async Task ExtractSpellIconManifestAsync(string outputRoot, CancellationToken ct)
    {
        var outDir = Path.Combine(outputRoot, "manifests");
        Directory.CreateDirectory(outDir);

        // SpellIcon.dbc: iconId → icon filename
        if (_spellIcons != null)
        {
            var iconLookup = _spellIcons.ToDictionary(
                e => e.Key.ToString(),
                e => e.Value.ToLowerInvariant()
            );

            var json = JsonSerializer.Serialize(iconLookup, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(outDir, "spell_icon_lookup.json"), json, ct);
            Log($"Wrote spell_icon_lookup.json — {iconLookup.Count} entries");
        }

        // Spell.dbc: spellId → spellIconId
        if (_spellToIcon != null)
        {
            var spellMap = _spellToIcon.ToDictionary(
                e => e.Key.ToString(),
                e => e.Value
            );

            var json = JsonSerializer.Serialize(spellMap, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(outDir, "spell_to_icon.json"), json, ct);
            Log($"Wrote spell_to_icon.json — {spellMap.Count} entries");
        }

        // Combined: spellId → icon filename (direct resolution)
        if (_spellToIcon != null && _spellIcons != null)
        {
            var directLookup = new Dictionary<string, string>();
            foreach (var (spellId, iconId) in _spellToIcon)
            {
                if (_spellIcons.TryGetValue(iconId, out var iconName))
                {
                    directLookup[spellId.ToString()] = iconName.ToLowerInvariant();
                }
            }

            var json = JsonSerializer.Serialize(directLookup, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(outDir, "spell_direct_icon_lookup.json"), json, ct);
            Log($"Wrote spell_direct_icon_lookup.json — {directLookup.Count} entries (spellId → iconName)");
        }

        ReportProgress("Spell Icon Manifest", 1, 1, "spell_icons.json");
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW: Game Object Models (M2 → GLB, fully in-process)
    // ═══════════════════════════════════════════════════════════════

    private AssetCategory ScanGameObjectModels()
    {
        int count = _goDisplayInfo?.Count ?? 0;

        var cat = new AssetCategory
        {
            Name = "Game Object Models (M2 → GLB)",
            Description = $"3D models for game objects (chests, braziers, statues, etc.).\n{count} entries from GameObjectDisplayInfo.dbc.\n\n" +
                          "Fully self-contained pipeline — no external tools needed:\n" +
                          "  MPQ → M2 binary parse → SharpGLTF → GLB\n\n" +
                          "Output: models/{displayId}.glb  (one-click, ready for <model-viewer>)\n" +
                          "Also outputs manifests/gameobject_models.json",
            OutputFolder = "models",
            IsCustomExtraction = true,
        };

        if (count > 0)
            cat.Files.Add(new AssetFile { OutputName = $"gameobject_models ({count} entries)" });

        cat.CustomExtractAction = async (outputRoot, ct) =>
        {
            await ExtractGameObjectModelsAsync(outputRoot, ct);
        };

        Log($"Game object models: {count} display entries from GameObjectDisplayInfo.dbc");
        return cat;
    }

    private async Task ExtractGameObjectModelsAsync(string outputRoot, CancellationToken ct)
    {
        if (_goDisplayInfo == null || _goDisplayInfo.Count == 0) return;

        var glbDir = Path.Combine(outputRoot, "models");
        var manifestDir = Path.Combine(outputRoot, "manifests");
        Directory.CreateDirectory(glbDir);
        Directory.CreateDirectory(manifestDir);

        var progress = new ExtractionProgress { Total = _goDisplayInfo.Count };
        int converted = 0, skipped = 0, parseFailed = 0, errors = 0;
        long totalBytes = 0;

        // Manifest: displayId → { modelPath, glbFile, fileSize, vertexCount, triangleCount }
        var manifest = new Dictionary<string, object>();

        foreach (var (displayId, modelPath) in _goDisplayInfo)
        {
            ct.ThrowIfCancellationRequested();

            progress.Current++;
            progress.CurrentFile = $"DisplayId {displayId}: {Path.GetFileName(modelPath)}";
            progress.Status = $"[M2→GLB] {progress.Current}/{progress.Total} — {converted} converted";
            ProgressChanged?.Invoke(progress);

            try
            {
                var glbPath = Path.Combine(glbDir, $"{displayId}.glb");

                // Skip if already exists
                if (File.Exists(glbPath))
                {
                    converted++;
                    totalBytes += new FileInfo(glbPath).Length;
                    continue;
                }

                // 1. Extract the raw M2 bytes from MPQ
                var m2Data = TryExtractModelFile(modelPath);
                if (m2Data == null)
                {
                    skipped++;
                    if (progress.Current <= 20) // Log first 20 skips for diagnostics
                        Log($"  SKIP {displayId}: not found in MPQ ({modelPath})");
                    continue;
                }

                // 2. Parse the M2 binary into vertices, indices, texture refs
                var model = M2Reader.Parse(m2Data);
                if (model == null || !model.IsValid)
                {
                    parseFailed++;
                    if (parseFailed <= 20) // Log first 20 parse failures
                    {
                        var magic = m2Data.Length >= 4 ? System.Text.Encoding.ASCII.GetString(m2Data, 0, 4) : "???";
                        var vCount = model?.Vertices.Count ?? 0;
                        var iCount = model?.Indices.Count ?? 0;
                        Log($"  PARSE FAIL {displayId}: magic={magic}, len={m2Data.Length}, verts={vCount}, idx={iCount} ({Path.GetFileName(modelPath)})");
                    }
                    continue;
                }

                // 3. Extract ALL textures referenced by the M2
                var textureMap = new Dictionary<int, byte[]>();

                // First pass: extract type 0 (filename-based) textures
                for (int texIdx = 0; texIdx < model.Textures.Count; texIdx++)
                {
                    var texRef = model.Textures[texIdx];

                    if (texRef.Type == 0 && !string.IsNullOrEmpty(texRef.Filename))
                    {
                        var blpData = _mpq.ExtractFile(texRef.Filename);

                        if (blpData == null && !texRef.Filename.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                            blpData = _mpq.ExtractFile(texRef.Filename + ".blp");

                        if (blpData != null)
                            textureMap[texIdx] = blpData;
                    }
                }

                // Second pass: for any texture slots still missing (type != 0, or filename not found),
                // scan the model's directory for BLP files and assign them to unfilled slots.
                // This handles object skin textures (type 1) which have no filename in the M2.
                var missingSlots = Enumerable.Range(0, Math.Max(model.Textures.Count, 1))
                    .Where(i => !textureMap.ContainsKey(i))
                    .ToList();

                if (missingSlots.Count > 0)
                {
                    var baseDir = Path.GetDirectoryName(modelPath)?.Replace('/', '\\') ?? "";
                    if (!string.IsNullOrEmpty(baseDir))
                    {
                        var dirBlps = _mpq.FindFilesByPrefix(baseDir)
                            .Where(f => f.FilePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                            // Exclude BLPs we already loaded by filename
                            .Where(f => !textureMap.Values.Any())  // simplification: if we have any missing, scan all
                            .ToList();

                        for (int i = 0; i < dirBlps.Count && i < missingSlots.Count; i++)
                        {
                            var blpData = _mpq.ExtractFile(dirBlps[i].FilePath);
                            if (blpData != null)
                                textureMap[missingSlots[i]] = blpData;
                        }
                    }
                }

                // 4. Build GLB via SharpGLTF (in-memory, then write to disk)
                var success = await Task.Run(() => GlbWriter.SaveGlb(model, textureMap, glbPath), ct);

                if (success)
                {
                    converted++;
                    var fileSize = new FileInfo(glbPath).Length;
                    totalBytes += fileSize;

                    manifest[displayId.ToString()] = new
                    {
                        modelPath = modelPath,
                        glbFile = $"{displayId}.glb",
                        fileSize = fileSize,
                        vertices = model.Vertices.Count,
                        triangles = model.Indices.Count / 3,
                        textures = textureMap.Count,
                        submeshes = model.Submeshes.Count,
                    };

                    if (converted <= 5)
                        Log($"  OK {displayId}: {model.Vertices.Count}v/{model.Indices.Count / 3}t, {textureMap.Count}tex, {model.Submeshes.Count}sub, {new FileInfo(glbPath).Length / 1024}KB ({Path.GetFileName(modelPath)})");
                }
                else
                {
                    errors++;
                    if (errors <= 10)
                        Log($"  GLB FAIL {displayId}: verts={model.Vertices.Count}, idx={model.Indices.Count} ({Path.GetFileName(modelPath)})");
                }
            }
            catch (Exception ex)
            {
                errors++;
                progress.Errors++;
                Log($"Error converting displayId {displayId}: {ex.Message}");
            }
        }

        // Write manifest
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "gameobject_models.json"), manifestJson, ct);

        // Write flat lookup: displayId → model path (for reference)
        var flatLookup = _goDisplayInfo.ToDictionary(e => e.Key.ToString(), e => e.Value);
        var flatJson = JsonSerializer.Serialize(flatLookup, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "gameobject_display_lookup.json"), flatJson, ct);

        var totalMB = totalBytes / (1024.0 * 1024.0);
        var avgKB = converted > 0 ? totalBytes / converted / 1024.0 : 0;

        progress.Status = $"[M2→GLB] Done — {converted} converted, {skipped} not in MPQ, {parseFailed} parse failed, {errors} errors";
        ProgressChanged?.Invoke(progress);
        Log(progress.Status);
        Log($"  Total: {totalMB:F1} MB ({converted} GLBs, avg {avgKB:F1} KB)");
        Log($"  Output: {glbDir}");
        Log($"  Manifest: {Path.Combine(manifestDir, "gameobject_models.json")} ({manifest.Count} entries)");
    }

    // ═══════════════════════════════════════════════════════════════
    // NEW: Item Models (M2 → GLB) — weapons, armor, shields, etc.
    // ═══════════════════════════════════════════════════════════════

    private AssetCategory ScanItemModels()
    {
        // Count entries that have a model name (not just an icon)
        int count = _itemDisplayInfo?.Count(e => !string.IsNullOrEmpty(e.Value.ModelName)) ?? 0;

        var cat = new AssetCategory
        {
            Name = "Item Models (M2 → GLB)",
            Description = $"3D models for weapons, armor, shields, and other equippable items.\n{count} entries from ItemDisplayInfo.dbc with model references.\n\n" +
                          "Same pure C# pipeline as game object models:\n" +
                          "  MPQ → M2 binary parse → SharpGLTF → GLB\n\n" +
                          "Output: item_models/{{displayId}}.glb  (ready for <model-viewer>)\n" +
                          "Also outputs manifests/item_models.json",
            OutputFolder = "item_models",
            IsCustomExtraction = true,
        };

        if (count > 0)
            cat.Files.Add(new AssetFile { OutputName = $"item_models ({count} entries with models)" });

        cat.CustomExtractAction = async (outputRoot, ct) =>
        {
            await ExtractItemModelsAsync(outputRoot, ct);
        };

        Log($"Item models: {count} display entries with model references, {_itemModelIndex?.Count ?? 0} M2 files indexed");
        return cat;
    }

    private async Task ExtractItemModelsAsync(string outputRoot, CancellationToken ct)
    {
        if (_itemDisplayInfo == null || _itemModelIndex == null) return;

        var glbDir = Path.Combine(outputRoot, "item_models");
        var manifestDir = Path.Combine(outputRoot, "manifests");
        Directory.CreateDirectory(glbDir);
        Directory.CreateDirectory(manifestDir);

        // Filter to entries that have a model name
        var withModels = _itemDisplayInfo
            .Where(e => !string.IsNullOrEmpty(e.Value.ModelName))
            .ToList();

        var progress = new ExtractionProgress { Total = withModels.Count };
        int converted = 0, skipped = 0, notFound = 0, parseFailed = 0, errors = 0;
        long totalBytes = 0;

        var manifest = new Dictionary<string, object>();

        foreach (var (displayId, entry) in withModels)
        {
            ct.ThrowIfCancellationRequested();

            progress.Current++;
            progress.CurrentFile = $"DisplayId {displayId}: {entry.ModelName}";
            progress.Status = $"[Item M2→GLB] {progress.Current}/{progress.Total} — {converted} converted";
            ProgressChanged?.Invoke(progress);

            try
            {
                var glbPath = Path.Combine(glbDir, $"{displayId}.glb");

                // Skip if already exists
                if (File.Exists(glbPath))
                {
                    converted++;
                    totalBytes += new FileInfo(glbPath).Length;
                    continue;
                }

                // 1. Resolve the bare model name to a full MPQ path via the index
                var bareName = Path.GetFileNameWithoutExtension(entry.ModelName).ToLowerInvariant();
                if (!_itemModelIndex.TryGetValue(bareName, out var mpqModelPath))
                {
                    notFound++;
                    if (notFound <= 20)
                        Log($"  NOT FOUND {displayId}: {entry.ModelName} (no match in Item\\ObjectComponents\\)");
                    continue;
                }

                // 2. Extract the raw M2 bytes
                var m2Data = TryExtractModelFile(mpqModelPath);
                if (m2Data == null)
                {
                    skipped++;
                    if (skipped <= 10)
                        Log($"  SKIP {displayId}: MPQ extract failed ({mpqModelPath})");
                    continue;
                }

                // 3. Parse the M2 binary
                var model = M2Reader.Parse(m2Data);
                if (model == null || !model.IsValid)
                {
                    parseFailed++;
                    if (parseFailed <= 20)
                    {
                        var magic = m2Data.Length >= 4 ? Encoding.ASCII.GetString(m2Data, 0, 4) : "???";
                        Log($"  PARSE FAIL {displayId}: magic={magic}, len={m2Data.Length} ({Path.GetFileName(mpqModelPath)})");
                    }
                    continue;
                }

                // 4. Extract textures for this item model
                var textureMap = new Dictionary<int, byte[]>();

                // First pass: type 0 (filename-based) textures from M2 refs
                for (int texIdx = 0; texIdx < model.Textures.Count; texIdx++)
                {
                    var texRef = model.Textures[texIdx];
                    if (texRef.Type == 0 && !string.IsNullOrEmpty(texRef.Filename))
                    {
                        var blpData = _mpq.ExtractFile(texRef.Filename);
                        if (blpData == null && !texRef.Filename.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                            blpData = _mpq.ExtractFile(texRef.Filename + ".blp");
                        if (blpData != null)
                            textureMap[texIdx] = blpData;
                    }
                }

                // Second pass: use ItemDisplayInfo.ModelTexture from DBC to resolve missing slots.
                // The DBC gives us the bare texture name (e.g. "Sword_2H_Thunderfury_A_01")
                // which lives in the same directory as the model file as a .blp.
                var missingSlots = Enumerable.Range(0, Math.Max(model.Textures.Count, 1))
                    .Where(i => !textureMap.ContainsKey(i))
                    .ToList();

                if (missingSlots.Count > 0 && !string.IsNullOrEmpty(entry.ModelTexture))
                {
                    var baseDir = Path.GetDirectoryName(mpqModelPath)?.Replace('/', '\\') ?? "";
                    if (!string.IsNullOrEmpty(baseDir))
                    {
                        // Try the DBC texture name directly in the model's directory
                        var texName = entry.ModelTexture;
                        string[] texPaths = {
                            $"{baseDir}\\{texName}.blp",
                            $"{baseDir}\\{texName.ToLowerInvariant()}.blp",
                            $"{baseDir}\\{texName}",
                        };

                        foreach (var texPath in texPaths)
                        {
                            var blpData = _mpq.ExtractFile(texPath);
                            if (blpData != null)
                            {
                                textureMap[missingSlots[0]] = blpData;
                                missingSlots.RemoveAt(0);
                                break;
                            }
                        }
                    }
                }

                // Third pass: fallback — scan model directory for any remaining BLP files
                if (missingSlots.Count > 0)
                {
                    var baseDir = Path.GetDirectoryName(mpqModelPath)?.Replace('/', '\\') ?? "";
                    if (!string.IsNullOrEmpty(baseDir))
                    {
                        var dirBlps = _mpq.FindFilesByPrefix(baseDir)
                            .Where(f => f.FilePath.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
                            // Skip BLPs we already loaded
                            .Where(f => !textureMap.Values.Any())
                            .ToList();

                        for (int i = 0; i < dirBlps.Count && i < missingSlots.Count; i++)
                        {
                            var blpData = _mpq.ExtractFile(dirBlps[i].FilePath);
                            if (blpData != null)
                                textureMap[missingSlots[i]] = blpData;
                        }
                    }
                }

                // 5. Build GLB
                var success = await Task.Run(() => GlbWriter.SaveGlb(model, textureMap, glbPath), ct);

                if (success)
                {
                    converted++;
                    var fileSize = new FileInfo(glbPath).Length;
                    totalBytes += fileSize;

                    manifest[displayId.ToString()] = new
                    {
                        modelName = entry.ModelName,
                        mpqPath = mpqModelPath,
                        glbFile = $"{displayId}.glb",
                        fileSize,
                        vertices = model.Vertices.Count,
                        triangles = model.Indices.Count / 3,
                        textures = textureMap.Count,
                        submeshes = model.Submeshes.Count,
                    };

                    if (converted <= 5)
                        Log($"  OK {displayId}: {model.Vertices.Count}v/{model.Indices.Count / 3}t, {textureMap.Count}tex, {fileSize / 1024}KB ({entry.ModelName})");
                }
                else
                {
                    errors++;
                    if (errors <= 10)
                        Log($"  GLB FAIL {displayId}: verts={model.Vertices.Count}, idx={model.Indices.Count} ({entry.ModelName})");
                }
            }
            catch (Exception ex)
            {
                errors++;
                progress.Errors++;
                Log($"Error converting item displayId {displayId}: {ex.Message}");
            }
        }

        // Write manifest
        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "item_models.json"), manifestJson, ct);

        // Write flat lookup: displayId → model name
        var flatLookup = withModels.ToDictionary(e => e.Key.ToString(), e => e.Value.ModelName);
        var flatJson = JsonSerializer.Serialize(flatLookup, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(Path.Combine(manifestDir, "item_model_lookup.json"), flatJson, ct);

        var totalMB = totalBytes / (1024.0 * 1024.0);
        var avgKB = converted > 0 ? totalBytes / converted / 1024.0 : 0;

        progress.Status = $"[Item M2→GLB] Done — {converted} converted, {notFound} not found, {parseFailed} parse failed, {skipped} extract failed, {errors} errors";
        ProgressChanged?.Invoke(progress);
        Log(progress.Status);
        Log($"  Total: {totalMB:F1} MB ({converted} GLBs, avg {avgKB:F1} KB)");
        Log($"  Output: {glbDir}");
        Log($"  Manifest: {Path.Combine(manifestDir, "item_models.json")} ({manifest.Count} entries)");
    }

    /// <summary>
    /// Try to extract a model file from MPQ. The DBC stores paths with various extensions
    /// (.mdx, .MDX, .MDL, .m2) and casing. The actual files in the MPQ may differ.
    /// We try all reasonable variations.
    /// </summary>
    private byte[]? TryExtractModelFile(string modelPath)
    {
        // Skip WMO files entirely — different format, not M2 models
        if (modelPath.EndsWith(".wmo", StringComparison.OrdinalIgnoreCase))
            return null;

        // Build a list of path variations to try
        var basePath = modelPath;

        // Strip the extension to get the base
        string pathNoExt;
        if (basePath.EndsWith(".mdx", StringComparison.OrdinalIgnoreCase) ||
            basePath.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
        {
            pathNoExt = basePath.Substring(0, basePath.Length - 4);
        }
        else if (basePath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase))
        {
            pathNoExt = basePath.Substring(0, basePath.Length - 3);
        }
        else
        {
            pathNoExt = basePath;
        }

        // Try each extension with the original casing first, then lowercase
        string[] extensions = { ".m2", ".mdx", ".M2", ".MDX" };
        string[] pathBases = { pathNoExt, pathNoExt.ToLowerInvariant() };

        foreach (var pb in pathBases)
        {
            foreach (var ext in extensions)
            {
                var tryPath = pb + ext;
                var data = _mpq.ExtractFile(tryPath);
                if (data != null) return data;
            }
        }

        // Also try the exact original path as-is (might work for some entries)
        var exactData = _mpq.ExtractFile(modelPath);
        if (exactData != null) return exactData;

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // Extraction (updated to handle custom categories)
    // ═══════════════════════════════════════════════════════════════

    public async Task ExtractCategoryAsync(AssetCategory category, string outputRoot, CancellationToken ct = default)
    {
        // Handle custom extraction categories
        if (category.IsCustomExtraction && category.CustomExtractAction != null)
        {
            await category.CustomExtractAction(outputRoot, ct);
            return;
        }

        // Standard file-by-file extraction
        var outDir = Path.Combine(outputRoot, category.OutputFolder);
        Directory.CreateDirectory(outDir);

        var progress = new ExtractionProgress { Total = category.FileCount };

        for (int i = 0; i < category.Files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            var file = category.Files[i];
            progress.Current = i + 1;
            progress.CurrentFile = file.OutputName;
            progress.Status = $"[{category.Name}] {i + 1}/{category.FileCount}";
            ProgressChanged?.Invoke(progress);

            try
            {
                var destPath = Path.Combine(outDir, file.OutputName.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(destPath);
                if (!string.IsNullOrEmpty(destDir))
                    Directory.CreateDirectory(destDir);

                if (File.Exists(destPath)) continue;

                var data = _mpq.ExtractFile(file.MpqPath);
                if (data == null)
                {
                    progress.Errors++;
                    continue;
                }

                if (file.ConvertBlpToPng)
                {
                    await Task.Run(() => ConvertBlpToPng(data, destPath), ct);
                }
                else
                {
                    await File.WriteAllBytesAsync(destPath, data, ct);
                }
            }
            catch (Exception ex)
            {
                progress.Errors++;
                Log($"Error: {file.MpqPath} — {ex.Message}");
            }
        }

        progress.Status = $"[{category.Name}] Done — {category.FileCount} files, {progress.Errors} errors";
        ProgressChanged?.Invoke(progress);
        Log(progress.Status);
    }

    // ═══════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════

    private void ConvertBlpToPng(byte[] blpData, string pngPath)
    {
        using var ms = new MemoryStream(blpData);
        var blpFile = new BlpFile(ms);
        var pixels = blpFile.GetPixels(0, out int w, out int h);

        using var bitmap = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, w, h),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
        bitmap.UnlockBits(bmpData);
        bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
    }

    public List<(string Name, int TileCount)> GetMinimapMapNames(AssetCategory minimapCategory)
    {
        return minimapCategory.Files
            .Select(f =>
            {
                var parts = f.OutputName.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                return parts.Length > 1 ? parts[0] : "Unknown";
            })
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.Key, TileCount: g.Count()))
            .OrderByDescending(x => x.TileCount)
            .ToList();
    }

    private void ReportProgress(string categoryName, int current, int total, string currentFile)
    {
        ProgressChanged?.Invoke(new ExtractionProgress
        {
            Total = total,
            Current = current,
            CurrentFile = currentFile,
            Status = $"[{categoryName}] {current}/{total}"
        });
    }

    private void Log(string message)
    {
        LogMessage?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}");
    }
}