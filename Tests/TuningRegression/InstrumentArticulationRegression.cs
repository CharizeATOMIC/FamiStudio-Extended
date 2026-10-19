using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FamiStudio;

static class InstrumentArticulationRegression
{
    sealed class Version1003Writer : ProjectSaveBuffer, ProjectBuffer
    {
        public Version1003Writer(Project project) : base(project) { }
        int ProjectBuffer.Version => 1003;
    }
    static int checks;
    static void Check(bool condition,string label) { if(!condition) throw new Exception(label); checks++; Console.WriteLine("PASS instrument articulation: "+label); }
    static object Invoke(object target,string name,params object[] args) => target.GetType().GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(target,args);
    static string Label(string name) => typeof(InstrumentParamProvider).GetField(name,BindingFlags.Static|BindingFlags.NonPublic).GetValue(null).ToString();
    static ParamInfo Param(Instrument instrument,string name) => InstrumentParamProvider.GetParams(instrument).Single(p=>p.Name==Label(name));
    static IEnumerable<Control> Descendants(Container c)
    {
        foreach(var child in c.Controls) { yield return child; if(child is Container nested) foreach(var descendant in Descendants(nested)) yield return descendant; }
    }
    static ParamCheckBox Checkbox(ProjectExplorer explorer,string label)
    {
        return Descendants(explorer).OfType<ParamCheckBox>().Single(c=>((ParamInfo)typeof(ParamControl).GetField("param",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(c)).Name==label);
    }
    static void Click(Control c) { c.SendPointerDown(new PointerEventArgs(1,c.Width/2,c.Height/2));c.SendPointerUp(new PointerEventArgs(1,c.Width/2,c.Height/2)); }
    static bool Matches(Instrument instrument,int flags) => instrument.ResetPhaseOnAttack==((flags&1)!=0) && instrument.PitchClampToTimerByte==((flags&2)!=0);
    static void Set(Instrument instrument,int flags) { instrument.ResetPhaseOnAttack=(flags&1)!=0;instrument.PitchClampToTimerByte=(flags&2)!=0; }

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        Check(Project.Version>=1004 && Project.InstrumentArticulationFormatVersion==1004,"format 1004 identifies instrument articulation fields");
        string fixture=Path.Combine("Tests","TuningRegression","Fixtures","format1003.fms");
        Check(BitConverter.ToInt32(File.ReadAllBytes(fixture),4)==1003,"frozen self-authored fixture is genuinely format 1003");
        var legacy=new ProjectFile().Load(fixture);
        Check(legacy!=null && legacy.Instruments.All(i=>Matches(i,0)),"format 1003 loads with both new options disabled");
        var oldBody=legacy.Songs[0].Channels[0].PatternInstances[1];
        Check(oldBody.LoopVariant?.PlaybackDelay==2 && oldBody.LoopVariant.Notes[0].Value==49,"old loop variation, delay and notes survive compatibility load");
        var project=new Project();var instrument=project.CreateInstrument(0,"Articulation");
        var song=project.CreateSong();song.ChangeFamiStudioTempoGroove(new[]{1},false);song.SetLength(1);song.SetDefaultPatternLength(16);
        song.Channels[0].GetOrCreatePattern(0).Notes[0]=new Note(46){Instrument=instrument,Duration=8};
        Check(Matches(instrument,0),"new instrument defaults are disabled");
        var crc=new HashSet<uint>();
        for(int flags=0;flags<4;flags++)
        {
            Set(instrument,flags);crc.Add(instrument.ComputeCRC());
            string binary=Path.Combine(output,$"articulation-{flags}.fms"),text=Path.ChangeExtension(binary,".txt");
            Check(new ProjectFile().Save(project,binary),"binary save "+flags);
            Check(new FamistudioTextFile().Save(project,text,new[]{song.Id},false),"text save "+flags);
            foreach(var restored in new[]{new ProjectFile().Load(binary),new FamistudioTextFile().Load(text),project.DeepClone()})
                Check(Matches(restored.GetInstrument("Articulation"),flags),"binary/text/clone preserve independent flags "+flags);
            var duplicated=project.DuplicateInstrument(instrument);
            Check(duplicated.Id!=instrument.Id && Matches(duplicated,flags),"instrument duplicate preserves articulation "+flags);
            var undo=new ProjectSaveBuffer(project,ProjectBufferFlags.UndoRedo);instrument.Serialize(undo);
            Set(instrument,flags^3);instrument.Serialize(new ProjectLoadBuffer(project,undo.GetBuffer(),Project.Version,ProjectBufferFlags.UndoRedo));
            Check(Matches(instrument,flags),"instrument undo serialization restores flags "+flags);
            var clip=new ProjectSaveBuffer(project,ProjectBufferFlags.Clipboard);var nonpublic=BindingFlags.Static|BindingFlags.NonPublic;
            typeof(ClipboardUtils).GetMethod("SaveInstrumentList",nonpublic).Invoke(null,new object[]{clip,new HashSet<Instrument>{instrument}});
            var destination=new Project();var reader=new ProjectLoadBuffer(destination,clip.GetBuffer(),Project.Version,ProjectBufferFlags.Clipboard);
            typeof(ClipboardUtils).GetMethod("LoadAndMergeInstrumentList",nonpublic).Invoke(null,new object[]{reader,false,ClipboardImportFlags.CreateMissing});
            Check(Matches(destination.GetInstrument("Articulation"),flags),"clipboard import preserves flags "+flags);
        }
        Check(crc.Count==4,"instrument CRC distinguishes all four articulation combinations");
        // Serialize the actual older layout; truncating today's final fields
        // cannot remove newer fields embedded inside each envelope.
        Set(instrument,3);var serialized=new Version1003Writer(project);instrument.Serialize(serialized);
        var oldBytes=serialized.GetBuffer();
        instrument.Serialize(new ProjectLoadBuffer(project,oldBytes,1003));
        Check(Matches(instrument,0),"reading old instrument data into an existing object explicitly clears new flags");
        Localization.LocalizeStatic(typeof(InstrumentParamProvider));
        Param(instrument,"ResetPhaseOnAttackLabel").SetValue(1);Param(instrument,"PitchClampToTimerByteLabel").SetValue(1);
        Check(Matches(instrument,3),"parameter controls expose both editable model flags");
        project.SetExpansionAudioMask(ExpansionType.S5BMask);
        var expanded=project.CreateInstrument(ExpansionType.S5B,"Expansion");
        Check(!InstrumentParamProvider.GetParams(expanded).Any(p=>p.Name==Label("ResetPhaseOnAttackLabel")||p.Name==Label("PitchClampToTimerByteLabel")),"APU-only options do not appear on expansion instruments");
        foreach(string lang in new[]{"eng","zho","deu","spa","por","rus","kor"})
        {
            var ini=new IniFile();Check(ini.Load(Path.Combine("FamiStudio","Localization",$"FamiStudio.{lang}.ini")),"localization loads "+lang);
            foreach(string key in new[]{"ResetPhaseOnAttackLabel","ResetPhaseOnAttackTooltip","PitchClampToTimerByteLabel","PitchClampToTimerByteTooltip"})
                Check(!string.IsNullOrWhiteSpace(ini.GetString("InstrumentParamProvider",key,null)),"localized "+lang+" "+key);
        }
        Console.WriteLine($"PASS all {checks} instrument articulation checks");
    }

    public static void Ui(FamiStudioWindow window,string language,string output,Action<FamiStudioWindow,string> capture)
    {
        var app=window.PianoRoll.App;var explorer=window.ProjectExplorer;
        var instrument=app.Project.Instruments.FirstOrDefault(i=>i.IsRegular)??app.Project.CreateInstrument(0,"Articulation UI");
        Set(instrument,0);Localization.LocalizeStatic(typeof(InstrumentParamProvider));
        typeof(ProjectExplorer).GetField("expandedInstrument",BindingFlags.Instance|BindingFlags.NonPublic).SetValue(explorer,null);
        Invoke(explorer,"ToggleExpandInstrument",instrument);
        InstrumentLayoutUiRegression.ExpandHardware(explorer,instrument);
        foreach(string key in new[]{"ResetPhaseOnAttackLabel","PitchClampToTimerByteLabel"})
        {
            string label=Label(key);var check=Checkbox(explorer,label);int flag=key.StartsWith("Reset")?1:2;
            Click(check);Check(Matches(instrument,flag),language+" actual checkbox edits "+key);
            app.UndoRedoManager.Undo();Check(Matches(instrument,0),language+" checkbox undo restores defaults");
            app.UndoRedoManager.Redo();Check(Matches(instrument,flag),language+" checkbox redo restores setting");
            explorer.RefreshButtons();InstrumentLayoutUiRegression.ExpandHardware(explorer,instrument);check=Checkbox(explorer,label);
            typeof(ParamControl).GetMethod("ResetParamDefaultValue",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(check,null);
            Check(Matches(instrument,0),language+" reset-to-default clears setting");
            app.UndoRedoManager.Undo();Check(Matches(instrument,flag),language+" reset-to-default is undoable");
            app.UndoRedoManager.Redo();explorer.RefreshButtons();InstrumentLayoutUiRegression.ExpandHardware(explorer,instrument);
        }
        Click(Checkbox(explorer,Label("ResetPhaseOnAttackLabel")));Click(Checkbox(explorer,Label("PitchClampToTimerByteLabel")));
        Check(Matches(instrument,3),language+" both settings can be enabled independently");
        capture(window,Path.Combine(output,language+"-instrument-articulation.ppm"));
        string path=Path.Combine(output,language+"-instrument-articulation.fms");new ProjectFile().Save(app.Project,path);
        Check(Matches(new ProjectFile().Load(path).GetInstrument(instrument.Name),3),language+" edited settings survive save/reopen");
        Set(instrument,0);explorer.RefreshButtons();
    }
}
