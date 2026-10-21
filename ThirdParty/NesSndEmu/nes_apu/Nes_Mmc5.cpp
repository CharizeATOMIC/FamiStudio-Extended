// MMC5 audio chip emulator for FamiStudio.
// Added to Nes_Snd_Emu by @NesBleuBleu

#include "Nes_Mmc5.h"
#include <string.h>

#include BLARGG_SOURCE_BEGIN

Nes_Mmc5::Nes_Mmc5()
{
	square1.synth = &square_synth;
	square2.synth = &square_synth;
	square1.min_period = 0;
	square2.min_period = 0;

	oscs[0] = &square1;
	oscs[1] = &square2;
	pcm_output = NULL;
	pcm_dac = pcm_last_amp = 0;
	pcm_playing = false;
	last_time = 0;

	output(NULL);
	volume(1.0);
	reset();

}

Nes_Mmc5::~Nes_Mmc5()
{
}

void Nes_Mmc5::reset()
{
	square1.reset();
	square2.reset();

	frame_period = false /*pal_mode*/ ? 8314 : 7458;
	last_time = 0;
	frame_delay = 1;
	osc_enables = 0;
	pcm_control = pcm_dac = pcm_last_amp = 0;
	pcm_raw_sample = 128;
	pcm_volume = 15;
	pcm_position = 0;
	pcm_period_q16 = 0;
	pcm_next_time_q16 = 0;
	pcm_loop_start = pcm_loop_end = -1;
	pcm_playing = false;
	pcm_data.clear();

	square1.regs[0] = 0x10;
	square1.regs[1] = 0x08; // MMC5 has no sweep.
	square1.regs[2] = 0x00;
	square1.regs[3] = 0x00;
	square2.regs[0] = 0x10;
	square2.regs[1] = 0x08; // MMC5 has no sweep.
	square2.regs[2] = 0x00;
	square2.regs[3] = 0x00;

	write_register(0, 0x5015, 0x00);
	reset_triggers();
}

void Nes_Mmc5::volume(double v)
{
	square_synth.volume(0.3 * v);
	// The DAC is linear and eight-bit, with approximately the APU DMC's
	// per-code gain. The cartridge resistor/mixer can change the real gain.
	pcm_synth.volume((0.42545 / 127.0) * 255.0 * v);
}

void Nes_Mmc5::treble_eq(blip_eq_t const& eq)
{
	square_synth.treble_eq(eq);
	pcm_synth.treble_eq(eq);
}

void Nes_Mmc5::output(Blip_Buffer* buf)
{
	oscs[0]->output = buf;
	oscs[1]->output = buf;
	osc_output(2, buf);
}

void Nes_Mmc5::osc_output(int index, Blip_Buffer* buf)
{
	if (index >= 0 && index < square_count)
		oscs[index]->output = buf;
	else if (index == 2 && pcm_output != buf)
	{
		if (pcm_output && pcm_last_amp)
			pcm_synth.offset(last_time, -pcm_last_amp, pcm_output);
		pcm_output = buf;
		pcm_last_amp = pcm_output ? pcm_dac : 0;
		if (pcm_output && pcm_last_amp)
			pcm_synth.offset(last_time, pcm_last_amp, pcm_output);
	}
}

void Nes_Mmc5::write_pcm_dac(cpu_time_t time, int value)
{
	// $5011 zero holds the old DAC; read mode ignores direct CPU writes.
	if (!value || (pcm_control & 1))
		return;
	pcm_dac = value;
	if (pcm_output)
	{
		pcm_synth.offset(time, pcm_dac - pcm_last_amp, pcm_output);
		pcm_last_amp = pcm_dac;
	}
}

void Nes_Mmc5::write_pcm_sample(cpu_time_t time, int value)
{
	if (!value)
		return;
	pcm_raw_sample = value;
	int scaled = 128 + ((value - 128) * pcm_volume) / 15;
	// A zero code cannot express zero amplitude on MMC5. Keep it nonzero,
	// including the minimum negative endpoint after volume scaling.
	write_pcm_dac(time, scaled < 1 ? 1 : scaled);
}

bool Nes_Mmc5::start_pcm(cpu_time_t time, const unsigned char* data, int length, uint32_t period_q16, int loop_start, int loop_end, int volume)
{
	if (!data || length <= 0 || length > 0x1000000 || period_q16 < 65536 ||
		volume < 0 || volume > 15 ||
		!((loop_start == -1 && loop_end == -1) || (loop_start >= 0 && loop_start < loop_end && loop_end <= length)))
		return false;

	// Do not retain managed/pinned memory after the call. Allocation failure
	// must not cross the C ABI or destroy a currently playing sample.
	std::vector<unsigned char> copy;
	try { copy.assign(data, data + length); }
	catch (...) { return false; }
	run_until(time);
	pcm_data.swap(copy);
	pcm_position = 0;
	pcm_period_q16 = period_q16;
	pcm_loop_start = loop_start;
	pcm_loop_end = loop_end;
	pcm_volume = volume;
	pcm_playing = true;
	pcm_control = 0; // Software streaming uses the hardware write mode.
	pcm_trigger = time;
	write_pcm_sample(time, pcm_data[0]);
	pcm_next_time_q16 = (int64_t(time) << 16) + pcm_period_q16;
	return true;
}

void Nes_Mmc5::stop_pcm(cpu_time_t time)
{
	run_until(time);
	pcm_playing = false;
	pcm_control = 0;
	pcm_raw_sample = 128;
	write_pcm_dac(time, 128);
}

void Nes_Mmc5::set_pcm_volume(cpu_time_t time, int volume)
{
	run_until(time);
	pcm_volume = volume < 0 ? 0 : volume > 15 ? 15 : volume;
	if (pcm_playing)
		write_pcm_sample(time, pcm_raw_sample);
}

void Nes_Mmc5::run_pcm_until(cpu_time_t end_time)
{
	while (pcm_playing && pcm_next_time_q16 <= (int64_t(end_time) << 16))
	{
		const cpu_time_t time = cpu_time_t(pcm_next_time_q16 >> 16);
		++pcm_position;
		if (pcm_loop_start >= 0 && pcm_position == pcm_loop_end)
			pcm_position = pcm_loop_start;
		else if (pcm_position == int(pcm_data.size()))
		{
			pcm_playing = false;
			pcm_raw_sample = 128;
			write_pcm_dac(time, 128);
			break;
		}
		write_pcm_sample(time, pcm_data[pcm_position]);
		pcm_next_time_q16 += pcm_period_q16;
	}
}

void Nes_Mmc5::write_register(cpu_time_t time, cpu_addr_t addr, int data)
{
	// Ignore addresses outside range
	if (addr < start_addr || end_addr < addr)
		return;

	require(addr > 0x20); // addr must be actual address (i.e. 0x40xx)
	require((unsigned)data <= 0xff);

	run_until(time);

	if (addr == 0x5010)
		pcm_control = data & 0x81;
	else if (addr == 0x5011)
		write_pcm_dac(time, data);
	else if (addr == 0x5015)
	{
		// Channel enables
		for (int i = square_count; i--; )
			if (!((data >> i) & 1))
				oscs[i]->length_counter = 0;

		osc_enables = data;
	}
	else if (addr <= 0x5007)
	{
		// Write to channel
		int osc_index = (addr - start_addr) >> 2;
		Nes_Osc* osc = oscs[osc_index];

		int reg = addr & 3;

		// No sweep.
		if (reg == 1)
			return;

		osc->regs[reg] = data;
		osc->ages[reg] = 0;
		osc->reg_written[reg] = true;

		if (reg == 3)
		{
			// load length counter
			if ((osc_enables >> osc_index) & 1)
				osc->length_counter = length_table[(data >> 3) & 0x1f];

			// reset square phase
			if (osc_index < 2)
				((Nes_Square*)osc)->phase = Nes_Square::phase_range - 1;
		}
	}
}

void Nes_Mmc5::end_frame(cpu_time_t end_time)
{
	if (end_time > last_time)
		run_until(end_time);

	// make times relative to new frame
	last_time -= end_time;
	if (pcm_playing)
		pcm_next_time_q16 -= int64_t(end_time) << 16;
	require(last_time >= 0);
}

#include BLARGG_ENABLE_OPTIMIZER

void Nes_Mmc5::run_until(cpu_time_t end_time)
{
	require(end_time >= last_time);

	if (end_time == last_time)
		return;
	run_pcm_until(end_time);

	while (true)
	{
		// earlier of next frame time or end time
		cpu_time_t time = last_time + frame_delay;
		if (time > end_time)
			time = end_time;
		frame_delay -= time - last_time;

		// run oscs to present
		square1.run(last_time, time);
		square2.run(last_time, time);
		last_time = time;

		if (time == end_time)
			break; // no more frames to run

		frame_delay = frame_period;

		square1.clock_length(0x20);
		square2.clock_length(0x20);
	}
}

void Nes_Mmc5::start_seeking()
{
	memset(shadow_regs, -1, sizeof(shadow_regs));
}

void Nes_Mmc5::stop_seeking(blip_time_t& clock)
{
	for (int i = 0; i < array_count(shadow_regs); i++)
	{
		if (shadow_regs[i] >= 0)
			write_register(clock += 4, start_addr + i, shadow_regs[i]);
	}
}

void Nes_Mmc5::write_shadow_register(int addr, int data)
{
	if (addr >= start_addr && addr < start_addr + shadow_regs_count)
		shadow_regs[addr - start_addr] = data;
}

void Nes_Mmc5::get_register_values(struct mmc5_register_values* regs)
{
	for (int i = 0; i < square_count; i++)
	{
		Nes_Osc* osc = oscs[i];

		for (int j = 0; j < 4; j++)
		{
			regs->regs[i * 4 + j] = osc->regs[j];
			regs->ages[i * 4 + j] = osc->ages[j];

			osc->ages[j] = increment_saturate(osc->ages[j]);
		}
	}

	regs->regs[8] = osc_enables;
	regs->ages[8] = 0xff; // TODO : Keep track of that one too.
}

void Nes_Mmc5::reset_triggers()
{
	square1.trigger = trigger_hold;
	square2.trigger = trigger_hold;
	pcm_trigger = trigger_hold;
}

int Nes_Mmc5::get_channel_trigger(int idx) const
{
	return idx == 2 ? pcm_trigger : idx >= 0 && idx < square_count ? oscs[idx]->trigger : trigger_hold;
}

