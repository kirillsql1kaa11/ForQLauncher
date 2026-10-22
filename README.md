# ForQ Launcher

Лаунчер Minecraft для Windows с офлайн-входом и установкой контента с Modrinth. Запускается без установки, .NET уже внутри.

[**Скачать последнюю версию**](https://github.com/kirillsql1kaa11/ForQLauncher/releases/latest) · [Все релизы](https://github.com/kirillsql1kaa11/ForQLauncher/releases) · [Сообщить о проблеме](https://github.com/kirillsql1kaa11/ForQLauncher/issues)

![Главное окно ForQ Launcher](docs/screenshots/main.png)

## Что нового

- **1.5.0:** убрал настройку GitHub-токена — обновления проверяются без авторизации.
- **1.4.0:** добавил поиск по установленному, проверку конфликтов и открытие карточки по клику.

## Возможности

- Профили Minecraft с разными версиями и загрузчиками.
- Версии из официального манифеста Mojang; 1.20.1, 1.16.5 и 1.12.2 проверены с модами.
- Поддержка Fabric, Quilt и NeoForge.
- Офлайн-вход по локальному нику, без аккаунта Microsoft.
- Поиск и установка модов, ресурспаков, шейдеров и модпаков `.mrpack` с Modrinth.
- Отметки «Установлено», удаление и отключение контента.
- Проверка обновлений установленных проектов.
- Проверка дубликатов и несовместимостей модов.
- Автоматическая загрузка Java, библиотек и ресурсов игры.
- Настройки памяти, разрешения экрана и количества потоков загрузки.
- Автообновление лаунчера без GitHub-токена.

## Скриншоты

| Карточка проекта | Проверка конфликтов |
|:---:|:---:|
| [<img src="docs/screenshots/details.png" width="360" alt="Карточка проекта Sodium">](docs/screenshots/details.png) | [<img src="docs/screenshots/conflicts.png" width="460" alt="Проверка конфликтов модов">](docs/screenshots/conflicts.png) |

## Быстрый старт

1. Скачайте файл `ForQLauncher-*-win-x64.exe` со страницы [последнего релиза](https://github.com/kirillsql1kaa11/ForQLauncher/releases/latest).
2. Запустите его. Установка не требуется. Если Windows покажет SmartScreen, нажмите «Подробнее» → «Выполнить в любом случае».
3. Создайте профиль: укажите название, версию Minecraft и загрузчик.
4. Нажмите «Установить игру», дождитесь загрузки и запускайте.

## Требования

- Windows 10 или Windows 11, 64-бит.
- Интернет для загрузки игры и контента с Modrinth.
- Около 2 ГБ свободного места на один профиль с модами.
- .NET и Java отдельно устанавливать не нужно: лаунчер скачает Java автоматически.

## Где хранятся данные

Профили, настройки, Java, библиотеки, ресурсы и установленный контент находятся в:

```text
%APPDATA%\ForQLauncher
```

Чтобы удалить лаунчер вместе с профилями, закройте его и удалите эту папку.

## Сборка из исходников

Понадобится [.NET SDK 8.0](https://dotnet.microsoft.com/download) или новее.

```powershell
git clone https://github.com/kirillsql1kaa11/ForQLauncher.git
cd ForQLauncher
dotnet publish src\ForQ.App\ForQ.App.csproj -c Release -r win-x64 --self-contained true -o publish
```

Готовый файл появится в `publish\ForQ.App.exe`.

Структура проекта:

- `src/ForQ.App` — интерфейс на Avalonia.
- `src/ForQ.Core` — работа с Mojang и Modrinth, установка, обновления и проверка конфликтов.

## Обратная связь

Нашли ошибку или хотите предложить улучшение — создайте [issue](https://github.com/kirillsql1kaa11/ForQLauncher/issues). При ошибке приложите версию лаунчера, текст сообщения и шаги, которые к ней привели.

---

ForQ Launcher — неофициальный проект. Не связан с Mojang Studios или Microsoft. Minecraft является товарным знаком Mojang Studios.
