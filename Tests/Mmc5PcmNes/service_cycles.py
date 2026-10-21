"""Measure the exact linked 6502 feeder paths and fill its blanking delay table.

Requires py65. No game assets or generated musical data are used.
The padding reserves DPCM DMA time; it is not a claim of a jitter-free clock.
"""
import itertools
import re


def read_labels(path):
    return {match[2]: int(match[1], 16) for line in path.read_text().splitlines()
            if (match := re.match(r"al (\w+) \.(.+)", line))}


def patch_padding(rom, labels, pal):
    from py65.devices.mpu6502 import MPU

    base = bytearray(65536)
    base[0x8000:] = rom[16:16 + 32768]
    for index in range(256):
        base[0x6f00 + index] = index
    base[labels["famistudio_pcm_table_ptr"]] = 0
    base[labels["famistudio_pcm_table_ptr"] + 1] = 0x6f
    base[labels["famistudio_song_speed"]] = 1
    base[labels["song_bank"]] = 0x80
    paths = {}
    representatives = {}
    cases = 0
    states = itertools.product((0xff, 0, 16, 0x80), (0, 1), (0, 16),
                               (0, 1, 255, 256, 257, 65535),
                               (0x8000, 0x80fe, 0x80ff, 0x9fff), (0, 1),
                               (1, 256, 65535), (False, True))
    for pending, active, offset, remaining, pointer, loop, loop_length, paused in states:
        if pending in (0, 16) and remaining == 0:
            continue  # Export descriptors reject zero-length samples.
        memory = bytearray(base)
        memory[labels["famistudio_pcm_pending"]] = pending
        memory[labels["famistudio_pcm_active"]] = active
        memory[labels["famistudio_pcm_active_offset"]] = offset
        memory[labels["famistudio_song_speed"]] = 0x80 if paused else 1
        for slot in (0, 16):
            address = labels["famistudio_pcm_sample_ptr"] + slot
            values = [pointer & 255, pointer >> 8, remaining & 255, remaining >> 8,
                      2, loop, pointer & 255, pointer >> 8,
                      loop_length & 255, loop_length >> 8, 2]
            memory[address:address + len(values)] = bytes(values)
        initial = bytes(memory)
        cpu = MPU(memory=memory, pc=labels["famistudio_pcm_service"])
        cpu.stPushWord(0x7fff)
        for instruction in range(300):
            cpu.step()
            if cpu.pc == 0x8000:
                break
        else:
            raise AssertionError("PCM service failed to return")
        path = memory[labels["famistudio_pcm_path"]]
        paths.setdefault(path, set()).add(cpu.processorCycles)
        representatives.setdefault(path, initial)
        cases += 1
    assert all(len(costs) == 1 for costs in paths.values()), paths
    costs = {path: next(iter(values)) for path, values in paths.items()}
    # At the fastest DPCM bit rate a 4-cycle byte DMA can occur every
    # 400 PAL / 432 NTSC cycles. These
    # deliberately shortened nominal blank intervals leave room for all such
    # steals; the visible scanline clock resets the phase once per frame.
    target = 422 if pal else 450
    fixed_wrapper = 40
    for path, cost in costs.items():
        quotient, remainder = divmod(target - cost - fixed_wrapper, 5)
        assert 1 <= quotient <= 255 and 0 <= remainder <= 4, (path, cost)
        for label, value in (("pcm_padding", quotient),
                             ("pcm_padding_remainder", remainder)):
            position = 16 + labels[label] - 0x8000 + path
            rom[position] = value
    # Execute the actual wrapper, rather than trusting only its instruction
    # count formula. This catches page-crossing branches after source edits.
    measured_intervals = {}
    for path, initial in representatives.items():
        memory = bytearray(initial)
        memory[0x8000:] = rom[16:16 + 32768]
        memory[labels["blank_samples"]] = 2
        cpu = MPU(memory=memory, pc=labels["first_sample"])
        entries = []
        for instruction in range(1000):
            if cpu.pc == labels["famistudio_pcm_service"]:
                entries.append(cpu.processorCycles)
                if len(entries) == 2:
                    break
            cpu.step()
        else:
            raise AssertionError(("padding wrapper did not produce two samples", path))
        interval = entries[1] - entries[0]
        assert interval == target, ("linked padding interval", path, interval, target)
        measured_intervals[path] = interval
    return dict(cases=cases, paths=costs, nominal_blank_entry_cycles=target,
                max_service_cycles=max(costs.values()), min_service_cycles=min(costs.values()),
                measured_padding_intervals=measured_intervals, dma_reserve=True)
