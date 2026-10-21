"""Execute each linked cartridge's bounded controller/debounce code.

This tests button semantics and transient suppression, not DMA immunity.
The independent full-console tests separately exercise real DPCM interference.
"""
import argparse
import json
from pathlib import Path
import sys

parser = argparse.ArgumentParser()
parser.add_argument("templates", type=Path)
parser.add_argument("report", type=Path)
args = parser.parse_args()
from py65.devices.mpu6502 import MPU
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Mmc5PcmNes"))
from service_cycles import read_labels


class Memory(bytearray):
    def __init__(self, rom):
        super().__init__(65536)
        self[0x8000:] = rom[16:32784]
        self.buttons = self.bit = 0

    def __getitem__(self, at):
        if at == 0x4016:
            value = (self.buttons >> (7 - self.bit)) & 1 if self.bit < 8 else 1
            self.bit += 1
            return value
        return super().__getitem__(at)

    def __setitem__(self, at, value):
        if at == 0x4016 and value == 0:
            self.bit = 0
        super().__setitem__(at, value)


checks = []
for path in sorted(args.templates.glob("rom_*_pcm_m*.nes")):
    labels = read_labels(path.with_suffix(".lbl"))
    memory = Memory(path.read_bytes())
    accepted = []
    edges = []
    costs = []
    # Transient Right for one/two frames is discarded. Stable Right, release,
    # held Left and a second Right each produce exactly one rising edge.
    sequence = ([0] * 3 + [1] + [0] * 3 + [1] * 2 + [0] * 3 +
                [1] * 6 + [0] * 3 + [2] * 6 + [0] * 3 + [1] * 6)
    previous = 0
    for state in sequence:
        memory.buttons = state
        cpu = MPU(memory=memory, pc=labels["poll_buttons"])
        cpu.stPushWord(0x7fff)
        for _ in range(1000):
            cpu.step()
            if cpu.pc == 0x8000:
                break
        else:
            raise AssertionError((path.name, "unbounded controller read"))
        value = memory[labels["buttons"]]
        edge = value & ~previous
        if edge:
            edges.append(edge)
        previous = value
        memory[labels["previous_buttons"]] = value
        accepted.append(value)
        costs.append(cpu.processorCycles)
    assert edges == [1, 2, 1], (path.name, edges, accepted)
    assert all(value == 0 for value in accepted[:12]), (path.name, accepted)
    assert accepted[-1] == 1
    checks.append(dict(template=path.name, press_edges=edges,
                       minimum_poll_cycles=min(costs), maximum_poll_cycles=max(costs)))

args.report.write_text(json.dumps(dict(passed=True, count=len(checks), checks=checks), indent=2) + "\n")
print(f"PASS all {len(checks)} linked controller press/release/repress checks")
