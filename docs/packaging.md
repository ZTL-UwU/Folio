# Packaging

The Store build is an MSIX package (`src/Recto/Package.appxmanifest`) that registers Recto for `.pdf` files. It bundles .NET; the Store installs the Windows App SDK framework. When packaged, state and extracted attachments live in the package's own folders, since MSIX hides `%LOCALAPPDATA%` writes from other apps.

```powershell
dotnet publish src/Recto/Recto.csproj -c Release -p:Platform=x64 -r win-x64 -p:WindowsPackageType=MSIX
dotnet publish src/Recto/Recto.csproj -c Release -p:Platform=ARM64 -r win-arm64 -p:WindowsPackageType=MSIX
```

Like any publish, building the package needs Visual Studio's C++ build tools ("Desktop development with C++", plus the ARM64 tools for the ARM64 package), as Recto and the File Explorer thumbnail handler it includes, `src/Recto.Thumbnails`, are compiled with Native AOT. The handler is a COM server. The manifest registers it for `.pdf` in a COM surrogate (`dllhost.exe`), and it renders page 1 with the package's `pdfium.dll`. Windows uses it only while packaged Recto is the default PDF app, since packaged handlers attach to the app's own file type, not to `.pdf` itself.

Packages land in `out\AppPackages\`. CI's `msix` job combines them into `Recto_<version>.msixbundle` for Partner Center. They're unsigned; the Store signs them.

To test locally, enable Developer Mode, unpack the package and register the unpacked copy. Don't register the `AppxManifest.xml` under `bin\msix`: that folder has the JIT build, without the Native AOT `Recto.exe` the manifest starts, so the app is listed but won't launch.

```powershell
Expand-Archive out\AppPackages\Recto_1.0.0.0_x64_Test\Recto_1.0.0.0_x64.msix out\msix-layout\x64 -Force
Add-AppxPackage -Register out\msix-layout\x64\AppxManifest.xml
```

Remove with `Get-AppxPackage TonyZhang.RectoPDF | Remove-AppxPackage`. To see thumbnails, make Recto the default PDF app in Settings > Apps > Default apps; Explorer may keep showing icons it already cached until you clear thumbnails in Disk Cleanup.

The manifest's `Name`, `Publisher` and `PublisherDisplayName` are the ones on the app's Product identity page in Partner Center; keep them in step if that ever changes. The version is the manifest's `Identity Version` (last part must be 0), not the project's `<Version>`.
