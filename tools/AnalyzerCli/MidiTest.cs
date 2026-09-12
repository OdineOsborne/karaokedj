using NAudio.Midi;

/// <summary>Crea un piccolo file .kar di prova (melodia + testo sillabato) per testare rendering e lyrics.</summary>
public static class MidiTest
{
    public static void Run(string outPath)
    {
        var col = new MidiEventCollection(1, 480);
        var tempo = new List<MidiEvent> { new TempoEvent(500000, 0), new TextEvent("@T Canzone di prova", MetaEventType.TextEvent, 0) };
        tempo.Add(new MetaEvent(MetaEventType.EndTrack, 0, 480 * 32));
        col.AddTrack(tempo);
        var mel = new List<MidiEvent> { new PatchChangeEvent(0, 1, 0) };
        var lyr = new List<MidiEvent>();
        string[] syl = { "Que", "sta ", "è ", "u", "na ", "pro", "va ", "/Per ", "il ", "ka", "ra", "o", "ke ", "di ", "VO", "XA " };
        int[] notes = { 60, 62, 64, 65, 67, 69, 71, 72, 72, 71, 69, 67, 65, 64, 62, 60 };
        for (int i = 0; i < 16; i++)
        {
            long t = i * 480;
            mel.Add(new NoteOnEvent(t, 1, notes[i], 100, 400));
            mel.Add(new NoteEvent(t + 400, 1, MidiCommandCode.NoteOff, notes[i], 0));
            lyr.Add(new TextEvent(syl[i], MetaEventType.Lyric, t));
        }
        mel.Add(new MetaEvent(MetaEventType.EndTrack, 0, 480 * 32));
        lyr.Add(new MetaEvent(MetaEventType.EndTrack, 0, 480 * 32));
        col.AddTrack(mel);
        col.AddTrack(lyr);
        MidiFile.Export(outPath, col);
        Console.WriteLine("scritto " + outPath);
    }
}
