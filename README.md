# MangosSuperUI Extractor

[![License: GPL v2](https://img.shields.io/badge/License-GPL_v2-blue.svg)](https://www.gnu.org/licenses/old-licenses/gpl-2.0.en.html)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-purple.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Platform-Windows-lightgrey.svg)]()

A standalone C# WinForms tool that extracts and converts assets from WoW 1.12.1 (vanilla) MPQ archives into web-ready formats. Built for the [MangosSuperUI](https://github.com/your-org/MangosSuperUI) admin platform — no wow.export, no Node.js, no external converters. Everything runs in-process via NuGet packages.

---

## Features

- **Direct MPQ reading** — opens raw `.MPQ` archives from your WoW 1.12.1 `Data/` folder
- **BLP → PNG conversion** — all textures converted automatically
- **M2 → GLB conversion** — 3D models (items, game objects) converted to glTF 2.0 binary for `<model-viewer>` display
- **DBC parsing** — reads client database files (ItemDisplayInfo, SpellIcon, Spell, GameObjectDisplayInfo)
- **Minimap tile extraction** — resolves md5 hash filenames to real `mapXX_YY.png` names via `md5translate.trs`
- **Manifest generation** — JSON manifests for item icons, spell icons, game object models, and item models
- **Selective extraction** — WinForms UI with checkable category list; extract only what you need

## Asset Categories

| Category | Description | Output Format | Output Path |
|---|---|---|---|
| Minimap Tiles | 256×256 top-down terrain tiles | PNG | `minimap/{Continent}/map{X}_{Y}.png` |
| Interface Icons | Item/spell/ability icons (64×64) | PNG | `icons/{IconName}.png` |
| World Maps | Hand-painted zone maps (M key) | PNG | `worldmaps/{Zone}/{file}.png` |
| DBC Files | Client databases (spells, items, areas) | Raw binary | `dbc/{Name}.dbc` |
| Creature Textures | NPC/creature skins | PNG | `creatures/Creature/...` |
| Item Textures | Item model textures | PNG | `items/Item/...` |
| Item Icon Manifest | DisplayId → icon name mapping | JSON | `manifests/item_icons.json` |
| Spell Icon Manifest | SpellId → icon name mapping | JSON | `manifests/spell_icons.json` |
| Game Object Models | Chests, chairs, anvils, etc. | GLB | `models/{displayId}.glb` |
| Item Models | Weapons, shields, held items | GLB | `item_models/{displayId}.glb` |

## Requirements

- Windows 10 or 11
- [.NET 8.0 Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (or SDK for building)
- A WoW 1.12.1 client — specifically the `Data/` folder containing `.MPQ` files

## Building

```bash
# Clone the repo
git clone https://github.com/your-org/MangosSuperUI_Extractor.git
cd MangosSuperUI_Extractor

# Restore NuGet packages and build
dotnet build -c Release
```

The built executable will be in `bin/Release/net8.0-windows/`.

## Usage

1. Launch the application
2. Click **Browse** and point to your WoW 1.12.1 `Data/` folder
3. Click **Scan MPQs** — the tool opens all archives and categorizes available assets
4. Check the categories you want in the list
5. Click **Extract Selected** (or **Extract All**)
6. Assets are written to your chosen output directory

## How It Works

```
WoW 1.12.1 Data/ folder
    │
    ├── MpqManager — opens all .MPQ files, reads (listfile) from each
    │                 patches override base files (reverse archive order)
    │
    ├── ExtractorEngine — scans & categorizes assets
    │   ├── Standard categories: file-by-file extract + BLP→PNG convert
    │   └── Custom categories: full async pipelines (manifests, 3D models)
    │
    ├── DbcParser — reads WDBC binary format for 4 DBC files
    │   └── ItemDisplayInfo, SpellIcon, Spell, GameObjectDisplayInfo
    │
    ├── M2Reader — parses vanilla M2/MDX model format (version 256)
    │   └── Vertices, indices, submeshes, textures, coordinate conversion (Z-up → Y-up)
    │
    └── GlbWriter — M2Model + BLP textures → GLB via SharpGLTF
        └── Separate mesh per submesh, unlit materials, no winding swap needed
```

### 3D Model Pipeline

The extractor handles the full chain from binary M2 to web-ready GLB:

- **Game objects:** `GameObjectDisplayInfo.dbc` provides full MPQ paths. Textures resolved from M2 embedded filenames + directory scan fallback.
- **Items:** `ItemDisplayInfo.dbc` provides bare model filenames (no path). Resolved via a pre-built index of `Item\ObjectComponents\` in all MPQs. Textures resolved via M2 embedded filenames → DBC `ModelTexture` field → directory scan (3-pass).
- **Coordinate conversion:** WoW Z-up → glTF Y-up `(x, y, z) → (x, z, -y)` applied to positions and normals. This also flips handedness, so triangle winding comes out correct without any index swap.

> **Note:** Armor pieces (head, chest, shoulders, legs, etc.) do not have standalone M2 models — they are rendered as texture overlays on the character model at runtime. Only weapons, shields, and held-in-offhand items produce GLB files.

## Deploying to MangosSuperUI

After extraction, copy the output folders to your MangosSuperUI server's `wwwroot/`:

```
models/          →  wwwroot/models/          (game object GLBs)
item_models/     →  wwwroot/item_models/     (item GLBs)
icons/           →  wwwroot/icons/           (interface icons)
minimap/         →  wwwroot/minimap/         (Leaflet.js world map tiles)
```

Manifest files (`manifests/`) are for reference only — MangosSuperUI reads DBC data directly at runtime via `DbcService`.

### Minimap Tile Math

The Leaflet.js world map in MangosSuperUI uses these tiles with coordinate conversion:

```
tile_i = 32 - ceil(world_y / 533.5)
tile_j = 32 - ceil(world_x / 533.5)
```

Each tile is 256×256 pixels covering approximately 533 yards of terrain.

## NuGet Dependencies

| Package | Purpose |
|---|---|
| [War3Net.IO.Mpq](https://www.nuget.org/packages/War3Net.IO.Mpq) | Pure managed C# MPQ archive reader |
| [War3Net.Drawing.Blp](https://www.nuget.org/packages/War3Net.Drawing.Blp) | BLP texture → Bitmap conversion |
| [SharpGLTF.Toolkit](https://www.nuget.org/packages/SharpGLTF.Toolkit) | Build and write GLB (glTF 2.0 binary) files |
| [System.Drawing.Common](https://www.nuget.org/packages/System.Drawing.Common) | Bitmap/PNG operations |

## Known Limitations

- **WMO models** (~15 game object entries) reference `.wmo` files (World Map Objects), a completely different chunked format. These are skipped.
- **Armor models** don't exist as standalone M2 files — they're character model attachments. This is a WoW client architecture limitation.
- **Particle/animated textures** may not map correctly through the batch→TextureLookup→texture chain.
- **Spell.dbc SpellIconID** offset is hardcoded to field index 88 (byte offset 352) for build 5875.

## Project Structure

```
MangosSuperUI_Extractor/
├── Program.cs              # Entry point
├── MainForm.cs             # WinForms UI (category list, progress, controls)
├── MpqManager.cs           # MPQ archive wrapper (open, enumerate, extract)
├── ExtractorEngine.cs      # Category scanning + extraction logic
├── DbcParser.cs            # WDBC binary parser (4 DBC files)
├── M2Reader.cs             # WoW 1.12.1 M2 model binary parser
├── GlbWriter.cs            # M2Model + BLP textures → GLB via SharpGLTF
└── MangosSuperUI_Extractor.csproj
```

## License

This project is licensed under the GNU General Public License v2.0 — see the [LICENSE](LICENSE) file for details.

```
Copyright (C) 2025-2026 MangosSuperUI Contributors

This program is free software; you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation; either version 2 of the License, or
(at your option) any later version.
```

## Acknowledgments

- [VMaNGOS](https://github.com/vmangos) — the vanilla WoW server emulator this tooling supports
- [War3Net](https://github.com/Drake53/War3Net) — MPQ and BLP libraries that make this possible without native dependencies
- [SharpGLTF](https://github.com/vpenades/SharpGLTF) — excellent glTF toolkit for .NET
- [Google model-viewer](https://modelviewer.dev/) — the web component used to display extracted GLBs in MangosSuperUI