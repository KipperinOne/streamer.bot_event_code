// ============================================================
//  ACTION: "WR Command"  |  Command: !wr
//  Gibt den Eintrag aus der aktuellen Indexposition aus.
//  Index wird über !nextgame / !previousgame gesteuert.
// ============================================================

using System;
using System.IO;
using System.Text;

public class CPHInline
{
    public bool Execute()
    {
        // ── KONFIGURATION ─────────────────────────────────────────
        string csvPath = @"C:\StreamerBot\speedrun_wr.csv";
        char delimiter = ';';
        // ──────────────────────────────────────────────────────────

        // 1) CSV einlesen
        if (!File.Exists(csvPath))
        {
            CPH.SendMessage($"⚠️ CSV-Datei nicht gefunden: {csvPath}");
            return false;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(csvPath, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            CPH.SendMessage($"⚠️ Fehler beim Lesen der CSV-Datei: {ex.Message}");
            return false;
        }

        if (lines.Length < 2)
        {
            CPH.SendMessage("⚠️ Die CSV-Datei enthält keine Daten.");
            return false;
        }

        // 2) Aktuellen Index lesen
        int index = CPH.GetGlobalVar<int>("wr_row_index", true);
        int maxIndex = lines.Length - 2;

        // Sicherheitsprüfung falls Index außerhalb der Liste liegt
        if (index < 0 || index > maxIndex)
        {
            index = 0;
            CPH.SetGlobalVar("wr_row_index", 0, true);
        }

        // 3) Header-Indizes ermitteln
        string[] headers = lines[0].Split(delimiter);
        int idxSpiel  = FindHeader(headers, "spielname");
        int idxSRKat  = FindHeader(headers, "speedrun kategorie");
        int idxWrName = FindHeader(headers, "wr-halter name");
        int idxWrZeit = FindHeader(headers, "wr-zeit");
        int idxRName = FindHeader(headers, "runner name");
        int idxRZeit = FindHeader(headers, "runner zeit");

        if (idxSpiel < 0 || idxSRKat < 0 || idxWrName < 0 || idxWrZeit < 0)
        {
            CPH.SendMessage("⚠️ CSV-Header nicht erkannt. Prüfe ob alle Spalten vorhanden sind.");
            return false;
        }

        // 4) Datenzeile lesen (index 0 = lines[1], index 1 = lines[2], ...)
        string[] cols = lines[index + 1].Split(delimiter);

        string spielName   = Get(cols, idxSpiel);
        string srKategorie = Get(cols, idxSRKat);
        string wrName      = Get(cols, idxWrName);
        string wrZeit      = Get(cols, idxWrZeit);
        string runnerName = Get(cols, idxRName);
        string runnerZeit = Get(cols, idxRZeit);

        // 5) Ausgabe
        CPH.SendMessage($"Der Weltrekord bei {spielName} in der Kategorie {srKategorie} liegt bei {wrZeit} und wurde von {wrName} aufgestellt.");

        if (!string.IsNullOrEmpty(runnerName) && !string.IsNullOrEmpty(runnerZeit))
            CPH.SendMessage($"{runnerName} hat eine Zeit von {runnerZeit} aufgestellt.");

        return true;
    }

    private int FindHeader(string[] headers, string search)
    {
        for (int i = 0; i < headers.Length; i++)
            if (headers[i].Trim().ToLower() == search)
                return i;
        return -1;
    }

    private string Get(string[] cols, int idx)
    {
        if (idx < 0 || idx >= cols.Length) return "";
        return cols[idx].Trim();
    }
}
