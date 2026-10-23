using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>Гайд сборки (панель HDT «Comp Guides», бесплатный список — сборки JeefHS).</summary>
	internal class CompGuide
	{
		public string Name;
		public string TierText;
		public int TierRank;
		/// <summary>Основной тип сборки — имя из HearthDb.Race (ABERRATION, …); null — сборка без основного типа.</summary>
		public string Tribe;
		public HashSet<int> Core = new HashSet<int>();
		public HashSet<int> Addon = new HashSet<int>();
	}

	/// <summary>Насколько стол игрока похож на гайд.</summary>
	internal class GuideMatch
	{
		public CompGuide Guide;
		public int Percent;
		public string Why;
	}

	/// <summary>
	/// Гайды сборок: берутся из того, что Recorder сохранил (recordings\comp_guides_*.json, самый свежий файл),
	/// и обновляются, когда HDT загружает новый список. Своих запросов к сайтам нет.
	/// </summary>
	internal static class CompGuides
	{
		private static readonly object Lock = new object();
		private static List<CompGuide> _guides;

		public static IReadOnlyList<CompGuide> All
		{
			get
			{
				lock(Lock)
				{
					if(_guides == null)
						_guides = LoadNewestFile();
					return _guides;
				}
			}
		}

		/// <summary>Новый список из Recorder (содержимое поля Comps сохранённого файла).</summary>
		public static void Set(JArray comps)
		{
			var parsed = Parse(comps);
			if(parsed.Count > 0)
				lock(Lock)
					_guides = parsed;
		}

		private static List<CompGuide> LoadNewestFile()
		{
			try
			{
				var file = Directory.GetFiles(Paths.Recordings, "comp_guides_*.json").OrderBy(f => f).LastOrDefault();
				if(file != null)
					return Parse(JObject.Parse(File.ReadAllText(file))["Comps"] as JArray);
			}
			catch(Exception ex)
			{
				Paths.Log("CompGuides: не прочитаны: " + ex.Message);
			}
			return new List<CompGuide>();
		}

		private static List<CompGuide> Parse(JArray comps)
		{
			var list = new List<CompGuide>();
			foreach(var item in comps ?? new JArray())
			{
				var g = item["CompGuide"];
				if(g == null)
					continue;
				var tribe = (int?)g["PrimaryTribe"] ?? 0;
				list.Add(new CompGuide
				{
					Name = (string)g["Name"],
					TierText = (string)item["TierText"],
					TierRank = (int?)g["TierRank"] ?? 99,
					Tribe = tribe > 0 ? Enum.GetName(typeof(Race), tribe) : null,
					Core = new HashSet<int>((g["CoreCards"] as JArray ?? new JArray()).Select(x => (int)x)),
					Addon = new HashSet<int>((g["AddonCards"] as JArray ?? new JArray()).Select(x => (int)x)),
				});
			}
			return list;
		}

		/// <summary>
		/// Лучшие совпадения. Публичные данные — состав по типам (доля существ основного типа гайда);
		/// если стол видели (свой стол или стол соперника в бою с нами) — ещё и карты: ключевые целиком, дополнительные вполовину.
		/// </summary>
		public static List<GuideMatch> Match(JObject races, ICollection<string> cardIds, int count = 2)
		{
			var guides = All;
			if(guides.Count == 0)
				return new List<GuideMatch>();
			var counts = races?.Properties().ToDictionary(p => p.Name, p => (int)p.Value) ?? new Dictionary<string, int>();
			int typed = Math.Max(1, counts.Where(kv => kv.Key != "ALL").Sum(kv => kv.Value));
			var dbfs = new HashSet<int>((cardIds ?? new string[0]).Select(Dbf).Where(d => d > 0));

			var result = new List<GuideMatch>();
			foreach(var g in guides)
			{
				double tribe = g.Tribe == null ? 0.3 : Math.Min(1.0, ((counts.TryGetValue(g.Tribe, out var n) ? n : 0) + (counts.TryGetValue("ALL", out var all) ? all : 0)) / (double)typed);
				if(dbfs.Count == 0)
				{
					result.Add(new GuideMatch { Guide = g, Percent = (int)Math.Round(tribe * 100), Why = L.T("только по типу", "by tribe only") });
					continue;
				}
				int core = g.Core.Count(dbfs.Contains), addon = g.Addon.Count(dbfs.Contains);
				double weight = g.Core.Count + 0.5 * g.Addon.Count;
				double cards = weight > 0 ? (core + 0.5 * addon) / weight : 0;
				result.Add(new GuideMatch
				{
					Guide = g,
					Percent = (int)Math.Round(100 * (0.4 * tribe + 0.6 * cards)),
					Why = L.T($"тип {tribe:P0}, ключевые {core}/{g.Core.Count}, доп. {addon}/{g.Addon.Count}",
						$"tribe {tribe:P0}, core {core}/{g.Core.Count}, add-on {addon}/{g.Addon.Count}"),
				});
			}
			return result.Where(m => m.Percent > 0).OrderByDescending(m => m.Percent).ThenBy(m => m.Guide.TierRank).Take(count).ToList();
		}

		private static int Dbf(string cardId)
		{
			if(cardId == null)
				return 0;
			if(HearthDb.Cards.TripleToNormalCardIds.TryGetValue(cardId, out var normal))
				cardId = normal;
			return HearthDb.Cards.All.TryGetValue(cardId, out var card) ? card.DbfId : 0;
		}
	}
}
