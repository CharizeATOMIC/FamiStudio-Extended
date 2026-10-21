"""MMC5 DAC and software-stream contract, using only Python's standard library.

Usage: python Tests/check_mmc5_pcm_native.py candidate.dll report.json [baseline.dll]
No cartridge/NSF assets, installed files, or application settings are modified.
"""
import ctypes as c
import hashlib
import json
from pathlib import Path
import sys

I, U = c.c_int, c.c_uint
DMC = c.CFUNCTYPE(I, c.c_void_p, U)
dmc = DMC(lambda user, address: 0x55)
checks = []


def check(condition, name):
    if not condition:
        raise AssertionError(name)
    checks.append(name)


class Native:
    def __init__(self, path):
        self.path = Path(path).resolve()
        self.dll = c.CDLL(str(self.path))
        signatures = {
            "Create": (I,),
            "Destroy": (None, I),
            "ConfigureOutput": (None, I, I, I, c.POINTER(I), c.POINTER(I), c.POINTER(I)),
            "Init": (I, I, I, I, I, I, I, DMC),
            "Reset": (None, I),
            "WriteRegister": (None, I, U, I),
            "EnableChannel": (None, I, I, I, I),
            "SetPan": (None, I, I, I),
            "SkipCycles": (I, I, I),
            "EndFrame": (None, I),
            "SamplesAvailable": (I, I),
            "ReadSamples": (I, I, c.POINTER(c.c_short), I),
            "SetUpdateRateMultiplier": (None, I, I),
            "StartSeeking": (None, I),
            "StopSeeking": (None, I),
            "StartMmc5Pcm": (I, I, c.POINTER(c.c_ubyte), I, U, I, I, I),
            "StopMmc5Pcm": (None, I),
            "SetMmc5PcmVolume": (None, I, I),
            "GetMmc5PcmDac": (I, I),
            "GetMmc5PcmPosition": (I, I),
        }
        for name, signature in signatures.items():
            if hasattr(self.dll, "NesApu" + name):
                fn = getattr(self.dll, "NesApu" + name)
                fn.restype, fn.argtypes = signature[0], signature[1:]
                setattr(self, name, fn)

    def context(self, pal=False, rate=1, pans=None):
        return Context(self, pal, rate, pans)


class Context:
    def __init__(self, native, pal, rate, pans):
        self.native, self.stereo = native, pans is not None
        self.id = native.Create()
        if pans is None:
            native.ConfigureOutput(self.id, 0, 0, None, None, None)
        else:
            native.ConfigureOutput(self.id, 1, 3, (I * 3)(4, 4, 4),
                                   (I * 3)(0, 1, 2), (I * 3)(*pans))
        assert native.Init(self.id, 44100, 16, int(pal), 0, 8, dmc) == 0
        native.Reset(self.id)
        native.SetUpdateRateMultiplier(self.id, rate)
        for channel in range(5):
            native.EnableChannel(self.id, 0, channel, 0)
        for channel in range(3 if hasattr(native, "StartMmc5Pcm") else 2):
            native.EnableChannel(self.id, 4, channel, 1)

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.native.Destroy(self.id)

    def write(self, address, value):
        self.native.WriteRegister(self.id, address, value)

    def start(self, values, period=223 << 16, loop=(-1, -1), volume=15):
        data = (c.c_ubyte * len(values))(*values)
        result = self.native.StartMmc5Pcm(self.id, data, len(values), period, *loop, volume)
        return result, data

    def skip(self, cycles):
        return self.native.SkipCycles(self.id, cycles)

    def frame(self):
        self.native.EndFrame(self.id)
        n = self.native.SamplesAvailable(self.id)
        data = (c.c_short * (n * (2 if self.stereo else 1)))()
        assert self.native.ReadSamples(self.id, data, n) == n
        return bytes(data)

    def frames(self, count):
        return b"".join(self.frame() for _ in range(count))

    @property
    def dac(self):
        return self.native.GetMmc5PcmDac(self.id)

    @property
    def position(self):
        return self.native.GetMmc5PcmPosition(self.id)


native = Native(sys.argv[1])
with native.context() as ctx:
    for value in [1, 127, 128, 129, 254, 255]:
        ctx.write(0x5011, value)
        check(ctx.dac == value, f"eight-bit DAC {value}")
        ctx.write(0x5011, 0)
        check(ctx.dac == value, f"zero holds DAC {value}")
    ctx.write(0x5015, 0)
    check(ctx.dac == 255, "5015 disables only pulses")
    ctx.write(0x5010, 1)
    ctx.write(0x5011, 7)
    check(ctx.dac == 255, "read mode ignores direct writes")
    ctx.write(0x5010, 0x80)
    ctx.write(0x5011, 200)
    check(ctx.dac == 200, "IRQ bit alone permits write mode")
    for address in [0x5008, 0x5009, 0x500f, 0x5012, 0x5013, 0x5014]:
        ctx.write(address, 0xff)
        check(ctx.dac == 200, f"unmapped address {address:04x} safe")

with native.context() as ctx:
    success, pinned = ctx.start([100, 200, 0, 255], 10 << 16)
    check(success == 1 and ctx.dac == 100 and ctx.position == 0, "start immediate first sample")
    pinned[1] = 1
    ctx.skip(9)
    check(ctx.dac == 100 and ctx.position == 0, "holds until exact sample deadline")
    ctx.skip(1)
    check(ctx.dac == 200 and ctx.position == 1, "copies caller bytes; exact deadline")
    ctx.skip(10)
    check(ctx.dac == 200 and ctx.position == 2, "zero stream byte holds DAC")
    ctx.skip(10)
    check(ctx.dac == 255 and ctx.position == 3, "high-bit stream byte retained")
    ctx.skip(10)
    check(ctx.dac == 128 and ctx.position == -1, "natural end returns to center")
    check(ctx.start([7, 9], loop=(0, 2))[0] == 1, "restart after completed sample")
    native.SetMmc5PcmVolume(ctx.id, 0)
    check(ctx.dac == 128, "volume zero is center, not ignored code zero")
    native.SetMmc5PcmVolume(ctx.id, 15)
    check(ctx.dac == 7, "live volume restores held source value")
    native.StopMmc5Pcm(ctx.id)
    check(ctx.dac == 128 and ctx.position == -1, "explicit stop releases stream")

with native.context() as ctx:
    ctx.start([10, 20, 30, 40, 50], (10 << 16) + 32768, (1, 4))
    previous_deadline = 0
    for deadline, value, pos in [(10, 10, 0), (11, 20, 1), (21, 30, 2),
                                 (31, 30, 2), (32, 40, 3), (42, 20, 1)]:
        ctx.skip(deadline - previous_deadline)
        previous_deadline = deadline
        check(ctx.dac == value and ctx.position == pos, f"fractional deadline/loop {deadline}")
    for data, period, loop, volume in [
        ([], 1 << 16, (-1, -1), 15),
        ([1], 65535, (-1, -1), 15),
        ([1], 1 << 16, (0, 0), 15),
        ([1], 1 << 16, (0, 2), 15),
        ([1], 1 << 16, (-1, 1), 15),
        ([1], 1 << 16, (-1, -1), 16),
    ]:
        before = ctx.dac, ctx.position
        check(ctx.start(data, period, loop, volume)[0] == 0 and
              before == (ctx.dac, ctx.position), f"invalid start preserves active stream {period}/{loop}/{volume}")

# Explicit timed CPU register writes are an independent reference for the
# streaming implementation. Both begin at CPU cycle 8; STA costs four cycles.
values = [100 + (i * 29) % 57 for i in range(100)]
with native.context() as stream, native.context() as writes:
    stream.write(0x5010, 0)
    stream.skip(4)
    stream.start(values)
    writes.write(0x5010, 0)
    writes.write(0x5011, values[0])
    for value in values[1:] + [128]:
        writes.skip(223 - 4)
        writes.write(0x5011, value)
    check(stream.frames(4) == writes.frames(4), "stream equals actual timestamped 5011 writes byte-for-byte")


def render_stream(pal, rate, pans=None):
    with native.context(pal, rate, pans) as ctx:
        ctx.start([100, 120, 156, 136, 128, 105, 144], (223 << 16) + 14567, (1, 7))
        chunks = []
        for frame in range(40 * rate):
            # Parameter changes occur at equal physical times in all modes.
            if frame == 9 * rate:
                native.SetMmc5PcmVolume(ctx.id, 7)
            if frame == 17 * rate:
                native.SetMmc5PcmVolume(ctx.id, 15)
            if frame == 28 * rate:
                native.StopMmc5Pcm(ctx.id)
            chunks.append(ctx.frame())
        return b"".join(chunks)


for pal in [False, True]:
    for pans in [None, [50, 50, 50], [50, 50, 0], [50, 50, 100]]:
        reference = render_stream(pal, 1, pans)
        check(any(reference), f"audible PCM pal={pal} pans={pans}")
        for rate in [2, 4]:
            check(reference == render_stream(pal, rate, pans),
                  f"unchanged sample timing at {rate}x pal={pal} pans={pans}")
        if pans:
            samples = (c.c_short * (len(reference) // 2)).from_buffer_copy(reference)
            if pans[2] == 0:
                check(any(samples[::2]) and not any(samples[1::2]), f"PCM left stem pal={pal}")
            if pans[2] == 100:
                check(any(samples[1::2]) and not any(samples[::2]), f"PCM right stem pal={pal}")
            if pans[2] == 50:
                check(list(samples[::2]) == list(samples[1::2]), f"PCM centered stem pal={pal}")

with native.context(pans=[50, 50, 0]) as ctx:
    ctx.start([104, 152], 220 << 16, (0, 2))
    ctx.frames(10)
    native.SetPan(ctx.id, 2, 100)
    samples = (c.c_short * 200000)()
    data = ctx.frames(10)
    samples = (c.c_short * (len(data) // 2)).from_buffer_copy(data)
    check(not any(samples[::2]) and any(samples[1::2]), "live pan moves PCM stem")
    native.EnableChannel(ctx.id, 4, 2, 0)
    ctx.frames(30)  # Let the DC filter settle after muting.
    muted = ctx.frames(3)
    check(not any(muted) and ctx.position >= 0, "mute silences voice while sample clock continues")
    native.EnableChannel(ctx.id, 4, 2, 1)
    check(any(ctx.frames(3)), "unmute resumes running sample")

# Accurate seek advances the same clock and discards output rather than
# retriggering from the last note. The editor selects this path for PCM songs.
with native.context() as playback, native.context() as seek:
    for ctx in [playback, seek]:
        ctx.start([90, 100, 130, 166, 144], (199 << 16) + 1, (0, 5))
    playback.frames(37)
    seek.frames(37)
    check(playback.position == seek.position and playback.dac == seek.dac and
          playback.frames(10) == seek.frames(10), "accurate seek retains sample phase and filter state")

if len(sys.argv) > 3:
    baseline = Native(sys.argv[3])

    def render_old_squares(api, pal, rate):
        with api.context(pal, rate) as ctx:
            ctx.write(0x5015, 3)
            ctx.write(0x5000, 0xbf)
            ctx.write(0x5002, 253)
            ctx.write(0x5003, 8)
            ctx.write(0x5004, 0x79)
            ctx.write(0x5006, 201)
            ctx.write(0x5007, 8)
            return ctx.frames(50 * rate)

    for pal in [False, True]:
        for rate in [1, 2, 4]:
            check(render_old_squares(native, pal, rate) == render_old_squares(baseline, pal, rate),
                  f"legacy two-pulse audio unchanged pal={pal} rate={rate}")

report = {"passed": True, "checks": len(checks), "details": checks,
          "candidate_sha256": hashlib.sha256(native.path.read_bytes()).hexdigest(),
          "scope": "Native editor DAC, timestamp stream, PAL/NTSC, 1/2/4x, stems and legacy pulses; no physical hardware claim"}
Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"PASS all {len(checks)} MMC5 PCM native checks")
