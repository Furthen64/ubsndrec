# ubsndrec

PipeWire speaker capture as a .NET 10 CLI.

## Requirements

- .NET 10 SDK
- PipeWire tools: `pw-record` and `pw-link`
- `wpctl` from WirePlumber for richer default-sink detection (optional, but recommended)

## Modes

### Wizard

Guides you through sink selection, output file selection, and whether to capture stereo, left, or right.

```bash
dotnet run -- wizard
```

### Record

Records immediately with the provided options.

```bash
dotnet run -- record
dotnet run -- record capture.wav
dotnet run -- record --output capture.wav --channel left
dotnet run -- record --sink alsa_output.pci-0000_07_00.6.analog-stereo --channel right
```

You can also keep the old shell-style entrypoint:

```bash
./speaker_capture.sh capture.wav
```

### List sinks

Shows the available PipeWire sinks and marks the detected default sink when possible.

```bash
dotnet run -- list-sinks
```

## Help

```bash
dotnet run -- --help
```

## Legacy shorthand

Passing only an output file keeps the old script behavior and starts recording immediately with the default sink in stereo.

```bash
dotnet run -- capture.wav
```
