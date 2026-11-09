unhandled-exception = Необработанное исключение
unhandled-exception-message = Необработанное исключение: { $exception }
unobserved-task-exception = Исключение ненаблюдаемой задачи
draw-data-build-failed = Не удалось построить данные отрисовки
source-runtime = Среда выполнения
exception-message = { $context }: { $message }

open-messages-logs = Открыть Сообщения / Логи.

error-count =
    { $count ->
        [one] { $count } ошибка
        [few] { $count } ошибки
        [many] { $count } ошибок
       *[other] { $count } ошибки
    }
warning-count =
    { $count ->
        [one] { $count } предупреждение
        [few] { $count } предупреждения
        [many] { $count } предупреждений
       *[other] { $count } предупреждения
    }

filter-messages = Отфильтровать сообщения...
auto-scroll = Авто-скролл
copy = Копировать
clear = Очистить
messages-count-summary = { $shown } показано / { $total } всего

time = Время
tick = Тик
level = Уровень
source = Источник
message = Сообщение
icon-sheet-error = Не удалось загрузить лист значков { $path }: { $reason }. Значки скрыты.
icon-sheet-missing = файл не существует
icon-sheet-size = ожидалось не менее { $width } x { $height } пикселей, найдено { $actualWidth } x { $actualHeight }
