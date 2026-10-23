"""Стартовый набор столов для релиза: data/reference/seed_boards.jsonl.gz.

Без него у нового игрока нет своих записей — шансы до боя, сила стола и подозрительность пустые первые партии.
Берём записи боёв (свои и присланные игроками — data/community), оставляем только то, что нужно библиотеке столов плагина
(BoardLibrary.Add): ход, существа обеих сторон (поля для BobsBuddy) и числовые счётчики. Без времени, MMR, региона;
ключ партии — хэш (тот же, что считает плагин, чтобы не брать свои же записи дважды). Призраки и дуо — нет.

Запуск:
    py -I tools/build_seed_boards.py [папки с combats_*.jsonl ...]
"""
import glob
import gzip
import hashlib
import json
import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "data/reference/seed_boards.jsonl.gz"
MINION_FIELDS = ["CardID", "baseAttack", "baseHealth", "maxHealth", "taunt", "div", "cleave", "poisonous", "venomous", "windfury",
                 "megaWindfury", "stealth", "golden", "reborn", "tier", "ScriptDataNum1", "ScriptDataNum2", "ScriptDataNum3",
                 "ScriptDataNum4", "PrimaryRace", "SecondaryRace"]


def seed_key(key):
    """Как BoardLibrary.SeedKey в плагине: SHA-256 ключа боя, первые 16 hex."""
    return hashlib.sha256((key or "").encode("utf-8")).hexdigest()[:16]


def side(p):
    if not isinstance(p, dict) or not p.get("Side"):
        return None
    out = {k: v for k, v in p.items() if isinstance(v, (int, bool)) and not isinstance(v, float)}
    out["Side"] = [{f: m.get(f) for f in MINION_FIELDS if f in m} for m in p["Side"]]
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    folders = sys.argv[1:] or [os.path.join(os.environ["APPDATA"], "HSaiHelper", "recordings"), str(ROOT / "data/community")]
    seen, rows = set(), []
    for folder in folders:
        for f in sorted(glob.glob(os.path.join(folder, "**", "combats_*.jsonl"), recursive=True)):
            for line in open(f, encoding="utf-8"):
                if '"type":"combat"' not in line:
                    continue
                r = json.loads(line)
                if r.get("duos") or not isinstance(r.get("input"), dict) or not r.get("turn"):
                    continue
                key = r.get("key") or ""
                k = key if len(key) == 16 and "_" not in key else seed_key(key)  # присланные уже с хэшем
                if (k, r["turn"]) in seen:
                    continue
                seen.add((k, r["turn"]))
                inp = {s: side(r["input"].get(s)) for s in ("Player", "Opponent")}
                if inp["Opponent"] and r["input"]["Opponent"].get("HeroIsKelThuzad"):
                    inp["Opponent"] = None
                inp = {s: v for s, v in inp.items() if v}
                if inp:
                    rows.append({"type": "combat", "key": k, "turn": r["turn"], "input": inp})
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(OUT, "wt", encoding="utf-8") as g:
        for row in rows:
            g.write(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n")
    boards = sum(len(r["input"]) for r in rows)
    print(f"Боёв: {len(rows)}, столов: {boards} → {OUT} ({OUT.stat().st_size // 1024} КБ)")


if __name__ == "__main__":
    main()
