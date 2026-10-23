using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using HearthDb.Enums;
using Hearthstone_Deck_Tracker.Hearthstone.Entities;
using Core = Hearthstone_Deck_Tracker.API.Core;
using Type = System.Type;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// «Что сейчас на столе» — для ИИ-помощника, по-английски: золото, тир и цена повышения, мой стол (статы, ключевые слова, текст карты),
	/// рука, предложения таверны (в таверне существа Боба — это «соперник» HDT), в бою — стол соперника и прогноз BobsBuddy, который HDT уже посчитал.
	/// Тексты карт — из HearthDb (модель карты не знает — проверено тестом), с вычищенной разметкой. Только чтение сущностей HDT, UI-поток.
	/// </summary>
	internal static class BoardNow
	{
		private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
		private static readonly Type InvokerType = typeof(Core).Assembly.GetType("Hearthstone_Deck_Tracker.BobsBuddy.BobsBuddyInvoker");
		private static readonly FieldInfo InstancesField = InvokerType?.GetField("_instances", Any);
		private static readonly FieldInfo TurnField = InvokerType?.GetField("_turn", Any);
		private static readonly PropertyInfo OutputProp = InvokerType?.GetProperty("Output", Any);

		private static readonly (GameTag Tag, string Name)[] Keywords =
		{
			(GameTag.TAUNT, "Taunt"), (GameTag.DIVINE_SHIELD, "Divine Shield"), (GameTag.REBORN, "Reborn"), (GameTag.POISONOUS, "Poisonous"),
			(GameTag.VENOMOUS, "Venomous"), (GameTag.WINDFURY, "Windfury"), (GameTag.MEGA_WINDFURY, "Mega-Windfury"), (GameTag.STEALTH, "Stealth"),
		};

		public static string Text(int turn)
		{
			var sb = new StringBuilder();
			var game = Core.Game;
			bool combat = game.IsBattlegroundsCombatPhase;
			var me = game.PlayerEntity;
			var hero = game.Player.Hero;
			if(me != null)
			{
				int gold = me.GetTag(GameTag.RESOURCES) - me.GetTag(GameTag.RESOURCES_USED) + me.GetTag(GameTag.TEMP_RESOURCES);
				var upgrade = game.Player.PlayerEntities.FirstOrDefault(e => e.CardId?.StartsWith("TB_BaconShopTechUp") == true);
				sb.AppendLine($"Right now: {(combat ? "combat" : "tavern phase")}, turn {turn}; tavern tier {hero?.GetTag(GameTag.PLAYER_TECH_LEVEL)}"
							  + (combat ? "" : $", gold {gold}") + (upgrade != null && !combat ? $", tavern upgrade costs {upgrade.GetTag(GameTag.COST)}" : ""));
			}
			var gift = DarkGiftText(turn);
			if(gift != null)
				sb.AppendLine(gift);
			// Всё, что влияет на исход, кроме существ: аномалия, мой герой и его сила, безделушки, задание.
			var anomaly = game.GameEntity?.GetTag(GameTag.BACON_GLOBAL_ANOMALY_DBID) ?? 0;
			if(anomaly > 0)
				sb.AppendLine("Lobby anomaly: " + DbfText(anomaly));
			if(hero != null)
				sb.AppendLine($"My hero: {CardName(hero.CardId)}" + SideExtras(game.Player.PlayerEntities, true));
			sb.AppendLine("My board (left to right):");
			AppendMinions(sb, Minions(game.Player.Board));
			var hand = game.Player.Hand.Where(e => !string.IsNullOrEmpty(e.CardId)).ToList();
			if(hand.Count > 0)
				sb.AppendLine("My hand: " + string.Join("; ", hand.Select(e => e.IsMinion ? MinionLine(e) : CardName(e.CardId) + " — " + CardText(e.CardId))));
			if(!combat)
			{
				// В таверне предложения Боба — сущности «соперника» HDT на столе.
				var offers = game.Opponent.Board.Where(e => !string.IsNullOrEmpty(e.CardId)).OrderBy(e => e.GetTag(GameTag.ZONE_POSITION)).ToList();
				if(offers.Count > 0)
				{
					sb.AppendLine("Tavern offers now:");
					foreach(var e in offers)
						sb.AppendLine("  " + (e.IsMinion ? MinionLine(e) : CardName(e.CardId) + " (spell) — " + CardText(e.CardId)));
				}
			}
			else
			{
				var opHero = game.Opponent.Hero;
				if(opHero != null)
					sb.AppendLine($"Opponent in this combat: {CardName(opHero.CardId)}" + SideExtras(game.Opponent.PlayerEntities, false));
				sb.AppendLine("Opponent board in this combat:");
				AppendMinions(sb, Minions(game.Opponent.Board));
				var odds = BobsBuddyOdds(turn);
				if(odds != null)
					sb.AppendLine("BobsBuddy simulation of this combat (exact boards): " + odds);
			}
			return sb.ToString();
		}

		/// <summary>
		/// Тёмный дар (аномалия сезона, кнопка BG36_Button_DarkGift): сколько раскопок осталось и какого тира существо сейчас.
		/// Тиры — TAG_SCRIPT_DATA_NUM_3/4 («тир X или Y»), растут по ходам (логи: р3 — 2/3, р4 — 3, р5 — 3/4, р6 — 4, …); осталось — NUM_2.
		/// null — в этой партии тёмного дара нет.
		/// </summary>
		public static (int UsesLeft, int TierA, int TierB, int Cost)? DarkGift()
		{
			try
			{
				var button = Core.Game.Player.PlayerEntities.FirstOrDefault(e => e.CardId == "BG36_Button_DarkGift");
				if(button == null)
					return null;
				return (button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_2), button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_3), button.GetTag(GameTag.TAG_SCRIPT_DATA_NUM_4),
					button.GetTag(GameTag.COST));
			}
			catch(Exception)
			{
				return null;
			}
		}

		/// <summary>«Тёмный дар: осталось 2 · сейчас тир 2 или 3 · 3 золота» + совет JeefHS (брать на 3–5 ходу).</summary>
		public static string DarkGiftText(int turn)
		{
			if(!(DarkGift() is var g) || g == null)
				return null;
			var (left, a, b, cost) = g.Value;
			if(left <= 0)
				return L.T("Тёмный дар: раскопки закончились", "Dark Gift: no discovers left");
			var tiers = a == b ? $"{a}" : L.T($"{a} или {b}", $"{a} or {b}");
			if(turn < 3)
				return L.T($"Тёмный дар: откроется на 3-м ходу (тир 2 или 3) · осталось {left}", $"Dark Gift: unlocks on turn 3 (tier 2 or 3) · {left} left");
			var advice = turn <= 5 ? L.T(" · лучше брать на 3–5 ходу — сейчас самое время", " · best on turns 3–5 — now is the time")
				: L.T(" · позже дар может не вписаться в собранную сборку", " · later the gift may not fit the finished build");
			return L.T($"Тёмный дар: осталось {left} · сейчас тир {tiers} · {cost} золота", $"Dark Gift: {left} left · now tier {tiers} · {cost} gold") + advice;
		}

		/// <summary>Сила героя (текст, цена, нажата ли), безделушки, задание/награда стороны — из её сущностей в игре.</summary>
		private static string SideExtras(IEnumerable<Entity> entities, bool mine)
		{
			var sb = new StringBuilder();
			// Только известные карты (скрытые чужие секреты — без CardId) в игре или в зоне секретов (задания, награды, секреты).
			var list = entities.Where(e => !string.IsNullOrEmpty(e.CardId) && (e.GetTag(GameTag.ZONE) == (int)Zone.PLAY || e.GetTag(GameTag.ZONE) == (int)Zone.SECRET)).ToList();
			var power = list.FirstOrDefault(e => e.GetTag(GameTag.CARDTYPE) == (int)CardType.HERO_POWER);
			if(power != null)
				sb.Append($"; hero power «{CardName(power.CardId)}» (cost {power.GetTag(GameTag.COST)}"
						  + (mine ? (power.GetTag(GameTag.EXHAUSTED) > 0 ? ", already used this turn" : ", not used yet") : "") + $"): {CardText(power.CardId)}");
			foreach(var t in list.Where(e => e.GetTag(GameTag.CARDTYPE) == (int)CardType.BATTLEGROUND_TRINKET && e.GetTag(GameTag.BACON_TRINKET) > 0))
				sb.Append($"; trinket «{CardName(t.CardId)}»: {CardText(t.CardId)}");
			foreach(var q in list.Where(e => e.GetTag(GameTag.ZONE) == (int)Zone.SECRET && (e.HasTag(GameTag.QUEST) || e.GetTag(GameTag.CARDTYPE) == (int)CardType.BATTLEGROUND_QUEST_REWARD)))
				sb.Append($"; quest/reward «{CardName(q.CardId)}»: {CardText(q.CardId)}"
						  + (q.HasTag(GameTag.QUEST_PROGRESS_TOTAL) ? $" (progress {q.GetTag(GameTag.QUEST_PROGRESS)}/{q.GetTag(GameTag.QUEST_PROGRESS_TOTAL)})" : ""));
			if(mine)
				foreach(var s in list.Where(e => e.GetTag(GameTag.ZONE) == (int)Zone.SECRET && e.HasTag(GameTag.SECRET)))
					sb.Append($"; secret «{CardName(s.CardId)}»: {CardText(s.CardId)}");
			return sb.ToString();
		}

		/// <summary>
		/// Герои лобби — для ИИ: сила героя каждого и его безделушки (публично: таблица лидеров, теги BACON_FIRST/SECOND_TRINKET_DATABASE_ID).
		/// Сила героя — из описания героя (HERO_POWER в CardDefs).
		/// </summary>
		public static string LobbyHeroes()
		{
			var sb = new StringBuilder();
			try
			{
				var heroes = Core.Game.Entities.Values.ToList()
					.Where(e => e.GetTag(GameTag.CARDTYPE) == (int)CardType.HERO && e.HasTag(GameTag.PLAYER_LEADERBOARD_PLACE) && !string.IsNullOrEmpty(e.CardId))
					.GroupBy(e => e.CardId).Select(g => g.OrderByDescending(e => e.Id).First())
					.OrderBy(e => e.GetTag(GameTag.PLAYER_LEADERBOARD_PLACE)).ToList();
				foreach(var h in heroes)
				{
					var line = new StringBuilder($"  {CardName(h.CardId)}");
					if(HearthDb.Cards.All.TryGetValue(h.CardId, out var card) && card.Entity.GetTag(GameTag.HERO_POWER) is int hp && hp > 0
					   && HearthDb.Cards.AllByDbfId.TryGetValue(hp, out var power))
						line.Append($" — hero power «{power.GetLocName(Locale.enUS) ?? power.Name}»: {CardText(power.Id)}");
					foreach(var tag in new[] { GameTag.BACON_FIRST_TRINKET_DATABASE_ID, GameTag.BACON_SECOND_TRINKET_DATABASE_ID })
						if(h.GetTag(tag) is int t && t > 0)
							line.Append("; trinket " + DbfText(t));
					sb.AppendLine(line.ToString());
				}
			}
			catch(InvalidOperationException)
			{
				// HDT меняет список сущностей — в следующий раз
			}
			return sb.Length == 0 ? null : "Heroes in the lobby (hero power, public trinkets):\n" + sb;
		}

		private static string DbfText(int dbf) =>
			HearthDb.Cards.AllByDbfId.TryGetValue(dbf, out var c) ? $"«{c.GetLocName(Locale.enUS) ?? c.Name}»: {CardText(c.Id)}" : $"#{dbf}";

		private static List<Entity> Minions(IEnumerable<Entity> board) =>
			board.Where(e => e.IsMinion).OrderBy(e => e.GetTag(GameTag.ZONE_POSITION)).ToList();

		private static void AppendMinions(StringBuilder sb, List<Entity> minions)
		{
			if(minions.Count == 0)
				sb.AppendLine("  (empty)");
			foreach(var e in minions)
				sb.AppendLine("  " + MinionLine(e));
		}

		private static string MinionLine(Entity e)
		{
			var keywords = Keywords.Where(k => e.HasTag(k.Tag) && e.GetTag(k.Tag) > 0).Select(k => k.Name).ToList();
			var card = HearthDb.Cards.All.TryGetValue(e.CardId ?? "", out var c) ? c : null;
			var text = CardText(e.CardId);
			return $"«{CardName(e.CardId)}» {e.Attack}/{e.Health}" + (e.GetTag(GameTag.PREMIUM) > 0 ? " golden" : "")
				   + (card != null && card.TechLevel > 0 ? $" t{card.TechLevel}" : "")
				   + (keywords.Count > 0 ? " [" + string.Join(", ", keywords) + "]" : "")
				   + (text.Length > 0 ? " — " + text : "");
		}

		private static string CardName(string cardId) =>
			cardId != null && HearthDb.Cards.All.TryGetValue(cardId, out var c) ? c.GetLocName(Locale.enUS) ?? c.Name ?? cardId : cardId ?? "?";

		/// <summary>Текст карты без разметки ([x], &lt;b&gt;, переносов); числа-заглушки {0} заменяются на «X».</summary>
		private static string CardText(string cardId)
		{
			if(cardId == null || !HearthDb.Cards.All.TryGetValue(cardId, out var c))
				return "";
			var text = c.GetLocText(Locale.enUS) ?? c.Text ?? "";
			text = Regex.Replace(text, @"\[x\]|<[^>]+>|[$#@]", "");
			text = Regex.Replace(text, @"\{\d+\}", "X");
			return Regex.Replace(text, @"\s+", " ").Trim();
		}

		/// <summary>Прогноз, который HDT уже посчитал для текущего боя (экземпляр BobsBuddyInvoker этого раунда).</summary>
		private static string BobsBuddyOdds(int turn)
		{
			try
			{
				if(!(InstancesField?.GetValue(null) is IDictionary instances))
					return null;
				foreach(var invoker in instances.Values.Cast<object>().Reverse())
				{
					if(TurnField?.GetValue(invoker) as int? != turn)
						continue;
					var output = OutputProp?.GetValue(invoker);
					if(output == null)
						continue;
					double Get(string name) => Convert.ToDouble(output.GetType().GetField(name)?.GetValue(output) ?? output.GetType().GetProperty(name)?.GetValue(output) ?? 0.0);
					return $"win {Get("winRate"):P0} · tie {Get("tieRate"):P0} · loss {Get("lossRate"):P0}";
				}
			}
			catch(Exception)
			{
				// список меняется в другом потоке — без прогноза
			}
			return null;
		}
	}
}
