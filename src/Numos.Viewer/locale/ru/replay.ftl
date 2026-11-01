timeline-window = Таймлайн
timeline-branches = Ветки
timeline-transport = Транспорт
timeline-selected-tick = Выбранный тик
timeline-visible-ticks = Видимые тики
timeline-keep-playhead-centered = Центрировать индикатор
timeline-keep-playhead-centered-tooltip = Двигает промежуток видимых тиков пока изменяется позиция выбранного таймлайна.
timeline-first-visible-tick = Первый видимый тик
timeline-simulate-while-scrubbing = Симулировать при очистке
timeline-simulate-while-scrubbing-tooltip-active = Симуляция обновляется когда скруббер проходит через тик.
timeline-simulate-while-scrubbing-tooltip-inactive = Симуляция обновляется когда скруббер освобождается.
timeline-show-branch-history = Показать историю веток
timeline-show-branch-history-tooltip = Показывают каждую ветку сессии в выровненной исторической линии. Детали операций и контрольных точек остаются на выбрнной ветви.
timeline-branch = ВЕТКА
timeline-fork-tick = Отделённый тик { $tick }
timeline-start = НАЧАЛО
timeline-head = ОСНОВА
timeline-selected = ВЫБРАНО
timeline-mode = РЕЖИМ
timeline-read-only = Только для чтения
timeline-live-recording = Живая запись
timeline-selected-branch = Выбранная ветвь
timeline-head-tick = Основной тик { $tick }
timeline-branch-from-tick = Ответвление от тика
timeline-branch-from-tick-tooltip = Сохраняет эту ветвь и начинает новую ветвь прежде чем операции записаны в выбранном тике.
timeline-continue-branch = Продолжить ветвь
timeline-continue-branch-tooltip = Продолжает живую запись на основе текущей ветви.
timeline-tick = Тик { $tick }
timeline-through-operation = Через операцию #{ $sequence }
timeline-start-button = Начало
timeline-back-button = Назад
timeline-play-button = Продолжить
timeline-forward-button = Вперёд
timeline-return-to-head-button = Вернуться к Основе
timeline-checkpoint = КОНТРОЛЬНАЯ ТОЧКА
timeline-re-simulated = ПОВТОРНАЯ СИМУЛЯЦИЯ
timeline-verified = Подтверждено
timeline-replay-matches-reference-hash = Повтор совпадает со справочным хешом.
timeline-divergent = Отклоняющийся
timeline-replay-does-not-match-reference-hash = Повтор не совпадает со справочным хешом.
timeline-unverified = Не подтверждено
timeline-no-reference-hash-available = Справочный хеш отсутствует.
timeline-verification = ВЕРИФИКАЦИЯ
timeline-done-in-tick = Сделано в тике { $tick } ({ $count })
timeline-no-external-operations-recorded = Никакие внешние операции не были записаны в этом тике.
timeline-selected-operation = Выбранная операция
timeline-operation = ОПЕРАЦИЯ
timeline-done-in-tick-label = СДЕЛАНО В ТИК
timeline-order = ПОРЯДОК
timeline-inspect-after-operation = Осмотреть После Операции
timeline-recorded-mixture-empty = Записанная смесь пустая.
timeline-recorded-amount = Записано { $amount } моль
timeline-set-solver-layout = Установить Схему Решателей ({ $count } этапов)
timeline-initial-solver-stages = Изначальные ступени решателя
timeline-built-in = Встроенные
timeline-requested-solver-layout = Запрошенная схема решателей
timeline-error-create-branch = Не удалось создать ветвь повтора
timeline-error-select-branch = Не удалось выбрать ветвь повтора
timeline-error-continue-branch = Не удалось продолжить ветвь повтора
timeline-error-seek-tick = Не удалось воспроизвести повтор до тика { $tick }
timeline-error-seek-position = Не удалось воспроизвести повтор
timeline-error-return-to-head = Не получилось вернуть повтор к основе
replay-stage-table-column-index = #
replay-stage-table-column-stage = Этап
replay-stage-table-column-kind = Вид
replay-stage-table-column-enabled = Включено

replay-applied-configuration = Применённая конфигурация
replay-configuration-column-setting = Настройки
replay-configuration-column-value = Числа
replay-configuration-column-unit = Единица

replay-gas-registry = Регистр газов
replay-no-gases-registered = Нет зарегистрированных газов.
replay-gas-column-id = ID
replay-gas-column-name = Имя
replay-gas-column-heat-capacity = Cv (J/(mol·K))
replay-gas-column-diffusion = Диффуззия
replay-gas-column-boiling-point = Точка кипения (K)
replay-gas-column-condensation = Конденсация
replay-gas-column-latent-heat = Теплота затухания (J/mol)
replay-gas-column-liquid-id = ID Жидкости

replay-solver-configurations = Конфигурации Решателей
replay-solver-configuration = Конфигурация Решателя

replay-timeline-axis-title = ТИК / ВРЕМЯ СИМУЛЯЦИИ
replay-restorable-tick-range-tooltip = Тики от { $startTick } до { $endTick } имеют записанные состояния, которые могут быть восстановлены.
replay-single-operation-tooltip = { $code }
    Сделано в тик { $tick }
    Порядок 1
replay-multiple-operations-tooltip = { $count } операций в тике { $tick }
replay-checkpoint-tooltip = Контрольная точка на тике { $tick }, после операции #{ $operationSequence }
    Справочный хэш { $digest }
replay-divergent-checkpoint-tooltip = Отклоняющийся: воспроизведённое состояние не соответсвует справочному хэшу.

replay-session-branches-title = ВЕТВИ СЕССИИ
replay-branch-history-tooltip = { $name }
    Отделённый тик { $forkTick }
    Основной тик { $headTick }
replay-selected-branch-tooltip = Выбранная ветвь
replay-inspect-branch-tooltip = Выберите чтобы осмотреть данную ветвь