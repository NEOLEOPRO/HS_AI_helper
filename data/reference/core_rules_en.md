# Hearthstone Battlegrounds rules (short)

Draft for the LLM (English — fewer tokens than Russian). Exact combat mechanics live in the simulator (BobsBuddy); this is the big picture. "(logs)" = verified on our own games.

## Game
- 8 players (duos: 4 pairs), each hero has a unique hero power. The last survivor wins; placement = elimination order.
- A turn = tavern (shopping) phase + a combat against one opponent chosen by the game.
- A lobby has 5 of the 11 minion types; the tavern offers only those types plus neutral minions (logs).
- With an odd number of living players someone fights a "ghost" — the frozen board of an eliminated player (logs: hero Kel'Thuzad, 0 health).

## Tavern
- Gold is refilled every turn and grows with the round up to 10; unspent gold is lost.
- Buy a minion — 3 gold, sell — +1, refresh ("roll") — 1, freeze — free (offers stay for next turn).
- Tavern upgrades (tier 1→6, some modes 7) unlock stronger minions; the upgrade cost drops by 1 for every turn you wait.
  Gold spent on upgrades is not on the board: a player who upgraded early is weaker now and stronger later ("tempo vs economy").
- Standard opening (strong players): turn 1 — buy the best minion (freeze if a 2-gold spell is offered); turn 2 — level to tier 2
  almost always, even if the opponent did not (an early loss costs ~2 health, tier 2 pays off later); turn 3 — 5 gold (e.g. Dark Gift 3 + spell 2).
  Early ties deal no damage to anyone.
- Tavern offers per refresh (logs): tier 1 — 3 minions, tiers 2–3 — 4, tiers 4–5 — 5, tier 6 — 6.
- Minion copies are shared by the whole lobby (pool); a type collected by several players is rarer in the tavern.
- Three copies of the same minion merge into a golden one ("triplet": doubled stats/effect) and give a reward: discover a minion one tier higher.
- Max 7 minions on the board; hand limit 10 cards.
- Tavern spells, trinkets, hero quests and seasonal mechanics (Old God with a Deity, Dark Gifts, etc.) give permanent buffs.

## Combat
- Combat is automatic; the player only influences it through the board and its order before combat.
- The side with more minions attacks first; on equal counts — random. Minions attack in turn, left to right.
- The attack target is random among enemy minions; Taunt minions must be attacked first.
- Keywords: Divine Shield (absorbs the first hit), Reborn (returns with 1 health), Poisonous (kills any minion it damages; permanent),
  Venomous (same, but removed after the first hit), Windfury (attacks twice), Deathrattle (on death), Start of Combat, Avenge (after N friendly deaths), Rally.
- When effects happen: Battlecry — when the minion is played from hand during the player's turn in the tavern, never in combat
  (so "Battlecries trigger twice", e.g. Brann Bronzebeard, is a tavern effect — passive in combat);
  Deathrattle — when the minion dies or is destroyed (mostly in combat; "Deathrattles trigger an extra time", e.g. Titus Rivendare,
  matters in combat — kill it first); Start of Combat, Avenge, Rally, "whenever this attacks" — in combat.
- The side with minions left wins; if neither has any — a tie.
- Damage to the loser = winner's tavern tier + sum of the tiers of the winner's surviving minions.
- Damage cap per combat (logs): 5 early, then 10, then 15; removed when 4 players remain.
