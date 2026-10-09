<div align="center">

<img src="docs/favicon.ico" width="72" alt="">

# LoginChanger

**Права в SQL Server — одной матрицей.**<br>
Галочки вместо T-SQL: строки — представления, столбцы — пользователи.

[![Скачать](https://img.shields.io/github/v/release/MerdanOchanov/LoginChanger?label=%D1%81%D0%BA%D0%B0%D1%87%D0%B0%D1%82%D1%8C&color=2563eb)](https://github.com/MerdanOchanov/LoginChanger/releases/latest/download/LoginChanger.exe)
[![Build](https://github.com/MerdanOchanov/LoginChanger/actions/workflows/build.yml/badge.svg)](https://github.com/MerdanOchanov/LoginChanger/actions/workflows/build.yml)
![Platform](https://img.shields.io/badge/Windows-7%20%E2%86%92%2011%20%C2%B7%20Server%202019-0078d4)
![SQL Server](https://img.shields.io/badge/SQL%20Server-2012%E2%80%932022-cc2927)
![Size](https://img.shields.io/badge/%D1%80%D0%B0%D0%B7%D0%BC%D0%B5%D1%80-110%20%D0%9A%D0%91-16a34a)
[![License: MIT](https://img.shields.io/badge/license-MIT-lightgrey)](LICENSE)

[**⬇ Скачать**](https://github.com/MerdanOchanov/LoginChanger/releases/latest/download/LoginChanger.exe) ·
[**🌐 Сайт**](https://merdanochanov.github.io/LoginChanger/) ·
[**📖 Инструкция**](https://merdanochanov.github.io/LoginChanger/manual.html)

<img src="docs/img/matrix_clean.png" alt="Матрица прав LoginChanger" width="900">

</div>

## Зачем это нужно

В SSMS выдать доступ к десятку представлений пятерым пользователям — это полсотни кликов или скрипт, который потом нужно проверять. **LoginChanger показывает всю картину доступа в одной таблице**: отметили галочки → «Применить» → готово. Ошиблись — «Откатить последнее».

- 🗂 **Одна матрица** — кто что видит и выполняет, с цветовой подсветкой
- ☑ **Массовая расстановка** — «Все» по строке и столбцу, диапазон по <kbd>Shift</kbd>, выбор ячеек по <kbd>Ctrl</kbd>
- 👤 **Быстрые пользователи** — создание по шаблону, пароль генерируется (8 знаков, легко вводить)
- 📋 **Массовое создание** — вставьте список логинов, получите «логин — пароль — результат» и CSV
- ↺ **Откат** последнего применения; всё применяется **одной транзакцией**
- 🛟 **«Сироты» и логины без базы** — выявляются и чинятся (в т. ч. когда учётной записи Windows уже нет)
- 🔎 **Фильтры** — поиск словами, `-исключение`, схема, режимы, выпадающие списки с галочками
- 🛡 **Защита** — `sa`, `dbo`, sysadmin и db_owner не показываются и не меняются
- 🌗 Светлая/тёмная тема, адаптивный интерфейс, журнал действий, экспорт в CSV (Excel)

## Быстрый старт

1. Скачайте [`LoginChanger.exe`](https://github.com/MerdanOchanov/LoginChanger/releases/latest/download/LoginChanger.exe) — **установка не нужна**.
2. Запустите, укажите сервер, войдите через Windows или `sa`.
3. Выберите базу (список появляется после успешного входа) — и расставляйте галочки.

**Требования:** Windows 7 SP1 / 10 / 11 / Server 2019 с .NET Framework 4.0+ (в Windows 10+ уже есть) и вход администратора SQL Server. Дополнительные драйверы не нужны. Проверено на SQL Server 2019; запросы используют системные каталоги, доступные с версии 2012.

## Скриншоты

| Пользователи | Тёмная тема |
|---|---|
| <img src="docs/img/users_clean.png" alt="Пользователи"> | <img src="docs/img/dark.png" alt="Тёмная тема"> |
| **Массовое создание** | **Создание по шаблону** |
| <img src="docs/img/bulk2_clean.png" alt="Массовое создание"> | <img src="docs/img/create_clean.png" alt="Создание пользователя"> |

Полная инструкция с аннотированными скриншотами — на [странице руководства](https://merdanochanov.github.io/LoginChanger/manual.html).

## Безопасность

- Применение и откат — **одна транзакция**: любая ошибка = ничего не изменилось.
- Пароли подключения **не сохраняются**; в журнале пароли заменены на `***`.
- Программа ничего не отправляет в интернет; рядом с exe создаются только `logs\`, `templates\` и `loginchanger.ini`.

## Сборка из исходников

Компилятор уже есть в Windows (входит в .NET Framework) — ничего устанавливать не нужно:

```bat
build.bat
```

Получится `LoginChanger.exe`. Исходник — один файл [`src/Program.cs`](src/Program.cs) (C# 5, WinForms, `System.Data.SqlClient`).

## Дорожная карта

- [x] SQL Server 2012–2022
- [ ] PostgreSQL
- [ ] MySQL / MariaDB
- [ ] Firebird
- [ ] Права на столбцы и схемы

Нужна другая СУБД или функция? [Создайте обсуждение](https://github.com/MerdanOchanov/LoginChanger/issues) — идеи приветствуются. Если проект полезен, поставьте ⭐ — так его найдут другие администраторы.

## Вклад в проект

Pull request'ы и баг-репорты приветствуются: опишите версию SQL Server и Windows, шаги и текст ошибки (журнал в папке `logs`).

## Лицензия

[MIT](LICENSE)

---

## English

**LoginChanger** is a portable (≈110 KB, single `.exe`, no installer, no extra drivers) Windows tool for managing **SQL Server users and permissions through a checkbox matrix**: rows are views / procedures / tables, columns are users. It supports bulk user creation with generated passwords, templates, one-click undo, orphaned-user repair (even when the Windows account no longer exists), filters, CSV export and light/dark themes. All changes are applied in a single transaction; `sa`, `dbo`, sysadmins and db_owners are never touched.

**Download:** [LoginChanger.exe](https://github.com/MerdanOchanov/LoginChanger/releases/latest/download/LoginChanger.exe) · **Requirements:** Windows 7 SP1–11 / Server 2019 with .NET Framework 4.0+, SQL Server 2012–2022 admin login. **Build:** run `build.bat`. **License:** MIT. PostgreSQL, MySQL and Firebird support is on the roadmap.
