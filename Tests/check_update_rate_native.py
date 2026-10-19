"""Compare a previous native build with optional subframe scheduling.

Usage: python Tests/check_update_rate_native.py old.dll new.dll report.json
Synthetic register programs only. No game data or additional packages needed.
"""
import ctypes as c
import hashlib
import json
from pathlib import Path
import sys

integer = c.c_int
callback_type = c.CFUNCTYPE(integer, c.c_void_p, c.c_uint)
callback = callback_type(lambda user, address: (address * 37) & 255)

class Native:
    def __init__(self, path):
        self.lib = c.CDLL(str(Path(path).resolve()))
        def bind(name, result, *args):
            fn = getattr(self.lib, 'NesApu' + name)
            fn.restype, fn.argtypes = result, args
            return fn
        self.create = bind('Create', integer)
        self.destroy = bind('Destroy', None, integer)
        self.configure = bind('ConfigureOutput', None, integer, integer, integer,
                              c.POINTER(integer), c.POINTER(integer), c.POINTER(integer))
        self.init = bind('Init', integer, integer, integer, integer, integer, integer, integer, callback_type)
        self.reset = bind('Reset', None, integer)
        self.write = bind('WriteRegister', None, integer, c.c_uint, integer)
        self.frame = bind('EndFrame', None, integer)
        self.available = bind('SamplesAvailable', integer, integer)
        self.read = bind('ReadSamples', integer, integer, c.POINTER(c.c_short), integer)
        self.rate = bind('SetUpdateRateMultiplier', None, integer, integer) if hasattr(self.lib, 'NesApuSetUpdateRateMultiplier') else None

    def render(self, pal, stereo, rate=1):
        index = self.create()
        chunks = []
        try:
            self.configure(index, int(stereo), 0, None, None, None)
            assert self.init(index, 44100, 16, int(pal), 0, 32, callback) == 0
            self.reset(index)
            if self.rate:
                self.rate(index, rate)
            def write(address, value): self.write(index, address, value)
            write(0x4015, 15)
            write(0x4000, 0xbf); write(0x4002, 173); write(0x4003, 8)
            write(0x4008, 0xff); write(0x400a, 211); write(0x400b, 8)
            write(0x400c, 0x23); write(0x400e, 7); write(0x400f, 0xf8)
            for address, value in {0: 173, 1: 0, 6: 5, 7: 0x36, 8: 13}.items():
                write(0xc000, address); write(0xe000, value)
            for n in range(97 * rate):
                # Identical physical-frame writes, held during extra subframes.
                if n % rate == 0:
                    frame = n // rate
                    write(0x4002, 173 + frame % 19)
                    if frame % 13 == 0:
                        write(0x400f, 8)
                        write(0x4010, 15); write(0x4012, 0); write(0x4013, 16); write(0x4015, 31)
                self.frame(index)
                count = self.available(index)
                buffer = (c.c_short * (count * (2 if stereo else 1)))()
                assert self.read(index, buffer, count) == count
                chunks.append(bytes(buffer))
            return b''.join(chunks), [len(x) // 2 // (2 if stereo else 1) for x in chunks]
        finally:
            self.destroy(index)

old, new = Native(sys.argv[1]), Native(sys.argv[2])
results = []
for pal in [False, True]:
    for stereo in [False, True]:
        expected, sizes = old.render(pal, stereo)
        actual, sizes_new = new.render(pal, stereo)
        assert expected == actual and sizes == sizes_new, (pal, stereo, '1x compatibility')
        for rate in [2, 4]:
            subdivided, sizes_subdivided = new.render(pal, stereo, rate)
            assert len(subdivided) == len(actual), (pal, stereo, rate, 'physical duration')
            assert max(sizes_subdivided) - min(sizes_subdivided) <= 1, 'uniform subframes'
            assert subdivided == actual, (pal, stereo, rate, 'unchanged physical-time PCM')
        results.append(dict(pal=pal, stereo=stereo, pcm_samples=len(expected)//2,
                            sha256=hashlib.sha256(expected).hexdigest()))
        print('PASS unchanged 1x PCM and uniform 2x/4x clocks', pal, stereo)
report = dict(passed=True, baseline_sha256=hashlib.sha256(Path(sys.argv[1]).read_bytes()).hexdigest(),
              candidate_sha256=hashlib.sha256(Path(sys.argv[2]).read_bytes()).hexdigest(),
              unchanged_default_pcm_scenarios=len(results), high_rate_clock_scenarios=len(results)*2,
              unchanged_high_rate_pcm_scenarios=len(results)*2,
              results=results)
Path(sys.argv[3]).write_text(json.dumps(report, indent=2)+'\n', encoding='utf-8')
