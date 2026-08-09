# Ghidra adapter operational note

Use the explicit `kiriscope analyze ghidra` command documented in [the integration guide](../docs/integrations/GHIDRA_HEADLESS.md). Do not copy a Ghidra distribution into this repository.

The adapter is verified against a locally installed Ghidra 12.1.2 PUBLIC distribution on Windows. It starts batch launchers through `cmd.exe /d /c`, derives version identity from `Ghidra/application.properties`, and creates a fresh project/archive only. The generated project belongs in a dedicated research output directory, never beside or inside the input game directory.

When updating Ghidra, repeat a disposable-project run and retain the generated KiriScope JSON archive with the test record. An external-tool upgrade does not change any game or filter compatibility claim by itself.
