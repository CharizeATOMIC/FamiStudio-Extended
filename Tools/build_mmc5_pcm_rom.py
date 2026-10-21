"""Compatibility entry point: rebuild all current PCM cartridge variants.

The original single-DAC padding generator cannot be reused with four-target
feeder state. The current builder also measures mapper-5 APU/MMC5 handoffs.
"""
from pathlib import Path
import runpy
import sys
if len(sys.argv) < 2:
    sys.argv.append(str(Path(__file__).resolve().parents[1] / "work/pcm-rom-build"))
runpy.run_path(str(Path(__file__).with_name("build_pcm_rom.py")), run_name="__main__")
