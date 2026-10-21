# NSF2 IRQ CPU regression

Run `verify.py` with a built NotSoFatso DLL and an isolated output directory.
The script uses the bundled ca65/ld65 and Python's standard library. It runs
6502 code through the DLL; it does not inspect headers as a substitute for
playback. No user configuration or game data is used.

```
python Tests/Nsf2Timer/verify.py --dll path/to/NotSoFatso.dll --output temporary/nsf2
```

Optional independent reference:

- `--reference-nsf path/to/nsf2_irq.nsf`: Brad Smith's
  [original test](https://github.com/bbbradsmith/nes-audio-tests/blob/master/build/nsf2_irq.nsf),
  with [documented expected behavior](https://github.com/bbbradsmith/nes-audio-tests/blob/master/nsf2_irq.s).
  Download it separately; it is not redistributed here.
- `--baseline-dll path/to/old/NotSoFatso.dll --legacy path/to/old.nsf`:
  compare exact APU register values and CPU cycles with the previous emulator.
  Repeat `--legacy` for more cases.

`nsf2-timer-results.json` records the tested DLL hash and measured timing.
Timer expiration has a constant period; IRQ service waits for an instruction
boundary, so individual DAC write intervals have ordinary 6502 IRQ jitter.
This suite covers the returning INIT/PLAY + IRQ subset, not non-returning INIT
or mandatory NSF2 metadata.
