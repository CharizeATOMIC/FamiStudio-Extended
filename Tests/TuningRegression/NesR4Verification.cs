using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FamiStudio;

static class NesR4Verification
{
    sealed class Player : BasePlayer
    {
        readonly Dictionary<int,int> registers=new();
        public readonly List<int[]> Frames=new();
        public Player():base(NesApu.APU_WAV_EXPORT,false,false) { Loop=LoopMode.None; }
        public override void NotifyRegisterWrite(int apu,int address,int value,int metadata=0)=>registers[address]=value;
        protected override short[] EndFrame() {
            var audio=base.EndFrame();
            Frames.Add(new[]{registers.GetValueOrDefault(0x4000)&15,registers.GetValueOrDefault(0x400c),registers.GetValueOrDefault(0x400e)});
            return audio;
        }
        public void Render(Song song) { try {BeginPlaySong(song);while(PlaySongFrame()) {}}finally{Shutdown();} }
    }
    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var project=new Project{Name="R4 engine verification",Author="Original synthetic test"};
        var pulse=project.CreateInstrument(ExpansionType.None,"A4 decay");
        pulse.VolumeEnvelope.Length=1;pulse.VolumeEnvelope.Values[0]=15;
        var noise=project.CreateInstrument(ExpansionType.None,"One-key hardware snare");
        noise.NoisePeriodEnvelope.Length=2;noise.NoisePeriodEnvelope.Values[0]=9;noise.NoisePeriodEnvelope.Values[1]=2;
        noise.Envelopes[EnvelopeType.DutyCycle].Length=2;noise.Envelopes[EnvelopeType.DutyCycle].Values[0]=1;noise.Envelopes[EnvelopeType.DutyCycle].Values[1]=0;
        noise.NoiseControlEnvelope.Length=1;noise.NoiseControlEnvelope.Values[0]=2;
        noise.NoiseRestartEnvelope.Length=3;noise.NoiseRestartEnvelope.Values[0]=2;noise.NoiseRestartEnvelope.Values[1]=2;noise.NoiseRestartEnvelope.Values[2]=0;
        var song=project.CreateSong("Pulse decay then hardware snare");song.ChangeFamiStudioTempoGroove(new[]{1},false);
        song.SetDefaultPatternLength(128);song.SetBeatLength(32);song.SetLength(2);song.SetLoopPoint(0);
        var p=song.Channels[0].GetOrCreatePattern(0);
        p.Notes[0]=new Note(46){Instrument=pulse,Duration=128};p.Notes[32]=new Note{EnvelopeDecay=4};
        p.Notes[96]=new Note(46){Instrument=pulse,Duration=32};
        song.Channels[0].GetOrCreatePattern(1).Notes[0]=new Note(Note.NoteStop);
        var n=song.Channels[3].GetOrCreatePattern(1);
        for(int row=0;row<128;row+=32)n.Notes[row]=new Note(49){Instrument=noise,Duration=24};
        song.Channels[3].GetOrCreatePattern(0).Notes[0]=new Note(Note.NoteStop);
        string rom=Path.Combine(output,"R4-Noise-Decay.nes");
        if(!new RomFile().Save(project,rom,new[]{song.Id},project.Name,project.Author,false))throw new Exception("Actual RomFile.Save failed.");
        new ProjectFile().Save(project,Path.Combine(output,"R4-Noise-Decay.fms"));
        var player=new Player();player.Render(song);
        File.WriteAllText(Path.Combine(output,"editor-reference.json"),JsonSerializer.Serialize(player.Frames));
        Console.WriteLine($"PASS real RomFile.Save: {new FileInfo(rom).Length} bytes, {player.Frames.Count} reference frames");
    }
}
