# Building

Requires Windows 11 and the .NET 10 SDK. Publishing also needs Visual Studio's C++ build tools ("Desktop development with C++", plus the ARM64 tools for ARM64), which Native AOT links with.

```powershell
dotnet build src/Folio/Folio.csproj -c Release
src\Folio\bin\Release\net10.0-windows10.0.26100.0\win-x64\Folio.exe [file.pdf]
dotnet test tests/Folio.Tests
dotnet publish src/Folio/Folio.csproj -c Release -o out\win-x64   # Native AOT, as CI ships
```

For ARM64, add `-p:Platform=ARM64` (plus `-r win-arm64` when publishing). CI (`.github/workflows/build.yml`) publishes both architectures, verifies the bundled `pdfium.dll`, `Folio.pri` and license files, and runs the tests.

The tests build the UI-free code (`src/Folio/Pdf`, `Services/LaunchPolicy.cs`, the thumbnail handler in `src/Folio.Thumbnails`) against the real PDFium and generate their own PDFs.

The app is unpackaged and self-contained: it bundles .NET and the Windows App SDK 2.5 runtime. It references only the WinUI and Foundation components, not the `Microsoft.WindowsAppSDK` meta-package (whose AI/ML parts add ~60 MB). `dotnet publish` compiles it with Native AOT, which trims the output and can't JIT, so test a published build after adding reflection-based code; classes that WinUI holds on to (items, controls) must be `partial`. PDFium comes from the `bblanchon.PDFium.Win32` package.

To try a build as your PDF app, measure performance or rebuild the icon, see [Tools](tools.md). For the Store package, see [Packaging](packaging.md).

## Source layout

| Path | Purpose |
| --- | --- |
| `src/Folio/Pdf` | PDFium interop, the single-threaded work queue, and the document model |
| `src/Folio/Controls/DocumentView.cs` | Virtualized, zoomable page surface (pages laid out at 100%; ScrollViewer zoom is the zoom level), selection, links, annotation flyouts |
| `src/Folio/Controls/PageView.cs` | One page: bitmap, zoomed-in tiles, highlight overlay |
| `src/Folio/MainWindow.xaml` | Title bar, sidebar, start page |
| `src/Folio/Printing`, `PrintDialog.xaml` | Print dialog, page layout on sheets (unit tested), printers and GDI print jobs |
| `src/Folio/Services` | Persisted state, the Windows print dialog, single-instance handoff, link and attachment policy, the app language |
| `src/Folio/Strings` | The app's text, one `Resources.resw` per language ([Translations](#translations)) |
| `src/Folio/Package.appxmanifest`, `Assets/Package` | MSIX manifest and logos |
| `src/Folio/Program.cs` | Entry point; forwards files to a running instance |
| `src/Folio.Thumbnails` | File Explorer thumbnail handler (Native AOT COM DLL, MSIX and dev builds only) |
| `tests/Folio.Tests` | xUnit tests |
| `tools` | Developer tools: build-and-try script, benchmark, icon build ([Tools](tools.md)) |
| `LICENSE`, `licenses/`, `THIRD-PARTY-NOTICES.md` | License texts, copied into the build output |

## Translations

All text shown in the app is in `src/Folio/Strings/<language>/Resources.resw`; `en-US` is the source and the fallback for languages Folio doesn't have. XAML gets its strings with `x:Uid` (`ZoomInButton.[using:Microsoft.UI.Xaml.Controls]ToolTipService.ToolTip`), code with `Strings.Get("Name")`, `Strings.Format` for `{0}` placeholders and `Strings.Plural` for counts (`Name_One` and `Name_Other`). The tests check that every language has the same strings with the same placeholders, and that the names used in code and XAML exist.

Translations are made on [Crowdin](https://crowdin.com); only `en-US` is edited in the repo. The [Crowdin workflow](../.github/workflows/crowdin.yml) uploads it when it changes on `main`, and every Monday opens or updates a pull request from the `l10n` branch with the translations (strings not translated yet are in English). It can also be run by hand from the Actions tab. Its settings are in [crowdin.yml](../crowdin.yml), and it needs these repository secrets: `CROWDIN_PROJECT_ID`, `CROWDIN_PERSONAL_TOKEN`, and `CROWDIN_GITHUB_TOKEN`, a personal access token with read and write access to contents and pull requests. Without that token the pull request is opened with the workflow's own token, which doesn't start the Build workflow.

To add a language, add it to the Crowdin project. Once its pull request is merged, add its folder's tag and its own name to `AppLanguage.Available`, which makes it selectable in Settings; until then the translation is in the repo but not offered. Check that its `LanguageTag` is the culture it's written for. `Strings.Plural` only knows when English and French use the singular; teach it the new language's rule if that differs. Languages with more than two plural forms, such as Russian or Polish, need more than `_One` and `_Other`. The language picked in Settings applies when Folio next starts.
