// MMC5 audio chip emulator for FamiStudio.
// Added to Nes_Snd_Emu by @NesBleuBleu

#ifndef NES_MMC5_H
#define NES_MMC5_H

#include <stdint.h>
#include <vector>
#include "Nes_Apu.h"

class Nes_Mmc5 {
public:
	Nes_Mmc5();
	~Nes_Mmc5();

	// See Nes_Apu.h for reference
	void reset();
	void volume(double);
	void treble_eq(blip_eq_t const&);
	void output(Blip_Buffer*);
	void osc_output(int index, Blip_Buffer*);
	void end_frame(cpu_time_t);
	void run_until(cpu_time_t);
	void write_register(cpu_time_t time, cpu_addr_t addr, int data);
	void get_register_values(struct mmc5_register_values* regs);
	void reset_triggers();
	int  get_channel_trigger(int idx) const;
	bool start_pcm(cpu_time_t time, const unsigned char* data, int length, uint32_t period_q16, int loop_start, int loop_end, int volume);
	void stop_pcm(cpu_time_t time);
	void release_pcm(cpu_time_t time) { run_until(time); pcm_playing = false; }
	void set_pcm_volume(cpu_time_t time, int volume);
	int get_pcm_dac() const { return pcm_dac; }
	int get_pcm_position() const { return pcm_playing ? pcm_position : -1; }

	enum { start_addr = 0x5000 };
	enum { end_addr   = 0x5015 };

	enum { shadow_regs_count = 8 };
	void start_seeking();
	void stop_seeking(blip_time_t& clock);
	void write_shadow_register(int addr, int data);

private:
	// noncopyable
	Nes_Mmc5(const Nes_Mmc5&);
	Nes_Mmc5& operator = (const Nes_Mmc5&);

	typedef Nes_Osc    Mmc5_Osc;
	typedef Nes_Square Mmc5_Square;

	enum { square_count = 2, osc_count = 3 };

	Mmc5_Osc*   oscs[square_count];
	Mmc5_Square square1;
	Mmc5_Square square2;
	Mmc5_Square::Synth square_synth; // shared by squares
	Blip_Synth<blip_good_quality,255> pcm_synth;
	Blip_Buffer* pcm_output;
	int pcm_control, pcm_dac, pcm_last_amp, pcm_trigger;
	std::vector<unsigned char> pcm_data;
	uint32_t pcm_period_q16;
	int64_t pcm_next_time_q16;
	int pcm_position, pcm_loop_start, pcm_loop_end, pcm_volume, pcm_raw_sample;
	bool pcm_playing;
	void write_pcm_dac(cpu_time_t time, int value);
	void write_pcm_sample(cpu_time_t time, int value);
	void run_pcm_until(cpu_time_t end_time);

	cpu_time_t last_time;
	int frame_period;
	int frame_delay; // cycles until frame counter runs next
	int frame; // current frame (0-3)
	int osc_enables;

	short shadow_regs[shadow_regs_count];
};

// Must match the definition in NesApu.cs.
struct mmc5_register_values
{
	// $5000 to $5007
	// $5015
	unsigned char regs[9];
	unsigned char ages[9];
};

#endif

