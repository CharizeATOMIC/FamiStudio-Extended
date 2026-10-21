"""Run synthetic NSF2 programs on the real NotSoFatso 6502.

No game data, GUI, shared build, or user configuration is used.
Usage: python verify.py --dll /path/NotSoFatso.dll --output /temporary/directory
"""
import argparse
import ctypes
import hashlib
import json
from pathlib import Path
import re
import struct
import subprocess

ROOT = Path(__file__).resolve().parents[2]


def assemble(output, name, init_extra="", irq_extra="", play_extra="", reload=223,
             pal=False, flags=0x10, vector_override=True, enable=True, dmc=False):
    source = f"""
.setcpu "6502"
.export init, play
.segment "CODE"
init:
    sei
    lda #$40
    sta $4017
    lda #0
    sta $5010
    sta $00
    sta $01
    sta $02
    sta $03
    {("lda #<irq" + chr(10) + "sta $fffe" + chr(10) + "lda #>irq" + chr(10) + "sta $ffff") if vector_override else ""}
    lda #<{reload}
    sta $401b
    lda #>{reload}
    sta $401c
    lda #{1 if enable else 0}
    sta $401d
    {("lda #$4f" + chr(10) + "sta $4010" + chr(10) + "lda #0" + chr(10) + "sta $4012" + chr(10) + "lda #$40" + chr(10) + "sta $4013" + chr(10) + "lda #$10" + chr(10) + "sta $4015") if dmc else ""}
    {init_extra}
    cli
    rts
play:
    lda #$a1
    sta $4009
    {play_extra}
    lda #$a2
    sta $4009
    rts
irq:
    pha
    txa
    pha
    tya
    pha
    lda $401d
    sta $400d
    inc $00
    lda $00
    ora #$80
    sta $5011
    {irq_extra}
    pla
    tay
    pla
    tax
    pla
    rti
bad_irq:
    pha
    lda #$ee
    sta $400b
    lda $401d
    pla
    rti
.segment "VECTORS"
.word 0,0,{("bad_irq" if vector_override else "irq")}
"""
    asm = output / (name + ".s")
    obj = output / (name + ".o")
    binary = output / (name + ".bin")
    labels = output / (name + ".lbl")
    asm.write_text(source, encoding="ascii")
    config = output / "test.cfg"
    config.write_text("""MEMORY { ROM: start=$8000, size=$8000, fill=yes; }
SEGMENTS { CODE: load=ROM, type=ro; VECTORS: load=ROM, type=ro, start=$FFFA; }
""", encoding="ascii")
    subprocess.run([str(ROOT / "Tools/ca65.exe"), str(asm), "-o", str(obj)], check=True, capture_output=True)
    subprocess.run([str(ROOT / "Tools/ld65.exe"), "-C", str(config), str(obj), "-o", str(binary),
                    "-Ln", str(labels)], check=True, capture_output=True)
    payload = binary.read_bytes()
    play = int(re.search(r"al ([0-9A-Fa-f]+) \.play", labels.read_text()).group(1), 16)
    header = bytearray(128)
    header[:5] = b"NESM\x1a"
    header[5:8] = bytes([2, 1, 1])
    struct.pack_into("<HHH", header, 8, 0x8000, 0x8000, play)
    header[14:14+len(name)] = name.encode("ascii")
    struct.pack_into("<H", header, 0x6e, 16639)
    struct.pack_into("<H", header, 0x78, 19997)
    header[0x7a] = int(pal)
    header[0x7b] = 8
    header[0x7c] = flags
    header[0x7d:0x80] = len(payload).to_bytes(3, "little")
    path = output / (name + ".nsf")
    path.write_bytes(header + payload)
    return path


class Player:
    def __init__(self, dll):
        self.dll = ctypes.CDLL(str(dll))
        d = self.dll
        d.NsfOpen.argtypes = [ctypes.c_char_p]
        d.NsfOpen.restype = ctypes.c_void_p
        d.NsfClose.argtypes = [ctypes.c_void_p]
        d.NsfSetTrack.argtypes = [ctypes.c_void_p, ctypes.c_int]
        d.NsfRunFrame.argtypes = [ctypes.c_void_p]
        d.NsfGetState.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int]
        self.callback_type = ctypes.CFUNCTYPE(None, ctypes.c_int, ctypes.c_int)
        d.NsfSetApuWriteCallback.argtypes = [ctypes.c_void_p, self.callback_type]

    def run(self, path, frames=8, reset=False):
        d = self.dll
        ptr = d.NsfOpen(str(path).encode("utf-8"))
        assert ptr, f"Could not open {path}"
        events = []
        total = 0
        callback = self.callback_type(lambda reg, value: events.append(
            (total + d.NsfGetState(ptr, -1, 1000, 0), reg, value)))
        try:
            d.NsfSetTrack(ptr, 0)
            d.NsfSetApuWriteCallback(ptr, callback)
            for _ in range(frames):
                d.NsfRunFrame(ptr)
                total += d.NsfGetState(ptr, -1, 1001, 0)
            original = list(events)
            if reset:
                d.NsfSetApuWriteCallback(ptr, self.callback_type())
                d.NsfSetTrack(ptr, 0)
                events.clear()
                total = 0
                d.NsfSetApuWriteCallback(ptr, callback)
                for _ in range(frames):
                    d.NsfRunFrame(ptr)
                    total += d.NsfGetState(ptr, -1, 1001, 0)
                assert events == original, "Track reset did not reset IRQ/CPU state"
            return events, total
        finally:
            d.NsfClose(ptr)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dll", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--reference-nsf", type=Path)
    parser.add_argument("--baseline-dll", type=Path)
    parser.add_argument("--legacy", type=Path, action="append", default=[])
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    player = Player(args.dll)
    results = []
    # A deliberately over-budget PLAY must not masquerade as one completed
    # music update per host interval. The legacy flag was sticky after its
    # first completion, hiding this failure under high-rate PCM load.
    path = assemble(args.output, "play-completion-counter", enable=False,
                    play_extra="ldy #60\nlong_outer: ldx #255\nlong_inner: dex\nbne long_inner\ndey\nbne long_outer")
    d = player.dll
    ptr = d.NsfOpen(str(path).encode("utf-8"))
    assert ptr
    complete_frames, entry_counts, play_ends, timings = [], [], [], []
    frame_index = 0
    callback = player.callback_type(lambda reg, value: play_ends.append(frame_index)
                                    if reg == 0x4009 and value == 0xa2 else None)
    try:
        d.NsfSetTrack(ptr, 0)
        d.NsfSetApuWriteCallback(ptr, callback)
        for frame_index in range(15):
            complete_frames.append(d.NsfRunFrame(ptr))
            entry_counts.append(d.NsfGetState(ptr, -1, 1002, 0))
            timings.append([d.NsfGetState(ptr, -1, state, 0) for state in [1003, 1004, 1005]])
        assert [i for i, value in enumerate(complete_frames) if value] == play_ends, (complete_frames, play_ends)
        assert sum(complete_frames) < len(complete_frames) // 2 and sum(complete_frames) >= 3
        assert 0 in complete_frames[complete_frames.index(1) + 1:], "PLAY completion remained sticky"
        assert entry_counts[-1] == len(play_ends) + 1, (entry_counts, play_ends)
        # 60 identical delay loops take ~77k CPU cycles and cross several
        # host-counter resets. Max/last must report the whole invocation.
        assert 76000 < timings[-1][0] < 78000, timings
        assert timings[-1][0] == timings[-1][2]
        assert any(30000 < active < 78000 for _, active, _ in timings)
        d.NsfSetTrack(ptr, 0)
        assert d.NsfGetState(ptr, -1, 1002, 0) == 0
        assert all(d.NsfGetState(ptr, -1, state, 0) == 0 for state in [1003, 1004, 1005])
        assert d.NsfRunFrame(ptr) == 0
    finally:
        d.NsfClose(ptr)
    results.append(dict(name="play-completion-and-entry-counter-not-sticky", completed_frames=play_ends,
                        entry_counts=entry_counts, max_active_last_play_cycles=timings, passed=True))
    for pal, period in [(False, 224), (True, 208)]:
        name = "periodic-pal" if pal else "periodic-ntsc"
        path = assemble(args.output, name, reload=period-1, pal=pal,
                        play_extra="ldy #5\nouter: ldx #255\ninner: dex\nbne inner\ndey\nbne outer")
        events, cycles = player.run(path, reset=True)
        samples = [(t, value) for t, reg, value in events if reg == 0x5011]
        assert len(samples) > 900, f"IRQ did not continue during idle: {len(samples)}"
        assert all(value == ((i+1) & 255) | 128 for i, (_, value) in enumerate(samples)), "Lost/corrupted IRQ writes"
        deltas = [b[0]-a[0] for a, b in zip(samples, samples[1:])]
        # Instruction completion produces bounded hardware IRQ jitter. It
        # must not produce the millisecond-scale stalls of PLAY-rate PCM.
        assert max(abs(delta-period) for delta in deltas) <= 6, (min(deltas), max(deltas))
        starts = [t for t, reg, value in events if reg == 0x4009 and value == 0xa1]
        ends = [t for t, reg, value in events if reg == 0x4009 and value == 0xa2]
        assert len(starts) == len(ends) == 7, (len(starts), len(ends))
        in_play = sum(any(a < t < b for a, b in zip(starts, ends)) for t, _ in samples)
        assert in_play > 100, "IRQ did not preempt music PLAY"
        assert all(value == 0x81 for _, reg, value in events if reg == 0x400d), "IRQ acknowledgment status"
        results.append(dict(name=name, samples=len(samples), cycles=cycles, nominal_period=period,
                            interval_min=min(deltas), interval_max=max(deltas), samples_during_play=in_play))

    # The initial vector is copied from mapped ROM, and an explicit write
    # uses overlay RAM without changing the banked program data.
    path = assemble(args.output, "rom-vector", vector_override=False)
    events, _ = player.run(path, 3)
    assert len([1 for _, r, _ in events if r == 0x5011]) > 300
    results.append(dict(name="rom-vector-initialization", passed=True))

    # Timer flags persist while disabled, and reading status acknowledges.
    extra = """
    sei
    ldx #100
wait_pending: dex
    bne wait_pending
    lda #0
    sta $401d
    lda $401d
    sta $4009
    lda $401d
    sta $4009
    lda $401b
    sta $4009
    lda $401c
    sta $4009
"""
    path = assemble(args.output, "disabled-ack", init_extra=extra, reload=300)
    events, _ = player.run(path, 2)
    markers = [v for _, r, v in events if r == 0x4009]
    assert markers[:4] == [0x80, 0, 44, 1], markers
    assert not any(r == 0x5011 for _, r, _ in events)
    results.append(dict(name="disable-does-not-acknowledge-and-reload-readable", passed=True))

    # A pending IRQ is delayed for one instruction after CLI. The write of
    # $55 must execute, then the handler, then the write of $66. Merely
    # checking I at the next opcode fetch fails this case.
    extra = """
    sei
    ldx #100
cli_wait: dex
    bne cli_wait
    lda #0
    sta $401d
    lda #$55
    cli
    sta $4009
    lda #$66
    sta $4009
"""
    path = assemble(args.output, "cli-delay", init_extra=extra)
    events, _ = player.run(path, 2)
    filtered = [(r, v) for _, r, v in events if r in (0x4009, 0x5011)]
    assert filtered[:3] == [(0x4009, 0x55), (0x5011, 0x81), (0x4009, 0x66)], filtered[:5]
    results.append(dict(name="sei-masking-cli-one-instruction-delay", passed=True))

    # IRQ support also makes the existing APU frame counter and DMC IRQ
    # signals visible to the CPU. Each handler must acknowledge its source.
    for name, extra in [
        ("apu-frame-irq", "lda #0\nsta $4017"),
        ("apu-dmc-irq", "lda #$8f\nsta $4010\nlda #0\nsta $4012\nsta $4013\nlda #$10\nsta $4015")
    ]:
        path = assemble(args.output, name, enable=False, init_extra=extra,
                        irq_extra="lda $4015\nsta $4009\nlda #0\nsta $4010")
        events, _ = player.run(path, 4)
        irq_count = len([1 for _, r, _ in events if r == 0x5011])
        assert irq_count > 0
        if name == "apu-dmc-irq":
            assert irq_count == 1
            assert any(r == 0x4009 and v & 0x80 and v not in (0xa1, 0xa2) for _, r, v in events)
        else:
            assert 3 <= irq_count <= 5
        results.append(dict(name=name, irq_count=irq_count, passed=True))

    # DMC DMA burns real CPU cycles; it must not pause the IRQ timer.
    path = assemble(args.output, "dmc-dma", dmc=True)
    events, cycles = player.run(path, 5)
    times = [t for t, r, _ in events if r == 0x5011]
    assert len(times) > 600
    assert abs((times[-1]-times[0]) - 224*(len(times)-1)) <= 10
    results.append(dict(name="dmc-dma-timer-keeps-clock", samples=len(times), cycles=cycles))

    # NSFe's NSF2 chunk uses the same runtime, not a header-only workaround.
    raw = (args.output / "rom-vector.nsf").read_bytes()
    def chunk(name, payload):
        return struct.pack("<I", len(payload)) + name + payload
    info = raw[8:14] + bytes([0,8,1,0])
    nsfe = b"NSFE" + chunk(b"INFO", info) + chunk(b"NSF2", b"\x10") + chunk(b"DATA", raw[128:]) + chunk(b"NEND", b"")
    path = args.output / "timer.nsfe"
    path.write_bytes(nsfe)
    events, _ = player.run(path, 3)
    assert len([1 for _, r, _ in events if r == 0x5011]) > 300
    results.append(dict(name="nsfe-nsf2-chunk-actual-execution", passed=True))

    for flags in [1, 0x20, 0x40, 0x80]:
        path = assemble(args.output, f"unsupported-{flags}", flags=flags)
        ptr = player.dll.NsfOpen(str(path).encode("utf-8"))
        if ptr:
            player.dll.NsfClose(ptr)
        assert not ptr, f"Unsupported feature {flags:x} silently accepted"
    results.append(dict(name="unsupported-nsf2-features-rejected", passed=True))
    path = args.output / "epsm-address-conflict.nsf"
    conflict = bytearray((args.output / "rom-vector.nsf").read_bytes())
    conflict[0x7b] |= 0x80
    path.write_bytes(conflict)
    ptr = player.dll.NsfOpen(str(path).encode("utf-8"))
    if ptr:
        player.dll.NsfClose(ptr)
    assert not ptr, "NSF2 timer and EPSM use overlapping register addresses"
    results.append(dict(name="epsm-timer-address-conflict-rejected", passed=True))
    if args.reference_nsf:
        events, _ = player.run(args.reference_nsf, 480)
        dac = [(t, v) for t, r, v in events if r == 0x4011]
        assert len(dac) > 4000
        assert {v for _, v in dac} == {0, 31}
        assert not any(r == 0x400b for _, r, _ in events), "Reference test took its bad ROM IRQ vector"
        first = [t for t, _ in dac if t < 1780000]
        intervals = [b-a for a, b in zip(first, first[1:])]
        assert min(intervals) >= 1983 and max(intervals) <= 1993
        results.append(dict(name="rainwarrior-nsf2-irq-reference",
                            path=str(args.reference_nsf), samples=len(dac),
                            sha256=hashlib.sha256(args.reference_nsf.read_bytes()).hexdigest(),
                            initial_interval_min=min(intervals), initial_interval_max=max(intervals)))
    if args.baseline_dll:
        baseline = Player(args.baseline_dll)
        for path in args.legacy:
            before, cycles_before = baseline.run(path, 40)
            after, cycles_after = player.run(path, 40)
            # The new expansion callback is intentionally more complete.
            before = [x for x in before if 0x4000 <= x[1] <= 0x401f]
            after = [x for x in after if 0x4000 <= x[1] <= 0x401f]
            assert before == after and cycles_before == cycles_after, f"Legacy NSF changed: {path}"
            results.append(dict(name="legacy-nsf-exact-register-cycles",
                                path=str(path), writes=len(after), cycles=cycles_after))
    report = dict(passed=True, dll=str(args.dll.resolve()),
                  dll_sha256=hashlib.sha256(args.dll.read_bytes()).hexdigest(), cases=results)
    (args.output / "nsf2-timer-results.json").write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
