# PCM cartridge execution regressions

`--pcm-nes <directory>` in the managed regression harness creates synthetic,
original test projects and exported NES files. These tests then execute those
actual ROMs; no commercial game data is needed.

```powershell
python Tests/PcmNes/run_matrix.py <fixtures> <template-build-directory> `
    --py65 <directory-containing-py65> --frames 3200 --workers 4
python Tests/PcmNes/run_matrix.py <fixtures> <template-build-directory> `
    --console-only --console-core <mesen-libretro-library> --workers 2
```

The first command runs every fixture twice, with and without conservative
four-cycle DPCM byte-fetch stalls. It checks sample descriptors against the
independently generated fixture, follows a separate sample cursor through
loops and bank boundaries, computes destination-specific gain/quantization,
and compares the actual DAC writes. It also checks ordinary channel registers
against editor playback, consecutive NMI music clocks, per-update deadlines,
IRQ intervals, samples per frame, and song switching where available.

Mapper 24 uses the VRC IRQ's continuous 341/3-CPU-cycle prescaler in both
regions. Four counter ticks therefore take 454 or 455 whole CPU cycles.
Mapper 5 uses the cartridge's visible-scanline IRQ and blanking scheduler.
The test catches a deferred-NMI race that previously lost an entire MMC5
frame when NMI interrupted the final IRQ between its deferred-work check
and clearing the busy flag.

The second command uses an independent complete NES emulator, records video
and audio, checks sustained music clocks, and exercises controller selection.
The Python CPU timing model is not a pixel renderer and does not model exact
DPCM DMA bus alignment. The independent console pass covers CPU/PPU/APU/mapper
integration, but neither pass is a physical cartridge test.

The libretro core is an external test dependency and must not be bundled with
the application. Mesen supports mapper-24 work RAM and VRC6 gate-mode output.
The currently inspected FCEUmm mapper-24 implementation does not allocate
the required work RAM; merely setting NES 2.0 RAM size fields does not add
that allocation. Do not change a valid cartridge RAM layout to accommodate
that emulator limitation.

Primary implementation references:

- [VRC IRQ register behavior](https://www.nesdev.org/wiki/VRC_IRQ)
- [VRC6 mapping and RAM](https://www.nesdev.org/wiki/VRC6)
- [Mesen VRC6 mapper](https://github.com/libretro/Mesen/blob/master/Core/VRC6.h)
- [FCEUmm VRC6 mapper](https://github.com/libretro/libretro-fceumm/blob/master/src/boards/vrc6.c)
- [libretro frontend API](https://docs.libretro.com/development/cores/developing-cores/)

Each report records the tested ROM SHA-256. Console reports additionally
record the core name, version, and library SHA-256. Logs, WAV captures, images,
and downloaded test dependencies belong in the caller's cache/output
directory, not in the source tree.
