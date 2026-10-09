# Packaging

The Store build is an MSIX package (`src/Folio/Package.appxmanifest`) that registers Folio for `.pdf` files. It bundles .NET; the Store installs the Windows App SDK framework. When packaged, state and extracted attachments live in the package's own folders, since MSIX hides `%LOCALAPPDATA%` writes from other apps.

```powershell
dotnet publish src/Folio/Folio.csproj -c Release -p:Platform=x64 -r win-x64 -p:WindowsPackageType=MSIX
dotnet publish src/Folio/Folio.csproj -c Release -p:Platform=ARM64 -r win-arm64 -p:WindowsPackageType=MSIX
```

Building the package also needs Visual Studio's C++ build tools ("Desktop development with C++", plus the ARM64 tools for the ARM64 package): it includes the File Explorer thumbnail handler, `src/Folio.Thumbnails`, a COM server published with Native AOT. The manifest registers it for `.pdf` in a COM surrogate (`dllhost.exe`), and it renders page 1 with the package's `pdfium.dll`. Windows uses it only while packaged Folio is the default PDF app, since packaged handlers attach to the app's own file type, not to `.pdf` itself.

Packages land in `out\AppPackages\`. CI's `msix` job combines them into `Folio_<version>.msixbundle` for Partner Center. They're unsigned; the Store signs them.

To test locally, enable Developer Mode and run `Add-AppxPackage -Register src\Folio\bin\msix\x64\Release\net10.0-windows10.0.26100.0\win-x64\AppxManifest.xml`. Remove with `Get-AppxPackage Folio | Remove-AppxPackage`. To see thumbnails, make Folio the default PDF app in Settings > Apps > Default apps; Explorer may keep showing icons it already cached until you clear thumbnails in Disk Cleanup.

Before the first submission, reserve the name in Partner Center and copy `Name`, `Publisher` and `PublisherDisplayName` from Product identity into the manifest. The version is the manifest's `Identity Version` (last part must be 0), not the project's `<Version>`.
