<p align="center">
  <img alt="Recto hero image" src="./.github/assets/hero.png" />
</p>

A simple PDF viewer for Windows 11, modeled on [GNOME Papers](https://apps.gnome.org/Papers/) and built with WinUI 3.

## Building from source

```powershell
dotnet build src/Recto/Recto.csproj -c Release
```

## Performance

<p align="center">
  <img alt="Time to first page and memory use of Recto, Microsoft Edge, Adobe Acrobat and SumatraPDF, and File Explorer thumbnail time of Recto and PowerToys" src="./.github/assets/benchmark.png" />
</p>

Measured with the [benchmark tool](docs/tools.md#benchmark). It's not a rigorous benchmark: an app can't be asked when its first page is ready, so the tool watches the screen for it and the times are only approximate. Take the numbers with a grain of salt. They reflect the overall experience more than exact speed.

## Credits

- [GNOME Papers](https://apps.gnome.org/Papers/) for inspiration
- [PDFium](https://pdfium.googlesource.com/pdfium/) for PDF rendering

## License

[MIT License](LICENSE) · [Third-party notices](THIRD-PARTY-NOTICES.md) · [Privacy policy](PRIVACY.md)
