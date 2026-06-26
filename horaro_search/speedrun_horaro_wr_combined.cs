// ============================================================
//  COMBINED: speedrun_horaro_common + speedrun_horaro_wr
//  Action: "WR Command"  |  Command: !wr
//  Diesen GESAMTEN Inhalt in EINE Execute-C#-Code-Sub-Action einfuegen.
//  WICHTIG: Im Tab "References" dieser Sub-Action System.dll hinzufuegen!
//  (z.B. C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll)
//
//  NEU: Unterstuetzt mehrere Runner im Horaro-"Runner"-Feld
//  (Trenner: "&", ",", "/", "+", " and ", " vs " / " vs. ", " versus ").
//  Fuer jeden erkannten Runner wird einzeln die PB auf speedrun.com gesucht.
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
        string scheduleJsonPath = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "horaro_schedule_json_path", @"C:\StreamerBot\speedrun_schedule.json");
        string csvPath = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "speedrun_wr_csv_path", @"C:\StreamerBot\speedrun_wr.csv");
        char csvDelimiter = ';';

        List<SpeedrunHoraroCommon.ScheduleRun> runs;
        try
        {
            runs = SpeedrunHoraroCommon.LoadRuns(scheduleJsonPath);
        }
        catch (Exception ex)
        {
            CPH.SendMessage("Horaro-Schedule konnte nicht gelesen werden: " + ex.Message);
            return false;
        }

        if (runs.Count == 0)
        {
            CPH.SendMessage("Im Horaro-Schedule wurden keine Runs gefunden.");
            return false;
        }

        DateTimeOffset now = SpeedrunHoraroCommon.GetNowForSchedule(CPH);
        SpeedrunHoraroCommon.ScheduleRun currentRun = SpeedrunHoraroCommon.FindCurrentRun(runs, now);

        if (currentRun == null)
        {
            SpeedrunHoraroCommon.ScheduleRun nextRun = SpeedrunHoraroCommon.FindNextRun(runs, now);
            if (nextRun == null)
                CPH.SendMessage("Gerade laeuft kein Speedrun und es gibt keine weiteren Runs im Schedule.");
            else
                CPH.SendMessage("Gerade laeuft kein Speedrun. Als Naechstes: " + SpeedrunHoraroCommon.FormatRun(nextRun, true));

            return true;
        }

        WrInfo csvInfo = null;
        LiveWrInfo liveInfo = TryGetSpeedrunComWr(currentRun);
        string message = "Aktuell laeuft: " + SpeedrunHoraroCommon.FormatRun(currentRun, false);

        if (liveInfo.Success)
        {
            message += " | WR: " + liveInfo.WrTime;
            if (!string.IsNullOrEmpty(liveInfo.WrRunner))
                message += " von " + liveInfo.WrRunner;

            if (liveInfo.PbEntries.Count > 0)
            {
                List<string> pbParts = new List<string>();
                foreach (RunnerPbEntry entry in liveInfo.PbEntries)
                {
                    string pb = "PB";
                    if (!string.IsNullOrEmpty(entry.Runner))
                        pb += " (" + entry.Runner + ")";
                    pb += ": " + entry.Time;
                    if (entry.Place > 0)
                        pb += " (#" + entry.Place.ToString(CultureInfo.InvariantCulture) + ")";
                    pbParts.Add(pb);
                }
                message += " | " + string.Join(" | ", pbParts.ToArray());
            }
            else
            {
                csvInfo = FindCsvWrInfo(csvPath, csvDelimiter, currentRun);
                if (csvInfo != null && !string.IsNullOrEmpty(csvInfo.RunnerTime))
                {
                    string pb = "PB";
                    if (!string.IsNullOrEmpty(csvInfo.RunnerName))
                        pb += " (" + csvInfo.RunnerName + ")";
                    message += " | " + pb + ": " + csvInfo.RunnerTime;
                }
            }

            CPH.SendMessage(message);
            return true;
        }

        csvInfo = FindCsvWrInfo(csvPath, csvDelimiter, currentRun);
        if (csvInfo != null)
        {
            List<string> parts = new List<string>();

            if (!string.IsNullOrEmpty(csvInfo.WrTime))
            {
                string wr = "WR: " + csvInfo.WrTime;
                if (!string.IsNullOrEmpty(csvInfo.WrRunner))
                    wr += " von " + csvInfo.WrRunner;
                parts.Add(wr);
            }

            if (!string.IsNullOrEmpty(csvInfo.RunnerTime))
            {
                string pb = "PB";
                if (!string.IsNullOrEmpty(csvInfo.RunnerName))
                    pb += " (" + csvInfo.RunnerName + ")";
                pb += ": " + csvInfo.RunnerTime;
                parts.Add(pb);
            }

            if (parts.Count > 0)
                message += " | " + string.Join(" | ", parts.ToArray());
            else
                message += " | WR/PB-Zeiten sind leer.";

            CPH.SendMessage(message);
            return true;
        }

        message += " | Kein WR/PB gefunden.";

        CPH.SendMessage(message);
        return true;
    }

    private LiveWrInfo TryGetSpeedrunComWr(SpeedrunHoraroCommon.ScheduleRun run)
    {
        string cacheKey = SpeedrunHoraroCommon.Normalize(run.Game) + "|" + SpeedrunHoraroCommon.Normalize(run.Category) + "|" + SpeedrunHoraroCommon.Normalize(run.Runner);
        int cacheSeconds = SpeedrunHoraroCommon.GetConfiguredInt(CPH, "speedruncom_cache_seconds", 300);
        int errorCacheSeconds = SpeedrunHoraroCommon.GetConfiguredInt(CPH, "speedruncom_error_cache_seconds", 60);

        LiveWrInfo cached = TryReadSpeedrunComCache(cacheKey, cacheSeconds, errorCacheSeconds);
        if (cached != null)
            return cached;

        try
        {
            SpeedrunComGame game = FindSpeedrunComGame(run.Game);
            if (game == null)
                return CacheFailure(cacheKey, "Spiel auf speedrun.com nicht gefunden");

            SpeedrunComCategory category = FindSpeedrunComCategory(game.Id, run.Category);
            if (category == null)
                return CacheFailure(cacheKey, "Kategorie auf speedrun.com nicht gefunden");

            LiveWrInfo result = GetSpeedrunComLeaderboardWr(game, category);
            if (result.Success)
                AddSpeedrunComRunnerPb(result, run, game, category);

            if (result.Success)
                WriteSpeedrunComCache(cacheKey, result);
            else
                WriteSpeedrunComErrorCache(cacheKey, result.Error);

            return result;
        }
        catch (WebException ex)
        {
            return CacheFailure(cacheKey, FormatWebException(ex));
        }
        catch (Exception ex)
        {
            return CacheFailure(cacheKey, ex.Message);
        }
    }

    private LiveWrInfo CacheFailure(string cacheKey, string error)
    {
        WriteSpeedrunComErrorCache(cacheKey, error);
        return LiveWrInfo.Fail(error);
    }

    private SpeedrunComGame FindSpeedrunComGame(string gameName)
    {
        JObject root = GetJson("https://www.speedrun.com/api/v1/games?name=" + Uri.EscapeDataString(gameName) + "&max=5");
        JArray data = root["data"] as JArray;
        if (data == null || data.Count == 0)
            return null;

        string wanted = SpeedrunHoraroCommon.Normalize(gameName);
        JObject best = null;
        int bestScore = -1;

        foreach (JToken token in data)
        {
            JObject game = token as JObject;
            if (game == null)
                continue;

            int score = ScoreGame(game, wanted);
            if (score > bestScore)
            {
                bestScore = score;
                best = game;
            }
        }

        if (best == null)
            return null;

        SpeedrunComGame result = new SpeedrunComGame();
        result.Id = GetString(best["id"]);
        result.Name = GetString(best["names"], "international");
        if (string.IsNullOrEmpty(result.Name))
            result.Name = gameName;

        return string.IsNullOrEmpty(result.Id) ? null : result;
    }

    private int ScoreGame(JObject game, string wanted)
    {
        string international = SpeedrunHoraroCommon.Normalize(GetString(game["names"], "international"));
        string twitch = SpeedrunHoraroCommon.Normalize(GetString(game["names"], "twitch"));
        string abbreviation = SpeedrunHoraroCommon.Normalize(GetString(game["abbreviation"]));

        if (international == wanted || twitch == wanted)
            return 100;
        if (ContainsEither(international, wanted))
            return 80;
        if (ContainsEither(twitch, wanted))
            return 75;
        if (abbreviation == wanted)
            return 70;

        return 10;
    }

    private SpeedrunComCategory FindSpeedrunComCategory(string gameId, string categoryName)
    {
        JObject root = GetJson("https://www.speedrun.com/api/v1/games/" + Uri.EscapeDataString(gameId) + "/categories");
        JArray data = root["data"] as JArray;
        if (data == null || data.Count == 0)
            return null;

        string wanted = NormalizeCategory(categoryName);
        JObject best = null;
        int bestScore = -1;

        foreach (JToken token in data)
        {
            JObject category = token as JObject;
            if (category == null)
                continue;

            int score = ScoreCategory(category, wanted);
            if (score > bestScore)
            {
                bestScore = score;
                best = category;
            }
        }

        if (best == null || bestScore < 50)
            return null;

        SpeedrunComCategory result = new SpeedrunComCategory();
        result.Id = GetString(best["id"]);
        result.Name = GetString(best["name"]);
        result.VariableFilters = GetDefaultVariableFilters(result.Id);
        return string.IsNullOrEmpty(result.Id) ? null : result;
    }

    private List<SpeedrunComVariableFilter> GetDefaultVariableFilters(string categoryId)
    {
        List<SpeedrunComVariableFilter> filters = new List<SpeedrunComVariableFilter>();
        JObject root = GetJson("https://www.speedrun.com/api/v1/categories/" + Uri.EscapeDataString(categoryId) + "/variables");
        JArray data = root["data"] as JArray;
        if (data == null)
            return filters;

        foreach (JToken token in data)
        {
            JObject variable = token as JObject;
            if (variable == null)
                continue;

            if (GetString(variable["is-subcategory"]).ToLowerInvariant() != "true")
                continue;

            string defaultValue = GetString(variable["values"], "default");
            if (string.IsNullOrEmpty(defaultValue))
                continue;

            SpeedrunComVariableFilter filter = new SpeedrunComVariableFilter();
            filter.Id = GetString(variable["id"]);
            filter.Value = defaultValue;
            if (!string.IsNullOrEmpty(filter.Id))
                filters.Add(filter);
        }

        return filters;
    }

    private int ScoreCategory(JObject category, string wanted)
    {
        string name = NormalizeCategory(GetString(category["name"]));
        string wantedBase = StripCategoryDetails(wanted);
        string nameBase = StripCategoryDetails(name);

        if (name == wanted)
            return 100;
        if (nameBase == wantedBase)
            return 90;
        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(wanted) && (wanted.StartsWith(name) || name.StartsWith(wanted)))
            return 80;
        if (ContainsEither(wanted, name))
            return 70;
        if (ContainsEither(wanted, nameBase) || ContainsEither(wantedBase, nameBase))
            return 60;

        return 0;
    }

    private LiveWrInfo GetSpeedrunComLeaderboardWr(SpeedrunComGame game, SpeedrunComCategory category)
    {
        string url = "https://www.speedrun.com/api/v1/leaderboards/" +
            Uri.EscapeDataString(game.Id) +
            "/category/" +
            Uri.EscapeDataString(category.Id) +
            "?top=1&embed=players" +
            BuildVariableQuery(category.VariableFilters);

        JObject data = GetJson(url)["data"] as JObject;
        JArray runs = data == null ? null : data["runs"] as JArray;

        if (runs == null || runs.Count == 0)
            return LiveWrInfo.Fail("Leaderboard hat keinen Top-Run");

        JObject runEntry = runs[0] as JObject;
        JObject run = runEntry == null ? null : runEntry["run"] as JObject;
        if (run == null)
            return LiveWrInfo.Fail("Leaderboard-Antwort enthaelt keine Run-Daten");

        string time = FormatSpeedrunComTime(run["times"]);
        if (string.IsNullOrEmpty(time))
            return LiveWrInfo.Fail("Leaderboard-Antwort enthaelt keine Hauptzeit");

        LiveWrInfo info = new LiveWrInfo();
        info.Success = true;
        info.WrTime = time;
        info.WrRunner = FormatSpeedrunComPlayers(run["players"], data["players"]);
        return info;
    }

    private void AddSpeedrunComRunnerPb(LiveWrInfo info, SpeedrunHoraroCommon.ScheduleRun run, SpeedrunComGame game, SpeedrunComCategory category)
    {
        List<string> segments = SplitRunnerSegments(run.Runner);

        if (segments.Count <= 1)
        {
            // Nur ein Runner im Feld (ggf. mit Leerzeichen im Namen, z.B. "John Doe").
            string wholeText = segments.Count == 1 ? segments[0] : run.Runner;
            RunnerPbEntry entry = FindPbForRunnerText(wholeText, game, category, null);
            if (entry != null)
                info.PbEntries.Add(entry);
            return;
        }

        // Mehrere Runner im Feld (Race/Co-op): jeden einzeln auf speedrun.com nachschlagen.
        List<string> seenUserIds = new List<string>();
        foreach (string segment in segments)
        {
            RunnerPbEntry entry = FindPbForRunnerText(segment, game, category, seenUserIds);
            if (entry != null)
                info.PbEntries.Add(entry);
        }
    }

    private RunnerPbEntry FindPbForRunnerText(string runnerText, SpeedrunComGame game, SpeedrunComCategory category, List<string> seenUserIds)
    {
        foreach (string candidate in GetRunnerNameCandidates(runnerText))
        {
            SpeedrunComUser user = FindSpeedrunComUser(candidate);
            if (user == null)
                continue;

            if (seenUserIds != null && !string.IsNullOrEmpty(user.Id))
            {
                if (seenUserIds.Contains(user.Id))
                    continue; // dieser Runner wurde ueber einen anderen Namens-Treffer schon erfasst

                seenUserIds.Add(user.Id);
            }

            RunnerPbInfo pb = GetSpeedrunComUserPersonalBest(user, game, category);
            if (pb == null)
                continue;

            RunnerPbEntry entry = new RunnerPbEntry();
            entry.Runner = string.IsNullOrEmpty(user.Name) ? candidate : user.Name;
            entry.Time = pb.Time;
            entry.Place = pb.Place;
            return entry;
        }

        return null;
    }

    // Trennt das Runner-Feld in einzelne Personen auf, z.B.
    // "Alice & Bob" / "Alice, Bob" / "Alice vs. Bob" / "Alice and Bob" -> ["Alice", "Bob"].
    // Liefert genau 1 Eintrag zurueck, wenn keine Trenner gefunden wurden (Einzel-Runner).
    private List<string> SplitRunnerSegments(string runnerText)
    {
        List<string> segments = new List<string>();
        string clean = SpeedrunHoraroCommon.Clean(runnerText);
        if (string.IsNullOrEmpty(clean))
            return segments;

        string splitText = Regex.Replace(clean, @"\s+(and|vs\.?|versus)\s+", "|", RegexOptions.IgnoreCase);
        splitText = splitText.Replace("&", "|").Replace(",", "|").Replace("/", "|").Replace("+", "|");

        foreach (string part in splitText.Split('|'))
        {
            string segment = part.Trim();
            if (segment.Length == 0)
                continue;

            if (!ContainsNormalized(segments, segment))
                segments.Add(segment);

            if (segments.Count >= 8)
                break;
        }

        return segments;
    }

    private List<string> GetRunnerNameCandidates(string runnerText)
    {
        List<string> candidates = new List<string>();
        string clean = SpeedrunHoraroCommon.Clean(runnerText);
        if (string.IsNullOrEmpty(clean))
            return candidates;

        candidates.Add(clean);

        string splitText = Regex.Replace(clean, @"\s+(and|vs|versus)\s+", " ", RegexOptions.IgnoreCase);
        splitText = splitText.Replace("&", " ").Replace(",", " ");

        foreach (string part in Regex.Split(splitText, @"\s+"))
        {
            string candidate = part.Trim();
            if (candidate.Length == 0 || IsCommonRunnerWord(candidate))
                continue;

            if (!ContainsNormalized(candidates, candidate))
                candidates.Add(candidate);

            if (candidates.Count >= 6)
                break;
        }

        return candidates;
    }

    private SpeedrunComUser FindSpeedrunComUser(string runnerName)
    {
        SpeedrunComUser exact = FindSpeedrunComUserByUrl("https://www.speedrun.com/api/v1/users?lookup=" + Uri.EscapeDataString(runnerName), runnerName);
        if (exact != null)
            return exact;

        return FindSpeedrunComUserByUrl("https://www.speedrun.com/api/v1/users?name=" + Uri.EscapeDataString(runnerName) + "&max=5", runnerName);
    }

    private SpeedrunComUser FindSpeedrunComUserByUrl(string url, string runnerName)
    {
        JObject root = GetJson(url);
        JArray data = root["data"] as JArray;
        if (data == null || data.Count == 0)
            return null;

        string wanted = SpeedrunHoraroCommon.Normalize(runnerName);
        JObject best = null;
        int bestScore = -1;

        foreach (JToken token in data)
        {
            JObject user = token as JObject;
            if (user == null)
                continue;

            int score = ScoreUser(user, wanted);
            if (score > bestScore)
            {
                bestScore = score;
                best = user;
            }
        }

        if (best == null || bestScore < 60)
            return null;

        SpeedrunComUser result = new SpeedrunComUser();
        result.Id = GetString(best["id"]);
        result.Name = GetString(best["names"], "international");
        if (string.IsNullOrEmpty(result.Name))
            result.Name = runnerName;

        return string.IsNullOrEmpty(result.Id) ? null : result;
    }

    private int ScoreUser(JObject user, string wanted)
    {
        string name = SpeedrunHoraroCommon.Normalize(GetString(user["names"], "international"));
        string twitch = SpeedrunHoraroCommon.Normalize(GetSocialName(user, "twitch"));
        string youtube = SpeedrunHoraroCommon.Normalize(GetSocialName(user, "youtube"));
        string twitter = SpeedrunHoraroCommon.Normalize(GetSocialName(user, "twitter"));
        string weblinkName = SpeedrunHoraroCommon.Normalize(GetIdFromUri(GetString(user["weblink"])));

        if (name == wanted || twitch == wanted || youtube == wanted || twitter == wanted || weblinkName == wanted)
            return 100;
        if (ContainsEither(name, wanted))
            return 80;
        if (ContainsEither(twitch, wanted))
            return 75;
        if (ContainsEither(weblinkName, wanted))
            return 70;

        return 0;
    }

    private string GetSocialName(JObject user, string property)
    {
        JObject social = user[property] as JObject;
        if (social == null)
            return "";

        return GetIdFromUri(GetString(social["uri"]));
    }

    private RunnerPbInfo GetSpeedrunComUserPersonalBest(SpeedrunComUser user, SpeedrunComGame game, SpeedrunComCategory category)
    {
        string url = "https://www.speedrun.com/api/v1/users/" +
            Uri.EscapeDataString(user.Id) +
            "/personal-bests?game=" +
            Uri.EscapeDataString(game.Id) +
            "&embed=game,category";

        JObject root = GetJson(url);
        JArray data = root["data"] as JArray;
        if (data == null || data.Count == 0)
            return null;

        foreach (JToken token in data)
        {
            JObject entry = token as JObject;
            if (entry == null)
                continue;

            JObject run = entry["run"] as JObject;
            if (run == null || GetString(run["category"]) != category.Id || !RunMatchesVariableFilters(run, category.VariableFilters))
                continue;

            string time = FormatSpeedrunComTime(run["times"]);
            if (string.IsNullOrEmpty(time))
                continue;

            RunnerPbInfo pb = new RunnerPbInfo();
            pb.Time = time;
            pb.Place = GetInt(entry["place"]);
            return pb;
        }

        return null;
    }

    private JObject GetJson(string url)
    {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
        request.Method = "GET";
        request.Accept = "application/json";
        request.UserAgent = SpeedrunHoraroCommon.GetConfiguredPath(CPH, "speedruncom_user_agent", "streamerbot-speedrun-event/1.0");
        request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
        request.Timeout = SpeedrunHoraroCommon.GetConfiguredInt(CPH, "speedruncom_request_timeout_ms", 6000);
        request.ReadWriteTimeout = request.Timeout;

        using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
        using (Stream stream = response.GetResponseStream())
        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
        {
            return JObject.Parse(reader.ReadToEnd());
        }
    }

    private string BuildVariableQuery(List<SpeedrunComVariableFilter> filters)
    {
        if (filters == null || filters.Count == 0)
            return "";

        StringBuilder query = new StringBuilder();
        foreach (SpeedrunComVariableFilter filter in filters)
        {
            if (string.IsNullOrEmpty(filter.Id) || string.IsNullOrEmpty(filter.Value))
                continue;

            query.Append("&var-");
            query.Append(Uri.EscapeDataString(filter.Id));
            query.Append("=");
            query.Append(Uri.EscapeDataString(filter.Value));
        }

        return query.ToString();
    }

    private bool RunMatchesVariableFilters(JObject run, List<SpeedrunComVariableFilter> filters)
    {
        if (filters == null || filters.Count == 0)
            return true;

        JObject values = run["values"] as JObject;
        if (values == null)
            return false;

        foreach (SpeedrunComVariableFilter filter in filters)
        {
            if (string.IsNullOrEmpty(filter.Id) || string.IsNullOrEmpty(filter.Value))
                continue;

            if (GetString(values[filter.Id]) != filter.Value)
                return false;
        }

        return true;
    }

    private string FormatSpeedrunComTime(JToken timesToken)
    {
        JObject times = timesToken as JObject;
        if (times == null)
            return "";

        double seconds;
        if (double.TryParse(GetString(times["primary_t"]), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
            return FormatSeconds(seconds);

        return GetString(times["primary"]);
    }

    private string FormatSeconds(double seconds)
    {
        if (seconds < 0)
            return "";

        TimeSpan time = TimeSpan.FromSeconds(seconds);
        int totalHours = (int)Math.Floor(time.TotalHours);

        if (Math.Abs(seconds - Math.Round(seconds)) > 0.0001)
        {
            int centiseconds = (int)Math.Round(time.Milliseconds / 10.0);
            if (totalHours > 0)
                return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}.{3:00}", totalHours, time.Minutes, time.Seconds, centiseconds);
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:00}", time.Minutes, time.Seconds, centiseconds);
        }

        if (totalHours > 0)
            return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", totalHours, time.Minutes, time.Seconds);

        return string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", time.Minutes, time.Seconds);
    }

    private string FormatSpeedrunComPlayers(JToken runPlayersToken, JToken embeddedPlayersToken)
    {
        JArray players = runPlayersToken as JArray;
        if (players == null || players.Count == 0)
            return "";

        Dictionary<string, string> embeddedNames = BuildEmbeddedPlayerNames(embeddedPlayersToken);
        List<string> names = new List<string>();

        foreach (JToken token in players)
        {
            JObject player = token as JObject;
            if (player == null)
                continue;

            string name = GetString(player["name"]);
            string id = GetString(player["id"]);
            if (string.IsNullOrEmpty(id))
                id = GetIdFromUri(GetString(player["uri"]));
            if (string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(id) && embeddedNames.ContainsKey(id))
                name = embeddedNames[id];
            if (string.IsNullOrEmpty(name))
                name = id;

            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return string.Join(", ", names.ToArray());
    }

    private Dictionary<string, string> BuildEmbeddedPlayerNames(JToken embeddedPlayersToken)
    {
        Dictionary<string, string> names = new Dictionary<string, string>();
        JArray players = embeddedPlayersToken as JArray;

        JObject wrapper = embeddedPlayersToken as JObject;
        if (players == null && wrapper != null)
            players = wrapper["data"] as JArray;

        if (players == null)
            return names;

        foreach (JToken token in players)
        {
            JObject player = token as JObject;
            if (player == null)
                continue;

            string id = GetString(player["id"]);
            string name = GetString(player["name"]);
            if (string.IsNullOrEmpty(name))
                name = GetString(player["names"], "international");
            if (string.IsNullOrEmpty(name))
                name = GetString(player["names"], "twitch");

            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(name) && !names.ContainsKey(id))
                names.Add(id, name);
        }

        return names;
    }

    private LiveWrInfo TryReadSpeedrunComCache(string cacheKey, int cacheSeconds, int errorCacheSeconds)
    {
        if (cacheSeconds <= 0 && errorCacheSeconds <= 0)
            return null;

        try
        {
            string key = CPH.GetGlobalVar<string>("speedruncom_wr_cache_key", true);
            if (key != cacheKey)
                return null;

            string cachedAtValue = CPH.GetGlobalVar<string>("speedruncom_wr_cache_time_utc", true);
            DateTime cachedAt;
            if (!DateTime.TryParse(cachedAtValue, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out cachedAt))
                return null;

            double ageSeconds = (DateTime.UtcNow - cachedAt).TotalSeconds;

            string wrTime = CPH.GetGlobalVar<string>("speedruncom_wr_cache_time", true);
            if (!string.IsNullOrEmpty(wrTime))
            {
                if (cacheSeconds <= 0 || ageSeconds > cacheSeconds)
                    return null;

                LiveWrInfo info = new LiveWrInfo();
                info.Success = true;
                info.WrTime = wrTime;
                info.WrRunner = CPH.GetGlobalVar<string>("speedruncom_wr_cache_runner", true);
                info.PbEntries = DeserializePbEntries(CPH.GetGlobalVar<string>("speedruncom_pb_cache_entries", true));
                return info;
            }

            string error = CPH.GetGlobalVar<string>("speedruncom_wr_cache_error", true);
            if (!string.IsNullOrEmpty(error) && errorCacheSeconds > 0 && ageSeconds <= errorCacheSeconds)
                return LiveWrInfo.Fail(error);

            return null;
        }
        catch
        {
            return null;
        }
    }

    private void WriteSpeedrunComCache(string cacheKey, LiveWrInfo info)
    {
        try
        {
            CPH.SetGlobalVar("speedruncom_wr_cache_key", cacheKey, true);
            CPH.SetGlobalVar("speedruncom_wr_cache_time_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
            CPH.SetGlobalVar("speedruncom_wr_cache_time", info.WrTime ?? "", true);
            CPH.SetGlobalVar("speedruncom_wr_cache_runner", info.WrRunner ?? "", true);
            CPH.SetGlobalVar("speedruncom_wr_cache_error", "", true);
            CPH.SetGlobalVar("speedruncom_pb_cache_entries", SerializePbEntries(info.PbEntries), true);
        }
        catch
        {
        }
    }

    private void WriteSpeedrunComErrorCache(string cacheKey, string error)
    {
        try
        {
            CPH.SetGlobalVar("speedruncom_wr_cache_key", cacheKey, true);
            CPH.SetGlobalVar("speedruncom_wr_cache_time_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), true);
            CPH.SetGlobalVar("speedruncom_wr_cache_time", "", true);
            CPH.SetGlobalVar("speedruncom_wr_cache_runner", "", true);
            CPH.SetGlobalVar("speedruncom_wr_cache_error", error ?? "", true);
            CPH.SetGlobalVar("speedruncom_pb_cache_entries", "", true);
        }
        catch
        {
        }
    }

    private string SerializePbEntries(List<RunnerPbEntry> entries)
    {
        if (entries == null || entries.Count == 0)
            return "";

        List<string> parts = new List<string>();
        foreach (RunnerPbEntry entry in entries)
            parts.Add((entry.Runner ?? "").Replace("|", " ") + "::" + (entry.Time ?? "") + "::" + entry.Place.ToString(CultureInfo.InvariantCulture));

        return string.Join("||", parts.ToArray());
    }

    private List<RunnerPbEntry> DeserializePbEntries(string serialized)
    {
        List<RunnerPbEntry> entries = new List<RunnerPbEntry>();
        if (string.IsNullOrEmpty(serialized))
            return entries;

        foreach (string part in serialized.Split(new string[] { "||" }, StringSplitOptions.None))
        {
            if (part.Length == 0)
                continue;

            string[] fields = part.Split(new string[] { "::" }, StringSplitOptions.None);
            if (fields.Length < 2)
                continue;

            RunnerPbEntry entry = new RunnerPbEntry();
            entry.Runner = fields[0];
            entry.Time = fields[1];

            int place;
            entry.Place = fields.Length >= 3 && int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out place) ? place : 0;
            entries.Add(entry);
        }

        return entries;
    }

    private WrInfo FindCsvWrInfo(string csvPath, char delimiter, SpeedrunHoraroCommon.ScheduleRun run)
    {
        try
        {
            if (!File.Exists(csvPath))
                return null;

            string[] lines = File.ReadAllLines(csvPath, new UTF8Encoding(true));
            if (lines.Length < 2)
                return null;

            string[] headers = lines[0].Split(delimiter);
            int idxGame = FindCsvHeader(headers, "spielname");
            int idxCategory = FindCsvHeader(headers, "speedrun kategorie");
            int idxWrRunner = FindCsvHeader(headers, "wr-runner");
            int idxWrTime = FindCsvHeader(headers, "wr-zeit");
            int idxRunnerName = FindCsvHeader(headers, "runner name");
            int idxRunnerTime = FindCsvHeader(headers, "runner zeit");

            if (idxGame < 0)
                return null;

            string gameKey = SpeedrunHoraroCommon.Normalize(run.Game);
            string categoryKey = SpeedrunHoraroCommon.Normalize(run.Category);
            string[] gameOnlyMatch = null;

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                string[] cols = lines[i].Split(delimiter);
                if (SpeedrunHoraroCommon.Normalize(GetCsv(cols, idxGame)) != gameKey)
                    continue;

                if (gameOnlyMatch == null)
                    gameOnlyMatch = cols;

                if (idxCategory >= 0 && SpeedrunHoraroCommon.Normalize(GetCsv(cols, idxCategory)) == categoryKey)
                    return ToWrInfo(cols, idxWrRunner, idxWrTime, idxRunnerName, idxRunnerTime);
            }

            return gameOnlyMatch == null ? null : ToWrInfo(gameOnlyMatch, idxWrRunner, idxWrTime, idxRunnerName, idxRunnerTime);
        }
        catch
        {
            return null;
        }
    }

    private int FindCsvHeader(string[] headers, string search)
    {
        string key = SpeedrunHoraroCommon.Normalize(search);
        for (int i = 0; i < headers.Length; i++)
        {
            if (SpeedrunHoraroCommon.Normalize(headers[i]) == key)
                return i;
        }

        return -1;
    }

    private WrInfo ToWrInfo(string[] cols, int idxWrRunner, int idxWrTime, int idxRunnerName, int idxRunnerTime)
    {
        WrInfo info = new WrInfo();
        info.WrRunner = GetCsv(cols, idxWrRunner);
        info.WrTime = GetCsv(cols, idxWrTime);
        info.RunnerName = GetCsv(cols, idxRunnerName);
        info.RunnerTime = GetCsv(cols, idxRunnerTime);
        return info;
    }

    private string GetCsv(string[] cols, int index)
    {
        if (index < 0 || index >= cols.Length)
            return "";

        return cols[index].Trim();
    }

    private string FormatWebException(WebException ex)
    {
        HttpWebResponse response = ex.Response as HttpWebResponse;
        if (response != null)
            return "speedrun.com antwortete mit HTTP " + (int)response.StatusCode + " " + response.StatusCode;

        if (ex.Status == WebExceptionStatus.Timeout)
            return "speedrun.com-Anfrage hat zu lange gedauert";

        return ex.Message;
    }

    private string StripCategoryDetails(string value)
    {
        string result = Regex.Replace(value ?? "", @"\s*\([^)]*\)", "");
        result = Regex.Replace(result, @"\s*\[[^\]]*\]", "");
        result = Regex.Replace(result, @"\s+-\s+.*$", "");
        return NormalizeCategory(result);
    }

    private string NormalizeCategory(string value)
    {
        string normalized = SpeedrunHoraroCommon.Normalize(value);
        normalized = normalized.Replace(" unrestricted", "");
        normalized = normalized.Replace(" restricted", "");
        normalized = normalized.Replace(" race", "");
        return SpeedrunHoraroCommon.Clean(normalized);
    }

    private string GetString(JToken token)
    {
        if (token == null || token.Type == JTokenType.Null)
            return "";

        return token.ToString().Trim();
    }

    private string GetString(JToken token, string property)
    {
        JObject obj = token as JObject;
        return obj == null ? "" : GetString(obj[property]);
    }

    private int GetInt(JToken token)
    {
        if (token == null)
            return 0;

        int value;
        if (int.TryParse(token.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return value;

        return 0;
    }

    private string GetIdFromUri(string uri)
    {
        if (string.IsNullOrEmpty(uri))
            return "";

        int lastSlash = uri.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash >= uri.Length - 1)
            return "";

        return uri.Substring(lastSlash + 1);
    }

    private bool IsCommonRunnerWord(string value)
    {
        string normalized = SpeedrunHoraroCommon.Normalize(value);
        return normalized == "the" || normalized == "and" || normalized == "vs" || normalized == "versus";
    }

    private bool ContainsEither(string left, string right)
    {
        return !string.IsNullOrEmpty(left) &&
               !string.IsNullOrEmpty(right) &&
               (left.Contains(right) || right.Contains(left));
    }

    private bool ContainsNormalized(List<string> values, string candidate)
    {
        string normalized = SpeedrunHoraroCommon.Normalize(candidate);
        foreach (string value in values)
        {
            if (SpeedrunHoraroCommon.Normalize(value) == normalized)
                return true;
        }

        return false;
    }

    private class WrInfo
    {
        public string WrRunner;
        public string WrTime;
        public string RunnerName;
        public string RunnerTime;
    }

    private class LiveWrInfo
    {
        public bool Success;
        public string WrRunner;
        public string WrTime;
        public List<RunnerPbEntry> PbEntries = new List<RunnerPbEntry>();
        public string Error;

        public static LiveWrInfo Fail(string error)
        {
            LiveWrInfo info = new LiveWrInfo();
            info.Success = false;
            info.Error = error;
            return info;
        }
    }

    private class RunnerPbEntry
    {
        public string Runner;
        public string Time;
        public int Place;
    }

    private class SpeedrunComGame
    {
        public string Id;
        public string Name;
    }

    private class SpeedrunComCategory
    {
        public string Id;
        public string Name;
        public List<SpeedrunComVariableFilter> VariableFilters;
    }

    private class SpeedrunComVariableFilter
    {
        public string Id;
        public string Value;
    }

    private class SpeedrunComUser
    {
        public string Id;
        public string Name;
    }

    private class RunnerPbInfo
    {
        public string Time;
        public int Place;
    }
}
