using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Официальные таблицы лидеров Blizzard (публичный API без ключей):
	/// https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData?region=EU&amp;leaderboardId=battlegrounds&amp;page=N
	/// В таблице только верх рейтинга (EU 2026-10: ~6000 игроков, рейтинг ≥ 8000). Кэш на диске — сутки.
	/// Страницы качаем не больше чем в 4 потока с паузой, чтобы не нагружать сервер Blizzard.
	/// </summary>
	internal class LeaderboardService
	{
		private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
		private static readonly HttpClient Http = CreateClient();

		private readonly object _lock = new object();
		private readonly Dictionary<string, Task<Dictionary<string, int>>> _loading = new Dictionary<string, Task<Dictionary<string, int>>>();

		/// <summary>Имя (без #1234, в нижнем регистре) → рейтинг. Пустой словарь, если регион не поддерживается или сеть недоступна.</summary>
		public Task<Dictionary<string, int>> GetAsync(string region, bool duos)
		{
			var key = $"{region}_{(duos ? "battlegroundsduo" : "battlegrounds")}";
			lock(_lock)
			{
				if(!_loading.TryGetValue(key, out var task) || task.IsFaulted)
					_loading[key] = task = Task.Run(() => Load(region, duos ? "battlegroundsduo" : "battlegrounds", key));
				return task;
			}
		}

		public static string NormalizeName(string name)
		{
			if(string.IsNullOrEmpty(name))
				return "";
			var hash = name.IndexOf('#');
			return (hash >= 0 ? name.Substring(0, hash) : name).Trim().ToLowerInvariant();
		}

		private static async Task<Dictionary<string, int>> Load(string region, string leaderboardId, string key)
		{
			var cacheFile = Path.Combine(Paths.Cache, $"leaderboard_{key}.json");
			var cached = ReadCache(cacheFile);
			if(cached != null && DateTime.Now - File.GetLastWriteTime(cacheFile) < CacheTtl)
				return cached;
			ActivityFeed.Post(L.T($"Таблица лидеров {region}: загружаю с сайта Blizzard…", $"Leaderboard {region}: downloading from Blizzard…"));

			var result = new Dictionary<string, int>();
			try
			{
				// Одна страница отвечает ~1,5 с, страниц ~243 → качаем в 4 потока (~1,5 мин вместо ~6 мин).
				var first = await FetchPage(region, leaderboardId, 1);
				int totalPages = first?["pagination"]?["totalPages"]?.Value<int>() ?? 1;
				Merge(result, first);
				SetProgress(key, 1, totalPages);

				int done = 1;
				using(var gate = new SemaphoreSlim(4))
				{
					var tasks = Enumerable.Range(2, Math.Max(0, totalPages - 1)).Select(async page =>
					{
						await gate.WaitAsync();
						try
						{
							var board = await FetchPage(region, leaderboardId, page);
							lock(result)
								Merge(result, board);
							SetProgress(key, Interlocked.Increment(ref done), totalPages);
							await Task.Delay(100);
						}
						finally
						{
							gate.Release();
						}
					});
					await Task.WhenAll(tasks);
				}
				File.WriteAllText(cacheFile, JsonConvert.SerializeObject(result));
				Paths.Log($"Leaderboard {key}: {result.Count} игроков, {totalPages} страниц");
				return result;
			}
			catch(Exception ex)
			{
				// Связь пропала или сайт не отвечает — не портим кэш неполными данными, берём старый (любой давности).
				Paths.Log($"Leaderboard {key}: ошибка загрузки: {ex.Message}");
				lock(Progress)
					Progress.Remove(key);
				if(cached != null)
				{
					var age = DateTime.Now - File.GetLastWriteTime(cacheFile);
					ActivityFeed.Post(L.T($"Таблица лидеров: нет связи — использую сохранённую ({age.TotalHours:F0} ч назад)",
						$"Leaderboard: offline — using the saved copy ({age.TotalHours:F0} h old)"), ActivityLevel.Warning);
					return cached;
				}
				ActivityFeed.Post(L.T("Таблица лидеров: нет связи и нет сохранённой копии", "Leaderboard: offline and no saved copy"), ActivityLevel.Warning);
				return new Dictionary<string, int>();
			}
		}

		private static Dictionary<string, int> ReadCache(string file)
		{
			try
			{
				return File.Exists(file) ? JsonConvert.DeserializeObject<Dictionary<string, int>>(File.ReadAllText(file)) : null;
			}
			catch
			{
				return null; // битый кэш — как будто его нет
			}
		}

		/// <summary>Страница с повторами: до 4 попыток с нарастающей паузой (1, 2, 4 с) — переживает короткие обрывы связи.</summary>
		private static async Task<JToken> FetchPage(string region, string leaderboardId, int page)
		{
			var url = $"https://hearthstone.blizzard.com/en-us/api/community/leaderboardsData?region={region}&leaderboardId={leaderboardId}&page={page}";
			for(int attempt = 1; ; attempt++)
			{
				try
				{
					return JObject.Parse(await Http.GetStringAsync(url))["leaderboard"];
				}
				catch when(attempt < 4)
				{
					await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));
				}
			}
		}

		private static void Merge(Dictionary<string, int> result, JToken board)
		{
			foreach(var row in board?["rows"] ?? new JArray())
			{
				var name = NormalizeName(row["accountid"]?.Value<string>());
				var rating = row["rating"]?.Value<int>() ?? 0;
				// Одинаковые имена бывают — оставляем максимальный рейтинг.
				if(name.Length > 0 && (!result.TryGetValue(name, out var old) || old < rating))
					result[name] = rating;
			}
		}

		private static readonly Dictionary<string, string> Progress = new Dictionary<string, string>();

		private static void SetProgress(string key, int done, int total)
		{
			lock(Progress)
				Progress[key] = $"{done}/{total}";
		}

		/// <summary>Прогресс загрузки для панели, например «57/243»; null — не грузится.</summary>
		public static string GetProgress(string region, bool duos)
		{
			lock(Progress)
				return Progress.TryGetValue($"{region}_{(duos ? "battlegroundsduo" : "battlegrounds")}", out var p) ? p : null;
		}

		private static HttpClient CreateClient()
		{
			var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
			client.DefaultRequestHeaders.Add("User-Agent", "HSaiHelper-HDT-plugin/0.1");
			return client;
		}
	}
}
