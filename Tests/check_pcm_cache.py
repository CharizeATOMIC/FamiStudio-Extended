"""Exercise the linked 6502 PCM trigger/cache lifecycle for all four DACs.

Uses the actual NSF kernel and its sparse control parser. The memory window
can be remapped after the first read, while the immutable source sample and
its cached byte remain unchanged. Requires py65 and rebuilt PCM NSF kernels.
"""
from pathlib import Path
import argparse
import hashlib
import json
import re
from py65.devices.mpu6502 import MPU

parser = argparse.ArgumentParser()
parser.add_argument("output", type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
checks = 0
results = []


def check(ok, message):
    global checks
    assert ok, message
    checks += 1


for region in ("ntsc", "pal"):
    name = f"nsf_famistudio_multi_n163_1ch_{region}_pcm"
    labels = {}
    for line in (root / "work/nsf-build" / (name + ".dbg")).read_text().splitlines():
        match = re.search(r'name="([^"]+)".*?val=(0x[0-9A-F]+)', line)
        if line.startswith("sym") and match:
            labels[match[1]] = int(match[2], 16)
    binary = (root / "FamiStudio/Nsf" / (name + ".bin")).read_bytes()
    code = binary[128:]
    window = 65536 - len(code) - 8192
    for voice, dac in enumerate((0x4011, 0x5011, 0x9000, 0xa000)):
        before = checks
        memory = [0] * 65536
        memory[65536 - len(code):] = code
        memory[window:window + 4096] = [34, 201] * 2048
        memory[0x6f00:0x7000] = list(range(256))
        memory[0x6700:0x6800] = [
            max(1, 128 + int((raw - 128) * 7 / 15)) for raw in range(256)
        ]

        def put(symbol, value, offset=0):
            memory[labels[symbol] + offset] = value

        def get(symbol, offset=0):
            return memory[labels[symbol] + offset]

        put("famistudio_song_speed", 1)
        put("nsf_pcm_page_reg", (window >> 12) - 8)
        for destination in range(4):
            put("famistudio_pcm_pending", 255, destination)
            put("famistudio_pcm_active_offset", destination * 32, destination)
            put("famistudio_pcm_table_ptr", 0x6f, destination * 2 + 1)
        cpu = MPU(memory=memory)

        def call(symbol):
            cpu.pc, cpu.sp, cpu.p = labels[symbol], 255, 0x20
            cpu.stPushWord(0x7fff)
            start = cpu.processorCycles
            for _ in range(3000):
                cpu.step()
                if cpu.pc == 0x8000:
                    return cpu.processorCycles - start
            raise AssertionError(("routine did not return", symbol))

        def command(data):
            memory[0x7100:0x7100 + len(data) + 3] = data + [0, 1, 0]
            put("famistudio_pcm_stream", 0)
            put("famistudio_pcm_stream", 0x71, 1)
            put("famistudio_pcm_wait", 0)
            put("famistudio_pcm_wait", 0, 1)
            call("famistudio_pcm_update")

        def trigger(pointer, length, loop):
            memory[0x7000:0x7006] = [
                pointer & 255, pointer >> 8, length & 255, length >> 8, 2, int(loop)
            ]
            command([5, voice, 1, 0, 0x70])
            slot = get("famistudio_pcm_pending", voice)
            check(slot in (voice * 32, voice * 32 + 16), "inactive slot published")
            expected_flag = 0x81 if length == 1 and loop else 1
            check(get("famistudio_pcm_sample_ptr", slot + 12) == expected_flag, "activation mode initialized")
            check(get("famistudio_pcm_sample_ptr", slot + 11) == 0, "old cache cleared before publication")

        service = "famistudio_pcm_service"

        def quantize(value):
            return value >> 1 if voice == 0 else value if voice == 1 else 0x80 | (value >> 4)

        trigger(window, 1, True)
        call(service)
        slot = get("famistudio_pcm_active_offset", voice)
        check(memory[dac] == quantize(34), "first IRQ reads actual source byte")
        check(get("famistudio_pcm_active", voice) == 0x81, "one-byte loop activates cache")
        check(get("famistudio_pcm_sample_ptr", slot + 11) == 34, "cache contains source, not scaled output")
        old_bank = get("famistudio_pcm_current_bank")
        # Represents another user remapping the sample CPU window. PCM's ROM
        # source remains immutable; cached service must not depend on this view.
        memory[window] = 222
        call(service)
        check(memory[dac] == quantize(34), "cached output survives window remapping")
        check(get("famistudio_pcm_current_bank") == old_bank, "cached IRQ does not alter bank bookkeeping")
        command([5, voice, 0x17])
        constant_cycles = call(service)
        check(memory[dac] == quantize(memory[0x6700 + 34]), "gain changes on next sample")
        command([5, voice, 0x1f])
        trigger(window + 1, 1, True)
        call(service)
        check(memory[dac] == quantize(201), "retrigger reads new immutable source instead of old cache")
        put("famistudio_song_speed", 0x81)
        call(service)
        check(memory[dac] == quantize(128), "pause outputs neutral DAC")
        put("famistudio_song_speed", 1)
        call(service)
        check(memory[dac] == quantize(201), "resume retains constant loop")
        command([5, voice, 2])
        call(service)
        check(memory[dac] == quantize(128) and not get("famistudio_pcm_active", voice), "stop cancels cached stream")
        trigger(window + 1, 1, True)
        call(service)
        command([5, voice, 6])
        memory[dac] = 0x55
        call(service)
        check(memory[dac] == 0x55 and not get("famistudio_pcm_owns", voice), "yield does not overwrite next owner's DAC")
        memory[window] = 34
        trigger(window, 2, True)
        call(service)
        check(memory[dac] == quantize(34) and get("famistudio_pcm_active", voice) == 1, "reused slot returns to ordinary stream")
        call(service)
        check(memory[dac] == quantize(201), "ordinary stream advances after cached reuse")
        call(service)
        check(memory[dac] == quantize(34), "ordinary two-byte loop reloads")
        trigger(window + 1, 1, False)
        call(service)
        check(memory[dac] == quantize(201) and get("famistudio_pcm_active", voice) == 1, "one-shot length one does not enable cache")
        call(service)
        check(memory[dac] == quantize(128) and not get("famistudio_pcm_active", voice), "one-shot terminates normally")
        row = dict(region=region, destination=voice, checks=checks - before,
                   cached_dispatch_cycles=constant_cycles,
                   kernel_sha256=hashlib.sha256(binary).hexdigest())
        results.append(row)
        print("PASS", region, voice, row["checks"], "cache lifecycle checks")

args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(dict(passed=True, checks=checks, results=results,
    scope="Actual linked 6502 control parser and sample service. Immutable one-byte loops, gain, pause, stop, yield, retrigger, slot reuse and bank-window independence."),
    indent=2) + "\n")
print("PASS all", checks, "PCM cache lifecycle checks")
