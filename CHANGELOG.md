# История изменений OwnDay

Здесь собраны пользовательские изменения по выпущенным версиям OwnDay. Формат основан на [Keep a Changelog](https://keepachangelog.com/ru/1.1.0/).

## [0.5.0] - 2026-11-24

### Добавлено

- Экспорт тестировщика показывает подписи кнопок, язык интерфейса, бюджет пользователя и причину отмены.

### Изменено

- Медленная генерация урока получила больше времени. Планировщик может предложить проверяемый критерий цели.
- После верного ответа на узнавание для зачёта темы требуется более глубокая проверка.

### Added

- Tester exports include button labels, interface language, per-user budget and cancellation causes.

### Changed

- Slow lesson generation has more time to finish. The planner can propose a measurable goal criterion.
- A correct recognition answer now requires a deeper check before the topic is credited.

## [0.4.0] - 2026-11-23

### Добавлено

- В Telegram можно посмотреть настройки командой `/settings` и выбрать русский или английский через `/language`. Выбор сохраняется и не прерывает текущий урок.
- В режиме тестировщика появились журнал диагностических событий и выгрузки `/export`, `/export recent` и `/export last`. Они показывают текущее состояние и историю до 30 дней. Выгрузки доступны авторизованным тестировщикам в личном чате, а в истории возможны пробелы.

### Изменено

- Бот использует выбранный язык для новых ответов и кнопок, а также учитывает его при подготовке новых учебных материалов и обратной связи.

### Исправлено

- Сбои провайдера при диагностике знаний теперь отражаются как ошибки в журнале и выгрузках.

### Added

- Use `/settings` to view your Telegram settings and `/language` to choose Russian or English. Your choice is saved without interrupting your current lesson.
- Tester Mode adds a diagnostic event journal and the `/export`, `/export recent`, and `/export last` commands. Exports include the current state and up to 30 days of retained history. Authorized testers can request them in private chats, and the journal can contain gaps.

### Changed

- The bot uses your selected language for new replies and buttons and takes it into account when generating new learning material and feedback.

### Fixed

- Provider failures during skill diagnostics now appear as failures in the journal and exports.

## [0.3.1] - 2026-11-22

### Добавлено

- Команда `/changes` показывает историю изменений текущей версии.
- В уроках появляются объяснения новых понятий перед проверкой знаний. После правильного ответа бот может предложить следующее упражнение.

### Исправлено

- Улучшены обратная связь, повторные попытки и ограничения подсказок в заданиях с вариантами ответа.

### Added

- The `/changes` command shows the current version's release notes.
- Lessons explain new concepts before assessment and can offer a follow-up exercise after a correct answer.

### Fixed

- Improved feedback, retries, and hint limits in multiple-choice exercises.

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

[0.4.0]: https://github.com/ConstantinProg/own-day/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/ConstantinProg/own-day/compare/v0.2.0...v0.3.1
[0.2.0]: https://github.com/ConstantinProg/own-day/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/ConstantinProg/own-day/tree/v0.1.0
