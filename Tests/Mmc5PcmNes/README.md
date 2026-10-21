# PCM cartridge validation

The current regression covers independent PCM libraries and four hardware
outputs: the APU DPCM DAC, MMC5 DAC, and each VRC6 pulse in gate mode. All
fixtures use original synthetic sounds, never game or user assets.

```text
python Tools/build_pcm_rom.py <isolated-build-directory>
dotnet Tests/TuningRegression/bin/Release/net8.0/TuningRegression.dll --pcm-nes <fixture-directory>
python Tests/PcmNes/verify.py <fixture-directory> <isolated-build-directory> --frames 800
```

Python requires py65. The repository supplies ca65 and ld65. The builder emits
44 combinations of used destinations, PAL/NTSC and tempo system. For mapper 5
it executes the actual linked feeder and blanking wrapper to generate padding
for every permitted path; these tables must be regenerated after engine or
cartridge changes. The old `build_mmc5_pcm_rom.py` and `--mmc5-pcm-nes` commands
now delegate to the generalized builders/tests. The earlier
`Tests/Mmc5PcmNes/verify.py` is retained as historical R11 evidence, not the
current verifier.

APU-only projects and VRC6 projects use mapper 24. Its IRQ prescaler runs
continuously, including blanking, and delivers a PCM tick every 454 or 455 CPU
cycles. On PAL this remains a CPU-clock prescaler, not four PPU scanlines.
MMC5 projects use mapper 5: visible four-scanline IRQs and a measured NMI
blanking loop produce 131 samples per two NTSC frames or 78 per PAL frame.
Samples are resampled for each actual rate, preserving pitch and duration.
Normal score playback runs once per video frame. Rates above 1x, unsupported
mappers, overlapping PCM streams and PRG capacity overflow are rejected.
Different DACs may alternate at the same score tick: the NES-only immediate
stop option writes the old DAC's center atomically before publishing a new
trigger. The normal NSF stop protocol is unchanged. APU PCM shares its DAC
with DPCM; VRC6 PCM occupies its respective pulse. MMC5 PCM remains a separate
voice. Other original voices continue normally.

Single-byte looping samples cache their immutable source byte after the first
read. Every subsequent sample interrupt still reads the current volume table
and writes the DAC. This removes repeated bank switching and loop restoration
without changing the sample clock, gain changes or stop/yield behavior.

Fixtures include long sample bank crossings, one-byte loops, independent DPCM,
ordinary sound-engine notes, PAL/NTSC and both tempo systems, two-song
selection, and cross-DAC handoffs. The cycle verifier runs actual 6502 bytes,
models mapper banking/IRQs and video timing, compares each DAC output against
an independent sample cursor and compares ordinary score states to editor
captures. Both DMA-free and conservative four-cycle DPCM-stall cases run.
Reports include actual worst sample gaps, cross-frame gaps, interrupt cost and
music-update timing. This is not a pixel renderer, bus-phase emulator or
physical-console certification.

These static-screen cartridges need reserved CPU time, IRQ/NMI integration,
8 KiB board WRAM (gain tables use $6000..$6fff), zero-page/state RAM, a temporary
$8000 sample bank window, and fixed code at $e000. The $c000 bank remains
independent for DPCM. Mapper 24 PRG is limited to 256 KiB; MMC5 to 1 MiB.
Changing songs intentionally stops playback while redrawing the screen.
Game integration must reserve measured CPU windows: arbitrary rendering work
or OAM DMA cannot be inserted without a new timing proof.

The demonstration controller uses short serial reads, bounded matching-read
retries and three consecutive music frames of stable state before accepting
a change. This suppresses transient DPCM false input, and preserves held
presses and release/repress. Interrupts can stretch the serial-read pair beyond
the DMC-safe window, so this filtering is not a guarantee of DMA immunity.
The linked `check_controller.py` and `check_nmi_handoff.py` tests cover button
semantics and every instruction boundary in the IRQ/NMI completion handshake.

For independent pixel/APU/mapper execution use a libretro core that implements
the board's WRAM and VRC6 gate DAC. Some older cores omit mapper-24 WRAM or
silence VRC6 gate mode; ordinary pulse audio alone is not PCM verification.
The helper `Tests/PcmNes/verify_console.py` runs a supplied external core,
saves screenshots/audio and checks song selection and sustained playback.
External cores are test dependencies and are not bundled with the application.

Hardware references: [MMC5](https://www.nesdev.org/wiki/MMC5),
[VRC6 board](https://www.nesdev.org/wiki/VRC6),
[VRC IRQ](https://www.nesdev.org/wiki/VRC_IRQ),
[VRC6 audio](https://www.nesdev.org/wiki/VRC6_audio), and
[DMC DMA](https://www.nesdev.org/wiki/APU_DMC).
