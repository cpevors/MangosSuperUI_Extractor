# MangosSuperUI Extractor — Technical Reference

**Purpose:** Complete reference for the MangosSuperUI_Extractor C# WinForms tool. Read this document to understand the architecture, modify extraction categories, fix model conversion bugs, or add new asset types.

**Last Updated:** April 8, 2026

---

## What This Is

A standalone C# WinForms desktop application that extracts and converts assets from WoW 1.12.1 (vanilla) MPQ archives into web-ready formats for the MangosSuperUI web admin platform. Zero external dependencies — no wow.export, no Node.js, no obj2gltf. Everything runs in-process via NuGet packages.

**Project:** `MangosSuperUI_Extractor.sln` (separate from the MangosSuperUI web app)  
**Target:** .NET 8.0, WinForms  
**Dev environment:** Windows, Visual Studio 2022

---

## NuGet Packages

| Package | Version | Purpose |
|---|---|---|
| `War3Net.IO.Mpq` | 5.8.1 | Open and read MPQ archives |
| `War3Net.Drawing.Blp` | 5.8.1 (→5.9.0) | Decode BLP textures to pixel arrays |
| `SharpGLTF.Toolkit` | 1.0.6 | Build and write GLB (glTF 2.0 binary) files |
| `System.Drawing.Common` | 8.0.0 | Bitmap creation for BLP→PNG conversion |

---

## File Structure

```
MangosSuperUI_Extractor/
├── Program.cs                  # Entry point
├── MainForm.cs                 # WinForms UI (CheckedListBox of categories)
├── MpqManager.cs               # MPQ archive wrapper (open, enumerate, extract)
├── ExtractorEngine.cs          # Category scanning + extraction logic (~1145 lines)
├── DbcParser.cs                # WDBC binary parser (4 DBC files)
├── M2Reader.cs                 # WoW M2 model binary parser (~430 lines)
├── GlbWriter.cs                # M2Model + BLP textures → GLB via SharpGLTF (~170 lines)
└── MangosSuperUI_Extractor.csproj
```

---

## Architecture Flow

```
User clicks "Scan MPQs" in WinForms
    │
    ├── MpqManager.OpenClientFolder(dataPath)
    │     Opens all .MPQ files, reads (listfile) from each
    │
    ├── ExtractorEngine.LoadTrs()
    │     Parses md5translate.trs for minimap tile hash→name mapping
    │
    └── ExtractorEngine.ScanCategories()
          Calls EnsureDbcLoaded() then builds category list:
          │
          ├── ScanMinimapTiles()          → minimap/        (BLP→PNG, TRS rename)
          ├── ScanIcons()                 → icons/          (BLP→PNG)
          ├── ScanWorldMaps()             → worldmaps/      (BLP→PNG)
          ├── ScanDbcFiles()              → dbc/            (raw binary copy)
          ├── ScanCreatureTextures()      → creatures/      (BLP→PNG)
          ├── ScanItemTextures()          → items/          (BLP→PNG)
          ├── ScanItemIconManifest()      → manifests/      (DBC→JSON)
          ├── ScanSpellIconManifest()     → manifests/      (DBC→JSON)
          ├── ScanGameObjectModels()      → models/         (M2→GLB) ★
          └── ScanItemModels()            → item_models/    (M2→GLB) ★

Each category becomes a checkable row in the WinForms CheckedListBox.
User selects categories, clicks "Extract Selected" or "Extract All".
```

Categories marked ★ use the `IsCustomExtraction` / `CustomExtractAction` pattern — they run custom async logic instead of simple file-by-file extraction.

---

## MpqManager.cs

Wraps `War3Net.IO.Mpq` to handle multiple MPQ archives.

**Key methods:**
- `OpenClientFolder(dataPath)` — opens all `.MPQ` files in the folder, reads `(listfile)` from each
- `FindFiles(filter)` — search across all archives with a predicate
- `FindFilesByPrefix(prefix)` — case-insensitive prefix search
- `FindFilesByExtension(ext)` — extension search
- `ExtractFile(filePath)` — extracts raw bytes; searches archives in **reverse order** so patches override base files
- `ExtractFileStream(filePath)` — returns a `MemoryStream` wrapper

---

## DbcParser.cs

Lightweight WDBC binary parser. All DBC files share the same header format:

```
Bytes 0-3:    "WDBC" magic
Bytes 4-7:    recordCount (uint32)
Bytes 8-11:   fieldCount (uint32)
Bytes 12-15:  recordSize (uint32)
Bytes 16-19:  stringBlockSize (uint32)
Bytes 20+:    records (recordCount × recordSize bytes)
After records: string block (stringBlockSize bytes, null-terminated strings)
```

String fields are `uint32` offsets into the string block.

**Parsed DBCs:**

| Method | DBC File | Returns | Key Fields |
|---|---|---|---|
| `ParseItemDisplayInfo()` | ItemDisplayInfo.dbc | `Dict<int, ItemDisplayInfoEntry>` | displayId → icon(5), modelName(1), modelTexture(3) |
| `ParseSpellIcon()` | SpellIcon.dbc | `Dict<int, string>` | iconId → icon filename |
| `ParseSpellToIconMap()` | Spell.dbc | `Dict<int, int>` | spellId → spellIconId (field 88, offset 352) |
| `ParseGameObjectDisplayInfo()` | GameObjectDisplayInfo.dbc | `Dict<int, string>` | displayId → model path |

**ItemDisplayInfoEntry fields:**
- `DisplayId` (int) — the key
- `IconName` (string) — inventory icon filename, e.g. "INV_Sword_39"
- `ModelName` (string) — bare model filename, e.g. "Sword_2H_BroadSword_A_01.mdx" (no path)
- `ModelTexture` (string) — bare texture filename, e.g. "Sword_2H_BroadSword_A_01" (no path, no extension)

---

## M2Reader.cs — WoW 1.12.1 M2 Model Parser

Parses the binary M2/MDX format (version 256, magic "MD20") used by WoW 1.12.1 for all 3D models (items, game objects, creatures, etc.).

### M2 Header Layout (vanilla, version 256)

```
0x000  char[4]   magic "MD20"
0x004  uint32    version (256)
0x008  uint32    nName / 0x00C ofsName
0x044  uint32    nVertices / 0x048 ofsVertices
0x04C  uint32    nViews (nSkinProfiles)
0x050  uint32    ofsViews          ← ONLY in version < 264 (vanilla/TBC)
0x05C  uint32    nTextures / 0x060 ofsTextures
0x08C  uint32    nTextureLookup / 0x090 ofsTextureLookup
```

**Critical:** In vanilla (version < 264), `ofsViews` is present at 0x050, which shifts all subsequent fields by +4 compared to WotLK+ headers. M2Reader handles both layouts.

### Vertex Format (48 bytes each)

```
+0   float[3] position      (12 bytes)
+12  uint8[4] boneWeights   (4 bytes, ignored)
+16  uint8[4] boneIndices   (4 bytes, ignored)
+20  float[3] normal        (12 bytes)
+32  float[2] texCoords0    (8 bytes, UV set 1 — used)
+40  float[2] texCoords1    (8 bytes, UV set 2 — ignored)
```

### Coordinate Conversion

WoW uses Z-up, glTF uses Y-up. Applied to both positions and normals:
```
(x, y, z) → (x, z, -y)
```
This transform also flips handedness, which means triangle indices come out in glTF's expected CCW front-face order **without** any winding swap.

### Inlined Views (Skin Profiles)

In vanilla, views are **inlined** in the M2 file (not separate `.skin` files like WotLK+). Each view is 44 bytes:

```
M2Array<uint16>  localVertices    (indices into global vertex array)
M2Array<uint16>  indices          (triangle indices into LOCAL vertex list)
M2Array<ubyte4>  bones            (skipped)
M2Array<M2SkinSection> submeshes  (32 bytes each)
M2Array<M2Batch> batches          (24 bytes each)
uint32           boneCountMax
```

**Index remapping:** Indices reference the local vertex list, which must be remapped through `localVertices` to get global vertex indices. M2Reader does this automatically.

### M2SkinSection (Submesh) — 32 bytes

```
+0   uint16 id
+4   uint16 vertexStart
+6   uint16 vertexCount
+8   uint16 indexStart
+10  uint16 indexCount
```

### M2Batch (Texture Unit) — 24 bytes

```
+0   uint8  flags
+1   uint8  priorityPlane
+2   uint16 shaderId
+4   uint16 submeshIndex      ← maps to submesh
+6   uint16 geosetIndex
+8   int16  colorIndex
+10  uint16 materialIndex
+12  uint16 materialLayer
+14  uint16 textureCount
+16  uint16 textureIndex      ← index into TextureLookup
+18  uint16 textureTransformIndex
+20  uint16 textureWeightIndex
```

### Texture Resolution Chain

```
batch.TextureIndex → TextureLookup[idx] → Textures[texIdx]
```

**Texture types:**
- **Type 0:** Filename embedded in M2 string data (full MPQ path to BLP)
- **Type 1+:** Object skin texture — no filename in M2. Must be resolved externally (directory scan or DBC lookup)

### Output Data Model

```csharp
M2Model {
    uint Version;
    string Name;
    List<M2Vertex> Vertices;      // position + normal + UV (already Z→Y converted)
    List<ushort> Indices;          // triangle indices (already remapped to global)
    List<M2Submesh> Submeshes;     // indexStart, indexCount per geoset
    List<M2Batch> Batches;         // submesh→texture mapping
    List<M2TextureRef> Textures;   // type + filename
    List<ushort> TextureLookup;    // indirection table
    bool IsValid;                  // true if vertices > 0 && indices >= 3
}
```

---

## GlbWriter.cs — M2Model → GLB

Converts a parsed `M2Model` + `Dictionary<int, byte[]>` of BLP texture data into a GLB file using SharpGLTF Toolkit.

### Key Architecture Decisions

1. **Separate MeshBuilder per submesh:** Each submesh becomes its own `MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>` added to the scene via `scene.AddRigidMesh()`. This prevents SharpGLTF from merging primitives that share a material (which was the original bug).

2. **Unlit shader:** All materials use `WithUnlitShader()` (KHR_materials_unlit extension) because WoW models use pre-baked lighting, not PBR.

3. **No winding swap:** Triangle indices are emitted as `(i0, i1, i2)` — the Z-up→Y-up coordinate transform in M2Reader already flips handedness, so indices come out in glTF's expected CCW convention. **Do NOT swap i1/i2** — this was a bug that caused inside-out rendering.

4. **BLP→PNG in-memory:** `War3Net.Drawing.Blp.BlpFile.GetPixels()` → `System.Drawing.Bitmap` (32bpp ARGB) → PNG byte array → `SharpGLTF.Memory.MemoryImage`.

### Submesh→Texture Mapping

`BuildSubmeshTextureMap()` follows the batch chain:
```
For each batch: batch.SubmeshIndex → batch.TextureIndex → TextureLookup[idx] → texture index
First batch wins per submesh.
Fallback: submeshIndex used as textureIndex if no batch maps.
```

### Method Signatures

```csharp
// Multi-texture (primary)
static bool SaveGlb(M2Model m2, Dictionary<int, byte[]> textures, string outputPath)

// Single-texture convenience overload
static bool SaveGlb(M2Model m2, byte[]? singleTexture, string outputPath)
```

---

## ExtractorEngine.cs — Category System

### Category Pattern

Each extraction category is an `AssetCategory`:

```csharp
AssetCategory {
    string Name;                    // Display name in CheckedListBox
    string Description;             // Shown in info panel when selected
    string OutputFolder;            // Subdirectory under output root
    List<AssetFile> Files;          // For standard extraction
    bool IsCustomExtraction;        // True = uses custom delegate
    Func<string, CancellationToken, Task>? CustomExtractAction;
}
```

**Standard categories** (icons, minimap tiles, etc.) populate `Files` with MPQ paths and output names. The engine iterates them and extracts/converts one by one.

**Custom categories** (manifests, 3D models) set `IsCustomExtraction = true` and provide a `CustomExtractAction` delegate that runs the full extraction logic.

### Cached State

```csharp
Dictionary<int, ItemDisplayInfoEntry>? _itemDisplayInfo;  // from ItemDisplayInfo.dbc
Dictionary<int, string>? _spellIcons;                     // from SpellIcon.dbc
Dictionary<int, int>? _spellToIcon;                       // from Spell.dbc
Dictionary<int, string>? _goDisplayInfo;                   // from GameObjectDisplayInfo.dbc
Dictionary<string, string>? _itemModelIndex;               // bare filename → full MPQ path
```

`_itemModelIndex` is built by scanning `Item\ObjectComponents\` in all MPQs for `.m2`/.mdx` files. Maps lowercase bare filenames (no extension) to full MPQ paths. Used by item model extraction to resolve `ItemDisplayInfo.ModelName` (which is just a filename) to an extractable path.

### Game Object Model Extraction (`ExtractGameObjectModelsAsync`)

Source: `GameObjectDisplayInfo.dbc` → full MPQ model paths (e.g. `World\Generic\ActiveDoodads\Chest02\Chest02.mdx`)

Texture resolution:
1. **M2 type 0** — filename from M2 binary
2. **Directory scan** — find BLPs in model's MPQ directory, assign to missing slots

Output: `models/{displayId}.glb` + `manifests/gameobject_models.json`

### Item Model Extraction (`ExtractItemModelsAsync`)

Source: `ItemDisplayInfo.dbc` → `ModelName` field (bare filename only, no path)

Path resolution: `_itemModelIndex[bareName]` → full MPQ path under `Item\ObjectComponents\`

Texture resolution (3-pass):
1. **M2 type 0** — filename from M2 binary
2. **DBC ModelTexture** — `ItemDisplayInfo.ModelTexture` field, tried as `{modelDir}\{texName}.blp` in MPQ
3. **Directory scan fallback** — any remaining BLPs in model directory

The DBC pass is critical for items because most item M2s use type 1 textures (no embedded filename), and the texture BLP may be named differently from the model file. `ItemDisplayInfo.ModelTexture` gives the exact name.

Output: `item_models/{displayId}.glb` + `manifests/item_models.json`

### TryExtractModelFile(modelPath)

Handles DBC path quirks:
- `.mdx` in DBC but `.m2` in MPQ → tries both extensions
- `.MDL` (Warcraft 3 legacy, ~31 entries) → strips extension, tries `.m2`/`.mdx`
- ALL CAPS paths → tries lowercase
- `.wmo` entries → skipped entirely (World Map Objects, different format)

### Item Model Coverage

**Items with standalone M2 models:** Weapons (swords, maces, axes, daggers, staves, polearms, bows, guns, crossbows, wands), shields, some held-in-offhand items.

**Items WITHOUT standalone models:** Armor pieces (head, chest, shoulders, legs, feet, wrists, hands, back). These are rendered as texture overlays and geometry modifications on the character model at runtime — not extractable as individual GLB files.

---

## Web Integration

### Game Objects (existing)

- Controller: `GameObjectsController.cs` — `Detail` and `Search` return `modelPath` by checking `wwwroot/models/{displayId}.glb`
- JS: `gameobjects.js` — `<model-viewer>` in detail panel and edit form
- Endpoint: `GET /GameObjects/ModelExists?displayId=N`

### Items (added April 8, 2026)

- Controller: `ItemsController.cs` — `Detail`, `FullRow` return `modelPath` by checking `wwwroot/item_models/{displayId}.glb`
- JS: `items.js` — `<model-viewer>` in detail panel (auto-shows if model exists) and edit form (Identity section)
- Endpoint: `GET /Items/ModelExists?displayId=N`
- Icon change auto-refreshes model via `checkItemModel(displayId)`
- Detail icon is clickable → opens edit panel (clone for base game, direct edit for custom)
- CSS: `.model-preview-container` (200px height), `.detail-icon-lg` (56px, clickable with hover effect)
- Requires: `<script type="module" src="~/lib/model-viewer.min.js">` in the view's Scripts section

### Deployment

After running the extractor:
1. Copy `models/` folder to `wwwroot/models/` on the server (game objects)
2. Copy `item_models/` folder to `wwwroot/item_models/` on the server (items)
3. Copy `icons/` folder to `wwwroot/icons/` (if re-extracting)
4. Copy `manifests/` folder for reference (not required at runtime — DbcService reads DBC files directly)

---

## Known Issues & Gotchas

1. **Vertex deduplication:** SharpGLTF's `AddTriangle()` deduplicates vertices with identical pos+normal+UV. Our GLBs may have fewer vertices than wow.export output but render identically.

2. **WMO models:** 15 game object entries reference `.wmo` files (World Map Objects). Completely different chunked format — currently skipped. Would require a separate parser.

3. **Spell.dbc SpellIconID offset:** Hardcoded to field index 88 (byte offset 352) for build 5875. If counts look wrong, verify via `recordSize / 4`.

4. **Complex texture models:** The batch→TextureLookup→texture chain handles most models. Particle systems and animated textures may not map correctly.

5. **Armor models don't exist as standalone M2s** — they're character model attachments. This is a WoW client architecture limitation, not a bug.

6. **resolveSpellName in items.js:** The Spells Detail endpoint may return spell data under `data.spell` or `data.item` — the JS handles both shapes to prevent TypeError crashes that kill page rendering.
