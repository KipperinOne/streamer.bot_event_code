// ============================================================
//  COMBINED: speedrun_horaro_common + speedrun_horaro_next
//  Action: “Next Run”  |  Command: !next
//  Insert ALL of this content into ONE Execute C# Code sub-action.
//  IMPORTANT: Add System.dll in the “References” tab of this sub-action!
//  (e.g., C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll)
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

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
            throw new InvalidDataException("Spalten oder Einträge fehlen");

        int gameIndex = FindColumn(columns, "Spieltitel", "Game", "Game Name");
        int runnerIndex = FindColumn(columns, "Runner", "Runners");
        int categoryIndex = FindColumn(columns, "Kategorie", "Category");
        int estimateIndex = FindColumn(columns, "Estimate");

        if (gameIndex < 0 || runnerIndex < 0 || categoryIndex < 0)
            throw new InvalidDataException("benötigte Spalten nicht gefunden");

        List<ScheduleRun> runs = new List<ScheduleRun>();

        // Some sources (e.g. Oengus's JSON export) don't provide an absolute
        // "scheduled_t"/"length_t" per item like real Horaro does. Instead
        // they only give a schedule-level start time plus a per-item ISO
        // 8601 duration string ("length", e.g. "PT1H35M"). In that case we
        // reconstruct each run's start time by walking through the items in
        // order and accumulating durations from the schedule's start time.
        long cursorUnix = GetScheduleStartUnix(schedule);
        string scheduleTimezone = (string)schedule["timezone"];

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
            run.ScheduleTimezone = scheduleTimezone;

            // Fallback path: no absolute scheduled_t/length_t present, but an
            // ISO 8601 "length" duration is - derive the timing from that.
            if (run.LengthSeconds <= 0)
            {
                long isoLength;
                if (TryParseIso8601DurationSeconds((string)item["length"], out isoLength))
                    run.LengthSeconds = isoLength;
            }

            if (run.ScheduledUnix <= 0 && cursorUnix > 0 && run.LengthSeconds > 0)
                run.ScheduledUnix = cursorUnix;

            // Always advance the cursor by this item's length so entries
            // that get filtered out below (e.g. non-runnable rows) don't
            // desync the running total for the items after them.
            if (cursorUnix > 0 && run.LengthSeconds > 0)
                cursorUnix += run.LengthSeconds;

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
            text += " (Estimate: " + Clean(run.Estimate) + ")";

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

    // Resolves the schedule's absolute start time as Unix seconds.
    // Prefers the ISO 8601 "start" date string (reliable on both real Horaro
    // and Oengus exports); falls back to the numeric "start_t" field only if
    // present and positive, since some exports (e.g. Oengus) leave it at 0.
    private static long GetScheduleStartUnix(JObject schedule)
    {
        string startText = (string)schedule["start"];
        if (!string.IsNullOrEmpty(startText))
        {
            DateTimeOffset parsed;
            if (DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed))
                return ToUnixSeconds(parsed);
        }

        long startT = GetLong(schedule["start_t"]);
        return startT > 0 ? startT : 0;
    }

    // Parses an ISO 8601 duration string (e.g. "PT1H35M", "PT30M", "PT45S")
    // into total whole seconds. Used as a fallback for exports (e.g. Oengus)
    // that give each item's length as an ISO 8601 duration instead of the
    // "length_t" seconds field that real Horaro provides.
    private static bool TryParseIso8601DurationSeconds(string iso, out long totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrEmpty(iso) || !iso.StartsWith("PT", StringComparison.OrdinalIgnoreCase))
            return false;

        string s = iso.Substring(2);
        double h = 0, m = 0, sec = 0;

        Match matchHours = Regex.Match(s, @"([\d\.]+)H", RegexOptions.IgnoreCase);
        Match matchMinutes = Regex.Match(s, @"([\d\.]+)M", RegexOptions.IgnoreCase);
        Match matchSeconds = Regex.Match(s, @"([\d\.]+)S", RegexOptions.IgnoreCase);

        if (!matchHours.Success && !matchMinutes.Success && !matchSeconds.Success)
            return false;

        if (matchHours.Success)
            double.TryParse(matchHours.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out h);
        if (matchMinutes.Success)
            double.TryParse(matchMinutes.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out m);
        if (matchSeconds.Success)
            double.TryParse(matchSeconds.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out sec);

        totalSeconds = (long)Math.Round(h * 3600 + m * 60 + sec);
        return true;
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
        // Preferred path: use the human-readable "scheduled" text that real
        // Horaro provides directly on each item.
        if (!string.IsNullOrEmpty(run.ScheduledText))
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

        // Fallback: some sources (e.g. Oengus) don't provide "scheduled" text
        // at all, only the numeric ScheduledUnix we derived ourselves in
        // LoadRuns(). Format that instead so a time is still shown, converted
        // into the schedule's timezone if we can resolve it.
        if (run.ScheduledUnix > 0)
        {
            DateTimeOffset scheduledUtc = DateTimeOffset.FromUnixTimeSeconds(run.ScheduledUnix);
            TimeZoneInfo tz = ResolveTimeZone(run.ScheduleTimezone);

            if (tz != null)
            {
                DateTimeOffset local = TimeZoneInfo.ConvertTime(scheduledUtc, tz);
                return local.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
            }

            return scheduledUtc.ToString("ddd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        }

        return "";
    }

    // Classic .NET Framework (which Streamer.bot runs on) only understands
    // Windows timezone IDs (e.g. "W. Europe Standard Time"), not the IANA
    // names (e.g. "Europe/Berlin") that sources like Oengus provide. This
    // tries the IANA name directly first (works on newer .NET runtimes),
    // then falls back to a small mapping table of common IANA -> Windows IDs.
    private static TimeZoneInfo ResolveTimeZone(string timezoneId)
    {
        if (string.IsNullOrEmpty(timezoneId))
            return null;

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timezoneId);
        }
        catch
        {
            // Fall through to the mapping table below.
        }

        string windowsId;
        if (IanaToWindowsTimeZones.TryGetValue(timezoneId, out windowsId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    // Common IANA timezone names used by speedrunning marathons, mapped to
    // their classic .NET Framework / Windows timezone ID equivalent.
    // Extend this list if your event uses a timezone not covered here.
    private static readonly Dictionary<string, string> IanaToWindowsTimeZones = new Dictionary<string, string>
    {
        { "Europe/Berlin", "W. Europe Standard Time" },
        { "America/New_York", "Eastern Standard Time" },
        { "UTC", "UTC" },
    };

    public class ScheduleRun
    {
        public string Game;
        public string Runner;
        public string Category;
        public string Estimate;
        public string ScheduledText;
        public long ScheduledUnix;
        public long LengthSeconds;

        // IANA timezone of the schedule (e.g. "Europe/Berlin"), used to
        // display the correct local time when ScheduledText is missing
        // (e.g. Oengus exports) and we only have the raw UTC ScheduledUnix.
        public string ScheduleTimezone;
    }
}
public class CPHInline
{
    public bool Execute()
    {
        string scheduleJsonPath = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "horaro_schedule_json_path", @"C:\StreamerBot\speedrun_schedule.json");

        List<SpeedrunHoraroCommon.ScheduleRun> runs;
        try
        {
            runs = SpeedrunHoraroCommon.LoadRuns(scheduleJsonPath);
        }
        catch (Exception ex)
        {
            CPH.SendMessage("Schedule konnte nicht gelesen werden: " + ex.Message);
            return false;
        }

        if (runs.Count == 0)
        {
            CPH.SendMessage("Im Schedule wurden keine Runs gefunden.");
            return false;
        }

        DateTimeOffset now = SpeedrunHoraroCommon.GetNowForSchedule(CPH);
        SpeedrunHoraroCommon.ScheduleRun nextRun = SpeedrunHoraroCommon.FindNextRun(runs, now);

        if (nextRun == null)
        {
            CPH.SendMessage("Es gibt keine weiteren Runs im Schedule.");
            return true;
        }

        CPH.SendMessage("Als Nächstes: " + SpeedrunHoraroCommon.FormatRun(nextRun, true));
        return true;
    }
}
