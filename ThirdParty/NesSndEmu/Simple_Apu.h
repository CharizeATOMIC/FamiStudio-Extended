
// NES 2A03 APU sound chip emulator with simpler interface

// Nes_Snd_Emu 0.1.7. Copyright (C) 2003-2005 Shay Green. GNU LGPL license.

#ifndef SIMPLE_APU_H
#define SIMPLE_APU_H

#include <vector>
#include "nes_apu/Nes_Apu.h"
#include "nes_apu/Nes_Vrc6.h"
#include "nes_apu/Nes_Vrc7.h"
#include "nes_apu/Nes_Fds.h"
#include "nes_apu/Nes_Mmc5.h"
#include "nes_apu/Nes_Namco.h"
#include "nes_apu/Nes_Sunsoft.h"
#include "nes_apu/Nes_EPSM.h"
#include "nes_apu/Nes_Fme7.h"

class Simple_Apu {
public:

	enum { expansion_none       = 0 };
	enum { expansion_vrc6       = 1 };
	enum { expansion_vrc7       = 2 };
	enum { expansion_fds        = 3 };
	enum { expansion_mmc5       = 4 };
	enum { expansion_namco      = 5 };
	enum { expansion_sunsoft    = 6 };
	enum { expansion_epsm		= 7 };

	enum { expansion_mask_none       = 0 };
	enum { expansion_mask_vrc6       = 1 << 0 };
	enum { expansion_mask_vrc7       = 1 << 1 };
	enum { expansion_mask_fds        = 1 << 2 };
	enum { expansion_mask_mmc5       = 1 << 3 };
	enum { expansion_mask_namco      = 1 << 4 };
	enum { expansion_mask_sunsoft    = 1 << 5 };
	enum { expansion_mask_epsm       = 1 << 6 };

	// These mode are used for separate channel WAV export or stereo export.
	// When exporting each channel individually, we don't get the volume interactions
	// between the triangle-noise-dpcm channels. 
	// 
	// To overcome this, we have a "tnd_mode_separate" mode where 3 separate blip 
	// buffers will be used to correctly split the intensity across the channels, 
	// even when they are not audible. This create another problem where the triangle 
	// may bleed in the DPCM channel (well, all 3 channels bleed in each other).
	// This is a bit undesirable. 
	//
	// So we offer a 3rd mode "tnd_mode_separate_tn_only" where the DPCM will influence
	// the noise/triangle channels, but not the other way around. This will not result
	// in the exact correct intensity, but is often more desirable for people doing
	// stereo exports. 
	enum { tnd_mode_single           = 0 };
	enum { tnd_mode_separate         = 1 };
	enum { tnd_mode_separate_tn_only = 2 };

	Simple_Apu();
	~Simple_Apu();
	
	// This simpler interface works well for most games. Some benefit from
	// the higher precision of the full Nes_Apu interface, which provides
	// clock-cycle accurate register read/write and IRQ timing functions.
	
	// Set function for APU to call when it needs to read memory (DMC samples)
	void dmc_reader( int (*callback)( void* user_data, cpu_addr_t ), void* user_data = NULL );
	
	// Set output sample rate
	blargg_err_t sample_rate( long sample_rate, bool pal, int tnd_mode );
	
	// Write to register (0x4000-0x4017, except 0x4014 and 0x4016)
	void write_register( cpu_addr_t, int data );
	void set_update_rate_multiplier(int value);
	// Destinations: 0=$4011, 1=MMC5 $5011, 2/3=VRC6 pulse gate DACs.
	bool start_pcm(int destination, const unsigned char* data, int length, uint32_t period_q16, int loop_start, int loop_end, int volume);
	void stop_pcm(int destination);
	void release_pcm(int destination);
	void set_pcm_volume(int destination, int volume);
	int get_pcm_dac(int destination) const;
	int get_pcm_position(int destination) const;
	bool start_mmc5_pcm(const unsigned char* data, int length, uint32_t period_q16, int loop_start, int loop_end, int volume);
	void stop_mmc5_pcm();
	void set_mmc5_pcm_volume(int volume);
	int get_mmc5_pcm_dac() const { return mmc5.get_pcm_dac(); }
	int get_mmc5_pcm_position() const { return mmc5.get_pcm_position(); }
	void get_register_values(int exp, void* regs);

	int get_namco_wave_pos(int n163ChanIndex);
	int get_fds_wave_pos();
	void set_namco_mix(bool mix);

	// Read from status register at 0x4015
	int read_status();
	
	// End a 1/60 sound frame
	void end_frame();
	int skip_cycles(long cycles);
	
	// Resets
	void reset();

	void set_audio_expansions(long exp);
	int get_audio_expansions() const { return expansions; }

	// Number of samples in buffer
	long samples_avail() const;

	void enable_channel(int, int, bool);
	
	void reset_triggers();
	int get_channel_trigger(int exp, int idx);

	void treble_eq(int exp, double treble_amount, int treble_freq, int sample_rate);
	void bass_freq(int exp, int bass_freq);
	void set_expansion_volume(int expansion, double evolume);

	// Read at most 'count' samples and return number of samples actually read
	typedef blip_sample_t sample_t;
	long read_samples( sample_t* buf, long buf_size );

	// Discard 'count' samples.
	void remove_samples(long buf_size);
	
	// Save/load snapshot of emulation state
	void save_snapshot( apu_snapshot_t* out ) const;
	void load_snapshot( apu_snapshot_t const& );

	void start_seeking();
	void stop_seeking();
	bool is_seeking() const { return seeking; }

private:
	struct PcmStream {
		std::vector<unsigned char> data;
		uint32_t period_q16 = 0;
		int64_t next_time_q16 = 0;
		int position = 0, loop_start = -1, loop_end = -1, volume = 15, raw = 128, dac = 0;
		bool playing = false;
	};
	PcmStream pcm_streams[4]; // MMC5 retains its chip-local stream for ABI compatibility.
	void run_pcm_until(cpu_time_t end_time);
	void write_pcm_sample(int destination, cpu_time_t clock, int raw);
	bool pal_mode;
	bool seeking;
	float tnd_volume;
	int expansions;
	int separate_tnd_mode;
	int fds_filter_accum;
	int fds_filter_alpha;
	int tnd_skip; // Initial skipped samples to avoid intitial triangle pop. Channels will not be affected, as it takes place before output.
	bool separate_tnd_channel_enabled[3];
	long tnd_accum[3];
	long sq_accum;
	long prev_nonlinear_tnd;
	long prev_sq_mix;
	Nes_Apu apu;
	Nes_Vrc6 vrc6;
	Nes_Vrc7 vrc7;
	Nes_Fds fds;
	Nes_Mmc5 mmc5;
	Nes_Namco namco;
	Nes_Sunsoft sunsoft; // My version, based on emu2149
	//Nes_Fme7 sunsoft; // Blaarg's version from Game_Music_Emu.
	Nes_EPSM epsm;
	Blip_Buffer buf;
	Blip_Buffer buf_tnd[3]; // [0] is used normally, [0][1][2] are only used in "separate_tnd_mode", for stereo/separate channels export.
	Blip_Buffer buf_fds;
	Blip_Buffer buf_exp;
	Blip_Buffer buf_epsm_left;
	Blip_Buffer buf_epsm_right;
	blip_time_t time;
	blip_time_t frame_length;
	int update_rate_multiplier = 1;
	int update_subframe = 0;
	blip_time_t clock(blip_time_t t = 4) { return time += t; }

	const long fds_filter_bits = 12;
};

#endif

