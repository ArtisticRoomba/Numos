# Workspace shell: toolbar, dock panes, start page, and layout reset.

slice-viewport-title = Срез симуляции 2D
viewport-3d-title = { $name } 3D
viewport-3d-title-active = { $name } 3D (Активная)
start-page-title = Начало
pane-no-project = Проект не открыт.
pane-no-simulation = Нет активной симуляции.

toolbar-new-tooltip = Новая симуляция
toolbar-open-tooltip = Открыть файл повтора
toolbar-save-tooltip = Сохранить повтор в файл
toolbar-tick = ТИК
toolbar-no-value = -
toolbar-messages-tooltip = Показать или скрыть Сообщения / Логи
toolbar-realtime = СКОРОСТЬ
toolbar-realtime-tooltip = Секунды симуляции за секунду реального времени. Ниже 1x тики идут дольше реального времени, и симуляция отстаёт.
toolbar-busy-advance = Шаг
toolbar-busy-seek = Перемотка
toolbar-busy-load-replay = Загрузка повтора
toolbar-busy-branch = Смена ветви
toolbar-busy-other = Выполняется

layout-reset-menu = Сбросить макет...
layout-reset-tooltip = Возвращает каждую панель на её место по умолчанию.
layout-reset-title = Сброс макета
layout-reset-confirmation = Каждая панель вернётся на своё место по умолчанию, а закрытые панели откроются снова. Текущая расстановка будет потеряна.
layout-reset-confirm = Сбросить макет

# Simulation tree and Properties.

tree-pane-title = Дерево симуляции
properties-pane-title = Свойства

tree-kind-project = Проект
tree-kind-world = Мир
tree-kind-simulation = Симуляция
tree-kind-chunk = Чанк
tree-kind-topology = Топология
tree-kind-portal = Портал
tree-kind-dock = Стык
tree-kind-link-set = Набор связей
tree-kind-solvers = Решатели
tree-kind-solver-stage = Этап решателя
tree-kind-gases = Газы
tree-kind-gas = Газ
tree-kind-atmos-configuration = Атмос конфигурация
tree-kind-timeline = Таймлайн
tree-kind-branch = Ветвь
tree-kind-checkpoint = Контрольная точка
tree-kind-visualization = Визуализация
tree-kind-views-3d = 3D виды
tree-kind-slice-2d = 2D срез

tree-chunk = Чанк { $position }
tree-portal = Портал { $number }
tree-dock = Стык { $number }
tree-link-set = Набор связей { $number }
tree-checkpoint = Тик { $tick }
tree-active-marker = активная
tree-current-marker = текущая

tree-menu-close-project = Закрыть проект...
tree-menu-add-simulation = Добавить симуляцию...
tree-menu-add-chunk = Добавить чанк...
tree-menu-add-portal = Добавить портал...
tree-menu-add-dock = Добавить стык...
tree-menu-add-stage = Добавить этап...
tree-menu-enable-stage = Включить этап
tree-menu-disable-stage = Выключить этап
tree-menu-add-gas = Добавить газ...
tree-menu-remove-gas = Удалить газ
tree-menu-no-actions = Нет действий

properties-none-selected = Выберите узел в дереве симуляции, чтобы увидеть его свойства.
properties-missing-at-tick = Этого элемента нет на тике { $tick }. Он появится снова на тике, где он есть.
properties-name = Имя
properties-state = Состояние
properties-tick = Тик
properties-tick-rate = Частота тиков
properties-start = Начальный тик
properties-head = Последний тик
properties-fork = Тик ответвления
properties-simulations = Симуляции
properties-simulation = Симуляция
properties-chunks = Чанки
properties-chunk-size = Размер чанка
properties-active = Активная
properties-position = Позиция
properties-status = Статус
properties-gases = Газы
properties-active-links = Активные связи
properties-topology-version = Версия топологии
properties-portals = Порталы
properties-docks = Стыки
properties-type = Тип
properties-built-in = Встроенный
properties-enabled = Включён
properties-interval-ticks = Интервал (тики)
properties-gas-id = ID газа
properties-molar-cv = Молярная Cv
properties-boiling-point = Точка кипения
properties-condensation = Конденсация
properties-vaporization-enthalpy = Энтальпия испарения
properties-liquid-id = ID жидкости
properties-diffusion = Коэффициент диффузии
properties-molar-mass = Молярная масса
properties-branch = Ветвь
properties-current-branch = Текущая ветвь
properties-operation-sequence = Номер операции
properties-yes = Да
properties-no = Нет
properties-switch-branch = Перейти на ветвь
properties-switch-branch-tooltip = Переводит таймлайн на эту ветвь. После перехода мир показывает состояние ветви.
properties-seek-checkpoint = Перейти к контрольной точке
properties-seek-checkpoint-tooltip = Переводит таймлайн к этой контрольной точке. Мир доступен только для чтения, пока таймлайн не вернётся к последнему тику.

legend-pane-title = Легенда
ignite-tool = Поджечь
properties-stepped-to = Шаг до тика
properties-active-simulation = Активная симуляция
properties-awake-chunks = Активные чанки
properties-chunk-state = Состояние чанка
properties-selected-voxels = Выбранные воксели
properties-inject-by-coordinates = Впрыск по координатам
