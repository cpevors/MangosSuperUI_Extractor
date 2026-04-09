# Contributing to MangosSuperUI Extractor

Thanks for your interest in contributing! This project is part of the MangosSuperUI ecosystem — a web admin platform for VMaNGOS vanilla WoW servers.

## Getting Started

1. Fork the repository
2. Clone your fork locally
3. Open `MangosSuperUI_Extractor.sln` in Visual Studio 2022
4. Build with `dotnet build -c Debug`
5. You'll need a WoW 1.12.1 client's `Data/` folder to test extractions

## Development Environment

- **IDE:** Visual Studio 2022 (recommended) or any .NET 8.0 compatible editor
- **Target:** .NET 8.0, WinForms (Windows only)
- **NuGet packages** restore automatically on build

## Architecture Overview

See `ARCHITECTURE.md` for the full technical reference. The short version:

- `MpqManager.cs` — MPQ archive I/O
- `ExtractorEngine.cs` — category scanning and extraction orchestration
- `DbcParser.cs` — WDBC binary format reader
- `M2Reader.cs` — WoW 1.12.1 M2 model parser
- `GlbWriter.cs` — M2 → GLB converter via SharpGLTF

## Pull Requests

- Keep changes focused — one feature or fix per PR
- Test your extraction against a real WoW 1.12.1 client
- If adding a new asset category, follow the existing `AssetCategory` pattern in `ExtractorEngine.cs`
- For 3D model changes, verify GLB output renders correctly in `<model-viewer>`

## Areas That Could Use Help

- **WMO parser** — ~15 game object entries use `.wmo` files (World Map Objects), a completely different format from M2. Currently skipped.
- **Animated texture support** — particle systems and animated textures don't always map correctly
- **Linux/macOS support** — currently Windows-only due to WinForms. A CLI mode or cross-platform UI would be welcome.

## License

By contributing, you agree that your contributions will be licensed under the GNU General Public License v2.0, consistent with the project's existing license.
