"""Ключевые карты — заметки игрока о картах, которые меняют ход боя (для «опасных карт сборки» соперника и для ИИ).

1) Таблица для заполнения (все существа пула BG, по типам и тирам): эффект карты по-русски (как в игре), в каких гайдах HDT
   она ключевая/дополнительная/упомянута в описании, фраза из описания гайда, автоматическая метка:
       py -I tools/key_cards.py make      → data/reference/key_cards.md
   Заполняются столбцы «Важность» (1–5: 5 — меняет исход боя, бить/брать первым) и «Заметка» (чем опасна, как играть против).
   Пустые строки пропускаются; при повторном make уже заполненное сохраняется.
2) Файл для плагина из заполненных строк:
       py -I tools/key_cards.py build     → data/reference/key_cards.json (кладётся рядом с плагином: install.ps1, package.ps1)
   Заметка игрока главнее автоматической метки (Analysis.DangerCards).
"""
import glob
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "tools"))
from parse_power_log import DEFAULT_CARDDEFS, load_card_names  # noqa: E402
from turn1_eval import carddefs  # noqa: E402

MD = ROOT / "data/reference/key_cards.md"
OUT = ROOT / "data/reference/key_cards.json"
RECORDINGS = Path(os.environ.get("APPDATA", "")) / "HSaiHelper" / "recordings"
HEADER = ["тир", "карта", "эффект", "в гайдах HDT", "из описания гайда", "авто", "id", "Важность", "Заметка"]
TRIBES_RU = {"BEAST": "Звери", "DEMON": "Демоны", "DRAGON": "Драконы", "ELEMENTAL": "Элементали", "MECHANICAL": "Механизмы", "MURLOC": "Мурлоки",
             "NAGA": "Наги", "PIRATE": "Пираты", "QUILBOAR": "Свинобразы", "UNDEAD": "Нежить", "ABERRATION": "Аберрации"}


def auto_label(text):
    """Как Analysis.Danger в плагине: что в тексте карты может перевернуть бой."""
    t = re.sub(r"<[^>]+>", "", text or "")
    # «Бить первым» — только удвоение того, что действует в бою; удвоение кличей (Бранн) — таверна, в бою пассивен.
    if re.search(r"trigger(s)? (an extra time|twice)", t, re.I) and re.search(r"Deathrattle|Avenge|Rally|Start of Combat|attack", t, re.I) \
            and not re.search(r"Battlecr", t, re.I):
        return "удваивает эффекты в бою — бить первым"
    if re.search(r"\b(Venomous|Poisonous)\b", t):
        return "токсичность/яд"
    if re.search(r"\bDestroy\b", t):
        return "уничтожает"
    if re.search(r"Divine Shield", t, re.I):
        return "щиты"
    if re.search(r"Start of Combat", t, re.I):
        return "в начале боя"
    return ""


def clean(text):
    """Текст карты без разметки: <b>, [x], $5 → 5, {0}/{1} → X/Y."""
    t = re.sub(r"<[^>]+>", "", text or "").replace("[x]", "").replace("\n", " ")
    t = re.sub(r"\$(\d+)", r"\1", t)
    t = re.sub(r"\{0\}", "X", t)
    t = re.sub(r"\{1\}", "Y", t)
    return re.sub(r"\s+", " ", t).strip()


def ru_texts():
    texts = {}
    path = DEFAULT_CARDDEFS / "CardDefs.ruRU.xml"
    if not path.exists():
        return texts
    for _, e in ET.iterparse(path):
        if e.tag != "Entity":
            continue
        for tag in e.findall("Tag"):
            if tag.get("name") == "CARDTEXT" and tag.find("ruRU") is not None:
                texts[e.get("CardID")] = clean(tag.find("ruRU").text)
        e.clear()
    return texts


def dbf_ids():
    ids = {}
    for _, e in ET.iterparse(DEFAULT_CARDDEFS / "CardDefs.base.xml"):
        if e.tag == "Entity" and e.get("ID"):
            ids[int(e.get("ID"))] = e.get("CardID")
            e.clear()
    return ids


def guides(dbf):
    """По карте: [(роль, гайд)] и фраза из описания гайда, где она упомянута (последний файл гайдов из записей плагина)."""
    files = sorted(glob.glob(str(RECORDINGS / "comp_guides_*.json")))
    roles, quotes = {}, {}
    if not files:
        return roles, quotes
    for c in json.load(open(files[-1], encoding="utf-8")).get("Comps", []):
        g = c.get("CompGuide") or {}
        name = g.get("Name")
        for role, key in (("ключевая", "CoreCards"), ("доп.", "AddonCards")):
            for d in g.get(key) or []:
                if d in dbf:
                    roles.setdefault(dbf[d], []).append(f"{role}: {name}")
        text = " ".join(x for x in (g.get("HowToPlay"), g.get("CommonEnablers")) if isinstance(x, str))
        for sentence in re.split(r"(?<=[.!?])\s+", text):
            for m in re.finditer(r"\|\|(\d+)\]\]", sentence):
                cid = dbf.get(int(m.group(1)))
                if cid:
                    plain = re.sub(r"\[\[([^|\]]*)\|\|\d+\]\]", r"\1", sentence).strip()
                    if f"в описании: {name}" not in roles.get(cid, []) and not any(r.endswith(name) for r in roles.get(cid, [])):
                        roles.setdefault(cid, []).append(f"в описании: {name}")
                    if cid not in quotes or len(plain) < len(quotes[cid]):
                        quotes[cid] = f"{name}: {plain}"
    return roles, quotes


def grouped(items):
    """«ключевая: A, B; доп.: C; в описании: D» вместо повтора роли у каждого гайда."""
    by_role = {}
    for item in items:
        role, name = item.split(": ", 1)
        by_role.setdefault(role, []).append(name)
    order = ["ключевая", "доп.", "в описании"]
    return "; ".join(f"{role}: {', '.join(by_role[role])}" for role in sorted(by_role, key=lambda r: order.index(r) if r in order else 9))


def cell(s):
    return (s or "").replace("|", "/").replace("\n", " ").strip()


def make():
    minions, _ = carddefs()
    names = load_card_names(DEFAULT_CARDDEFS)
    texts = ru_texts()
    roles, quotes = guides(dbf_ids())
    old = parse() if MD.exists() else {}
    by_tribe = {}
    for cid, m in minions.items():
        if cid.endswith("_G") or not m.get("tier"):
            continue
        tribe = ", ".join(TRIBES_RU.get(t, t) for t in sorted(m.get("subsets") or [])) or "Без типа"
        by_tribe.setdefault(tribe, []).append((m["tier"], cid, m))
    lines = ["# Ключевые карты — заметки игрока", "",
             "Заполняй «Важность» (1–5: 5 — меняет исход боя, бить/брать первым) и «Заметку» (чем опасна, как играть против) только у карт,",
             "которые меняют ход игры; остальное оставь пустым. «В гайдах HDT» и «из описания гайда» — подсказки из гайдов дектрекера.",
             "Потом: `py -I tools/key_cards.py build` — плагин подхватит заметки (или напиши в чат — перенесу).", ""]
    for tribe in sorted(by_tribe):
        lines += [f"## {tribe}", "", "| " + " | ".join(HEADER) + " |", "|" + "---|" * len(HEADER)]
        for tier, cid, m in sorted(by_tribe[tribe], key=lambda x: (x[0], (names.get(x[1]) or {}).get("ruRU") or x[2]["name"])):
            ru = (names.get(cid) or {}).get("ruRU") or m["name"]
            imp, note = old.get(cid, ("", ""))
            row = [str(tier), f"{ru} ({m['name']})", texts.get(cid) or clean(m.get("text")), grouped(roles.get(cid, [])),
                   quotes.get(cid, ""), auto_label(m.get("text")), cid, imp, note]
            lines.append("| " + " | ".join(cell(x) for x in row) + " |")
        lines.append("")
    MD.write_text("\n".join(lines), encoding="utf-8")
    n = sum(len(v) for v in by_tribe.values())
    print(f"Таблица: {MD} ({n} карт; в гайдах HDT: {sum(1 for v in by_tribe.values() for _, c, _ in v if c in roles)}; заполненное сохранено: {len(old)})")


def parse():
    """{id: (важность, заметка)} — столбцы по заголовку таблицы (старый формат без «Важности» тоже читается)."""
    notes, cols = {}, None
    for line in MD.read_text(encoding="utf-8").splitlines():
        if not line.startswith("|"):
            continue
        parts = [c.strip() for c in line.strip().strip("|").split("|")]
        if "id" in parts and "Заметка" in parts:
            cols = {name: i for i, name in enumerate(parts)}
            continue
        if not cols or not re.match(r"^[A-Z0-9_]+$", parts[cols["id"]] if len(parts) > cols["id"] else ""):
            continue
        imp = parts[cols["Важность"]] if "Важность" in cols and len(parts) > cols["Важность"] else ""
        note = parts[cols["Заметка"]] if len(parts) > cols["Заметка"] else ""
        if imp or note:
            notes[parts[cols["id"]]] = (imp, note)
    return notes


def build():
    notes = parse()
    data = {}
    for cid, (imp, note) in notes.items():
        entry = {"ru": note}
        if re.match(r"^[1-5]$", imp):
            entry["importance"] = int(imp)
        data[cid] = entry
    OUT.write_text(json.dumps(data, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"Заметок: {len(data)} → {OUT}")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    {"make": make, "build": build}.get(sys.argv[1] if len(sys.argv) > 1 else "", lambda: print(__doc__))()
