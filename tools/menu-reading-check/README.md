# Проверка чтения навыков из журнала

Из корня проекта:

```powershell
dotnet run --project tools/menu-reading-check/MenuReadingCheck.csproj -c Release -- 'ПУТЬ_К_GAME\sqpack' 'artifacts/logs/menu-reading-game-check.json'
```

Проверяются пять строк из журнала 29 сентября 2026: Aspect Mastery III,
Maim and Mend II, Firestarter, Enhanced Swiftcast и Enhanced Addle.
Используются реальные таблицы игры, русские справочники и рабочий путь
`GameDescriptionService.FromActionMenuLabel`. Проверяются русский и английский
тексты интерфейса при английском и немецком языке игровых таблиц.

В плагине допустимые пассивные навыки берутся из `AgentActionMenu.TraitList`.
Здесь соответствующий список оккультиста/чёрного мага подставляется из таблиц:
это проверяет выбор профессии, но не чтение памяти запущенной игры.
Подставной обработчик строк сохраняет управляющие вставки; расчёт динамических
чисел в зависимости от персонажа этим инструментом не проверяется.

Также проверяются отключение и возврат перевода на четырёх языках клиента,
а с 6.08.86 — полнота выбранного рецепта. Проверка перебирает все рецепты
с результатом из установленной таблицы Recipe: полный список принимается,
обнуление каждого нужного материала или кристалла и другой результат отвергаются.
Для первых двадцати рецептов вызывается настоящее чтение материалов:
запас кристаллов получается по собственному ID, а подставные старые поля
окна не используются. Пустая ссылка кристалла -1 при нулевом количестве
учтена отдельно. Число рецептов, проверок и неизвестных схем записывается
в отчёт; это не чтение памяти живой игры.

Описание структур сверено с установленной FFXIVClientStructs и исходниками:
[AgentActionMenu](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentActionMenu.cs),
[AtkResNode](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Component/GUI/AtkResNode.cs),
[DetailKind](https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/Enums/DetailKind.cs).
