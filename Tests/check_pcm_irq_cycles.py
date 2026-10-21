"""Measure linked PCM NSF2 interrupt paths with py65 (pip install py65).

Run after Tools/build_tuning_nsf.py _pcm. This measures interrupt service cost,
not the host's music/graphics budget, and deliberately excludes DMA stalls.
"""
from pathlib import Path
import argparse
import hashlib
import itertools
import json
import re
from py65.devices.mpu6502 import MPU

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
results = []
for region, period in [("ntsc", 448), ("pal", 416)]:
    configurations = [
        (f"nsf_famistudio_{region}_pcm", 1),
        (f"nsf_famistudio_mmc5_{region}_m2_pcm", 2),
        (f"nsf_famistudio_vrc6_{region}_m4_pcm", 4),
        (f"nsf_famistudio_vrc6_{region}_m8_pcm", 8),
        (f"nsf_famistudio_vrc6_{region}_mc_pcm", 12),
        (f"nsf_famistudio_multi_n163_1ch_{region}_m3_pcm", 3),
        (f"nsf_famistudio_multi_n163_1ch_{region}_pcm", 15),
    ]
    for name, mask in configurations:
        labels = {}
        for line in (root / "work/nsf-build" / (name + ".dbg")).read_text().splitlines():
            match = re.search(r'name="([^"]+)".*?val=(0x[0-9A-F]+)', line)
            if line.startswith("sym") and match:
                labels[match[1]] = int(match[2], 16)
        binary = (root / "FamiStudio/Nsf" / (name + ".bin")).read_bytes()
        code = binary[128:]
        window = 65536 - len(code) - 8192
        cases = []
        cached_checks = 0
        for remaining, pending, low, high_offset, different_bank, loop, reload_length, cached in itertools.product(
            [0, 1, 256], [False, True], [0x10, 0xff], [0, 15], [0, 1], [False, True], [1, 256], [False, True]
        ):
            if pending and remaining == 0:
                continue  # Exported descriptors never publish zero-length data.
            if cached and (pending or remaining != 0 or not loop or reload_length != 1):
                continue
            memory = [0] * 65536
            memory[65536 - len(code):] = code
            memory[window:window + 4096] = [128] * 4096
            memory[0x6f00:0x7000] = list(range(256))

            def put(symbol, value, offset=0):
                memory[labels[symbol] + offset] = value

            for voice in range(4):
                put("famistudio_pcm_owns", int(bool(mask & (1 << voice))), voice)
                put("famistudio_pcm_active", 0x81 if cached else 1, voice)
                put("famistudio_pcm_active_offset", voice * 32, voice)
                put("famistudio_pcm_pending", voice * 32 if pending else 255, voice)
                put("famistudio_pcm_table_ptr", 0x6f, voice * 2 + 1)
                slot = labels["famistudio_pcm_sample_ptr"] + voice * 32
                memory[slot:slot + 11] = [
                    low, (window >> 8) + high_offset, remaining & 255, remaining >> 8, voice, int(loop),
                    0x10, window >> 8, reload_length & 255, reload_length >> 8, voice,
                ]
                memory[slot + 11] = 201
                memory[slot + 12] = 0x81 if pending and remaining == 1 and loop else 1
            put("famistudio_song_speed", 1)
            put("famistudio_pcm_current_bank", different_bank)
            put("nsf_pcm_page_reg", (window >> 12) - 8)
            memory[0xfffe] = labels["nsf_pcm_irq"] & 255
            memory[0xffff] = labels["nsf_pcm_irq"] >> 8
            cpu = MPU(memory=memory)
            cpu.pc, cpu.sp, cpu.p, cpu.a, cpu.x, cpu.y = 0x8000, 255, 0x20, 37, 42, 53
            cpu.irq()
            for _ in range(2000):
                if cpu.pc == 0x8000:
                    break
                cpu.step()
            else:
                raise AssertionError("IRQ did not return")
            assert (cpu.sp, cpu.a, cpu.x, cpu.y) == (255, 37, 42, 53)
            if cached:
                assert memory[labels["famistudio_pcm_current_bank"]] == different_bank
                for voice, dac in enumerate((0x4011, 0x5011, 0x9000, 0xa000)):
                    if mask & (1 << voice):
                        expected = 201 >> 1 if voice == 0 else 201 if voice == 1 else 0x80 | (201 >> 4)
                        assert memory[dac] == expected, (name, voice, memory[dac], expected)
                # A gain change must be read on the very next IRQ, even though
                # immutable sample data and its bank no longer need a read.
                memory[0x6f00:0x7000] = [128] * 256
                old_cycles = cpu.processorCycles
                cpu.irq()
                for _ in range(2000):
                    if cpu.pc == 0x8000:
                        break
                    cpu.step()
                else:
                    raise AssertionError("Cached IRQ did not return")
                assert memory[labels["famistudio_pcm_current_bank"]] == different_bank
                for voice, dac in enumerate((0x4011, 0x5011, 0x9000, 0xa000)):
                    if mask & (1 << voice):
                        expected = 64 if voice == 0 else 128 if voice == 1 else 0x88
                        assert memory[dac] == expected
                cached_checks += 1
                measured_cycles = old_cycles
            else:
                measured_cycles = cpu.processorCycles
            cases.append(dict(remaining=remaining, pending=pending, pointer_low=low, pointer_page=high_offset, reload_length=reload_length,
                              different_bank=different_bank, loop=loop, cached_single_byte_loop=cached,
                              complete_irq_cycles=measured_cycles))
        row = dict(kernel=name, kernel_sha256=hashlib.sha256(binary).hexdigest(),
                   destinations_mask=mask, timer_period=period, cases=len(cases),
                   cached_loop_live_gain_and_bank_checks=cached_checks,
                   cached_loop_irq_cycles=sorted({c["complete_irq_cycles"] for c in cases if c["cached_single_byte_loop"]}),
                   minimum=min(c["complete_irq_cycles"] for c in cases),
                   maximum=max(c["complete_irq_cycles"] for c in cases),
                   maximum_case=max(cases, key=lambda c: c["complete_irq_cycles"]))
        results.append(row)
        print("PASS", name, "IRQ cycles", row["minimum"], "..", row["maximum"])
report = dict(passed=True, method="py65 actual linked IRQ entry through RTI",
              includes=["IRQ entry", "A/X/Y preservation", "timer acknowledge", "bank callback", "PCM service", "RTI"],
              excludes=["DMC DMA stalls", "music PLAY work", "graphics/OAM DMA"],
              scope="Enumerated state combinations. High-cost configurations are measurements, not export acceptance.",
              results=results)
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(report, indent=2))
print("PASS", len(results), "linked PCM IRQ configurations")
