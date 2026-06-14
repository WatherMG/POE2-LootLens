# POE2 LootLens — запуск, тесты и публикация

Требуется **.NET 8 SDK для Windows**. PowerShell-скрипты не обязательны: ниже приведены обычные команды `dotnet`, не требующие изменения Execution Policy.

## Восстановление и тесты

```powershell
dotnet restore .\POE2LootLens.sln

dotnet test .\src\POE2LootLens.Tests\POE2LootLens.Tests.csproj --configuration Release --logger "console;verbosity=normal"
```

Подробный вывод упавших тестов:

```powershell
dotnet test .\src\POE2LootLens.Tests\POE2LootLens.Tests.csproj --configuration Release --logger "console;verbosity=detailed"
```

## Запуск

Отладочная сборка с PDB и диагностической консолью:

```powershell
dotnet run --project .\src\POE2LootLens\POE2LootLens.csproj --configuration Debug -- --debug
```

Оптимизированная сборка с диагностической консолью:

```powershell
dotnet run --project .\src\POE2LootLens\POE2LootLens.csproj --configuration Release -- --debug
```

Обычный запуск:

```powershell
dotnet run --project .\src\POE2LootLens\POE2LootLens.csproj --configuration Release
```

## Журналы и runtime-файлы

При `Release`:

```text
src\POE2LootLens\bin\Release\net8.0-windows\
```

Основные файлы:

```text
logs\price-scan.log
logs\rumors.log
config.json
rumor_catalog.default.json
rumor_catalog.user.json
price_snapshot.json
```

Уровень `Debug` в общих настройках включает подробные OCR-строки и время обработки в файлах логов. Аргумент `--debug` дополнительно дублирует сообщения уровней `Error`–`Debug` в консоль.

## Одноразовая проверка OCR изображения

```powershell
dotnet run --project .\src\POE2LootLens\POE2LootLens.csproj --configuration Debug -- --ocr-test "C:\Screenshots\loot.png"
```

## Публикация Windows x64

```powershell
dotnet publish .\src\POE2LootLens\POE2LootLens.csproj --configuration Release --runtime win-x64 --self-contained true --output .\publish
```

Запуск опубликованной версии:

```powershell
& ".\publish\POE2 LootLens.exe"
```

Диагностический запуск:

```powershell
& ".\publish\POE2 LootLens.exe" --debug
```

## Visual Studio / Rider

Стартовый проект:

```text
src\POE2LootLens\POE2LootLens.csproj
```

Аргументы для диагностического запуска:

```text
--debug
```

Приложение допускает только один экземпляр. Перед повторным запуском из IDE завершите уже работающий процесс через диспетчер задач или:

```powershell
Get-Process -Name "POE2 LootLens" -ErrorAction SilentlyContinue | Stop-Process -Force
```
