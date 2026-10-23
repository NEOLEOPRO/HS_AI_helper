using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BobsBuddy.Simulation;
using HearthDb.Enums;
using Core = Hearthstone_Deck_Tracker.API.Core;

namespace HSaiHelper.Hdt
{
	/// <summary>
	/// Подсказки на первые ходы (симуляция, а не решения игроков — tools/turn1_eval.py, tools/econ_plan.py):
	///   ход 1 — какое существо из таверны лучше для первого боя: каждое предложение против каждого возможного одиночного существа
	///   соперника 1-го хода в этом лобби (BobsBuddy, 300 боёв на пару; моя сила героя учитывается — Иллидан, Ал'Акир меняют ответ;
	///   эффект «в конце хода +N» — до боя); ничья = 0 (урона нет ни тебе, ни сопернику), считаем «победа − поражение»;
	///   ходы 1–3 — план: сильное существо; заклинание за 2 в таверне — заморозить; 2-й ход — повысить таверну почти всегда (2026-10-23:
	///   econ_plan считал только бои 1–3 и советовал существо — горизонт слишком короткий, 2-й тир окупается позже);
	///   3-й ход — тёмный дар + заклинание за 2 (5 золота = 3 + 2), если дар есть.
	/// Считается в фоне; пересчёт — когда сменились предложения таверны.
	/// </summary>
	internal static class OpeningAdvisor
	{
		private static readonly string[] TwoGoldMinionSpells = { "BG28_504", "BG28_512" }; // Recruit a Trainee, Enchanted Lasso
		private static string _signature;
		private static Task _task;
		private static List<(string CardId, double Win, double Loss)> _ranking;

		public static void Reset()
		{
			_signature = null;
			_ranking = null;
		}

		/// <summary>Строки для окна «Оценки» и сводки ИИ на ходах 1–3 (в таверне).</summary>
		public static IEnumerable<string> Lines(int turn, List<Race> lobbyRaces)
		{
			if(turn < 1 || turn > 3 || Core.Game.IsBattlegroundsCombatPhase)
				yield break;
			var offers = Core.Game.Opponent.Board.Where(e => !string.IsNullOrEmpty(e.CardId)).ToList();
			bool spell2 = offers.Any(e => TwoGoldMinionSpells.Contains(e.CardId));
			bool gift = BoardNow.DarkGift() != null;
			if(turn == 1)
			{
				var minions = offers.Where(e => e.IsMinion).Select(e => e.CardId).ToList();
				MaybeRank(minions, lobbyRaces);
				var r = _ranking;
				if(r != null && r.Count > 0)
					yield return L.T("Ход 1 — 1-й бой (победа/поражение): ", "Turn 1 — fight 1 (win/loss): ") + string.Join(" · ", r.Select((x, i) =>
						$"{(i == 0 ? "«" : "")}{Name(x.CardId)}{(i == 0 ? "»" : "")} {x.Win * 100:F0}/{x.Loss * 100:F0} %"))
						+ L.T(" — только первый бой, ценность на потом не учтена", " — first fight only, later value not counted");
				else if(minions.Count > 0)
					yield return L.T("Ход 1: считаю лучшую покупку…", "Turn 1: calculating the best buy…");
				yield return spell2
					? L.T("  в таверне заклинание за 2 — купи существо и заморозь таверну: заклинание пригодится на 2–3-м ходу",
						"  a 2-gold spell is in the tavern — buy a minion and freeze: the spell is useful on turn 2–3")
					: L.T("  план: сильное существо; увидишь заклинание за 2 — заморозь", "  plan: a strong minion; see a 2-gold spell — freeze");
			}
			else if(turn == 2)
				yield return L.T("Ход 2 — повышай таверну: почти всегда верно, даже если соперник не повысит (проигрыш сейчас — пара здоровья, 2-й тир окупается дальше)",
					"Turn 2 — level up: almost always right, even if the opponent doesn't (a loss now costs a couple of health, tier 2 pays off later)")
					+ (spell2 ? L.T("; заклинание за 2 — заморозь на 3-й ход", "; freeze the 2-gold spell for turn 3") : "");
			else if(gift)
				yield return spell2
					? L.T("Ход 3 — тёмный дар + заклинание за 2 (5 золота = 3 + 2)", "Turn 3 — Dark Gift + the 2-gold spell (5 gold = 3 + 2)")
					: L.T("Ход 3 — тёмный дар (3 золота); на оставшиеся 2 — заклинание за 2, если появится", "Turn 3 — Dark Gift (3 gold); the remaining 2 — a 2-gold spell if one shows up");
		}

		private static void MaybeRank(List<string> offers, List<Race> lobbyRaces)
		{
			var signature = string.Join(",", offers.OrderBy(x => x)) + "|" + string.Join(",", lobbyRaces);
			if(offers.Count == 0 || signature == _signature || _task?.IsCompleted == false)
				return;
			_signature = signature;
			_ranking = null;
			var lobby = new HashSet<Race>(lobbyRaces);
			var heroPower = Core.Game.Player.PlayerEntities.FirstOrDefault(e => e.GetTag(GameTag.CARDTYPE) == (int)CardType.HERO_POWER && e.GetTag(GameTag.ZONE) == (int)Zone.PLAY)?.CardId;
			var opponents = HearthDb.Cards.BaconPoolMinions.Values
				.Where(c => c.TechLevel == 1 && !HearthDb.Cards.TripleToNormalCardIds.ContainsKey(c.Id) && PoolOdds.Eligible(c, lobby)).Select(c => c.Id).ToList();
			_task = Task.Run(() =>
			{
				var result = new List<(string, double, double)>();
				foreach(var x in offers.Distinct())
				{
					double win = 0, loss = 0;
					int n = 0;
					foreach(var y in opponents)
					{
						try
						{
							var sim = new Simulator();
							var input = new Input();
							input.Player.Side.Add(Minion(sim, x, true));
							input.Opponent.Side.Add(Minion(sim, y, false));
							input.Player.Health = input.Opponent.Health = 30;
							input.Player.Tier = input.Opponent.Tier = 1;
							input.SetTurn(1);
							if(lobbyRaces.Count > 0)
								input.availableRaces = lobbyRaces;
							if(heroPower != null)
								input.Player.AddHeroPower(heroPower, true, false, 0, 0, 0, null, 0, false);
							var output = new SimulationRunner().SimulateMultiThreaded(input, 300, 1, 2000).GetAwaiter().GetResult();
							if(output?.simulationCount > 0)
							{
								win += output.winRate;
								loss += output.lossRate;
								n++;
							}
						}
						catch(Exception)
						{
							// неподдержанное сочетание — пропускаем
						}
					}
					if(n > 0)
						result.Add((x, win / n, loss / n));
				}
				_ranking = result.OrderByDescending(r => r.Item2 - r.Item3).ToList(); // ничья — урона нет никому
			});
		}

		/// <summary>Существо с базовыми статами карты; «в конце вашего хода получает +N» срабатывает до первого боя (Баюбот).</summary>
		private static BobsBuddy.Minion Minion(Simulator sim, string cardId, bool friendly)
		{
			var m = sim.MinionFactory.CreateFromCardId(cardId, friendly);
			if(HearthDb.Cards.All.TryGetValue(cardId, out var card))
			{
				int atk = card.Attack, hp = card.Health;
				var text = card.GetLocText(Locale.enUS) ?? "";
				var eot = Regex.Match(Regex.Replace(text, "<[^>]+>", ""), @"end of your turn,? gain \+(\d+)(?:/\+(\d+)| (Health|Attack))", RegexOptions.IgnoreCase);
				if(eot.Success)
				{
					if(eot.Groups[2].Success)
					{
						atk += int.Parse(eot.Groups[1].Value);
						hp += int.Parse(eot.Groups[2].Value);
					}
					else if(eot.Groups[3].Value == "Health")
						hp += int.Parse(eot.Groups[1].Value);
					else
						atk += int.Parse(eot.Groups[1].Value);
				}
				m.baseAttack = m.maxAttack = atk;
				m.baseHealth = m.maxHealth = hp;
			}
			return m;
		}

		private static string Name(string cardId) =>
			HearthDb.Cards.All.TryGetValue(cardId, out var c) ? c.GetLocName(L.CardLocale) ?? c.Name : cardId;
	}
}
