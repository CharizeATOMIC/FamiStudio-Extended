Simple NES tuning verification cartridge

TuningVerification.nes is a mapper-0 NROM-256 NTSC cartridge (40,976 bytes).
It uses the base NES pulse channel, without expansion hardware or stereo.
Start it in an NES emulator. No buttons are needed. Reset restarts the sequence.
Every 128 frames (~2.13 seconds) the screen advances to the next of four steps:

1. A4 with pattern override A=440 Hz, equal temperament.
2. A4 inheriting the song's A=432 Hz reference.
3. A4 with pattern override A=450 Hz, equal temperament.
4. C4 inheriting A=432 Hz and the song's C offset of +50 cents.
The loop returns to step 1; the test also measures the first repeated step.

The expected frequencies include the NES timer quantization. For example, the
closest NTSC pulse setting to 450 Hz produces approximately 449.240 Hz. This is
the hardware limit, not a failure to apply the requested reference.

Rebuild on Windows, from the source repository root:
  dotnet run --project Tests/TuningRegression -c Release -- --nes-demo work/nes-demo
  python Tests/NesTuning/build.py work/nes-demo

The first command creates the FamiStudio binary/text project and exports music.s
through the real FamiStudio exporter. The second assembles demo.s against the
fork's SoundEngine/famistudio_ca65.s, with FAMISTUDIO_USE_CUSTOM_TUNING=1, using
the bundled CA65/LD65 tools. The music stream itself changes the tuning tables;
the cartridge program only initializes/updates the engine and displays the step.

Independent emulation test (Python 3, numpy and Pillow required):
  python Tests/NesTuning/verify.py <fceumm_libretro.dll> work/nes-demo/TuningVerification.nes work/nes-verified

The test records 660 frames of actual emulated audio, checks pitch against the
nearest supported timer, and captures the screen at all four steps and the loop.
NES-verification.json records emulator/ROM hashes and measured results.
The emulator core is an external test dependency and is not bundled here.
FCEUmm core used: (SVN) ac0d00a, Windows x86_64 libretro build.
Source of test dependency:
https://buildbot.libretro.com/nightly/windows/x86_64/latest/fceumm_libretro.dll.zip
Host API documentation:
https://docs.libretro.com/development/cores/developing-cores/

The demo source uses the repository's existing MIT-licensed demo font.
There has been no physical NES test. This simple cartridge uses only the base
APU; expansion-chip tuning has separate NSF regression coverage.
