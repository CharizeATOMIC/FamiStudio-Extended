# FamiStudio Tuning

An independent desktop fork of [FamiStudio 4.5.3](https://github.com/BleuBleu/FamiStudio/releases/tag/4.5.3), based on commit `c09dca64788606c09fcb5e70db28b9dd1f34be78`. This is not an official FamiStudio release. Original copyright and license notices remain in place.

## Features

- Song tuning overrides the project reference; timeline pattern-column tuning overrides the song. A disabled override inherits its parent. Overrides are complete snapshots containing A4 reference and twelve cents offsets, repeated in every octave.
- A4 range: 300–580 Hz in 1 Hz steps. C through B offsets: −1200 to +1200 cents in 1-cent steps. The A offset also affects the reference note. Note names and existing channel octave conventions are unchanged.
- Custom tables select the closest realizable pitch in cents, within each chip's register limits. VRC7 and EPSM FM search the block/f-number combinations. N163 and EPSM preserve the extra two frequency bits omitted by the original common pitch-table representation.
- Song and pattern properties append controls to their original scrollable page. Expansion checkboxes show a quantity stepper on the same row when enabled. Each expansion can have 1–8 copies, with a total limit of 64 channels. The base APU remains single.
- Each song stores stereo enable and pan per channel. Pan is 0% left, 50% center, 100% right, using unity center gain. Playback and keyboard preview follow these values. Changes in the song dialog preview immediately; Cancel restores them. EPSM's instrument stereo remains active, with the song pan applied as a balance.
- All seven existing languages include the new controls and messages: English, Chinese, German, Spanish, Portuguese, Russian and Korean.

## Format and installation

The Windows x64 package is self-contained: extract the whole directory and run `FamiStudio.exe`. It does not require installing .NET. Settings use `%LOCALAPPDATA%/FamiStudio-Tuning`; the installed official application and its settings are not replaced. Upstream update notifications default to off because official releases do not include these additions.

Official project formats through version 19 can be read. Saved fork projects use version **1001** and cannot be opened by the official application. Save a copy of an existing project before editing it in this fork. Clipboard data has a separate namespace. FamiStudio text exports preserve tuning, mixer and chip-copy metadata.

## Export support

| Output | Song / pattern tuning | Extra copies of the same chip | Song stereo / pan |
| --- | --- | --- | --- |
| WAV / MP3 / OGG and video audio | Yes | Yes | Yes |
| FamiStudio binary / text project | Preserved | Preserved | Preserved |
| FamiStudio sound engine / NSF / NSFe | Yes, with the modified engine | Rejected | Not encoded |
| NES / FDS images | Yes, with rebuilt bundled engine | Rejected | Not encoded |
| VGM register stream | Pitch register writes retained | Rejected | Not encoded |
| FamiTone2 engine, FamiTracker text, MIDI | Custom song/pattern tuning is rejected | Rejected | Not encoded |

Noise, DPCM and EPSM rhythm voices retain their hardware/sample pitch behavior. They do not offer arbitrary chromatic frequency registers. Instrument FM ratios, pitch envelopes, modulation and N163 waveform-length behavior remain part of the instrument sound. Out-of-range pitched notes clamp to the nearest supported register value. Enabling stereo separates channel stems; the result is not guaranteed to reproduce the original mono nonlinear mix exactly. Very loud combined chip copies can clip, so lower instrument or mixer levels as needed. The register viewer displays the first copy of each chip.

## Sound engine integration

Use the engine sources distributed with this fork together with music exported by this fork. In the engine configuration, before including the source, enable:

```asm
FAMISTUDIO_USE_CUSTOM_TUNING = 1
```

Keep the other options emitted in the export's header, including PAL/NTSC and expansion settings. CA65, ASM6, NESASM and SDAS single-expansion sources are supported; the multi-expansion source is CA65. The bundled NSF, NES and FDS kernels already enable this option.

The new `$5f` music-stream instruction supplies three 16-bit pointers: period low byte, period high byte and fractional hardware bits. Tables have 97 entries including the stop entry and are deduplicated per export. Dual-region data includes PAL and NTSC tables. Every pitched channel receives the effective tuning at a pattern boundary, even for an empty pattern, so held notes, loops and seeks retain the correct tuning. Track initialization clears the override pointers. Slides use the active tables.

The single-expansion engine adds six bytes per pitch-envelope channel in BSS and nine bytes in zero page when the option is enabled. There is additional ROM data and CPU work; integrators must budget their own game's frame time and memory. The option defaults to zero when building the standalone engine. Custom-tuning music is incompatible with an unmodified engine. Existing global project tuning can still emit the original replacement `.bin` tables; follow the export header for that case as well.

The FDS driver now places its TOC at `$7600` and song data at `$7800`, leaving `$4800` bytes for one song before the DPCM area. Its linker configuration and exporter agree on these addresses.

## Architecture

`TuningSettings` holds validated inheritance snapshots. `Song`, pattern-column settings, `Channel` and `Project` serialize tuning, pan and copy counts through binary, text and undo paths. `CustomTuningTables` is shared by playback and export. `FamitoneMusicFile` emits tuning changes and the modified 6502 engine consumes them.

Additional expansion copies use independently allocated native emulator contexts. State routing includes channel type and copy number. Per-channel stereo stems forward all register writes, retaining shared chip state, and mix their output at the configured balance. N163 automatic wave placement runs independently for each copy. Muting, clipboard ownership, project merging and separate audio filenames include the copy identity.

## Building and checking

Windows prerequisites: .NET 8 SDK, Visual Studio C++ build tools, Python 3, and the assemblers already in `Tools/`.

```powershell
msbuild ThirdParty/NesSndEmu/SndEmu.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 /p:OutDir="<absolute repository path>/FamiStudio/"
python Tools/build_tuning_nsf.py
python Tools/build_tuning_rom.py
dotnet build FamiStudio/FamiStudio.csproj -c Release
dotnet run --project Tests/TuningRegression -c Release -- work/tuning-tests
python Tests/check_engine_assemblers.py
python Tests/check_legacy.py <absolute path to dotnet.exe>
dotnet publish FamiStudio/FamiStudio.csproj -c Release -r win-x64 --self-contained true -o work/publish
```

Set `PlatformToolset` to the C++ toolset installed on your machine. UI smoke tests require an OpenGL-capable Windows session:

```powershell
dotnet run --project Tests/TuningRegression -c Release -- --ui zho work/ui-tests
dotnet run --project Tests/TuningRegression -c Release -- --demo work/examples
python Tests/check_export_assemblers.py work/examples/SoundEngine
```

The UI harness renders actual application dialogs in a hidden application window, without desktop screenshots or browser control. Repeat for `eng`, `deu`, `spa`, `por`, `rus`, `kor`. Linux and macOS project files include the managed changes and isolated settings paths, but this delivery builds and validates **Windows x64 only**. Rebuild the native audio library before testing other platforms; the upstream prebuilt libraries do not expose the new functions. Mobile is not a validated target.

## Verification and limits

The local regression suite passes 1,506 assertions. Coverage includes serialization, inheritance, cents quantization, every expansion's NSF pitch registers in PAL/NTSC modes, dual-region export, held notes, slides, vibrato, loop/track tuning resets, duplicate-chip stereo isolation, N163 wave allocation, keyboard preview, clipboard ownership, project merging and the 64-channel boundary. Ten upstream register-trace fixtures remain byte-identical. All four assemblers produce identical executable bytes for 32 engine configurations. All seven UI languages render; the UI test also exercises live playback changes and Cancel restoration.

NSF behavior is executed in the bundled 6502 emulator and compared with editor register output. NES/FDS images are built and exported successfully, but have not been boot-tested on physical hardware. This is a locally tested development fork, not an upstream-reviewed release. Long sessions, every third-party NSF player and every operating system have not been validated.

## Contributing upstream

See `UPSTREAM_PROPOSAL.txt` for a prepared discussion draft. Nothing has been sent or published by this local build. The proposal separates hardware-compatible tuning from desktop-only multi-chip mixing so maintainers can choose the scope they want to review.
