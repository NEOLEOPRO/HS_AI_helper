"""Приём обезличенных выгрузок от игроков (плагин: «Настройки» → «Подготовить данные для проекта» → hsai_data_*.zip).

Каждый архив распаковывается в data/community/<export_id>/ (manifest.json + combats_*.jsonl + lobby_*.jsonl).
Повторно присланные данные одной установки отсекаются по (key, turn, type, seq-независимым полям): бои — по key (хэш id партии + раунд).
Проверяется, что в архиве нет точного времени и точного MMR (формат 1).

Запуск:
    py -I tools/import_exports.py <папка с zip или файлы zip>
"""
import json
import re
import sys
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "data" / "community"


def check(record):
    """Обезличено ли: нет time, rating — диапазон."""
    if "time" in record:
        return "есть точное время"
    for f in ("rating", "rating_after"):
        if isinstance(record.get(f), int):
            return "есть точный MMR"
    for f in ("context", "question", "answer", "reasoning", "text"):
        if isinstance(record.get(f), str) and re.search(r"MMR\s*\d{3,5}(?!\d|–)", record[f]):
            return "точный MMR в тексте"
    return None


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    paths = []
    for a in sys.argv[1:]:
        p = Path(a)
        paths += sorted(p.glob("hsai_data_*.zip")) if p.is_dir() else [p]
    OUT.mkdir(parents=True, exist_ok=True)
    seen_keys = set()
    for d in OUT.iterdir():
        for f in d.glob("combats_*.jsonl"):
            for line in open(f, encoding="utf-8"):
                r = json.loads(line)
                if r.get("type") == "combat":
                    seen_keys.add((r.get("key"), r.get("turn")))
    for z in paths:
        with zipfile.ZipFile(z) as zf:
            manifest = json.loads(zf.read("manifest.json"))
            target = OUT / manifest["export_id"]
            if target.exists():
                print(f"{z.name}: уже принят")
                continue
            target.mkdir()
            (target / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=1), encoding="utf-8")
            kept = dup = bad = 0
            for name in zf.namelist():
                if not name.endswith(".jsonl"):
                    continue
                lines = []
                for line in zf.read(name).decode("utf-8").splitlines():
                    r = json.loads(line)
                    if check(r):
                        bad += 1
                        continue
                    if r.get("type") == "combat":
                        k = (r.get("key"), r.get("turn"))
                        if k in seen_keys:
                            dup += 1
                            continue
                        seen_keys.add(k)
                    lines.append(line)
                    kept += 1
                (target / name).write_text("\n".join(lines) + ("\n" if lines else ""), encoding="utf-8")
            print(f"{z.name}: версия плагина {manifest.get('plugin_version')}, {manifest.get('dates')} — принято {kept}, повторов {dup}, не обезличено {bad}")


if __name__ == "__main__":
    main()
