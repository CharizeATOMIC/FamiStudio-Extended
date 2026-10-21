"""Cycle-proof the linked multi-destination PCM cartridge blanking wrapper."""
import itertools
from service_cycles import read_labels


def patch_padding(rom, labels, pal, mask):
    from py65.devices.mpu6502 import MPU
    image = bytearray(65536)
    image[0x8000:] = rom[16:32784]
    image[labels["famistudio_song_speed"]] = 1
    paths = {}
    for voice in (0, 1):
        if not mask & (1 << voice):
            continue
        found = {}
        for owned, active, pending, remaining, pointer, loop, loop_pointer, loop_length in itertools.product(
                (0, 1), (0, 1, 129), (255, voice * 32, voice * 32 + 16, 128),
                (0, 1, 255, 256, 257), (0x8000, 0x80ff, 0x9fff),
                (0, 1), (0x8000, 0x80ff, 0x9fff), (1, 256)):
            if remaining == 0 and pending in (voice * 32, voice * 32 + 16):
                continue
            memory = bytearray(image)
            memory[labels["famistudio_pcm_owns"] + voice] = owned
            memory[labels["famistudio_pcm_active"] + voice] = active
            memory[labels["famistudio_pcm_pending"] + voice] = pending
            memory[labels["famistudio_pcm_active_offset"] + voice] = voice * 32
            memory[labels["famistudio_pcm_table_ptr"] + voice * 2 + 1] = 0x6f
            for slot in (voice * 32, voice * 32 + 16):
                at = labels["famistudio_pcm_sample_ptr"] + slot
                memory[at:at + 13] = bytes((pointer & 255, pointer >> 8,
                    remaining & 255, remaining >> 8, 2, loop,
                    loop_pointer & 255, loop_pointer >> 8, loop_length & 255, loop_length >> 8, 2,
                    213, 129 if loop and remaining == 1 else 1))
            initial = bytes(memory)
            cpu = MPU(memory=memory, pc=labels["famistudio_pcm_service_" + ("apu" if voice == 0 else "mmc5")])
            cpu.stPushWord(0x7fff)
            for _ in range(500):
                cpu.step()
                if cpu.pc == 0x8000:
                    break
            else:
                raise AssertionError("Feeder did not return")
            path = memory[labels["famistudio_pcm_path"] + voice]
            if path in found:
                assert found[path][0] == cpu.processorCycles, (voice, path, found[path][0], cpu.processorCycles)
            else:
                found[path] = (cpu.processorCycles, initial)
        paths[voice] = found
    active = sorted({path for found in paths.values() for path in found if path < 32 or path == 64})
    assert len(active) == 19, active
    index_map = {32: 0, 96: 1, 97: 2, 255: 3}
    index_map.update({path: 16 + 4 * index for index, path in enumerate(active)})
    def patch(label, index, value):
        rom[16 + labels[label] - 0x8000 + index] = value
    for path, index in index_map.items():
        patch("pcm_path_index", path, index)
    target = 422 if pal else 450
    checked = {}
    combinations = itertools.product(paths.get(0, {255: (0, bytes(image))}),
                                     paths.get(1, {255: (0, bytes(image))}))
    for a, b in combinations:
        if mask == 3 and a in active and b in active:
            continue  # Export rejects concurrent active streams at this clock.
        if mask == 3 and ((a in (96, 97) and b in active) or (b in (96, 97) and a in active)):
            continue  # Immediate stop/yield completes the old DAC before the new trigger.
        memory = bytearray(image)
        for voice, path in ((0, a), (1, b)):
            if voice not in paths:
                continue
            state = paths[voice][path][1]
            for name, count, stride in (
                    ("famistudio_pcm_owns", 1, 1), ("famistudio_pcm_active", 1, 1),
                    ("famistudio_pcm_pending", 1, 1), ("famistudio_pcm_active_offset", 1, 1),
                    ("famistudio_pcm_table_ptr", 2, 2), ("famistudio_pcm_sample_ptr", 32, 32)):
                start = labels[name] + voice * stride
                memory[start:start + count] = state[start:start + count]
        initial = bytes(memory)
        ia, ib = index_map[a], index_map[b]
        index = ia if mask == 1 else ib if mask == 2 else ia + ib if a in active else ia + ib + 76 if b in active else ia * 4 + ib
        assert 0 <= index < 176
        patch("pcm_padding", index, 1)
        patch("pcm_padding_remainder", index, 0)
        def interval():
            mem = bytearray(initial)
            mem[0x8000:] = rom[16:32784]
            mem[labels["blank_samples"]] = 2
            cpu = MPU(memory=mem, pc=labels["first_sample"])
            calls = []
            for _ in range(1500):
                if cpu.pc == labels["famistudio_pcm_service"]:
                    calls.append(cpu.processorCycles)
                    if len(calls) == 2:
                        return calls[1] - calls[0]
                cpu.step()
            raise AssertionError(("Blanking wrapper did not return", a, b))
        baseline = interval()
        quotient, remainder = divmod(target - baseline, 5)
        assert quotient >= 0, ("Unsafe non-concurrent feeder path", mask, pal, a, b, baseline, target)
        patch("pcm_padding", index, quotient + 1)
        patch("pcm_padding_remainder", index, remainder)
        assert interval() == target, (a, b, index, interval(), target)
        if index in checked:
            assert checked[index]["baseline"] == baseline
        checked[index] = dict(a=a, b=b, baseline=baseline, cycles=target)
    return dict(mask=mask, target_cycles=target, cases=len(checked),
                service_costs={v: {p: c for p, (c, _) in found.items()} for v, found in paths.items()},
                interval_proofs=checked)
