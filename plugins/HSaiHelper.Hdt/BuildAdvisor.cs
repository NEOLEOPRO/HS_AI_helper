using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HearthDb.Enums;
using Newtonsoft.Json.Linq;

namespace HSaiHelper.Hdt
{
	/// <summary>Что известно о моей партии для советов по сборке (снимок на конец таверны — в бою не пересчитывается).</summary>
	internal class BuildState
	{
		public List<string> MyCards = new List<string>();
		public List<string> Offers = new List<string>();
		public List<string> Trinkets = new List<string>();
		public int Tier = 1, Gold, BoardCount, UpgradeCost, Turn;
		public HashSet<Race> Lobby = new HashSet<Race>();
		/// <summary>Карты, которых нет в пуле (мои + увиденные у соперников).</summary>
		public Dictionary<string, int> Taken = new Dictionary<string, int>();
		/// <summary>Состав соперников по типам (таблица лидеров) — кто ещё собирает тот же тип.</summary>
		public List<JObject> OtherTiles = new List<JObject>();
		/// <summary>Сила моего стола против поля раунда (BobsBuddy), null — не посчитано.</summary>
		public FieldResult Field;
	}

	/// <summary>
	/// Советы по сборке (2026-10-23): советовать то, что проще собрать. Для каждого гайда JeefHS — сколько золота
	/// примерно стоит добрать ключевые карты: 3 за покупку, роллы до появления (1 / шанс за ролл по пулу), повышения до нужного тира;
	/// ключевая карта в таверне прямо сейчас — подсветка «★»; разобранная (копий в пуле 0) — сборку не собрать.
	/// Безделушка, привязанная к типу, — бонус сборкам этого типа; не привязана — все сборки на равных (часто стоит переключиться).
	/// Смена сборки: JeefHS при сильной экономике меняет весь стол за ход — советуем, когда другая сборка заметно дешевле
	/// и хватает золота с продажей стола, или когда текущую не собрать (ключевая разобрана, тип собирают несколько соперников, стол слабее поля).
	/// Эвристика, проверять в игре.
	/// </summary>
	internal static class BuildAdvisor
	{
		/// <summary>Цена повышения с тира t на t+1 без скидок (BG): 5, 7, 8, 9, 11, 10.</summary>
		private static readonly int[] BaseUpgrade = { 5, 7, 8, 9, 11, 10 };
		private const int TrinketBonus = 6;
		private const int SwitchGap = 8;

		private static readonly (string Word, Race Race)[] TribeWords =
		{
			("beast", Race.BEAST), ("demon", Race.DEMON), ("dragon", Race.DRAGON), ("elemental", Race.ELEMENTAL), ("mech", Race.MECHANICAL),
			("murloc", Race.MURLOC), ("naga", Race.NAGA), ("pirate", Race.PIRATE), ("quilboar", Race.QUILBOAR), ("undead", Race.UNDEAD),
		};

		private class Plan
		{
			public CompGuide Guide;
			public int Owned, CoreCount, OwnedAddon;
			public double Gold;
			public List<string> Parts = new List<string>();
			public List<HearthDb.Card> InTavern = new List<HearthDb.Card>();
			public List<HearthDb.Card> Exhausted = new List<HearthDb.Card>();
			public bool TrinketFit;
		}

		public static IEnumerable<string> Lines(BuildState s)
		{
			if(CompGuides.All.Count == 0)
				yield break;
			var myCounts = PoolOdds.Taken(s.MyCards);
			var trinketTribes = new HashSet<Race>(s.Trinkets.SelectMany(TribesIn));
			var plans = CompGuides.All.Select(g => Evaluate(g, s, myCounts, trinketTribes)).Where(p => p != null).ToList();
			if(plans.Count == 0)
				yield break;
			// Тир гайда (S/A/B/C) — небольшая добавка: при равной цене лучше сильная сборка. TierRank — место внутри тира, не тир.
			double Score(Plan p) => p.Gold - (p.TrinketFit ? TrinketBonus : 0) + 1.5 * TierLetter(p.Guide.TierText);
			var ranked = plans.OrderBy(Score).ToList();
			var current = plans.Where(p => p.Owned + 0.5 * p.OwnedAddon >= 1).OrderByDescending(p => p.Owned + 0.5 * p.OwnedAddon).ThenBy(Score).FirstOrDefault();

			yield return L.T("Сборка — что проще собрать (≈ золота до ключевых карт):", "Build — easiest to finish (≈ gold to the core cards):");
			foreach(var p in ranked.Take(3))
			{
				var mark = p == current ? L.T(" ← твоя", " ← yours") : "";
				var trinket = p.TrinketFit ? L.T(" · под безделушку", " · fits your trinket") : "";
				yield return $"  «{p.Guide.Name}» [{p.Guide.TierText}] · " + L.T($"есть {p.Owned}/{p.CoreCount}", $"have {p.Owned}/{p.CoreCount}")
							 + (p.Exhausted.Count > 0 ? "" : $" · ≈{p.Gold:F0}" + L.T(" зол.", " gold")) + trinket + mark
							 + (p.Parts.Count > 0 ? ": " + string.Join(", ", p.Parts.Take(3)) : "");
			}

			// Подсветка: карты из первых трёх сборок (и моей), которые прямо сейчас в таверне.
			var highlighted = new HashSet<string>();
			foreach(var p in ranked.Take(3).Concat(current == null ? new Plan[0] : new[] { current }).Distinct())
				foreach(var c in p.InTavern.Concat(AddonInTavern(p.Guide, s, myCounts)))
					if(highlighted.Add(c.Id + "|" + p.Guide.Name))
						yield return L.T($"★ в таверне: «{Name(c)}» — для «{p.Guide.Name}»", $"★ in the tavern: \"{Name(c)}\" — for \"{p.Guide.Name}\"");

			var reason = SwitchReason(current, ranked[0], s);
			if(reason != null)
				yield return reason;
		}

		/// <summary>Когда действительно стоит менять сборку — и хватит ли золота сделать это за один ход.</summary>
		private static string SwitchReason(Plan current, Plan best, BuildState s)
		{
			if(current == null || s.Turn < 4)
				return null;
			var why = new List<string>();
			if(current.Exhausted.Count > 0)
				why.Add(L.T($"ключевая «{Name(current.Exhausted[0])}» разобрана — «{current.Guide.Name}» не собрать",
					$"core \"{Name(current.Exhausted[0])}\" is gone from the pool — \"{current.Guide.Name}\" can't be finished"));
			if(current.Guide.Tribe != null && Enum.TryParse(current.Guide.Tribe, out Race tribe))
			{
				int rivals = s.OtherTiles.Count(t => (bool?)t["dead"] != true && TribeCount(t, tribe) >= 3);
				if(rivals >= 2)
					why.Add(L.T($"этот тип собирают ещё {rivals} игрока — карты редки", $"{rivals} other players collect this tribe — cards are scarce"));
			}
			if(s.Field != null && s.Field.Boards > 0 && s.Turn >= 7 && s.Field.Win < 0.35)
				why.Add(L.T($"стол слабее поля (побед ≈{s.Field.Win:P0})", $"board weaker than the field (≈{s.Field.Win:P0} wins)"));
			bool cheaper = best != current && current.Gold - best.Gold >= SwitchGap;
			if(cheaper)
				why.Insert(0, L.T($"«{best.Guide.Name}» дешевле на ≈{current.Gold - best.Gold:F0} золота", $"\"{best.Guide.Name}\" is ≈{current.Gold - best.Gold:F0} gold cheaper"));
			if(why.Count == 0 || best == current && current.Exhausted.Count == 0)
				return null;
			if(best == current)
				return "⚠ " + L.T("Сборка под угрозой: ", "Build at risk: ") + string.Join("; ", why);
			// Переход за один ход (как у JeefHS при сильной экономике): золото + продажа стола против цены новой сборки.
			int budget = s.Gold + s.BoardCount;
			var oneTurn = budget >= best.Gold
				? L.T($"можно за один ход: {s.Gold} золота + {s.BoardCount} за продажу стола", $"doable in one turn: {s.Gold} gold + {s.BoardCount} from selling")
				: L.T($"за один ход не хватит ({budget} из ≈{best.Gold:F0}) — копи или переходи по частям", $"not in one turn ({budget} of ≈{best.Gold:F0}) — save up or switch piece by piece");
			return "⚠ " + L.T($"Смена сборки → «{best.Guide.Name}»: ", $"Switch build → \"{best.Guide.Name}\": ") + string.Join("; ", why) + " · " + oneTurn;
		}

		private static Plan Evaluate(CompGuide g, BuildState s, Dictionary<string, int> myCounts, HashSet<Race> trinketTribes)
		{
			Race tribe = Race.INVALID;
			if(g.Tribe != null && Enum.TryParse(g.Tribe, out tribe) && s.Lobby.Count > 0 && !s.Lobby.Contains(tribe))
				return null;
			var core = g.Core.Select(d => HearthDb.Cards.AllByDbfId.TryGetValue(d, out var c) ? c : null).Where(c => c != null).ToList();
			var eligible = core.Where(c => PoolOdds.Eligible(c, s.Lobby)).ToList();
			if(eligible.Count == 0 || eligible.Count * 2 < core.Count)
				return null;
			var plan = new Plan { Guide = g, CoreCount = eligible.Count, TrinketFit = tribe != Race.INVALID && trinketTribes.Contains(tribe) };
			plan.OwnedAddon = g.Addon.Count(d => HearthDb.Cards.AllByDbfId.TryGetValue(d, out var c) && myCounts.ContainsKey(c.Id));
			int needTier = s.Tier;
			double rolls = 0, buys = 0;
			foreach(var c in eligible.OrderBy(c => c.TechLevel))
			{
				if(myCounts.ContainsKey(c.Id))
				{
					plan.Owned++;
					continue;
				}
				buys += 3;
				if(s.Offers.Contains(c.Id))
				{
					plan.InTavern.Add(c);
					plan.Parts.Add(L.T($"«{Name(c)}» ★ в таверне", $"\"{Name(c)}\" ★ in the tavern"));
					continue;
				}
				needTier = Math.Max(needTier, c.TechLevel);
				var chance = PoolOdds.RollChance(c, Math.Max(s.Tier, c.TechLevel), s.Lobby, s.Taken, out var left);
				if(left == 0 || chance <= 0)
				{
					plan.Exhausted.Add(c);
					plan.Parts.Add(L.T($"«{Name(c)}» разобрана", $"\"{Name(c)}\" gone"));
					continue;
				}
				double r = Math.Min(25, 1 / chance.Value);
				rolls += r;
				plan.Parts.Add(c.TechLevel > s.Tier
					? L.T($"«{Name(c)}» т{c.TechLevel}", $"\"{Name(c)}\" t{c.TechLevel}")
					: L.T($"«{Name(c)}» ≈{r:F0} ролл.", $"\"{Name(c)}\" ≈{r:F0} rolls"));
			}
			plan.Gold = plan.Exhausted.Count > 0 ? 99 : buys + rolls + UpgradeGold(s.Tier, needTier, s.UpgradeCost);
			return plan;
		}

		private static IEnumerable<HearthDb.Card> AddonInTavern(CompGuide g, BuildState s, Dictionary<string, int> myCounts) =>
			g.Addon.Select(d => HearthDb.Cards.AllByDbfId.TryGetValue(d, out var c) ? c : null)
				.Where(c => c != null && s.Offers.Contains(c.Id) && !myCounts.ContainsKey(c.Id));

		private static double UpgradeGold(int tier, int needTier, int currentCost)
		{
			// Тир ещё не прочитан (0) — считаем с 1-го (1.2.1 падал здесь: индекс −1, вся панель не обновлялась).
			tier = Math.Max(1, tier);
			double gold = 0;
			for(int t = tier; t < needTier && t <= BaseUpgrade.Length; t++)
				gold += t == tier && currentCost > 0 ? currentCost : BaseUpgrade[t - 1] - 1;
			return gold;
		}

		/// <summary>Типы, названные в тексте безделушки («Your Undead…») — значит, она привязана к архетипу.</summary>
		private static IEnumerable<Race> TribesIn(string cardId)
		{
			if(!HearthDb.Cards.All.TryGetValue(cardId, out var card))
				yield break;
			var text = Regex.Replace(card.GetLocText(Locale.enUS) ?? "", "<[^>]+>", "").ToLowerInvariant();
			foreach(var (word, race) in TribeWords)
				if(Regex.IsMatch(text, $@"\b{word}s?\b"))
					yield return race;
		}

		private static int TribeCount(JObject tile, Race tribe)
		{
			if(!(tile["races"] is JObject races))
				return 0;
			int n = 0;
			foreach(var p in races.Properties())
				if(p.Name == tribe.ToString() || p.Name == "MECH" && tribe == Race.MECHANICAL || p.Name == "ALL")
					n += (int)p.Value;
			return n;
		}

		private static int TierLetter(string tier) => tier == "S" ? 0 : tier == "A" ? 1 : tier == "B" ? 2 : 3;

		private static string Name(HearthDb.Card c) => c.GetLocName(L.CardLocale) ?? c.Name;
	}
}
