using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Обезличенная выгрузка записей партий для проекта («Настройки» → «Подготовить данные для проекта»): ради сбора этих данных
	/// плагин и раздаётся игрокам. Ничего не отправляется само — получается zip, который человек решает отправить сам.
	///
	/// Что убирается/огрубляется (проверено 2026-10-23: ников и BattleTag в записях нет):
	///   time → только дата + порядковый номер (точное время + MMR + регион позволяют найти сильного игрока по таблицам лидеров);
	///   rating / rating_after → диапазон по 500 («2500–2999»);
	///   key (id партии HDT) → хэш с солью этой установки (записи одной партии связаны, к человеку — нет);
	///   вопросы к ИИ, ответы и заметки (ai_chat, ai_exchange, comment) — только если человек поставил галочку.
	/// В архиве — manifest.json: версия плагина и формата, число записей по типам, диапазон дат, случайный id выгрузки.
	/// </summary>
	internal static class DataExport
	{
		private static readonly HashSet<string> GameTypes = new HashSet<string> { "combat", "game_end", "lobby_state", "combat_history", "unusual_board" };
		private static readonly HashSet<string> ChatTypes = new HashSet<string> { "ai_chat", "ai_exchange", "comment" };

		public class Result
		{
			public string File;
			public Dictionary<string, int> Counts = new Dictionary<string, int>();
			public string Dates;
		}

		public static Result Export(bool includeChat)
		{
			var salt = Settings.Instance.InstallSalt;
			if(string.IsNullOrEmpty(salt))
			{
				salt = Guid.NewGuid().ToString("N");
				Settings.Instance.InstallSalt = salt;
				Settings.Instance.Save();
			}
			var dir = Directory.CreateDirectory(Path.Combine(Paths.Root, "export")).FullName;
			var exportId = Guid.NewGuid().ToString("N").Substring(0, 8);
			var file = Path.Combine(dir, $"hsai_data_{DateTime.Now:yyyyMMdd}_{exportId}.zip");
			var result = new Result { File = file };
			var dates = new SortedSet<string>();
			using(var zip = ZipFile.Open(file, ZipArchiveMode.Create))
			{
				foreach(var src in Directory.GetFiles(Paths.Recordings, "*.jsonl").OrderBy(f => f))
				{
					var sb = new StringBuilder();
					int seq = 0;
					foreach(var line in File.ReadLines(src))
					{
						JObject r;
						try
						{
							r = JObject.Parse(line);
						}
						catch(JsonException)
						{
							continue; // оборванная запись
						}
						var type = (string)r["type"] ?? "";
						if(!(GameTypes.Contains(type) || includeChat && ChatTypes.Contains(type)))
							continue;
						Anonymize(r, salt, ++seq, dates);
						sb.AppendLine(r.ToString(Formatting.None));
						result.Counts[type] = (result.Counts.TryGetValue(type, out var n) ? n : 0) + 1;
					}
					if(sb.Length == 0)
						continue;
					var entry = zip.CreateEntry(Path.GetFileName(src));
					using(var w = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
						w.Write(sb.ToString());
				}
				result.Dates = dates.Count == 0 ? "—" : $"{dates.First()} — {dates.Last()}";
				var manifest = new JObject
				{
					["format"] = 1,
					["plugin_version"] = Paths.VersionText,
					["export_id"] = exportId,
					["dates"] = result.Dates,
					["counts"] = JObject.FromObject(result.Counts),
					["includes_chat"] = includeChat,
					["anonymized"] = "time→date+seq; rating→500-bucket; key→salted hash; no names/BattleTags recorded",
				};
				var m = zip.CreateEntry("manifest.json");
				using(var w = new StreamWriter(m.Open(), new UTF8Encoding(false)))
					w.Write(manifest.ToString(Formatting.Indented));
			}
			Paths.Log($"DataExport: {file} — {string.Join(", ", result.Counts.Select(kv => $"{kv.Key} {kv.Value}"))}");
			return result;
		}

		private static void Anonymize(JObject r, string salt, int seq, SortedSet<string> dates)
		{
			if(r["time"]?.Type == JTokenType.Date || r["time"]?.Type == JTokenType.String)
			{
				// Newtonsoft сам превращает ISO-время в дату — берём её; строку (если не распозналась) разбираем как ISO.
				var date = r["time"].Type == JTokenType.Date ? r["time"].Value<DateTime>().ToString("yyyy-MM-dd")
					: DateTime.TryParse((string)r["time"], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var t) ? t.ToString("yyyy-MM-dd") : null;
				r.Remove("time");
				if(date != null)
				{
					r["date"] = date;
					dates.Add(date);
				}
			}
			r["seq"] = seq;
			foreach(var field in new[] { "rating", "rating_after" })
				if(r[field] is JValue v && v.Type == JTokenType.Integer)
				{
					int bucket = (int)v / 500 * 500;
					r[field] = $"{bucket}–{bucket + 499}";
				}
			if(r["key"] is JValue key && key.Type == JTokenType.String)
				r["key"] = Hash(salt + (string)key);
		}

		private static string Hash(string s)
		{
			using(var sha = SHA256.Create())
				return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
		}
	}
}
