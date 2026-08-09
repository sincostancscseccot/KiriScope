# FreeMote TLG conversion adapter

## Scope

`kiriscope convert tlg-to-png <input-tlg> <output-png> --freemote <EmtConvert.exe>` provides an optional compatibility path for TLG files that the native decoder does not yet support, including TLG6. The user must provide the executable path explicitly; KiriScope does not assume an installation location or copy the tool into this repository.

## Validated environment

- Tool: FreeMote Toolkit 4.5.1, `EmtConvert.exe`
- Source: the user-managed `Reverse_Tools` directory
- Runtime: .NET Framework 4.8 (from the executable configuration)
- Capability: converts a TLG input to a PNG next to the supplied input path

## Safety and result handling

KiriScope copies the input TLG to an isolated temporary directory before invoking the tool, so any adjacent output created by FreeMote cannot modify the original input. It captures standard output, standard error, and exit code; enforces a two-minute timeout; requires the expected temporary PNG; validates it with KiriScope's PNG validator; and moves it to the user-selected destination without overwrite. The temporary directory is removed after every completion, failure, or timeout.

The adapter's success means `FormatValidated` for the exported PNG. It does not raise the original TLG's native-decoder evidence level, and it does not claim support for encrypted or nonstandard TLG variants.
