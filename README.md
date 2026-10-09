# UtaFormatix3 for .NET

A C# port of [UtaFormatix3](https://github.com/sdercolin/utaformatix3) — a tool for converting projects between singing voice synthesizer formats.

This is a cross-platform console application built with .NET 10 and Native AOT.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Build

```bash
dotnet build
```

## Run

```bash
dotnet run --project src/UtaFormatix.App
```

## Publish (Native AOT)

```bash
dotnet publish src/UtaFormatix.App -c Release
```

The native executable will be placed in `.artifacts/publish/UtaFormatix.App/release/`.

## License

Apache License 2.0. See [LICENSE](LICENSE) and [NOTICE](NOTICE).
