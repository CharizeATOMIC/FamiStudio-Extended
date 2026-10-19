# FamiStudio Tuning

An independent desktop fork of [FamiStudio 4.5.3](https://github.com/BleuBleu/FamiStudio/releases/tag/4.5.3), based on commit `c09dca64788606c09fcb5e70db28b9dd1f34be78`. This is revision 5 of the local fork, not an official FamiStudio release. Original copyright and license notices remain in place.

## Features

- Song tuning overrides the project reference; timeline pattern-column tuning overrides the song. A disabled override inherits its parent. Overrides are complete snapshots containing A4 reference and twelve cents offsets, repeated in every octave.
- A4 range: 300–580 Hz in 1 Hz steps. C through B offsets: −1200 to +1200 cents in 1-cent steps. The A offset also affects the reference note. Note names and existing channel octave conventions are unchanged.
- Revision 3 adds project-wide tuning curves for each chip: 96 independently adjustable notes expressed as absolute integer hardware timer, period or frequency values. Graph dragging uses the original FamiStudio value ±50, clipped by the selected hardware representation's limits. Manual entry can exceed that band within those limits. VRC7 retains each note's default block as detailed below. Copies of a chip share its curve; different octaves remain independent. Reset/default points preserve the existing audio path and unedited table entries.
- Open the curve editor from the original project-info page. The complete 96-key horizontal keyboard shares the piano roll's native key geometry, palette and drawing helpers. Small arpeggio-like blocks use an axis from -50 through 0 to +50 relative to each note's default, so an unchanged curve is flat. Each block labels its absolute integer hardware value. Manual values outside this band retain their exact value and label, with only the drawn block clipped at the edge. Zoom and horizontal scrolling make individual notes easier to select. Child-dialog edits take effect only when the project dialog is accepted, and project undo restores them.
- Custom tables select the closest realizable pitch in cents, within each chip's register limits. VRC7 and EPSM FM search the block/f-number combinations. N163 and EPSM preserve the extra two frequency bits omitted by the original common pitch-table representation.
- Song and pattern properties append controls to their original scrollable page. Expansion checkboxes show a quantity stepper on the same row when enabled. Each expansion can have 1–8 copies, with a total limit of 64 channels. The base APU remains single.
- Each song stores stereo enable and pan per channel. Pan is 0% left, 50% center, 100% right, using unity center gain. Playback and keyboard preview follow these values. Changes in the song dialog preview immediately; Cancel restores them. EPSM's instrument stereo remains active, with the song pan applied as a balance.
- All seven existing languages include the new controls and messages: English, Chinese, German, Spanish, Portuguese, Russian and Korean.
- Revision 2 enlarges numeric hit targets. Click a table's number to type cents or chip count; Enter accepts and Escape cancels. Shift-click steps by ten. Typed pan values preview during playback. Reference-Hz fields remain directly editable.
- Revision 3 replaces the NSF/NSFe song selector with a checklist. Import several subsongs from one file into one project, with a separate editable duration for each selection and Select All / Select None. Import progress can be cancelled. This retains the existing register-capture importer; it does not reconstruct a game's source instruments or sequencer commands.

## Revision 4: instrument and sequence fidelity

- Optional APU noise macros expose period/mode, hardware envelope control and explicit restart writes. A single chromatic trigger can play a complete percussion instrument; an empty macro preserves the original behavior. Hardware decay runs in the APU rather than being approximated by one software volume per video frame.
- The envelope-decay effect freezes the current software volume envelope and reduces its level every N video frames. Zero resumes the envelope. A fresh attack resets the effect, and a real volume release section cancels it. Hardware volume envelopes take precedence; EPSM rhythm voices do not support this effect.
- Pattern properties add playback delay in video frames and an optional loop-variant pattern. Delay retains the score's note positions while carrying notes, releases and stops across boundaries. A variant is selected only after the song actually returns to its loop point; selection-loop playback retains the first-pass score. The sequencer's Edit Loop Variant command opens the alternate notes in the existing piano roll, including undo, redo, clipboard and persistence.
- FamiStudio-engine export compiles delayed/variant note events into ordinary instrument and effect commands. The editor's loop markers stay at their musical boundaries. A separate first-pass stream may still be needed in the exported engine data when the introduction leaves different articulation state. This is not a per-frame register recording.

The new engine options are `FAMISTUDIO_USE_NOISE_ENVELOPES = 1` and `FAMISTUDIO_USE_ENVELOPE_DECAY = 1`; follow the flags printed in the music export header. Stream instructions `$60` and `$61` require this fork's matching engine. Both options default to zero in standalone sources; bundled NSF/NES/FDS kernels enable them. Decay adds two bytes per envelope channel. The assembler matrix checks 128 configurations across CA65, ASM6, NESASM and SDAS; 64 configurations with decay disabled retain their previous executable bytes.

Sound-engine export of playback delay requires FamiStudio tempo. Loop variants without delay also support FamiTracker tempo. Unsupported FamiTone2 combinations and FamiTracker delayed-note/cut combinations are rejected with an export error. This is an explicit compatibility boundary, not a conversion to a different tempo.

FamiTracker text and MIDI export reject the new delay, loop-variant, decay and hardware-noise semantics instead of silently dropping them. VGM export rejects pattern delay and loop variants because its current loop construction cannot preserve their boundary behavior; ordinary noise/decay register playback remains available.

## Format and installation

Revision 5 adds two optional regular-instrument properties. **Reset phase on attack** applies to the APU pulse channels and uses the normal timer-high write once per attack, including attacks that also cross a high-byte boundary. Legato, release and stop do not trigger it. Explicit note phase-reset effects retain their separate behavior. **Clamp pitch to timer byte** applies to APU pulse and triangle channels: pitch-envelope modulation saturates the timer's low byte instead of carrying or borrowing into its high byte. The base timer still includes tuning, fine pitch and slides. Both properties default to off, have controls in all seven languages, and survive binary/text, clipboard and undo operations.

These properties export through `FAMISTUDIO_USE_APU_INSTRUMENT_FLAGS = 1` and instruction `$62`. It adds three bytes of channel state and defaults to disabled in standalone engine sources; use the matching engine and the options emitted in the export header. Bundled NSF/NES/FDS kernels enable it. EPSM custom-table builds share table lookup code to preserve the existing 32-song ROM capacity. FamiTracker/FamiTone2/MIDI exporters reject the new semantics when used; VGM records their register playback normally. Duty-cycle text export also preserves explicit zero and disabled envelope structures without changing their existing playback meaning.

The Windows x64 package is self-contained: extract the whole directory and run `FamiStudio.exe`. It does not require installing .NET. Settings use `%LOCALAPPDATA%/FamiStudio-Tuning`; the installed official application and its settings are not replaced. Upstream update notifications default to off because official releases do not include these additions.

Official project formats through version 19 and this fork's previous formats 1001, 1002 and 1003 can be read. Revision 5 saves format **1004**, which cannot be opened by the official application or earlier fork builds. Save a copy of an existing project before editing it in this fork. Clipboard data has a separate namespace. FamiStudio text exports preserve tuning, curves, mixer and chip-copy metadata. Curves use sparse project-level `ChipTuning` entries with a chip name, note ID and integer `Value`, plus `ChipTuningBasis` reference information.

Curves are global to a project. Importing songs from a project with different curves on its active chips is rejected before either song is retuned; first make the curves match. An empty destination adopts the imported curves. Importing instruments alone does not require matching curves. Curves for inactive chips are preserved for when those chips are enabled.

Each curve retains its reference Hz, PAL/NTSC clock and N163 channel count. A fresh curve starts from the current project settings; existing overrides retain their saved basis. Playback derives pitch from the stored hardware value, applies the effective song/pattern reference ratio and twelve-note cents temperament, and chooses the nearest register value for the playing region. The song/pattern temperament controls still use cents; the project curve uses integer hardware values.

| Chip | Curve's canonical hardware unit |
| --- | --- |
| APU / MMC5 | Pulse timer; the APU triangle keeps its existing octave relationship |
| VRC6 | Pulse timer; saw uses the nearest value for the resulting pitch |
| S5B | Actual tone-period register, including the internal table's +1 adjustment |
| FDS | 12-bit frequency word |
| N163 | Full 18-bit frequency word at the saved channel-count basis |
| VRC7 | F-number at the displayed default block: 1–511 for block 0, 256–511 for higher blocks; these canonical bounds retain the block |
| EPSM | PSG tone period; FM uses the nearest value for the resulting pitch |

## Export support

| Output | Song / pattern tuning and project chip curves | Extra copies of the same chip | Song stereo / pan |
| --- | --- | --- | --- |
| WAV / MP3 / OGG and video audio | Yes | Yes | Yes |
| FamiStudio binary / text project | Preserved | Preserved | Preserved |
| FamiStudio sound engine / NSF / NSFe | Yes, with the modified engine | Rejected | Not encoded |
| NES / FDS images | Yes, with rebuilt bundled engine | Rejected | Not encoded |
| VGM register stream | Pitch register writes retained | Rejected | Not encoded |
| FamiTone2 engine, FamiTracker text, MIDI | Custom tuning and active chip curves are rejected | Rejected | Not encoded |

Noise, DPCM and EPSM rhythm voices retain their hardware/sample pitch behavior. They do not offer arbitrary chromatic frequency registers. Instrument FM ratios, pitch envelopes, modulation and N163 waveform-length behavior remain part of the instrument sound. Out-of-range pitched notes clamp to the nearest supported register value. Enabling stereo separates channel stems; the result is not guaranteed to reproduce the original mono nonlinear mix exactly. Very loud combined chip copies can clip, so lower instrument or mixer levels as needed. The register viewer displays the first copy of each chip.

## Sound engine integration

Use the engine sources distributed with this fork together with music exported by this fork. In the engine configuration, before including the source, enable:

```asm
FAMISTUDIO_USE_CUSTOM_TUNING = 1
```

Keep the other options emitted in the export's header, including PAL/NTSC and expansion settings. CA65, ASM6, NESASM and SDAS single-expansion sources are supported; the multi-expansion source is CA65. The bundled NSF, NES and FDS kernels already enable this option.

The fork's `$5f` music-stream instruction supplies three 16-bit pointers: period low byte, period high byte and fractional hardware bits. Revision 3 curves use this existing instruction and require no further assembly-engine or embedded-driver changes. Tables have 97 entries including the stop entry and are deduplicated per export. Dual-region data includes PAL and NTSC tables. Every pitched channel receives the effective tuning at a pattern boundary, even for an empty pattern, so held notes, loops and seeks retain the correct tuning. Track initialization clears the override pointers. Slides use the active tables.

The single-expansion engine adds six bytes per pitch-envelope channel in BSS and nine bytes in zero page when the option is enabled. There is additional ROM data and CPU work; integrators must budget their own game's frame time and memory. The option defaults to zero when building the standalone engine. Custom-tuning music is incompatible with an unmodified engine. Existing global project tuning can still emit the original replacement `.bin` tables; follow the export header for that case as well.

The FDS driver now places its TOC at `$7700` and song data at `$7900`, leaving `$4700` bytes for one song before the DPCM area. Its linker configuration and exporter agree on these addresses.

## Architecture

`TuningSettings` holds validated inheritance snapshots. `ChipTuningSettings` holds the project-global 8 × 96 integer override matrix and its reference basis; an unset point uses the original table. `Song`, pattern-column settings, `Channel` and `Project` serialize tuning, curves, pan and copy counts through binary, text and undo paths. `CustomTuningTables` derives the target pitch from the selected inheritance snapshot and chip curve, then quantizes once; playback, keyboard preview and export share it. `FamitoneMusicFile` emits tuning changes and the modified 6502 engine consumes them.

`NsfFile.LoadSongs` captures selected tracks into separate temporary projects, gives resources collision-safe identities, and merges the results into one project. A failed or cancelled batch does not return a partially imported project. This path is separate from the optional, local game-specific conversion work.

Additional expansion copies use independently allocated native emulator contexts. State routing includes channel type and copy number. Per-channel stereo stems forward all register writes, retaining shared chip state, and mix their output at the configured balance. N163 automatic wave placement runs independently for each copy. Muting, clipboard ownership, project merging and separate audio filenames include the copy identity.

## Building and checking

Windows prerequisites: .NET 8 SDK, Visual Studio C++ build tools, Python 3, and the assemblers already in `Tools/`.

```powershell
msbuild ThirdParty/NesSndEmu/SndEmu.vcxproj /p:Configuration=Release /p:Platform=x64 /p:PlatformToolset=v145 /p:OutDir="<absolute repository path>/FamiStudio/"
python Tools/build_tuning_nsf.py
python Tools/build_tuning_rom.py
dotnet build FamiStudio/FamiStudio.csproj -c Release
dotnet run --project Tests/TuningRegression -c Release -- work/tuning-tests
dotnet run --project Tests/TuningRegression -c Release -- --chip-curves work/tuning-tests
dotnet run --project Tests/TuningRegression -c Release -- --nsf-multi work/tuning-tests
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

The UI harness renders actual application dialogs in a hidden application window, without desktop screenshots or browser control. Repeat for `eng`, `deu`, `spa`, `por`, `rus`, `kor`. The Chinese run also exercises actual pointer, key and character events, typed live pan, quantity editing, curve selection/input/bounds, nested dialog cancellation and NSF song selection. Append a saved project path and `150` to exercise 150% scaling.

Windows x64 is the runtime-validated target. Linux and macOS managed projects also compile on Windows, but have not been run on those operating systems. A common CMake native build and three-OS CI configuration are included; see `PLATFORM_HANDOFF.txt` for commands, adoption steps and the exact verification boundary. The CI has not run remotely. Rebuild native audio libraries before using other platforms; the upstream prebuilt libraries do not expose the new functions.

For future mobile work, the shared curve editor includes touch selection, long-press/double-tap numeric input and pinch zoom, and its horizontal scrollbar consumes its gesture without also scrolling the parent page. The existing mobile property page and numeric editor are reused. This is integration preparation, not an Android release: the Android build attempt stops at the local toolchain, which has .NET 8 and no Android workload, while the project requires `net9.0-android36.0`. Android compilation, native libraries and physical touch-device behavior remain unverified.

## Verification and limits

Revision 4 adds independent noise-envelope, envelope-decay, pattern-delay, loop-variant and sound-engine-export regression suites. All seven languages load in the actual application window; English and Chinese loop-variant tests edit notes, undo/redo, save, unload and reopen. The packaged BUILD-INFO and verification logs record the final counts. Linux/macOS compilation remains separate from runtime validation.

Revision 3 passes **3,431 backend assertions** across three commands: the original 1,506 regression checks, 1,886 chip-curve checks and 39 multiple-subsong-import checks. Coverage includes serialization, inheritance, cents quantization, every expansion's NSF pitch registers in PAL/NTSC modes, dual-region export, held notes, slides, vibrato, loop/track tuning resets, duplicate-chip stereo isolation, N163 wave allocation, keyboard preview, clipboard ownership, project merging and the 64-channel boundary. The curve checks verify exact requested hardware values at all 96 notes for all eight chips in both regions, reference-basis persistence/conversion, binary/text/undo round trips, old-format loading, curve isolation and all-expansion editor/NSF execution. The import checks exercise ordering, individual durations, resource references, persistence, validation, cancellation and CLI compatibility.

All seven UI languages render in revision 3, and the Chinese interaction tests also pass at 150% scaling. Tests include integer input beyond the drag band, hardware bounds, the full keyboard, zoom/scroll and default restoration. An additional 29 touch-event checks pass on the desktop harness at both 100% and 150% scaling; these verify shared event handling, not Android device behavior. Earlier verification established that ten upstream register-trace fixtures remain byte-identical and all four assemblers produce identical executable bytes over 32 engine configurations; those revision 3 checks preceded the revision 4 engine additions described above. Linux and macOS managed builds pass with zero errors, which is compilation evidence only.

NSF behavior is executed in the bundled 6502 emulator and compared with editor register output. Revision 2 includes a simple NROM cartridge in `Tests/NesTuning`: actual exported music and the modified engine run in an independent FCEUmm CPU/PPU/APU emulator. Its captured audio verifies 440 Hz, 432 Hz, 450 Hz references, C +50 cents and loop restoration against the nearest hardware timer. See that directory's README for reproduction commands. NES/FDS images have not been tested on physical hardware. This is a locally tested development fork, not an upstream-reviewed release. Long sessions, every third-party NSF player and every operating system have not been validated.

## Contributing upstream

See `UPSTREAM_PROPOSAL.txt` for a prepared discussion draft. Nothing has been sent or published by this local build. The proposal separates hardware-compatible tuning from desktop-only multi-chip mixing so maintainers can choose the scope they want to review.

Newly extracted Gimmick! ROM/music/instrument data and newly derived game projects are excluded from public source packages and regression fixtures. The one-off local converter's data and outputs are kept separate from this application's features. The upstream repository's already tracked demo songs, including its original Gimmick demo, remain unchanged. The included format-1001 fixture is the fork's own original tuning demonstration.
