# PdfCompressor

makes your pdfs smaller. drag and drop a pdf, hit compress, pick where to save it, done.

![icon](https://img.shields.io/badge/platform-windows-0078d6)

## what you get

- drag and drop pdfs, or add a whole folder of them
- 3 presets: screen (smallest), ebook (balanced), printer (keeps quality)
- the save button stays locked until compression actually finishes
- one single exe at the end, no installs needed on other machines

## why this exists

some pdfs are just too big and email hates them. that's the whole reason.

## building it

stuff you need:

- .NET 8 SDK
- `gsdll64.dll` in `PdfCompressor\Assets\`

grab the dll from the ghostscript releases page:

https://github.com/ArtifexSoftware/ghostpdl-downloads/releases

(windows 64-bit installer, extract gsdll64.dll from the bin folder).
or fetch it automatically:

```
powershell -ExecutionPolicy Bypass -File scripts\download-engine.ps1
```

then run `build.bat`. it drops the finished exe in `Standalone\`.

## how it works

under the hood it's ghostscript (the `pdfwrite` device) talking through the Ghostscript.NET wrapper. the engine dll gets embedded into the exe and extracted to a temp folder on first run, so the app is always just one file.

## license

AGPL-3.0, because ghostscript is AGPL and this wouldn't exist without it. if you ship this thing you have to open up your source too.
