using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Разбор сессии (окно у панели MMR дектрекера — «разбор цифр панелей HDT»): по своим записям — сколько партий,
	/// среднее место, топ-4, изменение MMR; какие сборки и герои давали места; к какому ходу доходил до т5/т6 в топ-4 партиях и в остальных —
	/// то, чего на панели HDT не видно. Считается только после партии и при запуске (чтение записей), не во время игры.
	/// </summary>
	internal static class SessionStats
	{
		private class Game
		{
			public DateTime Time;
			public string Hero, Build;
			public int Place, Rating, RatingAfter;
			public int? Tier5Turn, Tier6Turn;
		}

		private static readonly object Lock = new object();
		private static List<string> _lines = new List<string>();
		private static Task _task;

		public static List<string> Lines
		{
			get { lock(Lock) return _lines.ToList(); }
		}

		/// <summary>Пересчитать в фоне (после партии, при запуске).</summary>
		public static void Refresh()
		{
			if(_task?.IsCompleted == false)
				return;
			_task = Task.Run(() =>
			{
				try
				{
					var lines = Build(Load());
					lock(Lock)
						_lines = lines;
				}
				catch(Exception ex)
				{
					Paths.Log("SessionStats: " + ex.Message);
				}
			});
		}

		/// <summary>Партии из записей: бои копятся, game_end закрывает партию (дуо — пропускаем).</summary>
		private static List<Game> Load()
		{
			var games = new List<Game>();
			var combats = new List<JObject>();
			foreach(var file in Directory.GetFiles(Paths.Recordings, "combats_*.jsonl").OrderBy(f => f))
				foreach(var line in File.ReadLines(file))
				{
					if(!line.Contains("\"type\":\"combat\"") && !line.Contains("\"type\":\"game_end\""))
						continue;
					JObject r;
					try
					{
						r = JObject.Parse(line);
					}
					catch(Exception)
					{
						continue;
					}
					if((string)r["type"] == "combat")
					{
						combats.Add(r);
						continue;
					}
					if((bool?)r["duos"] != true && (int?)r["placement"] > 0)
						games.Add(Summarize(r, combats));
					combats = new List<JObject>();
				}
			return games;
		}

		private static Game Summarize(JObject end, List<JObject> combats)
		{
			var g = new Game
			{
				Time = end["time"]?.Type == JTokenType.Date ? end["time"].Value<DateTime>().ToLocalTime() : DateTime.MinValue,
				Hero = LobbyState.HeroName((string)end["hero"]), Place = (int?)end["placement"] ?? 0,
				Rating = (int?)end["rating"] ?? 0, RatingAfter = (int?)end["rating_after"] ?? 0,
			};
			foreach(var c in combats.OrderBy(c => (int?)c["turn"] ?? 0))
			{
				int turn = (int?)c["turn"] ?? 0, tier = (int?)c["input"]?["Player"]?["Tier"] ?? 0;
				if(tier >= 5 && g.Tier5Turn == null)
					g.Tier5Turn = turn;
				if(tier >= 6 && g.Tier6Turn == null)
					g.Tier6Turn = turn;
			}
			// Сборка — по моему последнему столу (карты) и его составу.
			var last = combats.LastOrDefault()?["input"]?["Player"]?["Side"] as JArray;
			if(last != null)
			{
				var cards = last.OfType<JObject>().Select(m => (string)m["CardID"]).Where(x => x != null).ToList();
				var races = new JObject();
				foreach(var m in last.OfType<JObject>())
				{
					var r = (string)m["PrimaryRace"];
					if(!string.IsNullOrEmpty(r) && r != "INVALID")
						races[r] = ((int?)races[r] ?? 0) + 1;
				}
				g.Build = CompGuides.Match(races, cards, 1).FirstOrDefault()?.Guide.Name;
			}
			return g;
		}

		private static List<string> Build(List<Game> all)
		{
			var lines = new List<string>();
			if(all.Count == 0)
				return lines;
			var today = all.Where(g => g.Time.Date == DateTime.Today).ToList();
			void Summary(string ru, string en, List<Game> games)
			{
				if(games.Count == 0)
					return;
				int delta = games.Sum(g => g.RatingAfter > 0 && g.Rating > 0 ? g.RatingAfter - g.Rating : 0);
				lines.Add(L.T($"{ru}: {games.Count} парт. · среднее место {games.Average(g => g.Place):F1} · топ-4 {games.Count(g => g.Place <= 4) / (double)games.Count:P0} · 1-е {games.Count(g => g.Place == 1)} · MMR {delta:+#;−#;0}",
					$"{en}: {games.Count} games · avg place {games.Average(g => g.Place):F1} · top 4 {games.Count(g => g.Place <= 4) / (double)games.Count:P0} · 1st {games.Count(g => g.Place == 1)} · MMR {delta:+#;−#;0}"));
			}
			Summary("Сегодня", "Today", today);
			Summary("Все записи", "All recorded", all);

			// Сборки и герои: среднее место (больше одной партии — надёжнее).
			string Group(IEnumerable<IGrouping<string, Game>> groups) =>
				string.Join(" · ", groups.Where(gr => gr.Key != null).OrderBy(gr => gr.Average(g => g.Place)).ThenByDescending(gr => gr.Count()).Take(4)
					.Select(gr => $"«{gr.Key}» {gr.Average(g => g.Place):F1} ({gr.Count()})"));
			var builds = Group(all.GroupBy(g => g.Build));
			if(builds.Length > 0)
				lines.Add(L.T("Сборки (среднее место, партий): ", "Builds (avg place, games): ") + builds);
			var heroes = Group(all.GroupBy(g => g.Hero));
			if(heroes.Length > 0)
				lines.Add(L.T("Герои: ", "Heroes: ") + heroes);

			// Темп: к какому ходу т5/т6 в топ-4 партиях и в остальных — частая причина «почему такое место».
			string Tempo(Func<Game, int?> turnOf, int tier)
			{
				var top = all.Where(g => g.Place <= 4).Select(turnOf).Where(t => t != null).Select(t => (double)t).ToList();
				var bottom = all.Where(g => g.Place > 4).Select(turnOf).Where(t => t != null).Select(t => (double)t).ToList();
				int topNever = all.Count(g => g.Place <= 4 && turnOf(g) == null), bottomNever = all.Count(g => g.Place > 4 && turnOf(g) == null);
				if(top.Count + bottom.Count == 0)
					return null;
				string Part(List<double> v, int never) => (v.Count > 0 ? L.T($"к ходу {v.Average():F1}", $"by turn {v.Average():F1}") : "—")
														  + (never > 0 ? L.T($", не дошли {never}", $", never {never}") : "");
				return L.T($"Т{tier}: в топ-4 партиях {Part(top, topNever)}; в остальных {Part(bottom, bottomNever)}",
					$"T{tier}: top-4 games {Part(top, topNever)}; others {Part(bottom, bottomNever)}");
			}
			foreach(var line in new[] { Tempo(g => g.Tier5Turn, 5), Tempo(g => g.Tier6Turn, 6) })
				if(line != null)
					lines.Add(line);
			var lastGame = all.Last();
			lines.Add(L.T($"Последняя: {lastGame.Place}-е место, {lastGame.Hero}", $"Last: place {lastGame.Place}, {lastGame.Hero}")
					  + (lastGame.Build != null ? $", «{lastGame.Build}»" : "")
					  + (lastGame.Tier6Turn != null ? L.T($", т6 на ходу {lastGame.Tier6Turn}", $", t6 on turn {lastGame.Tier6Turn}") : L.T(", до т6 не дошёл", ", never reached t6")));
			return lines;
		}
	}
}
