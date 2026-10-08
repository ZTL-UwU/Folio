# Features

- **Start page**: recent documents as thumbnail cards; drag and drop PDFs onto the window.
- **Viewer**: continuous or single-page, dual pages (optionally odd on the left), fit page / fit width / custom zoom, Ctrl+wheel and pinch zoom, rotation, night mode, full screen, presentation mode.
- **Sidebar**: thumbnails, outline, annotations, attachments.
- **Search**: results with snippets in the sidebar; match case, whole words; F3 / Shift+F3.
- **Text**: select, copy, double-click a word; internal and web links.
- **Annotations**: five highlight colors and sticky notes; edit, delete, and save into the PDF as an incremental update, leaving the original bytes (and signatures) intact.
- **Printing** with page ranges, a **Properties** dialog, and **password-protected** documents.
- Remembers page, zoom and view mode per document, and reloads when the file changes on disk (encrypted files reuse the session password).
- One process per session: PDFs opened from Explorer open as new windows in the running instance.

Press F1 for keyboard shortcuts.

## Limitations

- **Forms** aren't interactive; saved values show only if the file includes appearances.
- **Signatures** are shown but not validated. After saving annotations, a signature covers only the earlier revision.
- **JavaScript** and **launch actions** are ignored. Only `http`, `https` and `mailto` links open, and never ones with a user name (`https://bank.example@evil.example/`). Link hover shows the host in ASCII to expose look-alike names.
- **Permission flags** (no copy/print/modify) aren't enforced, as in Papers and Evince: they don't protect the content and get in the way of legitimate use.
- **Attachments**: PDFs open in Folio; other files open in their default app after confirmation. Executables, scripts, shortcuts and installers are only saved, never opened, and types Windows parses on folder view (`.url`, `.lnk`, `.scf`, `.library-ms`, …) get `.txt` appended. Extracted files get the Mark of the Web, and aren't opened if the drive can't store it.
- **Size**: files up to 8 GB, up to 1,000,000 pages, attachments up to 2 GB.
- **Screen readers** get the page number, but page text isn't exposed to UI Automation yet.
