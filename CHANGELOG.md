# История изменений OwnDay

Здесь собраны пользовательские изменения по выпущенным версиям OwnDay. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

## [0.2.0] - 2026-11-21

### Добавлено

- В учебном диалоге появились кнопки для действий по текущему заданию. По-прежнему можно отвечать обычным текстом.
- В личном чате стал доступен режим тестировщика. После входа можно посмотреть свой учебный прогресс, сбросить данные для повторного первого запуска или запросить удаление своих данных из OwnDay. Удаление требует подтверждения кнопкой и отдельным сообщением.

### Исправлено

- Тестировщик может заново запустить вход или удаление данных после истечения предыдущего запроса.

### Added

- The learning chat now offers buttons for actions on the current exercise. You can still answer in plain text.
- You can sign in to Tester Mode in a private chat. Once signed in, you can check your learning progress, reset your data to repeat onboarding, or request that your data be deleted from OwnDay. Deletion requires a button confirmation and a separate message.

### Fixed

- Testers can start sign-in or data deletion again after the previous request expires.

## [0.1.0] - 2026-11-20

### Добавлено

- Учебный диалог в Telegram помогает уточнить цель и подобрать первый практический шаг. Можно проходить диагностику, вести несколько программ и возвращаться к сохранённому упражнению.
- Наставник предлагает объяснения, подсказки, уточнения и задания с вариантами ответа. Можно исправить ответ, разобрать оценку и посмотреть прогресс по навыкам.
- Можно попросить изменить цель или учебный маршрут, выбрать удобный способ объяснения и вернуться к материалу по предложению бота. Напоминания о занятиях требуют согласия.
- Команда `/version` показывает версию запущенного бота.

### Удалено

- В Telegram завершена поддержка Inbox и команд для управления задачами и записями. Старые команды открывают справку по учебному диалогу.

### Added

- The Telegram learning chat helps you clarify your goal and choose a first practical step. You can take a diagnostic, follow multiple programs, and return to a saved exercise.
- The tutor offers explanations, hints, clarifications, and multiple-choice exercises. You can revise an answer, review an assessment, and see your progress by skill.
- You can ask to revise your goal or learning path, choose how the tutor explains things, and revisit material when the bot suggests a review. Study reminders require your consent.
- The `/version` command shows the running bot version.

### Removed

- Inbox and the Telegram commands for managing tasks and records have been retired. The old commands open learning chat help.

[0.2.0]: https://github.com/ConstantinProg/own-day/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ConstantinProg/own-day/tree/v0.1.0
