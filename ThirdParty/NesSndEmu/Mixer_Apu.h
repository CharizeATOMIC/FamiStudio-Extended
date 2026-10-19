#pragma once
#include <memory>
#include <vector>
#include <algorithm>
#include <mutex>
#include "Simple_Apu.h"
#undef min
#undef max

// Independent channel stems preserve shared-chip registers. A stem contains the
// entire chip, but exposes one voice. This also preserves TND DAC interaction.
class Mixer_Apu : public Simple_Apu {
    struct Stem {
        int expansion, channel, pan;
        std::unique_ptr<Simple_Apu> apu;
    };
    std::vector<Stem> stems;
    bool stereo = false;
    bool nativeStereo = false;
    template<class F> void each(F f) { f(static_cast<Simple_Apu&>(*this)); for (auto& s : stems) f(*s.apu); }
public:
    void configure(bool outputStereo, int count, const int* expansions, const int* channels, const int* pans) {
        stems.clear(); stereo = outputStereo;
        for (int i = 0; i < count; i++) {
            Stem s; s.expansion = expansions[i]; s.channel = channels[i]; s.pan = pans[i];
            s.apu.reset(new Simple_Apu()); stems.push_back(std::move(s));
        }
    }
    void set_pan(int index, int pan) { if (index >= 0 && index < (int)stems.size()) stems[index].pan = std::max(0, std::min(100, pan)); }
    blargg_err_t sample_rate(long rate, bool pal, int tnd) {
        auto err = Simple_Apu::sample_rate(rate, pal, stems.empty() ? tnd : tnd_mode_separate);
        if (err) return err;
        for (auto& s : stems) { err = s.apu->sample_rate(rate, pal, tnd_mode_separate); if (err) return err; }
        return 0;
    }
    void set_audio_expansions(long mask) {
        nativeStereo = (mask & expansion_mask_epsm) != 0;
        Simple_Apu::set_audio_expansions(mask);
        for (auto& s : stems) s.apu->set_audio_expansions(s.expansion ? 1 << (s.expansion - 1) : 0);
    }
    void dmc_reader(int (*cb)(void*,cpu_addr_t),void* data) { each([&](Simple_Apu& a){ a.dmc_reader(cb,data); }); }
    void write_register(cpu_addr_t addr,int data) { each([&](Simple_Apu& a){ a.write_register(addr,data); }); }
    void reset() { each([](Simple_Apu& a){ a.reset(); }); }
    void end_frame() { each([](Simple_Apu& a){ a.end_frame(); }); }
    void set_update_rate_multiplier(int value) { each([&](Simple_Apu& a){ a.set_update_rate_multiplier(value); }); }
    int skip_cycles(long cycles) { int n=Simple_Apu::skip_cycles(cycles); for(auto& s:stems) s.apu->skip_cycles(cycles); return n; }
    void start_seeking() { each([](Simple_Apu& a){ a.start_seeking(); }); }
    void stop_seeking() { each([](Simple_Apu& a){ a.stop_seeking(); }); }
    void reset_triggers() { each([](Simple_Apu& a){ a.reset_triggers(); }); }
    void bass_freq(int exp,int value) { Simple_Apu::bass_freq(exp,value); for (auto& s:stems) if (exp == 0 || exp == s.expansion) s.apu->bass_freq(exp,value); }
    void treble_eq(int exp,double amount,int freq,int rate) { Simple_Apu::treble_eq(exp,amount,freq,rate); for (auto& s:stems) if (exp == 0 || exp == s.expansion) s.apu->treble_eq(exp,amount,freq,rate); }
    void set_expansion_volume(int exp,double volume) { Simple_Apu::set_expansion_volume(exp,volume); for (auto& s:stems) if (exp == 0 || exp == s.expansion) s.apu->set_expansion_volume(exp,volume); }
    void set_namco_mix(bool value) {
        Simple_Apu::set_namco_mix(value);
        for (auto& s : stems) if (s.expansion == expansion_namco) s.apu->set_namco_mix(value);
    }
    void enable_channel(int exp,int channel,bool enabled) {
        Simple_Apu::enable_channel(exp,channel,enabled);
        for(auto& s:stems) if (exp == 0 || exp == s.expansion) s.apu->enable_channel(exp,channel,enabled && exp==s.expansion && channel==s.channel);
    }
    long read_samples(sample_t* output,long count) {
        if (!stereo && stems.empty()) return Simple_Apu::read_samples(output,count);
        std::vector<sample_t> raw(count * 2);
        Simple_Apu::read_samples(raw.data(),count);
        if (stems.empty()) {
            if(output) for(int i=0;i<count;i++) { output[i*2]=raw[nativeStereo?i*2:i]; output[i*2+1]=raw[nativeStereo?i*2+1:i]; }
        } else {
            std::vector<double> mix(count*2,0.0);
            for(auto& s:stems) {
                s.apu->read_samples(raw.data(),count);
                bool two=s.expansion==expansion_epsm;
                // Unity at center, constant peak balance. Native EPSM L/R is retained.
                double left=std::min(1.0,(100-s.pan)/50.0),right=std::min(1.0,s.pan/50.0);
                for(int i=0;i<count;i++) { mix[i*2]+=raw[two?i*2:i]*left; mix[i*2+1]+=raw[two?i*2+1:i]*right; }
            }
            if(output) for(int i=0;i<count*2;i++) output[i]=(sample_t)std::max(-32768.0,std::min(32767.0,mix[i]));
        }
        return count;
    }
    void remove_samples(long count) { each([&](Simple_Apu& a){ a.remove_samples(count); }); }
};
