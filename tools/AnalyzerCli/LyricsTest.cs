using KaraokeDJ.Models;
using KaraokeDJ.Services;

public static class LyricsTest
{
    public static void Run()
    {
        var c = new Celebration { Name = "Mario", Pronoun = "lui", Occasion = "compleanno" };
        c.Messages.Add(new GuestMessage { From = "Giulia", Text = "Ricordi Ibiza 2019? Hai perso il volo per un mojito" });
        c.Messages.Add(new GuestMessage { From = "Luca", Text = "Sei il re della griglia, ma il sugo lo fa tua moglie" });
        c.Messages.Add(new GuestMessage { From = "", Text = "Auguri grande capo!" });
        var (t, l) = LyricsService.BuildFromMessages(c);
        Console.WriteLine("TITOLO: " + t + "\n" + l + "\n---\nDEDICA: " + c.DedicationText);
    }
}
