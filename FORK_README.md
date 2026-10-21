# FamiStudio Extended

An independent desktop fork of [FamiStudio 4.5.3](https://github.com/BleuBleu/FamiStudio/releases/tag/4.5.3), based on commit `c09dca64788606c09fcb5e70db28b9dd1f34be78`. This working revision 12 is a development preview, not an official FamiStudio release. Original copyright and license notices remain in place. See `RELEASE_NOTES.txt` for a bilingual overview and [PCM integration](Docs/docs/development/mmc5-pcm-integration.md) for its platform and timing requirements.

The display name is **FamiStudio Extended**, retaining the original project name with a suffix. The repository/package slug and launcher use `FamiStudio-Extended`. Existing source namespaces, engine symbols and the `FamiStudio-Tuning` settings path are retained to limit integration churn. The historical R10 feature patch applied without conflicts to the public upstream master checked on 2026-10-20 (`70625dd`), preserving its latest changelog. That result does not cover the new PCM changes, and the merged tree was not independently built or run. See `UPSTREAM_INTEGRATION.txt` for the exact scope and dependency-based porting plan.

## Features

- Independent DPCM and PCM sample libraries. A 2A03 instrument can map either kind per key, MMC5 instruments accept PCM only, and each VRC6 pulse can play mapped PCM while occupying that pulse. Ordinary 2A03 PCM mappings can also be used directly on MMC5 PCM. Hardware output precision is 7, 8 and 4 bits respectively. Matching engine/NSF2 support includes ordinary-voice handoff. See [PCM integration](Docs/docs/development/mmc5-pcm-integration.md) for NES templates, CPU limits and platform requirements. New projects use format 1010; R11 shared PCM projects migrate automatically without modifying their DPCM source assets.

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

## Revision 12: editing grid and Save As

The project 1x/2x/4x control now converts existing song grids as well as the output clock. On NTSC, 4x provides approximately 240 note-placement steps per second. Conversion scales note positions, durations, releases, pattern settings and delays while preserving musical speed. Existing timed note effects retain an independent clock, and envelopes retain their duration. Lowering the rate requires an exact, representable conversion; otherwise the complete project is left unchanged with an explanation.

New saves use format 1010 for the independent note-effect clock. Clocked engine streams can use optional instruction `$65` alongside `$64`; use the matching engine and regenerated NSF kernels. The PCM sample clock remains independent and approximately 4 kHz in this release.

Save As has its own floppy-and-pencil toolbar icon beside Save, with the existing shortcut and all seven translated tooltips. It uses the same atomic save workflow. The sections below retain the history of earlier revisions.

## Revision 10: noise envelopes directly in hardware settings

Expand **Hardware settings** to access the hardware-volume and envelope-restart text buttons directly. Each button shows the macro's step count or that it is unused, and opens the existing envelope editor. Regular instruments retain the four original envelope icons. Legacy period data adds a direct conversion button in the expanded hardware group. The controls reuse the seven existing language translations. Less frequently used hardware parameters start folded into a single inline row, which displays the number of enabled options. Folding changes only the display, not the sound. Disabling sweep hides its three subordinate controls. **Save As** remains a visible text action beside Save, without a second disk icon; its shortcut and context-menu action remain available.

New noise pitch uses the keyboard and the native instrument arpeggio envelope. The separate noise-period editor and follow-note switch are removed from the UI. Hardware volume and restart remain editable, with help identifying when hardware control overrides software volume. Existing instruments are never silently converted: compatibility period data stays readable, playable and exportable, and the editor displays a legacy-data hint so the source of the sound remains visible.

**Convert legacy noise pitch to arpeggio** remains an explicit, undoable action for compatibility data. It checks whether the existing curve can be represented without changing the sound. Shared use on other channels, a nonzero existing instrument arpeggio, independent note arpeggios, incompatible fixed-period notes/slides, no-attack continuity or unsupported macro data cause a clear refusal and leave all data intact. Successful migration uses the ordinary arpeggio editor. Noise still has only the hardware's discrete period indices, not an equal-tempered frequency register.

Revision 10 retains format **1007**, the existing engine stream, native DSP and ABI. It changes how the existing envelope editors are reached. Revision 9's legacy migration, seven-language explanations and INI-help fix remain. Current verification is recorded below, separately from retained revision 9 application and revision 8 engine/layout evidence.

The revision 8 save safety fixes remain: a complete sibling temporary file is flushed and committed atomically; cancellation/failure preserves the original file, current path and dirty state, and blocks any pending close/new/open continuation. Successful Save As changes the destination of subsequent saves. Short temporary names support long legal filenames, read-only files are refused, file symlinks retain their identity and Unix mode bits are copied. Symbolic-link creation was unavailable on the Windows test account, so link and Unix-mode behavior remain target-platform validation items.

## Revision 4: instrument and sequence fidelity

- Optional APU noise macros introduced hardware-period compatibility data, hardware envelope control and explicit restart writes. Revision 9 edits new noise pitch through native arpeggios as described above. A single trigger can play a complete percussion instrument; an empty macro preserves the original behavior. Hardware decay runs in the APU rather than being approximated by one software volume per video frame.
- The envelope-decay effect freezes the current software volume envelope and reduces its level every N video frames. Zero resumes the envelope. A fresh attack resets the effect, and a real volume release section cancels it. Hardware volume envelopes take precedence; EPSM rhythm voices do not support this effect.
- Pattern properties add playback delay in video frames and an optional loop-variant pattern. Delay retains the score's note positions while carrying notes, releases and stops across boundaries. A variant is selected only after the song actually returns to its loop point; selection-loop playback retains the first-pass score. The sequencer's Edit Loop Variant command opens the alternate notes in the existing piano roll, including undo, redo, clipboard and persistence.
- FamiStudio-engine export compiles delayed/variant note events into ordinary instrument and effect commands. The editor's loop markers stay at their musical boundaries. A separate first-pass stream may still be needed in the exported engine data when the introduction leaves different articulation state. This is not a per-frame register recording.

The new engine options are `FAMISTUDIO_USE_NOISE_ENVELOPES = 1` and `FAMISTUDIO_USE_ENVELOPE_DECAY = 1`; follow the flags printed in the music export header. Stream instructions `$60` and `$61` require this fork's matching engine. Both options default to zero in standalone sources; bundled NSF/NES/FDS kernels enable them. Decay adds two bytes per envelope channel. The assembler matrix checks 128 configurations across CA65, ASM6, NESASM and SDAS; 64 configurations with decay disabled retain their previous executable bytes.

Sound-engine export of playback delay requires FamiStudio tempo. Loop variants without delay also support FamiTracker tempo. Unsupported FamiTone2 combinations and FamiTracker delayed-note/cut combinations are rejected with an export error. This is an explicit compatibility boundary, not a conversion to a different tempo.

FamiTracker text and MIDI export reject the new delay, loop-variant, decay and hardware-noise semantics instead of silently dropping them. VGM export rejects pattern delay and loop variants because its current loop construction cannot preserve their boundary behavior; ordinary noise/decay register playback remains available.

## Revision 7: independent clocks and hardware pulse sweep

The project output clock can run at 1x, 2x or 4x. Changing this setting preserves the authored clocks of existing songs and envelopes, including their actual tempo, releases, vibrato, relative pitch, delays, envelope decay and slides. Existing song grids retain their original resolution; the song properties display that data clock. Newly created songs and envelopes use the currently selected output clock. This separation avoids overflowing the 256-step envelope limit merely by changing the output rate.

Envelope properties expose their step clock through the existing right-click/long-press menu. Changing an envelope's authored clock resamples its data while preserving its duration, loop and release points. Relative pitch and hardware-restart macros insert zero steps rather than repeat a delta or trigger. A conversion that cannot be represented exactly, or exceeds the supported envelope length/value limits, is rejected before changing the project. Lowering the project output clock below a used song/envelope clock is also rejected with the affected resource names. All seven languages include these controls and messages.

Same-region playback, audio export and FamiStudio sound-engine/NSF/NSFe export support the new clocks. High-rate playback/export across NTSC and PAL, high-rate standalone NES/FDS export templates, FamiTracker/FamiTone2, MIDI and VGM are currently rejected explicitly; ordinary 1x support remains. The separate cartridge example demonstrates how to integrate clocked engine data into a game with a suitable mapper timer. A video-frame callback alone cannot evenly schedule four hardware updates during that frame.

Engine option `FAMISTUDIO_USE_UPDATE_RATE = 1` enables instruction `$64` with output-rate and song-divider bytes. Macro labels retain their original bytecode and index semantics; a divider byte immediately before each exported macro label supplies its independent clock. The source option defaults to zero, so ordinary builds incur no clock state or update-loop cost. Clocked games must call `famistudio_update` evenly at the selected rate using their timer/IRQ. The optional scheduler preserves existing SFX stream speed while mixing their held output on intervening calls. Required options are printed in each music export header. High rates consume more CPU time; the accompanying synthetic cartridge reports its own measured budget rather than claiming every game fits the same budget.

APU pulse instruments also expose the hardware sweep enable, direction, divider and shift. Held notes retain the timer advanced by the physical APU, while new attacks and software pitch changes reload the software base. This exports through optional `FAMISTUDIO_USE_PULSE_SWEEP = 1` and instruction `$63`. Both pulse channels are considered when deciding whether smooth-vibrato emulation may touch the shared APU frame counter. Unsupported legacy exporters reject the new instrument semantics.

The NSF importer/emulator now honors the NSF timing fields and NSFe RATE chunk. Its hardware sweep reload, four-step half-frame phase, `$4017` immediate-clock behavior and PAL clock constants have also been corrected. Sweep validation compares a real CPU execution against an independent APU at the actual register-write cycles. Frame-head editor playback is checked separately: hardware sweep can legitimately differ when a CPU writes near a half-frame boundary, so these tests do not claim sample-identical scheduling between an editor and every cartridge program.

Format 1006 adds the clock/sweep fields and lossless Brotli project storage. The uncompressed-size field's high bit identifies the new codec; positive-size 1006 files still accept the previous Deflate payload. Older formats remain readable. Only project-file compression changes; undo and clipboard compression retain their prior format. Compression does not affect playback or sound-engine data. On the large local test project, the new codec reduced the saved file from roughly 104 KB to 75 KB, with approximately 0.9 seconds to compress and 1.3 ms to decompress on the tested machine; these are measurements, not performance guarantees.

The bundled NES/FDS menus were compacted without reducing song capacity or moving data boundaries. All 60 template configurations build, including the existing 32-song EPSM layout. Synthetic CPU tests check navigation, PPU writes, register preservation and the vblank budget. Native libraries must be rebuilt on each target OS because the audio emulator exposes the output-rate setting.

## Format and installation

Revision 5 adds two optional regular-instrument properties. **Reset phase on attack** applies to the APU pulse channels and uses the normal timer-high write once per attack, including attacks that also cross a high-byte boundary. Legato, release and stop do not trigger it. Explicit note phase-reset effects retain their separate behavior. **Clamp pitch to timer byte** applies to APU pulse and triangle channels: pitch-envelope modulation saturates the timer's low byte instead of carrying or borrowing into its high byte. The base timer still includes tuning, fine pitch and slides. Both properties default to off, have controls in all seven languages, and survive binary/text, clipboard and undo operations.

These properties export through `FAMISTUDIO_USE_APU_INSTRUMENT_FLAGS = 1` and instruction `$62`. It adds three bytes of channel state and defaults to disabled in standalone engine sources; use the matching engine and the options emitted in the export header. Bundled NSF/NES/FDS kernels enable it. EPSM custom-table builds share table lookup code to preserve the existing 32-song ROM capacity. FamiTracker/FamiTone2/MIDI exporters reject the new semantics when used; VGM records their register playback normally. Duty-cycle text export also preserves explicit zero and disabled envelope structures without changing their existing playback meaning.

Revision 5 also fixes the native S5B reset path: the chip's 16-CPU-cycle remainder and selected register are reset, so repeated rendering starts from the same state. Rebuild the native library on each platform; the ABI is unchanged. The synthetic test covers tone, noise and hardware envelopes in both regions and mono/stereo. Text import validates pattern steps against the frame limit once, and simultaneous engine exports use independent temporary directories.

Revision 6 allows each DPCM key mapping to store the full hardware initial DAC value, **0–127**, including odd values. The existing mapping dialog displays this absolute value and accepts typed input. A normal sample attack resets the DAC from its mapping, so an equal note-level DAC effect can be omitted. A no-attack note or a standalone DAC command retains its separate semantics. The sample waveform editor keeps its existing half-value controls. Older mapping values migrate without changing playback; text mappings use `DmcInitialValueRaw` for full precision and still accept the legacy half-value `DmcInitialValue`. The sound engine already stores the initial DAC as a full byte, so this addition needs no new opcode or native ABI.

FamiTone2, FamiTracker text and MIDI export reject an odd mapping initial value when a selected DPCM attack actually uses it. Unused mappings, other songs, no-attack notes and explicit note DAC overrides retain the previous export behavior. FamiStudio-engine and register/audio exports preserve the full value.

The Windows x64 package is self-contained: extract the whole directory and run `FamiStudio-Extended.exe`. It does not require installing .NET. `FamiStudio.exe` remains a byte-identical compatible launcher bound to the same managed assembly. Settings use `%LOCALAPPDATA%/FamiStudio-Tuning`; simply extracting the package does not replace the installed official application or its settings. Upstream update notifications default to off because official releases do not include these additions.

Official project formats through version 19 and fork formats 1001 through 1007 can be read. Revision 10 saves format **1007**, unchanged from revisions 8 and 9. The official application and fork revisions through 7 cannot read 1007; revisions 8 and 9 share this file format but not all of the current editor workflow. Save a copy of an existing project before editing it in this fork. Clipboard data has a separate namespace. FamiStudio text exports preserve tuning, curves, mixer and chip-copy metadata. Curves use sparse project-level `ChipTuning` entries with a chip name, note ID and integer `Value`, plus `ChipTuningBasis` reference information.

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

Set `PlatformToolset` to the C++ toolset installed on your machine. UI smoke tests require an OpenGL-capable Windows session and write settings, so run them in the isolated portable copy created by the script:

```powershell
dotnet build Tests/TuningRegression/TuningRegression.csproj -c Release
powershell -File Tests/RunUiSmoke.ps1 -OutputDirectory work/ui-tests
dotnet run --project Tests/TuningRegression -c Release -- --demo work/examples
python Tests/check_export_assemblers.py work/examples/SoundEngine
```

The script copies the harness, native libraries and localization into a fresh runtime with `portable.txt`, and leaves its logs, screenshots and `ui-results.json` under the output directory. It defaults to all seven languages at 100% and Chinese at 150%, using an original synthetic fixture. Supply `-DotnetPath` for a non-default SDK host or `-InputProject` for another saved fixture. Direct non-portable `--ui` execution is refused to protect the user's settings.

The UI harness renders actual application dialogs in a hidden application window, without desktop screenshots or browser control. The Chinese run also exercises actual pointer, key and character events, typed live pan, quantity editing, curve selection/input/bounds, nested dialog cancellation and NSF song selection.

Windows x64 is the runtime-validated target. Linux and macOS managed projects also compile on Windows, but have not been run on those operating systems. A common CMake native build and three-OS CI configuration are included; see `PLATFORM_HANDOFF.txt` for commands, adoption steps and the exact verification boundary. The CI has not run remotely. Rebuild native audio libraries before using other platforms; the upstream prebuilt libraries do not expose the new functions.

For future mobile work, the shared curve editor includes touch selection, long-press/double-tap numeric input and pinch zoom, and its horizontal scrollbar consumes its gesture without also scrolling the parent page. The existing mobile property page and numeric editor are reused. This is integration preparation, not an Android release: the Android build attempt stops at the local toolchain, which has .NET 8 and no Android workload, while the project requires `net9.0-android36.0`. Android compilation, native libraries and physical touch-device behavior remain unverified.

## Verification and limits

Revision 10's direct hardware controls pass all seven UI languages at 100% and Chinese at 150%: eight isolated runs, 1,981 PASS records and unchanged user INI hashes. These UI captures precede branding. The first branding build, then named FamiSpectrum, also matched R9 for four 12-second excerpts (529,200 samples each). Those full UI/four-song checks retain their original attribution and were not rerun for the subsequent naming-only change.

The final **FamiStudio Extended** naming commit `f41abaa` passes Windows publication and Linux/macOS managed compilation with zero errors. The actual `FamiStudio-Extended.exe -help` succeeds. Four independent four-second exports (compatibility launcher at 1x, new launcher at 1x/2x/4x) each produce 176,400 identical PCM samples, also matching the earlier R10 clock reference. All eight native DLL hashes and user INI hashes remain unchanged. These are fresh checks of the new launcher. The following revision 9 results retain their original attribution. Engine, native DSP and project format remain unchanged in R10.

Revision 9 passes **4,732 backend checks**: eight repeated suites total 2,896 (default 1,506, articulation playback 124, envelope clocks 134, instrument articulation 76, noise envelopes 141, noise following 597, project saving 22 and update rates 296), plus **1,836 new migration checks** across 36 scenarios and 72 actual NSF executions in PAL/NTSC at 1x/2x/4x. All seven UI languages at 100% and Chinese at 150% pass in an isolated portable runtime: eight runs with 1,869 PASS records, including the narrow-label geometry and legacy-conversion entry checks. Non-portable GUI test execution is refused before application initialization.

A separate four-song migration comparison verifies **53,314,344 identical editor PCM samples**, including the full mix and isolated noise. Actual NSF CPU execution matches **258,397 ordered APU/S5B writes across 36,328 music ticks**. This compares address/value order per tick, not CPU write timestamps or NSF PCM. The Windows self-contained release also matches the preceding release for four 12-second WAV excerpts (529,200 samples each); its original clock demonstration produces identical 176,400-sample PCM at 1x/2x/4x. The designated four-song Gimmick demonstration is included as described below; ROM inputs, other extraction intermediates and private projects are excluded.

The revision 9 Windows publish and Linux/macOS managed builds succeed with zero errors. All eight published native DLLs match the repository and revision 8 by SHA256. R9 does not change the engine or native DSP, so the R8 four-assembler and NES/FDS-layout reports are retained evidence, not rerun R9 counts. Linux/macOS native runtime, Android devices and physical consoles remain unverified.

Revision 4 adds independent noise-envelope, envelope-decay, pattern-delay, loop-variant and sound-engine-export regression suites. All seven languages load in the actual application window; English and Chinese loop-variant tests edit notes, undo/redo, save, unload and reopen. The packaged BUILD-INFO and verification logs record the final counts. Linux/macOS compilation remains separate from runtime validation.

Revision 3 passes **3,431 backend assertions** across three commands: the original 1,506 regression checks, 1,886 chip-curve checks and 39 multiple-subsong-import checks. Coverage includes serialization, inheritance, cents quantization, every expansion's NSF pitch registers in PAL/NTSC modes, dual-region export, held notes, slides, vibrato, loop/track tuning resets, duplicate-chip stereo isolation, N163 wave allocation, keyboard preview, clipboard ownership, project merging and the 64-channel boundary. The curve checks verify exact requested hardware values at all 96 notes for all eight chips in both regions, reference-basis persistence/conversion, binary/text/undo round trips, old-format loading, curve isolation and all-expansion editor/NSF execution. The import checks exercise ordering, individual durations, resource references, persistence, validation, cancellation and CLI compatibility.

All seven UI languages render in revision 3, and the Chinese interaction tests also pass at 150% scaling. Tests include integer input beyond the drag band, hardware bounds, the full keyboard, zoom/scroll and default restoration. An additional 29 touch-event checks pass on the desktop harness at both 100% and 150% scaling; these verify shared event handling, not Android device behavior. Earlier verification established that ten upstream register-trace fixtures remain byte-identical and all four assemblers produce identical executable bytes over 32 engine configurations; those revision 3 checks preceded the revision 4 engine additions described above. Linux and macOS managed builds pass with zero errors, which is compilation evidence only.

NSF behavior is executed in the bundled 6502 emulator and compared with editor register output. Revision 2 includes a simple NROM cartridge in `Tests/NesTuning`: actual exported music and the modified engine run in an independent FCEUmm CPU/PPU/APU emulator. Its captured audio verifies 440 Hz, 432 Hz, 450 Hz references, C +50 cents and loop restoration against the nearest hardware timer. See that directory's README for reproduction commands. NES/FDS images have not been tested on physical hardware. This is a locally tested development fork, not an upstream-reviewed release. Long sessions, every third-party NSF player and every operating system have not been validated.

## Contributing upstream

See `UPSTREAM_PROPOSAL.txt` for a prepared discussion draft. Nothing has been sent or published by this local build. The proposal separates hardware-compatible tuning from desktop-only multi-chip mixing so maintainers can choose the scope they want to review.

At the user's request, the public application and source packages replace the bundled Gimmick demonstration with the designated four-song version. Its project name and author attribution match the original demo; existing copyright and license notices are retained. This is music demonstration material, not an original synthetic test fixture or a new license grant for the score. Game ROMs, other extraction intermediates and private projects remain excluded. Other upstream demos remain unchanged. The format-1001 fixture and the synthetic clock examples are the fork's original test material.

The file is `Setup/Demo Songs/Gimmick.fms`, with original metadata `Name="Gimmick!"`, `Author="Masashi Kageyama,Naohisa Morota"`, `Copyright="1992 Sunsoft"`. Its songs are Good Morning, Happy Birthday, Good Night and Strange Memories of Death. The 33,029-byte replacement retains 99 instruments, 17 samples and 37 DPCM mappings. Metadata normalization leaves the four-song model unchanged; its SHA256 is `9b8ba001054c75ea6059f0126fb11ac1d4f5458d9fd556204eb871095f39168e`.

The replacement uses fork format 1007 and requires the corresponding format/features; it is not a drop-in demo for official 4.5.3. The feature-only integration patch ending at `52a246f` does not include this later demo replacement, isolated in commit `4e02175`. The final source/history bundle does. A functional upstream PR can omit the demo change and have that asset reviewed separately.
