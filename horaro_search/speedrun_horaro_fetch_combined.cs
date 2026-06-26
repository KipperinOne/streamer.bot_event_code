// ============================================================
//  COMBINED: speedrun_horaro_common + speedrun_horaro_fetch
//  Action: "Horaro Schedule aktualisieren"  |  Trigger: Timer
//  Diesen GESAMTEN Inhalt in EINE Execute-C#-Code-Sub-Action einfuegen.
//  WICHTIG: Im Tab "References" dieser Sub-Action System.dll hinzufuegen!
//  (z.B. C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll)
//
//  NEU: Loggt beim Start die tatsaechlich verwendete URL/Zieldatei
//  (sichtbar im Streamer.bot Logs-Fenster) zur einfacheren Fehlersuche.
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using System.Net;

public static class SpeedrunHoraroCommon
{
    private static string cachedSchedulePath;
    private static DateTime cachedScheduleWriteUtc;
    private static List<ScheduleRun> cachedScheduleRuns;
    private static Type cachedCphType;
    private static MethodInfo cachedGetGlobalVarString;

    public static string GetConfiguredPath(object cph, string globalVarName, string fallback)
    {
        string configured = GetGlobalVarString(cph, globalVarName);
        return string.IsNullOrWhiteSpace(configured) ? fallback : configured.Trim();
    }

    public static int GetConfiguredInt(object cph, string globalVarName, int fallback)
    {
        string configured = GetGlobalVarString(cph, globalVarName);
        int value;
        if (int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value;

        return fallback;
    }

    public static DateTimeOffset GetNowForSchedule(object cph)
    {
        string overrideValue = GetGlobalVarString(cph, "horaro_schedule_now");
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return DateTimeOffset.Parse(overrideValue.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

        return DateTimeOffset.Now;
    }

    public static List<ScheduleRun> LoadRuns(string scheduleJsonPath)
    {
        if (!File.Exists(scheduleJsonPath))
            throw new FileNotFoundException("Datei nicht gefunden: " + scheduleJsonPath);

        DateTime writeUtc = File.GetLastWriteTimeUtc(scheduleJsonPath);
        if (cachedScheduleRuns != null &&
            string.Equals(cachedSchedulePath, scheduleJsonPath, StringComparison.OrdinalIgnoreCase) &&
            cachedScheduleWriteUtc == writeUtc)
        {
            return cachedScheduleRuns;
        }

        string json = File.ReadAllText(scheduleJsonPath, new UTF8Encoding(false));
        JObject root = JObject.Parse(json);
        JObject schedule = root["schedule"] as JObject;
        if (schedule == null)
            throw new InvalidDataException("Schedule-Objekt fehlt");

        JArray columns = schedule["columns"] as JArray;
        JArray items = schedule["items"] as JArray;
        if (columns == null || items == null)
            throw new InvalidDataException("Spalten oder Eintraege fehlen");

        int gameIndex = FindColumn(columns, "Spieltitel", "Game", "Game Name");
        int runnerIndex = FindColumn(columns, "Runner", "Runners");
        int categoryIndex = FindColumn(columns, "Kategorie", "Category");
        int estimateIndex = FindColumn(columns, "Estimate", "Schaetzung");

        if (gameIndex < 0 || runnerIndex < 0 || categoryIndex < 0)
            throw new InvalidDataException("benoetigte Spalten nicht gefunden");

        List<ScheduleRun> runs = new List<ScheduleRun>();

        foreach (JToken itemToken in items)
        {
            JObject item = itemToken as JObject;
            if (item == null)
                continue;

            JArray data = item["data"] as JArray;
            if (data == null)
                continue;

            ScheduleRun run = new ScheduleRun();
            run.Game = GetData(data, gameIndex);
            run.Runner = GetData(data, runnerIndex);
            run.Category = GetData(data, categoryIndex);
            run.Estimate = GetData(data, estimateIndex);
            run.ScheduledText = ((string)item["scheduled"]) ?? "";
            run.ScheduledUnix = GetLong(item["scheduled_t"]);
            run.LengthSeconds = GetLong(item["length_t"]);

            if (run.ScheduledUnix <= 0 || run.LengthSeconds <= 0)
                continue;

            if (IsRunnableEntry(run))
                runs.Add(run);
        }

        runs.Sort((a, b) => a.ScheduledUnix.CompareTo(b.ScheduledUnix));
        cachedSchedulePath = scheduleJsonPath;
        cachedScheduleWriteUtc = writeUtc;
        cachedScheduleRuns = runs;
        return runs;
    }

    public static ScheduleRun FindCurrentRun(List<ScheduleRun> runs, DateTimeOffset now)
    {
        long nowUnix = ToUnixSeconds(now);
        foreach (ScheduleRun run in runs)
        {
            long endUnix = run.ScheduledUnix + run.LengthSeconds;
            if (run.ScheduledUnix <= nowUnix && nowUnix < endUnix)
                return run;
        }

        return null;
    }

    public static ScheduleRun FindNextRun(List<ScheduleRun> runs, DateTimeOffset now)
    {
        long nowUnix = ToUnixSeconds(now);
        ScheduleRun currentRun = FindCurrentRun(runs, now);

        foreach (ScheduleRun run in runs)
        {
            if (currentRun != null && run.ScheduledUnix <= currentRun.ScheduledUnix)
                continue;

            if (run.ScheduledUnix > nowUnix || currentRun != null)
                return run;
        }

        return null;
    }

    public static string FormatRun(ScheduleRun run, bool includeTime)
    {
        string text = Clean(run.Game);
        if (!string.IsNullOrEmpty(run.Category))
            text += " - " + Clean(run.Category);
        if (!string.IsNullOrEmpty(run.Runner))
            text += " von " + Clean(run.Runner);
        if (includeTime)
            text += " um " + FormatScheduleTime(run);
        if (!string.IsNullOrEmpty(run.Estimate))
            text += " (Schaetzung: " + Clean(run.Estimate) + ")";

        return text;
    }

    public static string Clean(string value)
    {
        return Regex.Replace((value ?? "").Trim(), @"\s+", " ");
    }

    public static string Normalize(string value)
    {
        return Clean(value).ToLowerInvariant();
    }

    public static long ToUnixSeconds(DateTimeOffset value)
    {
        return (long)(value.ToUniversalTime() - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
    }

    private static string GetGlobalVarString(object cph, string globalVarName)
    {
        try
        {
            Type cphType = cph.GetType();
            if (cachedGetGlobalVarString == null || cachedCphType != cphType)
            {
                cachedGetGlobalVarString = null;
                cachedCphType = cphType;

                foreach (MethodInfo candidate in cphType.GetMethods())
                {
                    ParameterInfo[] parameters = candidate.GetParameters();
                    if (candidate.Name == "GetGlobalVar" &&
                        candidate.IsGenericMethodDefinition &&
                        parameters.Length == 2 &&
                        parameters[0].ParameterType == typeof(string) &&
                        parameters[1].ParameterType == typeof(bool))
                    {
                        cachedGetGlobalVarString = candidate.MakeGenericMethod(typeof(string));
                        break;
                    }
                }
            }

            if (cachedGetGlobalVarString == null)
                return "";

            object value = cachedGetGlobalVarString.Invoke(cph, new object[] { globalVarName, true });
            return value == null ? "" : value.ToString();
        }
        catch
        {
            return "";
        }
    }

    private static int FindColumn(JArray columns, params string[] names)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            string column = Normalize((string)columns[i]);
            foreach (string name in names)
            {
                if (column == Normalize(name))
                    return i;
            }
        }

        return -1;
    }

    private static string GetData(JArray data, int index)
    {
        if (index < 0 || index >= data.Count || data[index].Type == JTokenType.Null)
            return "";

        return ((string)data[index] ?? "").Trim();
    }

    private static long GetLong(JToken token)
    {
        if (token == null)
            return 0;

        long value;
        if (long.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value;

        return 0;
    }

    private static bool IsRunnableEntry(ScheduleRun run)
    {
        string game = Normalize(run.Game);
        string runner = Normalize(run.Runner);

        if (string.IsNullOrEmpty(game) || string.IsNullOrEmpty(runner) || runner == "-")
            return false;

        if (game == "intro" || game == "kickoff" || game == "gdq" || game == "the checkpoint" || game.Contains("daily recap"))
            return false;

        if (runner == "interview crew")
            return false;

        return true;
    }

    private static string FormatScheduleTime(ScheduleRun run)
    {
        try
        {
            DateTimeOffset scheduled = DateTimeOffset.Parse(run.ScheduledText, CultureInfo.InvariantCulture);
            return scheduled.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        }
        catch
        {
            return run.ScheduledText;
        }
    }

    public class ScheduleRun
    {
        public string Game;
        public string Runner;
        public string Category;
        public string Estimate;
        public string ScheduledText;
        public long ScheduledUnix;
        public long LengthSeconds;
    }
}
public class CPHInline
{
    public bool Execute()
    {
        // Trage hier die JSON-Export-URL deines Schedules ein. Wenn deine
        // Schedule-Seite z.B. "https://horaro.org/meinevent/meinschedule" ist,
        // haengst du einfach ".json" an:
        // -> https://horaro.org/meinevent/meinschedule.json
        string defaultUrl = "https://horaro.org/DEIN-EVENT-SLUG/DEIN-SCHEDULE-SLUG.json";

        string apiUrl = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "horaro_schedule_api_url", defaultUrl);
        string outputPath = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "horaro_schedule_json_path", @"C:\StreamerBot\speedrun_schedule.json");
        int timeoutMs = SpeedrunHoraroCommon.GetConfiguredInt(CPH, "horaro_schedule_request_timeout_ms", 8000);

        CPH.LogInfo("Horaro-Fetch: verwende URL = " + apiUrl + " | Ziel-Datei = " + outputPath);

        try
        {
            SetUpTlsProtocols();

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(apiUrl);
            request.Method = "GET";
            request.Accept = "application/json";
            request.UserAgent = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "horaro_schedule_user_agent", "streamerbot-speedrun-event/1.0");
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            JObject root;
            using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                root = JObject.Parse(reader.ReadToEnd());
            }

            // Je nach Horaro-Endpoint liegt das Schedule-Objekt entweder
            // direkt im Root oder unter einem "schedule"-Schluessel.
            // Wir normalisieren das hier, damit LoadRuns() in
            // speedrun_horaro_common immer root.schedule.* findet.
            JObject scheduleObject = root["schedule"] as JObject ?? root;

            if (scheduleObject["items"] == null)
            {
                CPH.LogError("Horaro-Antwort enthaelt kein 'items'-Feld. Bitte URL pruefen: " + apiUrl);
                return false;
            }

            JObject normalized = new JObject();
            normalized["schedule"] = scheduleObject;

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(outputPath, normalized.ToString(), new UTF8Encoding(false));
            CPH.LogInfo("Horaro-Schedule aktualisiert: " + outputPath);
            return true;
        }
        catch (WebException ex)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            string detail = response != null
                ? "HTTP " + (int)response.StatusCode + " " + response.StatusCode
                : ex.Message;
            CPH.LogError("Horaro-Schedule konnte nicht abgerufen werden (" + apiUrl + "): " + detail);
            return false;
        }
        catch (Exception ex)
        {
            CPH.LogError("Horaro-Schedule konnte nicht abgerufen werden: " + ex.Message);
            return false;
        }
    }

    // Setzt eine moeglichst breite, sichere TLS-Verhandlung (1.2 + 1.3, falls vom
    // Betriebssystem/.NET unterstuetzt). Behebt "Es konnte kein geschuetztes
    // SSL/TLS-Kanal erstellt werden"-Fehler, die durch zu strikte Protokoll-
    // Vorgaben im .NET-Framework entstehen koennen.
    private static void SetUpTlsProtocols()
    {
        // Tls=192, Tls11=768, Tls12=3072, Tls13=12288 (Tls13-Enum-Wert existiert
        // nicht in jeder .NET-Framework-Version, daher als Zahl statt Konstante).
        int wanted = 192 | 768 | 3072 | 12288;

        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)wanted;
            return;
        }
        catch
        {
        }

        try
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        }
        catch
        {
            // Wenn selbst das fehlschlaegt, laeuft die Anfrage mit dem
            // Systemstandard weiter (kein harter Abbruch hier).
        }
    }
}
