viewer-description-line-one = Внешняя программа для просмотра Numos: независимая от движка,
viewer-description-line-two = псевдо-реалистичная, основанная на вокселях библиотека атмосферной симуляции.
viewer-description = Внешняя программа для просмотра Numos: независимая от движка, псевдо-реалистичная, основанная на вокселях библиотека атмосферной симуляции.
no-simulation-loaded = Симуляция сейчас не загружена.
create-workspace-instruction = "Выбрать Файл > Новая Симуляция чтобы создать рабочее пространство."

about = Подробнее
authors = Авторы
thanks-to = Особые благодарности

copyright = Авторские права
copyright-notice = © 2026 Numos contributors
license = Лицензия
build-provenance = Источник Сборки

viewer = Viewer
commit = Коммит
configuration = Конфигурация
framework = Фреймворк
sdk = SDK
repository = Репозиторий
copy-hash = Скопировать хэш

original-author-of-coresim = Original author of CoreSim
coresim-maintainer-viewer-author = CoreSim maintainer, Viewer author
numerical-methods-and-physics-guidance = Numerical methods and physics guidance
gas-reactions-solver = GasReactions solver

simulation-viewer-title = Numos Simulation Viewer

file = Файл
open-replay = Открыть Повтор...
save-replay = Сохранить Повтор...
exit = Выйти

view = Просмотр
solution = Решение
three-d-viewport = 3D Окно просмотра
two-d-slice-viewport = 2D Окно просмотра среза

settings = Настройки
help = Помощь

visualization = Визуализация
rendering-style = Стиль отрисовки
three-d-focus = 3D фокус
two-d-slice = 2D срез
show-slice-viewport = Показать окно просмотра среза

three-dimensional = 3D
two-dimensional = 2D

chunk-outlines = Обводка чанков
voxel-outlines = Обводка вокселей
voxel-outlines-tooltip = Каждая обводка отрисована на каждый воксель, так что большие сцены пропускают их. 3D вид обводит воксели только для сфокусированных чанков или сцены максимум для { $max-outlined-voxels } вокселей. 2D вид обводит клетки только когда они хотя бы { $min-outline-pixels } пикселя в ширину.

transparent-voxels = Прозрачные воксели
velocity-arrows = Стрелки скорости
velocity-arrows-tooltip = Рисует максимум { $max-arrows } стрелок. Большие сцены отрисовывают каждый N-ый воксель. Приблизьте 2D вид чтобы увидеть больше.

arrow-scale = Масштаб стрелок
velocity-arrow-scale-tooltip = Секунды перемещения которые показывает каждая стрелка. При 0.05 секунд воксель двигающийся при 20 м/с рисует стрелку длинной в один воксель. Стрелки длиннее чем { $max-length } ограничены и сохраняют только своё направление.

solver-pipeline = Последовательность Решателей
atmos-config = Атмос Конфигурация
configuration-apply-instructions = Values apply on Enter, when a field loses focus, or when a slider is released.
gas-registry = Регистр Газов

reset-configuration-success = Очищена каждая настройка Атмос Конфигурации до настроек по-умолчанию библиотеки.
reset-configuration-failure = Не удалось очистить настройки: { $reason }

config-value-minimum-error = {$field} должно быть 0 или более. Предыдущее значение всё ещё действует.
config-field-set-feedback = {$field} установлено на {$value}.
config-field-apply-error = Не удалось применить {$field}: {$error}

viewing = Просмотр
editing = Редактировать
simulation-inspection-status = {$mode} {$simulation}
chunks-section = Чанки
voxel-tools-section = Воксель Инструменты

viewport-tool-label = Инструменты Окна просмотра
select-box-tool = Выбрать / Коробка
paint-room-tool = Рисовать Комнату
paint-gas-tool = Рисовать Газ
erase-gas-tool = Стирать Газ
classification-label = Классификация
amount-mol-label = Количество (моль)
temperature-k-label = Температура (K)

selection-section = Выделение
no-voxels-selected = Воксели не выбраны.
selected-voxel-count-singular = {$count} воксель выбран
selected-voxel-count-plural = {$count} вокселей выбрано
clear-selection-button = Очистить Выделение

no-chunks-available = Нет доступных чанковю
axis-x-yz-plane = X (YZ плоскость)
axis-y-xz-plane = Y (XZ плоскость)
axis-z-xy-plane = Z (XY плоскость)
slice-plane-description = Глобальная воксель координата плоскости. Вид разрера показывает эту плоскость по каждому чанку. Прокрутите чтобы отдалить и передвигайте средней кнопкой мыши чтобы перемещать.
fit-plane-button = Плоскость прилегания
frame-focused-chunk-button = Выделение Целевого Чанка
showing-label = Показ
of-label = от
to-label = до
plane-label = Плоскость
slice-plane-cell-count = {$width} × {$height} клеток, {$chunks} чанков

minimum-label = Минимум
maximum-label = Максимум
custom-resolution-checkbox = Кастомное разрешение
default-resolution-levels = {$count} уровней (по умолчанию)
fixed-range-mode = Фиксированно
automatic-range-offset-mode = Автоматически ± {$offset}, рассчитывается каждый кадр
automatic-range-mode = Автоматический, рассчитывается каждый кадр
automatic-bounds-checkbox = Автоматические границы
offset-label = ± Смещение
automatic-bounds-offset-tooltip = Расширяет автоматический минимум и максимум на данное значение.
minimum-short-label = Мин
maximum-short-label = Макс

all-chunks = Все чанки
focus-chunk-id = Фокус##chunk-focus
frame-view = Вид Кадра
show-all-chunks = Показать Все Чанки
slice-cell-2d = 2D Разрез Клеток

chunk = Чанк
cell = Клетка
slice-uv = Разрез УФ
temperature = Температура
pressure = Давление
velocity = Вектор Скорости
speed = Модуль Скорости
total-amount = Общее количество
amount-excluding-invalid = { $amount }, кроме неверных значений
primary-gas = Основной газ
room = Комната

selected-voxel-chunk-missing = Выбранный воксель/чанк не существует на данной позиции таймлайна.
details-unavailable-revision = Детали недоступны для данной ревизии.

gas-makeup = Состав Газа
gas-count-singular = 1 газ
gas-count-plural = { $count } газов
no-valid-amounts = Нет валидных значений
vacuum = Вакуум

share = Поделиться
invalid-not-in-bar = неверный, нет в диаграмме

room-solid = Твёрдый ({ $id })
room-void = Пустота ({ $id })
room-unassigned = Не назначен ({ $id })

cell-index = индекс { $index }
none = нет
gas-name-id = { $name } ({ $id })
gas-id = Газ { $id }

simulation-state = Состояние Симуляции
no-chunks-in-simulation = Нет чанков в данной симуляции.
waiting-for-chunk-snapshot = Ожидание следующего снимка чанка.
dimensions = Размеры
voxel-dimensions = { $x } × { $y } × { $z } вокселей
total-voxels = Всего вокселей
active-voxels = Активные воксели
active-gases = Активные газы
simulation-state-label = Состояние
sleep-timer = Таймер сна
max-pressure = Максимальное давление
average-pressure-non-zero-voxels = Среднее давление (не нулевые воксели)
not-captured-current-visualization = Не снято для текущей визуализации
min-temperature-non-zero-voxels = Минимальная температура (не нулевые воксели)
max-temperature = Максимальная температура
flow = Поток
