"""Inject NMI at every instruction of the actual mapper-5 IRQ epilogue.

This specifically reproduces the former deferred-NMI lost-wakeup window.
It uses the linked cartridge's real interrupt vectors, stack and blanking code.
"""
import argparse
import json
from pathlib import Path
import sys

parser = argparse.ArgumentParser()
parser.add_argument("templates", type=Path)
parser.add_argument("report", type=Path)
parser.add_argument("--py65", type=Path)
args = parser.parse_args()
if args.py65:
    sys.path.insert(0, str(args.py65))
from py65.devices.mpu6502 import MPU
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Mmc5PcmNes"))
from service_cycles import read_labels

checks = []
for path in sorted(args.templates.glob("rom_mmc5_pcm_m*.nes")):
    labels = read_labels(path.with_suffix(".lbl"))
    rom = path.read_bytes()
    for phase in (0, 1):
        def create():
            memory = bytearray(65536)
            memory[0x8000:] = rom[16:32784]
            memory[labels["pcm_phase"]] = phase
            memory[labels["famistudio_song_speed"]] = 1
            cpu = MPU(memory=memory, pc=0x7000)
            cpu.p = 0x20
            cpu.irq()
            return cpu, memory
        cpu, memory = create()
        points = []
        recording = False
        for _ in range(1000):
            recording |= cpu.pc == labels["sample_return"]
            if cpu.pc == 0x7000:
                break
            if recording:
                points.append(cpu.pc)
            cpu.step()
        else:
            raise AssertionError("IRQ epilogue failed to return")
        for point in points:
            cpu, memory = create()
            injected = False
            samples = 0
            for _ in range(20000):
                if cpu.pc == point and not injected:
                    cpu.nmi()
                    injected = True
                if cpu.pc == labels["first_sample"]:
                    samples += 1
                if cpu.pc == 0x7000:
                    break
                cpu.step()
            else:
                raise AssertionError((path.name, phase, hex(point), "interrupt did not return"))
            wanted = 18 if "_pal" in path.name else 5 if phase else 6
            assert injected and memory[labels["frame_count"]] == 1, (
                path.name, phase, hex(point), "NMI lost or handled twice")
            assert samples == wanted, (path.name, phase, hex(point), samples, wanted)
            assert memory[labels["irq_busy"]] == memory[labels["blank_deferred"]] == 0
            checks.append(dict(template=path.name, phase=phase, pc=hex(point), blank_samples=samples))
args.report.write_text(json.dumps(dict(passed=True, count=len(checks), checks=checks), indent=2) + "\n")
print(f"PASS all {len(checks)} instruction-boundary NMI handoff checks")
