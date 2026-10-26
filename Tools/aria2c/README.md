# aria2c runtime asset

The Catalog downloader looks for a license-reviewed `aria2c.exe` at:

```text
Tools/aria2c/aria2c.exe
```

When present, the application uses aria2c for segmented/resumable downloads. If
the executable is absent or exits with an error, the application automatically
falls back to its built-in HttpClient downloader.

Before placing a binary in this directory, record its exact upstream version,
source URL, architecture, SHA-256, and the applicable aria2 license/NOTICE
text in the release artifact. Do not copy an unverified executable into a
published build.
