# HSaiHelper

**Бесплатный помощник для Hearthstone Battlegrounds** — плагин к [Hearthstone Deck Tracker](https://hsdecktracker.net)
с локальным ИИ. Всё работает на вашем компьютере, только чтение: ничего не нажимает за вас и не трогает игру.

*Free Hearthstone Battlegrounds assistant — a Hearthstone Deck Tracker plugin with a local AI. Runs on your PC, read-only.
English below.*

## Скачать и установить
1. Откройте **[Releases](../../releases)** → скачайте `HSaiHelper-<версия>.zip` → распакуйте.
2. Двойной щелчок по **`Install.cmd`**.
3. Перезапустите HDT → Options → Tracker → Plugins → включите **HSAI Recorder** и **HSAI Lobby**.
4. *(По желанию)* ИИ-помощник: двойной щелчок по **`Setup-AI.cmd`** (скачает бесплатную модель, 5–17 ГБ).

Подробная инструкция, что где на экране и что делать, если не работает: [plugins/release/README.md](plugins/release/README.md)
(тот же файл лежит в архиве релиза).

## Что умеет
- **Лобби** в порядке мест: тир, здоровье, состав, серия, шанс топ-4, динамика состава и вероятные сборки.
- **Следующий соперник**: шансы до боя (симулятор BobsBuddy, как в HDT, против похожих столов из ваших записей), прошлые бои,
  подозрительность его стола, соперник через ход.
- **Оценки**: шанс места и риск вылета, сила стола против столов этого хода, какую сборку проще добрать и когда её менять,
  пул карт (шанс найти за ролл), тёмный дар, подсказки на первые ходы, разбор партии.
- **ИИ** (llama.cpp + открытая модель Gemma 4): ответит на «что купить?», «повышать?», видя стол, руку, таверну и все оценки.

## Помочь проекту данными
Плагин записывает ваши партии **только у вас** (`%APPDATA%\HSaiHelper`). Чтобы поделиться: «Настройки» →
**«Подготовить данные для проекта»** → архив без ников, точного времени и точного MMR. Откройте
[новый Issue → «Данные партий»](../../issues/new/choose) и перетащите архив. Из таких архивов подсказки и ИИ становятся точнее.
Приём архивов у автора: `py -I tools/import_exports.py <папка с архивами>`.

## Сборка из исходников
Нужны .NET SDK 8+ и установленный HDT (библиотеки берутся из `%LOCALAPPDATA%\HearthstoneDeckTracker\current`).
```
dotnet build plugins/HSaiHelper.Hdt -c Release
powershell -ExecutionPolicy Bypass -File plugins/package.ps1   # → dist/HSaiHelper-<версия>.zip для релиза
```
| Путь | Что |
|---|---|
| `plugins/HSaiHelper.Hdt/` | исходники плагина (C#, net472, WPF-оверлей HDT) |
| `plugins/release/` | то, что кладётся в архив релиза: установщики и инструкция |
| `plugins/package.ps1` | сборка архива релиза |
| `data/reference/core_rules_en.md` | правила игры для ИИ |
| `tools/import_exports.py` | приём обезличенных архивов от игроков |

## Лицензия и автор
[MIT](LICENSE) — открытый код: можно свободно использовать, менять и распространять. Автор — [NEOLEOPRO](https://github.com/NEOLEOPRO);
код написан в основном с помощью ИИ (Claude).

## Правила проекта
- Не автоматизировать игру и не нарушать правила Blizzard: память игры — только чтение и только библиотеками, которые использует HDT.
- Только бесплатное и локальное: никаких платных API и облаков.

---

## English
**Install:** [Releases](../../releases) → download and extract `HSaiHelper-<version>.zip` → double-click `Install.cmd` → restart HDT →
Options → Tracker → Plugins → enable **HSAI Recorder** and **HSAI Lobby**. Optional local AI: double-click `Setup-AI.cmd`.
Full guide: [plugins/release/README.md](plugins/release/README.md).

**Features:** lobby in placement order; next opponent with pre-combat odds (BobsBuddy vs similar recorded boards), past fights,
suspicion of their board and the turn-after opponent; estimates (placement, elimination risk, board vs this turn's boards,
easiest build and when to switch, pool odds, Dark Gift, opening tips, post-game review); a local AI (llama.cpp + Gemma 4).

**Share data:** Settings → *Prepare data for the project* → an anonymized archive (no names, exact times or exact MMR) — open a
["Game data" issue](../../issues/new/choose) and drop it there.

**Build:** .NET SDK 8+ and HDT installed; `dotnet build plugins/HSaiHelper.Hdt -c Release`, `plugins/package.ps1` makes the release zip.

**License:** [MIT](LICENSE). Author: [NEOLEOPRO](https://github.com/NEOLEOPRO); the code was written mostly with AI assistance (Claude).
