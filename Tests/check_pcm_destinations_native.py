"""Exercise actual 2A03/MMC5/VRC6 DAC streams and compare timestamped writes.

Usage: python Tests/check_pcm_destinations_native.py candidate.dll report.json [r11.dll]
Only synthetic samples are used. No installed DLL or user settings are touched.
"""
import ctypes as c
import hashlib
import json
from pathlib import Path
import sys

I, U, B = c.c_int, c.c_uint, c.c_ubyte
DMC = c.CFUNCTYPE(I, c.c_void_p, U)
dmc = DMC(lambda user, address: 0x55)
checks = []


def check(value, name):
    if not value:
        raise AssertionError(name)
    checks.append(name)


class Native:
    def __init__(self, path):
        self.path = Path(path).resolve()
        self.dll = c.CDLL(str(self.path))
        signatures = {
            "Create": (I,), "Destroy": (None, I),
            "ConfigureOutput": (None, I, I, I, c.POINTER(I), c.POINTER(I), c.POINTER(I)),
            "Init": (I, I, I, I, I, I, I, DMC),
            "Reset": (None, I), "WriteRegister": (None, I, U, I),
            "EnableChannel": (None, I, I, I, I), "SetPan": (None, I, I, I),
            "SkipCycles": (I, I, I), "EndFrame": (None, I),
            "SamplesAvailable": (I, I), "ReadSamples": (I, I, c.POINTER(c.c_short), I),
            "SetUpdateRateMultiplier": (None, I, I),
            "StartPcm": (I, I, I, c.POINTER(B), I, U, I, I, I),
            "StopPcm": (None, I, I), "ReleasePcm": (None, I, I),
            "SetPcmVolume": (None, I, I, I),
            "GetPcmDac": (I, I, I), "GetPcmPosition": (I, I, I),
        }
        for name, sig in signatures.items():
            if hasattr(self.dll, "NesApu" + name):
                fn = getattr(self.dll, "NesApu" + name)
                fn.restype, fn.argtypes = sig[0], sig[1:]
                setattr(self, name, fn)


class Context:
    def __init__(self, native, destination, pal=False, rate=1, pan=None, all_voices=False):
        self.n = native
        self.id = native.Create()
        self.destination = destination
        self.stereo = pan is not None
        expansion, channel = [(0, 4), (4, 2), (1, 0), (1, 1)][destination]
        self.voice = expansion, channel
        if self.stereo:
            native.ConfigureOutput(self.id, 1, 1, (I * 1)(expansion), (I * 1)(channel), (I * 1)(pan))
        else:
            native.ConfigureOutput(self.id, 0, 0, None, None, None)
        assert native.Init(self.id, 44100, 16, int(pal), 0, 9, dmc) == 0
        native.Reset(self.id)
        native.SetUpdateRateMultiplier(self.id, rate)
        for exp, count in [(0, 5), (1, 3), (4, 3)]:
            for ch in range(count):
                native.EnableChannel(self.id, exp, ch, int(all_voices or (exp, ch) == self.voice))

    def __enter__(self): return self
    def __exit__(self, *args): self.n.Destroy(self.id)
    def write(self, reg, val): self.n.WriteRegister(self.id, reg, val)
    def skip(self, cycles): return self.n.SkipCycles(self.id, cycles)
    def start(self, values, period=448 << 16, loop=(-1, -1), volume=15, destination=None):
        data = (B * len(values))(*values)
        result = self.n.StartPcm(self.id, self.destination if destination is None else destination,
                                 data, len(values), period, *loop, volume)
        return result, data
    def frames(self, count):
        output = []
        for _ in range(count):
            self.n.EndFrame(self.id)
            count = self.n.SamplesAvailable(self.id)
            data = (c.c_short * (count * (2 if self.stereo else 1)))()
            assert self.n.ReadSamples(self.id, data, count) == count
            output.append(bytes(data))
        return b"".join(output)
    @property
    def dac(self): return self.n.GetPcmDac(self.id, self.destination)
    @property
    def position(self): return self.n.GetPcmPosition(self.id, self.destination)


native = Native(sys.argv[1])
registers = [0x4011, 0x5011, 0x9000, 0xa000]


def quantized(destination, value, volume=15):
    scaled = 128 + int((value - 128) * volume / 15)
    if destination == 1:
        return max(1, scaled)
    return scaled >> (1 if destination == 0 else 4)


for destination in range(4):
    with Context(native, destination) as ctx:
        ok, source = ctx.start([0, 255, 128, 1], 10 << 16)
        check(ok == 1 and ctx.position == 0, f"{destination}: start at first byte")
        source[1] = 1
        ctx.skip(10)
        check(ctx.dac == quantized(destination, 255), f"{destination}: copied data, full input range")
        ctx.skip(10)
        check(ctx.dac == quantized(destination, 128), f"{destination}: neutral quantization")
        ctx.skip(20)
        check(ctx.position == -1 and ctx.dac == quantized(destination, 128), f"{destination}: natural end")
        ctx.start([32, 200, 240], (10 << 16) + 32768, (1, 3))
        ctx.skip(11)
        check(ctx.position == 1 and ctx.dac == quantized(destination, 200), f"{destination}: fractional deadline")
        native.SetPcmVolume(ctx.id, destination, 7)
        check(ctx.dac == quantized(destination, 200, 7), f"{destination}: current sample gain")
        native.SetPcmVolume(ctx.id, destination, 0)
        check(ctx.dac == quantized(destination, 128), f"{destination}: zero volume center")
        native.SetPcmVolume(ctx.id, destination, 15)
        held = ctx.dac
        native.ReleasePcm(ctx.id, destination)
        ctx.skip(100)
        check(ctx.position == -1 and ctx.dac == held, f"{destination}: yield does not alter DAC")
        ctx.start([32, 240], loop=(0, 2))
        native.StopPcm(ctx.id, destination)
        check(ctx.position == -1 and ctx.dac == quantized(destination, 128), f"{destination}: explicit stop")
        ctx.start([40, 200], loop=(0, 2))
        for period, loop, volume in [(0, (-1, -1), 15), (65535, (-1, -1), 15),
                                    (65536, (1, 1), 15), (65536, (-1, 2), 15),
                                    (65536, (0, 3), 15), (65536, (0, 2), 16)]:
            before = ctx.dac, ctx.position
            check(ctx.start([40, 200], period, loop, volume)[0] == 0 and before == (ctx.dac, ctx.position),
                  f"{destination}: invalid input preserves existing stream {period}/{loop}/{volume}")

    # A separate path writes real hardware registers at the same CPU timestamps.
    # Source values are nonzero: MMC5 code zero intentionally means hold.
    values = [1 + (i * 37) % 255 for i in range(40)]
    for pal in (False, True):
        for volume in (0, 7, 15):
            with Context(native, destination, pal) as stream, Context(native, destination, pal) as reference:
                setup = [0x4015, 0x5010, 0x9002, 0xa002][destination]
                setup_value = [15, 0, 128, 128][destination]
                stream.write(setup, setup_value)
                reference.write(setup, setup_value)
                stream.skip(4)
                stream.start(values, 448 << 16, volume=volume)
                for index, value in enumerate(values + [128]):
                    if index: reference.skip(448 - 4)
                    value = quantized(destination, value, volume)
                    reference.write(registers[destination], value | (0x80 if destination >= 2 else 0))
                check(stream.frames(4) == reference.frames(4),
                      f"{destination}: actual register audio exact pal={pal} gain={volume}")

    def render(rate, pal, pan):
        with Context(native, destination, pal, rate, pan) as ctx:
            ctx.start([44, 188, 99, 220, 128, 170], (448 << 16) + 12345, (1, 6))
            audio = []
            for frame in range(30 * rate):
                if frame == 8 * rate: native.SetPcmVolume(ctx.id, destination, 5)
                if frame == 15 * rate: native.SetPcmVolume(ctx.id, destination, 15)
                if frame == 23 * rate: native.StopPcm(ctx.id, destination)
                audio.append(ctx.frames(1))
            return b"".join(audio)

    for pal in (False, True):
        for pan in (None, 0, 50, 100):
            reference = render(1, pal, pan)
            check(any(reference), f"{destination}: audible pal={pal} pan={pan}")
            for rate in (2, 4):
                check(render(rate, pal, pan) == reference, f"{destination}: identical 1/{rate}x timing pal={pal} pan={pan}")
            if pan is not None:
                samples = (c.c_short * (len(reference) // 2)).from_buffer_copy(reference)
                left, right = samples[::2], samples[1::2]
                check((left == right if pan == 50 else any(left) and not any(right) if pan == 0 else not any(left) and any(right)),
                      f"{destination}: real voice stem pan={pan} pal={pal}")

    with Context(native, destination, pan=0) as ctx:
        ctx.start([32, 224], loop=(0, 2))
        ctx.frames(12)
        native.SetPan(ctx.id, 0, 100)
        audio = ctx.frames(5)
        samples = (c.c_short * (len(audio)//2)).from_buffer_copy(audio)
        check(not any(samples[::2]) and any(samples[1::2]), f"{destination}: live pan")
        native.EnableChannel(ctx.id, *ctx.voice, 0)
        ctx.frames(45)
        check(not any(ctx.frames(4)) and ctx.position >= 0, f"{destination}: mute retains running clock")
        native.EnableChannel(ctx.id, *ctx.voice, 1)
        check(any(ctx.frames(4)), f"{destination}: unmute")

# Several destinations, independent loop lengths and rates, plus distinct APUs
# used for repeated expansion chips. Pausing one stream must not affect another.
with Context(native, 0, all_voices=True) as first, Context(native, 0, all_voices=True) as second:
    for dest in range(4):
        first.start([80 + dest, 180 + dest, 220 - dest], (19 + dest * 11) << 16, (0, 3), destination=dest)
        second.start([80 + dest, 180 + dest, 220 - dest], (19 + dest * 11) << 16, (0, 3), destination=dest)
    first.skip(1000); second.skip(1000)
    for dest in range(4):
        check(native.GetPcmPosition(first.id, dest) == (1000 // (19 + dest * 11)) % 3,
              f"{dest}: simultaneous independent sample deadline")
    native.StopPcm(first.id, 2)
    first.skip(100); second.skip(100)
    check(native.GetPcmPosition(first.id, 2) == -1 and native.GetPcmPosition(second.id, 2) >= 0,
          "repeated chip instances have isolated sample state")
    for dest in (0, 1, 3):
        check(native.GetPcmPosition(first.id, dest) == native.GetPcmPosition(second.id, dest),
              f"{dest}: another destination stop has no side effects")

if len(sys.argv) > 3:
    baseline = Native(sys.argv[3])
    def legacy(api, pal, rate):
        with Context(api, 0, pal, rate, all_voices=True) as ctx:
            for register, value in [(0x4015, 0x1f), (0x4010, 15), (0x4012, 0),
                                    (0x4013, 8), (0x4011, 65), (0x4015, 0x0f), (0x4015, 0x1f),
                                    (0x4000, 0xbf), (0x4002, 253), (0x4003, 8),
                                    (0x9000, 0x6d), (0x9001, 120), (0x9002, 0x80),
                                    (0xa000, 0x4b), (0xa001, 100), (0xa002, 0x80),
                                    (0xb000, 32), (0xb001, 180), (0xb002, 0x80)]:
                ctx.write(register, value)
            return ctx.frames(40 * rate)
    for pal in (False, True):
        for rate in (1, 2, 4):
            check(legacy(native, pal, rate) == legacy(baseline, pal, rate),
                  f"legacy DPCM, APU pulse, VRC6 pulses and saw unchanged pal={pal} rate={rate}")

report = dict(passed=True, checks=len(checks), details=checks,
              candidate_sha256=hashlib.sha256(native.path.read_bytes()).hexdigest(),
              physical_hardware=False,
              scope="Native DAC streams versus explicit hardware writes, gain/quantization, stereo/stems, clocks, isolation and legacy audio")
Path(sys.argv[2]).write_text(json.dumps(report, indent=2) + "\n")
print(f"PASS all {len(checks)} PCM destination native checks")
