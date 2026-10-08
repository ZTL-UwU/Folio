# Building

Requires Windows 11 and the .NET 10 SDK (no Visual Studio needed).

```powershell
dotnet build src/Folio/Folio.csproj -c Release
src\Folio\bin\Release\net10.0-windows10.0.26100.0\win-x64\Folio.exe [file.pdf]
dotnet test tests/Folio.Tests
dotnet publish src/Folio/Folio.csproj -c Release -o out\win-x64   # ReadyToRun, as CI ships
```

For ARM64, add `-p:Platform=ARM64` (plus `-r win-arm64` when publishing). CI (`.github/workflows/build.yml`) publishes both architectures, verifies the bundled `pdfium.dll`, `Folio.pri` and license files, and runs the tests.

The tests build the UI-free code (`src/Folio/Pdf`, `Services/LaunchPolicy.cs`) against the real PDFium and generate their own PDFs.

The app is unpackaged and self-contained: it bundles .NET and the Windows App SDK 2.5 runtime. It references only the WinUI and Foundation components, not the `Microsoft.WindowsAppSDK` meta-package (whose AI/ML parts add ~60 MB). `dotnet publish` trims the output, so test a published build after adding reflection-based code. PDFium comes from the `bblanchon.PDFium.Win32` package.

For the Store package, see [Packaging](packaging.md).

## Source layout

| Path | Purpose |
| --- | --- |
| `src/Folio/Pdf` | PDFium interop, the single-threaded work queue, and the document model |
| `src/Folio/Controls/DocumentView.cs` | Virtualized, zoomable page surface (pages laid out at 100%; ScrollViewer zoom is the zoom level), selection, links, annotation flyouts |
| `src/Folio/Controls/PageView.cs` | One page: bitmap, zoomed-in tiles, highlight overlay |
| `src/Folio/MainWindow.xaml` | Title bar, sidebar, start page |
| `src/Folio/Services` | Persisted state, printing, single-instance handoff, link and attachment policy |
| `src/Folio/Package.appxmanifest`, `Assets/Package` | MSIX manifest and logos |
| `src/Folio/Program.cs` | Entry point; forwards files to a running instance |
| `tests/Folio.Tests` | xUnit tests |
| `LICENSE`, `licenses/`, `THIRD-PARTY-NOTICES.md` | License texts, copied into the build output |
