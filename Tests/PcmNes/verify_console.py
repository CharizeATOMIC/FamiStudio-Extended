"""Run the NES cartridge in an independent libretro core; capture video and audio.

Usage: python verify_console.py <core DLL> <demo.nes> <output directory> <template.lbl>
Requires numpy and Pillow. The core is a test dependency, not part of the release.
API: https://docs.libretro.com/development/cores/developing-cores/
"""
from pathlib import Path
import ctypes as C
import sys, json, wave, hashlib
import numpy as np
from PIL import Image

core_path, rom, out = [Path(x).resolve() for x in sys.argv[1:4]]
fixture_path = rom.parent / 'fixture.json'
fixture = json.loads(fixture_path.read_text()) if fixture_path.is_file() else {}
multiple_songs = len(fixture.get('tracks', [0, 1])) > 1
from verify import read_labels
labels = read_labels(Path(sys.argv[4]))
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
screenshots = {120: 'first-song.png', 1050: 'second-song.png'}
video_checks = []

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
        if v.key in (b'fceumm_region', b'mesen_region'):
            v.value = b'PAL' if rom.read_bytes()[9] & 1 else b'NTSC'
            return True
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
    video_checks.append(dict(frame=frame, distinct_colors=len(np.unique(rgb.reshape(-1, 3), axis=0)), nonblack_pixels=int(np.count_nonzero(np.max(rgb, axis=2) > 30))))
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
def input_state(port, device, index, key):
    # RETRO_DEVICE_ID_JOYPAD_RIGHT = 7, LEFT = 6. Each held press changes
    # once; the opposite direction also checks release/rearm behavior.
    pressed = (key == 7 and 850 <= frame <= 855) or (key == 6 and 1250 <= frame <= 1255)
    return int(multiple_songs and port == 0 and device == 1 and index == 0 and pressed)

for name, callback, kind in [('environment', environment, Env), ('video_refresh', video, Video), ('audio_sample', audio, Audio), ('audio_sample_batch', batch, Batch), ('input_poll', poll, Poll), ('input_state', input_state, Input)]:
    method = getattr(core, 'retro_set_' + name); method.argtypes = [kind]; method.restype = None; method(callback)
    # Mesen constructs the console in retro_init; its video/audio setters
    # dereference that console, so initialization must precede those setters.
    if name == 'environment':
        core.retro_init()
core.retro_get_system_info.argtypes = [C.POINTER(Info)]
core.retro_get_system_av_info.argtypes = [C.POINTER(AV)]
core.retro_load_game.argtypes = [C.POINTER(Game)]; core.retro_load_game.restype = C.c_bool
core.retro_get_memory_data.argtypes = [C.c_uint]; core.retro_get_memory_data.restype = C.c_void_p
core.retro_get_memory_size.argtypes = [C.c_uint]; core.retro_get_memory_size.restype = C.c_size_t
info = Info(); core.retro_get_system_info(C.byref(info))
data = C.create_string_buffer(rom.read_bytes())
game = Game(str(rom).encode(), C.cast(data, C.c_void_p), len(data)-1, None)
assert core.retro_load_game(C.byref(game)), 'NES core rejected cartridge'
av = AV(); core.retro_get_system_av_info(C.byref(av))
assert (av.timing.fps < 55) == bool(rom.read_bytes()[9] & 1), ('wrong core region', av.timing.fps)
ram = core.retro_get_memory_data(2)
assert ram and core.retro_get_memory_size(2) >= 2048

trace, song_trace, debug_trace = [], [], []
for frame in range(1700):
    core.retro_run()
    boundaries.append(audio_frames)
    state = C.string_at(ram, 2048)
    trace.append(state[labels['frame_count']])
    song_trace.append(state[labels['song_index']])
    if 45 <= frame <= 80:
        row = dict(video_frame=frame)
        for name in ('frame_count', 'frame_seen', 'song_index', 'irq_busy', 'blank_deferred',
                     'blank_samples', 'pcm_phase', 'pcm_irq_line', 'buttons', 'previous_buttons'):
            if name in labels:
                row[name] = state[labels[name]]
        for name, count in (('famistudio_pcm_path', 4), ('famistudio_pcm_active', 4),
                            ('famistudio_pcm_pending', 4), ('famistudio_pcm_owns', 4)):
            if name in labels:
                row[name] = list(state[labels[name]:labels[name]+count])
        if 'famistudio_pcm_sample_ptr' in labels:
            pointer = labels['famistudio_pcm_sample_ptr'] + 32
            row['mmc5_descriptor'] = list(state[pointer:pointer+11])
        debug_trace.append(row)
samples = np.concatenate(audio_chunks)
rate = round(av.timing.sample_rate)
with wave.open(str(out/'cartridge-audio.wav'), 'wb') as f:
    f.setnchannels(2); f.setsampwidth(2); f.setframerate(rate)
    f.writeframes(samples.astype('<i2').tobytes())
increments = [(b-a)&255 for a,b in zip(trace, trace[1:])]
(out/'console-clock-trace.json').write_text(json.dumps(dict(
    rom_sha256=hashlib.sha256(rom.read_bytes()).hexdigest(),
    frame_count=trace, song_index=song_trace, debug_trace=debug_trace), indent=2))
windows = [(start, sum(increments[start:start+32])) for start in range(60, 1650, 32)
           if not (multiple_songs and any(start < end and start+32 >= begin
                                        for begin, end in ((845, 885), (1245, 1285))))]
assert all(song == 0 for song in song_trace[:850]), 'Song changed without any controller input.'
assert all(song == int(multiple_songs) for song in song_trace[900:1250]), 'Held Right selected more than once or selection was lost.'
assert all(song == 0 for song in song_trace[1300:]), 'Released button did not rearm for Left.'
assert all(abs(count-32) <= 1 for start,count in windows), windows
assert song_trace[120] == 0 and song_trace[1050] == int(multiple_songs) and song_trace[-1] == 0, song_trace[840:885]
transitions = [dict(video_frame=i, before=song_trace[i-1], after=song_trace[i])
               for i in range(1, len(song_trace)) if song_trace[i] != song_trace[i-1]]
assert len(transitions) == (2 if multiple_songs else 0), ('Unsolicited or duplicate song change', transitions)
assert all(item['distinct_colors'] >= 2 and item['nonblack_pixels'] > 500 for item in video_checks), video_checks
assert len(video_checks) == 2
energy = []
for start in range(60, 1620, 60):
    if multiple_songs and any(start <= press < start+60 for press in (855, 1255)): continue
    audio_slice = samples[boundaries[start]:boundaries[start+60]].astype(float)
    rms = float(np.sqrt(np.mean(audio_slice**2)))
    assert rms > 10, ('silent interval', start, rms)
    energy.append(dict(start_video_frame=start, rms=rms))
result = dict(passed=True, core=info.name.decode(), core_version=info.version.decode(),
    core_sha256=hashlib.sha256(core_path.read_bytes()).hexdigest(),
    rom_sha256=hashlib.sha256(rom.read_bytes()).hexdigest(), fps=av.timing.fps,
    sample_rate=rate, video_frames=1700, images=video_checks,
    sample_frames=len(samples), audio_intervals=energy,
    music_clock_32frame_windows=windows, left_right_song_selection_passed=True if multiple_songs else None,
    no_unsolicited_song_change=True,
    held_buttons_each_change_song_once=True if multiple_songs else None,
    controller_right_press_frames=[850, 855] if multiple_songs else [],
    controller_left_press_frames=[1250, 1255] if multiple_songs else [],
    song_transitions=transitions,
    physical_hardware=False,
    scope='Independent libretro real CPU/mapper/PPU/APU execution: visible static screen, synthetic audio, sustained update clock, controller song selection. PCM byte correctness is covered separately by verify.py.')
(out/'console-verification.json').write_text(json.dumps(result, indent=2)+'\n')
core.retro_unload_game(); core.retro_deinit()
print(json.dumps(result, indent=2))
print('PASS independent PCM NES CPU/mapper/PPU/APU, screen, sustained audio, clock and song selection')
