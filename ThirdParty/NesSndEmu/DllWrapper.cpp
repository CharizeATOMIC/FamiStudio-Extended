#include "Mixer_Apu.h"
#include <mutex>

#if defined(LINUX) || defined(__clang__)
#define __stdcall
#define __cdecl
#endif

// Must match NesApu.cs.
#define NUM_WAV_EXPORT_APU 8 

// 0  = Song player
// 1  = Instrument player
// 2+ = WAV/Video export, one for each potential thread.
static std::unique_ptr<Mixer_Apu> contexts[650];
static std::mutex context_mutex;
static Mixer_Apu& get_apu(int index) {
    std::lock_guard<std::mutex> lock(context_mutex);
    if (!contexts[index]) contexts[index].reset(new Mixer_Apu());
    return *contexts[index];
}
extern "C" int __stdcall NesApuCreate() {
    std::lock_guard<std::mutex> lock(context_mutex);
    for (int i=2+NUM_WAV_EXPORT_APU;i<650;i++) if (!contexts[i]) { contexts[i].reset(new Mixer_Apu()); return i; }
    return -1;
}
extern "C" void __stdcall NesApuDestroy(int index) {
    std::lock_guard<std::mutex> lock(context_mutex);
    if (index>=2+NUM_WAV_EXPORT_APU && index<650) contexts[index].reset();
}
extern "C" void __stdcall NesApuConfigureOutput(int index,int stereo,int count,const int* exps,const int* channels,const int* pans) {
    get_apu(index).configure(stereo!=0,count,exps,channels,pans);
}
extern "C" void __stdcall NesApuSetPan(int index,int stem,int pan) { get_apu(index).set_pan(stem,pan); }

extern "C" int __stdcall NesApuInit(int apuIdx, int sampleRate, int bass_freq, int pal, int seperate_tnd, int expansions, int (__cdecl *dmcReadFunc)(void* user_data, cpu_addr_t))
{
	if (get_apu(apuIdx).sample_rate(sampleRate, pal, seperate_tnd))
		return -1;

	get_apu(apuIdx).set_audio_expansions(expansions);
	get_apu(apuIdx).dmc_reader(dmcReadFunc, (void*)apuIdx);
	get_apu(apuIdx).bass_freq(0, bass_freq); // Any non FDS value will do for initialisation.

	return 0;
}

extern "C" void __stdcall NesApuWriteRegister(int apuIdx, unsigned int addr, int data)
{
	get_apu(apuIdx).write_register(addr, data);
}

extern "C" int __stdcall NesApuSamplesAvailable(int apuIdx)
{
	return get_apu(apuIdx).samples_avail();
}

extern "C" int __stdcall NesApuReadSamples(int apuIdx, blip_sample_t* buffer, int bufferSize)
{
	return get_apu(apuIdx).read_samples(buffer, bufferSize);
}

extern "C" void __stdcall NesApuRemoveSamples(int apuIdx, int count)
{
	return get_apu(apuIdx).remove_samples(count);
}

extern "C" int __stdcall NesApuReadStatus(int apuIdx)
{
	return get_apu(apuIdx).read_status();
}

extern "C" void __stdcall NesApuEndFrame(int apuIdx)
{
	get_apu(apuIdx).end_frame();
}
extern "C" void __stdcall NesApuSetUpdateRateMultiplier(int apuIdx, int value)
{
	get_apu(apuIdx).set_update_rate_multiplier(value);
}

extern "C" void __stdcall NesApuReset(int apuIdx)
{
	get_apu(apuIdx).reset();
}

extern "C" void __stdcall NesApuEnableChannel(int apuIdx, int exp, int idx, int enable)
{
	get_apu(apuIdx).enable_channel(exp, idx, enable != 0);
}

extern "C" void __stdcall NesApuStartSeeking(int apuIdx)
{
	get_apu(apuIdx).start_seeking();
}

extern "C" void __stdcall NesApuStopSeeking(int apuIdx)
{
	get_apu(apuIdx).stop_seeking();
}

extern "C" int __stdcall NesApuIsSeeking(int apuIdx)
{
	return get_apu(apuIdx).is_seeking();
}

extern "C" void __stdcall NesApuTrebleEq(int apuIdx, int expansion, double treble_amount, int treble_freq, int sample_rate)
{
	get_apu(apuIdx).treble_eq(expansion, treble_amount, treble_freq, sample_rate);
}

extern "C" void __stdcall NesApuBassFilter(int apuIdx, int expansion, int bass_freq)
{
	get_apu(apuIdx).bass_freq(expansion, bass_freq);
}

extern "C" int __stdcall NesApuGetAudioExpansions(int apuIdx)
{
	return get_apu(apuIdx).get_audio_expansions();
}

extern "C" void __stdcall NesApuSetExpansionVolume(int apuIdx, int expansion, double volume)
{
	get_apu(apuIdx).set_expansion_volume(expansion, volume);
}

extern "C" int __stdcall NesApuSkipCycles(int apuIdx, int cycles)
{
	return get_apu(apuIdx).skip_cycles(cycles);
}

extern "C" void __stdcall NesApuGetRegisterValues(int apuIdx, int exp, void* regs)
{
	get_apu(apuIdx).get_register_values(exp, regs);
}

extern "C" int __stdcall NesApuGetN163WavePos(int apuIdx, int n163ChanIndex)
{
	return get_apu(apuIdx).get_namco_wave_pos(n163ChanIndex);
}

extern "C" int __stdcall NesApuGetFdsWavePos(int apuIdx)
{
	return get_apu(apuIdx).get_fds_wave_pos();
}

extern "C" void __stdcall NesApuResetTriggers(int apuIdx)
{
	return get_apu(apuIdx).reset_triggers();
}

extern "C" int __stdcall NesApuGetChannelTrigger(int apuIdx, int exp, int idx)
{
	return get_apu(apuIdx).get_channel_trigger(exp, idx);
}

extern "C" void __stdcall NesApuSetN163Mix(int apuIdx, int mix)
{
	return get_apu(apuIdx).set_namco_mix(mix);
}
