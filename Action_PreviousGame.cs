// ============================================================
//  ACTION: "Previous Game"  |  Command: !previousgame
//  Decreases the line index by 1.
//  The current line is then printed using !wr.
// ============================================================

using System;
using System.IO;
using System.Text;

public class CPHInline
{
    public bool Execute()
    {
        // ── CONFIGURATION ─────────────────────────────────────────
        string csvPath = @"C:\StreamerBot\speedrun_wr.csv";
        // ──────────────────────────────────────────────────────────

        if (!File.Exists(csvPath))
        {
            CPH.SendMessage("⚠️ CSV-Datei nicht gefunden.");
            return false;
        }

        int zeilenAnzahl = File.ReadAllLines(csvPath, new UTF8Encoding(true)).Length - 1;
        int maxIndex = zeilenAnzahl - 1;

        int index = CPH.GetGlobalVar<int>("wr_row_index", true);
        index--;





        if (index < 0)
        {
            index = 0;
            CPH.SendMessage("⚠️ Du bist bereits am Anfang der Liste.");
            CPH.SetGlobalVar("wr_row_index", index, true);
            return true;
        }
        
        else
        {
            CPH.SendMessage($"⬅️ Eintrag {index + 1} von {zeilenAnzahl} ausgewählt. Tippe !wr für die Details.");
        }

        CPH.SetGlobalVar("wr_row_index", index, true);
        return true;
    }
}
