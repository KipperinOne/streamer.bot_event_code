This code is meant to be used in Streamer.bot! It is programmed in C# and open to use under the GPLv3 license.

To use the code, you will need to provide a CSV file with the information of your choosing. It can be different from what I used, as my version of the code is designed for speedrun events to automatically update chat command responses and stream titles.

If you want to use my version, just fill out the spreadsheet with the correct data and you're good to go. If you want to create your own version, you will also need to modify some lines in the code, but that's not difficult.

If you have any questions or ideas, feel free to let me know!

## Horaro based commands

The file `speedrun_horaro_common` contains shared Horaro/config/format helpers used by the Horaro actions. Add it to the same Streamer.bot C# compile context as each command action.

Important Streamer.bot setup:

- In the C# sub-action `References` tab, add `System.dll` manually. Streamer.bot does not always add it for inline code. Example path: `C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.dll`.
- Paste `speedrun_horaro_common` together with the action code into the same C# compile context, or otherwise make sure both are compiled together.
- The action files intentionally do not use a `ScheduleRun` alias, because that alias can collide with Streamer.bot's generated internal namespace.

The files `speedrun_horaro_wr`, `speedrun_horaro_next`, and `speedrun_horaro_fetch` are Streamer.bot inline C# actions for:

- `!wr`: reads the current runnable Horaro schedule item, tries speedrun.com for the current WR and runner PB, and falls back to the CSV only when live data is missing or unavailable.
- `!next`: reads the next runnable Horaro schedule item.
- Timer fetch: refreshes the Horaro JSON from the configured Horaro API URL and saves it to the schedule JSON path. It does not send chat messages; it writes fetch status into global variables.

Default paths:

- Schedule JSON: `C:\StreamerBot\speedrun_schedule.json`
- WR/PB CSV: `C:\StreamerBot\speedrun_wr.csv`

Optional global variables:

- `horaro_schedule_json_path`: override the schedule JSON path.
- `horaro_schedule_api_url`: Horaro API URL used by `speedrun_horaro_fetch`, for example `https://horaro.net/-/api/v1/schedules/0911pcbeeb1ep27a69` for the old AGDQ 2026 German restream test schedule.
- `horaro_schedule_timeout_ms`: Horaro request timeout for the fetch action. Defaults to `8000`.
- `horaro_schedule_user_agent`: custom Horaro User-Agent. Defaults to `streamerbot-horaro-schedule/1.0`.
- `speedrun_wr_csv_path`: override the CSV path.
- `horaro_schedule_now`: override the current time for testing old schedules, for example `2026-01-04T18:10:00+01:00`.
- `speedruncom_cache_seconds`: live WR cache duration in seconds. Defaults to `300`.
- `speedruncom_error_cache_seconds`: live lookup failure cache duration in seconds. Defaults to `60`.
- `speedruncom_request_timeout_ms`: speedrun.com request timeout. Defaults to `6000`.
- `speedruncom_user_agent`: custom speedrun.com User-Agent. Defaults to `streamerbot-speedrun-event/1.0`.

Fetch status globals written by `speedrun_horaro_fetch`:

- `horaro_schedule_last_fetch_status`: `updated`, `unchanged`, `missing horaro_schedule_api_url`, or `failed`.
- `horaro_schedule_last_fetch_error`: error detail when the fetch failed.
- `horaro_schedule_last_fetch_utc`: UTC timestamp of the last fetch attempt.

The commands use the Horaro export columns `Spieltitel`, `Runner`, `Kategorie`, and `Estimate`. Non-run schedule items such as intro, kickoff, GDQ recaps, and checkpoints are skipped for `!next`.

`!wr` uses speedrun.com REST API. The action sets a User-Agent, uses compressed responses, uses a short timeout, and caches successful live WR/PB lookups plus short-lived failures before falling back to the CSV.
