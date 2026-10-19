"""Run the NES cartridge in an independent libretro core; capture video and audio.

Usage: python verify.py <libretro core DLL/so/dylib> <demo.nes> <output directory>
Requires numpy and Pillow. The core is a test dependency, not part of the release.
API: https://docs.libretro.com/development/cores/developing-cores/
"""
from pathlib import Path
import ctypes as C
import sys, json, wave, hashlib
import numpy as np
from PIL import Image

core_path, rom, out = [Path(x).resolve() for x in sys.argv[1:4]]
multiplier = int(sys.argv[4])
out.mkdir(parents=True, exist_ok=True)
core = C.CDLL(str(core_path))
class Variable(C.Structure): _fields_ = [('key', C.c_char_p), ('value', C.c_char_p)]
class Game(C.Structure): _fields_ = [('path', C.c_char_p), ('data', C.c_void_p), ('size', C.c_size_t), ('meta', C.c_char_p)]
class Info(C.Structure): _fields_ = [('name', C.c_char_p), ('version', C.c_char_p), ('extensions', C.c_char_p), ('fullpath', C.c_bool), ('extract', C.c_bool)]
class Geometry(C.Structure): _fields_ = [('width', C.c_uint), ('height', C.c_uint), ('max_width', C.c_uint), ('max_height', C.c_uint), ('aspect', C.c_float)]
class Timing(C.Structure): _fields_ = [('fps', C.c_double), ('sample_rate', C.c_double)]
class AV(C.Structure): _fields_ = [('geometry', Geometry), ('timing', Timing)]
Env = C.CFUNCTYPE(C.c_bool, C.c_uint, C.c_void_p)
Video = C.CFUNCTYPE(None, C.c_void_p, C.c_uint, C.c_uint, C.c_size_t)
Audio = C.CFUNCTYPE(None, C.c_int16, C.c_int16)
Batch = C.CFUNCTYPE(C.c_size_t, C.POINTER(C.c_int16), C.c_size_t)
Poll = C.CFUNCTYPE(None)
Input = C.CFUNCTYPE(C.c_int16, C.c_uint, C.c_uint, C.c_uint, C.c_uint)
pixel_format = 0
variables = {}
directory = str(out).encode()
frame = 0
audio_chunks, boundaries = [], [0]
audio_frames = 0
screenshots = {120: 'clock-screen.png'}

@Env
def environment(command, pointer):
    global pixel_format
    command &= 0xffff
    if command in (9, 31): C.cast(pointer, C.POINTER(C.c_char_p))[0] = directory; return True
    if command == 3: C.cast(pointer, C.POINTER(C.c_bool))[0] = True; return True
    if command == 10: pixel_format = C.cast(pointer, C.POINTER(C.c_int))[0]; return pixel_format in (0, 1, 2)
    if command == 16:
        items = C.cast(pointer, C.POINTER(Variable)); i = 0
        while items[i].key:
            variables[items[i].key] = items[i].value.split(b';', 1)[1].strip().split(b'|')[0]; i += 1
        return True
    if command == 15:
        v = C.cast(pointer, C.POINTER(Variable)).contents
        if v.key not in variables: return False
        v.value = variables[v.key]; return True
    if command == 17: C.cast(pointer, C.POINTER(C.c_bool))[0] = False; return True
    if command in (39, 52): C.cast(pointer, C.POINTER(C.c_uint))[0] = 0; return True
    if command == 47: C.cast(pointer, C.POINTER(C.c_uint))[0] = 3; return True
    return command in (6, 8, 11, 18, 35, 37)

@Video
def video(pointer, width, height, pitch):
    if not pointer or frame not in screenshots: return
    raw = C.string_at(pointer, pitch * height)
    if pixel_format == 1:
        pixels = np.frombuffer(raw, np.uint32).reshape(height, pitch//4)[:, :width]
        rgb = np.stack([(pixels >> 16) & 255, (pixels >> 8) & 255, pixels & 255], axis=-1).astype(np.uint8)
    else:
        pixels = np.frombuffer(raw, np.uint16).reshape(height, pitch//2)[:, :width].astype(np.uint32)
        red_shift, green_mask = (11, 63) if pixel_format == 2 else (10, 31)
        rgb = np.stack([((pixels >> red_shift) & 31)*255//31, ((pixels >> 5) & green_mask)*255//green_mask, (pixels & 31)*255//31], axis=-1).astype(np.uint8)
    Image.fromarray(rgb).resize((width*3, height*3), Image.Resampling.NEAREST).save(out / screenshots[frame])

@Audio
def audio(left, right):
    global audio_frames
    audio_chunks.append(np.array([[left, right]], dtype=np.int16)); audio_frames += 1
@Batch
def batch(pointer, count):
    global audio_frames
    audio_chunks.append(np.ctypeslib.as_array(pointer, shape=(count*2,)).copy().reshape(-1, 2)); audio_frames += count
    return count
@Poll
def poll(): pass
@Input
def input_state(port, device, index, key): return 0

for name, callback, kind in [('environment', environment, Env), ('video_refresh', video, Video), ('audio_sample', audio, Audio), ('audio_sample_batch', batch, Batch), ('input_poll', poll, Poll), ('input_state', input_state, Input)]:
    method = getattr(core, 'retro_set_' + name); method.argtypes = [kind]; method.restype = None; method(callback)
core.retro_get_system_info.argtypes = [C.POINTER(Info)]
core.retro_get_system_av_info.argtypes = [C.POINTER(AV)]
core.retro_load_game.argtypes = [C.POINTER(Game)]; core.retro_load_game.restype = C.c_bool
core.retro_get_memory_data.argtypes = [C.c_uint]; core.retro_get_memory_data.restype = C.c_void_p
core.retro_get_memory_size.argtypes = [C.c_uint]; core.retro_get_memory_size.restype = C.c_size_t
core.retro_init()
info = Info(); core.retro_get_system_info(C.byref(info))
data = C.create_string_buffer(rom.read_bytes())
game = Game(str(rom).encode(), C.cast(data, C.c_void_p), len(data)-1, None)
assert core.retro_load_game(C.byref(game)), 'NES core rejected cartridge'
av = AV(); core.retro_get_system_av_info(C.byref(av))
ram = core.retro_get_memory_data(2)
assert ram and core.retro_get_memory_size(2) >= 2048

trace = []
for frame in range(660):
    core.retro_run()
    boundaries.append(audio_frames)
    state=C.string_at(ram,3)
    trace.append(state[1]|(state[2]<<8))
samples=np.concatenate(audio_chunks)
rate=round(av.timing.sample_rate)
with wave.open(str(out/'clock-audio.wav'),'wb') as f:
    f.setnchannels(2);f.setsampwidth(2);f.setframerate(rate);f.writeframes(samples.astype('<i2').tobytes())
increments=[b-a for a,b in zip(trace[120:],trace[121:])]
actual_rate=(trace[-1]-trace[120])/(len(trace)-121)
assert abs(actual_rate-multiplier)<.01,(actual_rate,multiplier)
assert max(increments)<=multiplier+1 and min(increments)>=multiplier-1
assert np.sqrt(np.mean(samples.astype(float)**2))>10,'Silent NES audio'
assert len(list(out.glob('*.png')))>=1
# Check the original 96-tick phrase in measured audio, allowing phase shifts
# caused by hardware IRQ timing and independently emulated APU oscillators.
mono=samples.mean(axis=1).astype(float)
block=256
energy=np.array([np.mean(mono[n:n+block]**2) for n in range(0,len(mono)-block,block)])
energy=energy[30:]-energy[30:].mean()
expected=96*av.timing.sample_rate/av.timing.fps/block
lags=range(round(expected)-4,round(expected)+5)
correlations=[np.corrcoef(energy[:-lag],energy[lag:])[0,1] for lag in lags]
peak=list(lags)[int(np.argmax(correlations))]
assert max(correlations)>.75,(peak,correlations)
result={'core':info.name.decode(),'core_version':info.version.decode(),'core_sha256':hashlib.sha256(core_path.read_bytes()).hexdigest(),'rom_sha256':hashlib.sha256(rom.read_bytes()).hexdigest(),'fps':av.timing.fps,'sample_rate':rate,'video_frames':660,'output_multiplier':multiplier,'measured_music_updates_per_video_frame':actual_rate,'min_updates_per_video_frame':min(increments),'max_updates_per_video_frame':max(increments),'phrase_energy_repeat_seconds':peak*block/av.timing.sample_rate,'expected_phrase_seconds':96/av.timing.fps,'phrase_repeat_correlation':max(correlations),'physical_hardware':False}
(out/'console-verification.json').write_text(json.dumps(result,indent=2)+'\n')
core.retro_unload_game();core.retro_deinit()
print(json.dumps(result,indent=2))
print('PASS independent NES CPU/PPU/APU emulation, visible screen, audible phrase and uniform update count')
