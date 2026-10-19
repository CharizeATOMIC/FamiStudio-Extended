FamiStudio independent output-clock cartridge examples
=====================================================

Clock-1x.nes, Clock-2x.nes and Clock-4x.nes play the same original 96-tick
phrase. The song and its envelopes were authored at 1x. Only the project's
output frequency changes. Notes, release, pitch envelopes and slides retain
their original duration. No game ROM/music, DPCM sample or expansion audio
is included. The font is the existing FamiStudio sound-engine demo font.

Open a ROM in a NES emulator supporting mapper 69 (Sunsoft FME-7), NTSC.
The screen identifies the output rate. Reset restarts the phrase. Hardware
cartridge execution has not been tested.

Build from this repository (Python 3 plus bundled ca65/ld65):
  python Tests/NesClock/build.py Tests/NesClock/Music <output-directory>
The Music folder contains the three ca65 exports and matching editable FMS
projects. The build makes a 32 KiB PRG + 8 KiB CHR ROM for each multiplier.
Engine/IRQ code stays in the fixed E000-FFFF bank; the other three PRG windows
are mapped to 8000/A000/C000. The static screen leaves NMI disabled.

Game integration
----------------
Keep FAMISTUDIO_USE_UPDATE_RATE at its default 0 for legacy music. For a
higher-rate export, enable it and satisfy the other required flags listed
at the top of that export. Initialize/play the song once, then call
famistudio_update at evenly spaced real times. The data's $64 command
specifies the output clock and song divider; executable envelope labels
have a divider prefix. The engine does not create a hardware timer itself.

This example uses the mapper's 16-bit CPU-cycle IRQ, with separate calls
near 29780.5, 14890.25 or 7445.125 CPU cycles apart. FME-7 command D controls
and acknowledges IRQs; E/F set the timer bytes. The IRQ saves A/X/Y,
acknowledges and reloads the timer, calls the engine, restores registers,
and returns. The eight-entry reload table includes fractional-period
rounding and the measured interrupt/reload overhead. Do not replace this
with repeated calls inside one NMI: those calls are not spaced in time.

When adapting a game, recalculate the timer for its clock and IRQ handler.
Budget the whole game, SFX, mapper banking and DPCM DMA. Protect shared
mapper-command writes and engine state from concurrent NMI/main-thread
use. This demo's measured CPU costs are not a bound for every composition.
The source follows the FME-7 register specification:
https://www.nesdev.org/wiki/Sunsoft_FME-7

Validation
----------
  python Tests/NesClock/verify.py <output-directory> [--py65 <module-root>]
Requires py65. It executes actual ROM instructions, models mapper banking
and the counter every CPU cycle, applies mapper writes on the STA's final
cycle, and delivers IRQs at instruction boundaries. PPU status permits
boot; this test does not emulate sound or graphics. It runs eight phrase
loops per rate, compares expanded music registers, checks IRQ deadlines,
and directly calls music_play with invalid index 255 during the 4x test.
That rejected call may use normal zero-page scratch but must leave every
persistent song/envelope clock and voice state unchanged.

Measured with the supplied phrase, final R7 engine:
Rate  Mean interval  Min..max interval  Worst engine  Worst whole IRQ
 1x   29780.402       29779..29782        3136 cycles    3266 cycles
 2x   14890.324       14889..14892        3774 cycles    3904 cycles
 4x    7444.976        7444..7447         3774 cycles    3904 cycles
All rates have at most 1.875 cycles of interval error and no IRQ overrun.
At 4x the largest observed IRQ occupies 52.44% of one interval; mean CPU
use is 22.32%. All 1/2/4x expanded musical register states match exactly.
These measurements include real instruction timing; they do not imply
identical oscillator phase or identical PCM samples between schedules.

  python Tests/NesClock/verify_console.py <libretro-core> <rom> <out> <rate>
Requires numpy/Pillow and an independently supplied libretro NES core.
FCEUmm ac0d00a ran each ROM for 660 video frames. Screen, audible output,
loop periodicity and mean 1/2/4 updates per video frame passed. A 4x IRQ
boundary can fall on either side of a video boundary (3 or 5 calls in an
individual video frame); real call spacing is checked above, not inferred
from these per-frame counts. The original phrase repeats around 1.597 s
at every rate; block-energy audio measurement resolved 1.595 s at 256-sample
resolution. Reports record core/ROM hashes and physical_hardware=false.

The small clock-only ROM exposed a missing $55..$5f opcode-table padding
guard when UPDATE_RATE was enabled without other new features. All five
assembler sources now include that case; pre-fix evidence remains in the
local validation cache, outside public example assets.
