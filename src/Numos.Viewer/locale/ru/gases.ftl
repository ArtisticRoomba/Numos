blank-gas-registry = Пустой регистр газов. Добавьте газ перед впрыскиванием.
add-gas-definition = Добавить определение газа
new-gas-name = Имя##new-gas
new-gas-molar-cv = Молярная теплоёмкость (J/(mol·K))##new-gas
new-gas-boiling-point = Точка кипения (K)##new-gas
new-gas-boiling-point-tooltip = Температура кипения при эталонном давлении насыщения в Атмос Конфигурации.
new-gas-condensation-enabled = Включить конденсацию##new-gas
new-gas-vaporization-enthalpy = Энтальпия испарения (J/mol)##new-gas
new-gas-liquid-id = ID Жидкости##new-gas
new-gas-liquid-id-tooltip = Зарезервированно для кастомной интеграции жидкостей; встроенный решатель игнорирует значение. -1 является заполнителем для отсутствия жидкости.
new-gas-diffusion-coefficient = Коэффициент диффузии##new-gas
new-gas-diffusion-coefficient-tooltip = Безразмерная доля мольного дисбаланса этого газа в смеси каждый тик, от 0 до 1. Решатель возвращает значения выше до 1.
new-gas-molar-mass = Молярная масса (kg/mol)##new-gas
new-gas-molar-mass-tooltip = Масса одного моля этого газа в килограммах. 0 означает неизвестную массу - в таком случае будет использоваться номинальная молярная масса.
add-gas = Добавить Газ
gas-id-column = ID
gas-name-column = Имя
gas-cv-column = Cv (J/(mol·K))
gas-boiling-column = Кипение (K)
remove = Удалить
gas-removal-tooltip = Удаление разрешено пока ID существующего газа, присутствующего внутри чанка, не будет сдвинуто.
gas-details = { $name }
    Конденсация: { $condensation }
    Энтальпия испарения: { $vaporization-enthalpy }
    ID Жидкости: { $liquid-id }
    Коэффициент диффузии: { $diffusion-coefficient }
    Молярная масса: { $molar-mass }
enabled = включено
disabled = выключено
use-selected-cell = Использовать выбранную клетку
injection-voxel-x = Воксель X##inject
injection-voxel-y = Воксель Y##inject
injection-voxel-z = Воксель Z##inject
move-camera-to-voxel = Переместить камеру к Вокселю
injection-gas = Газ##inject
no-gas-registered = Газ: нет зарегестрированных
injection-amount = Количество (моль)##inject
injection-temperature = температура (K)##inject
inject-gas = Впрыснуть газ##inject-action
ignition-volume = Объём (m³)
ignition-source-tooltip = Нагревает очень малый объём до высокой температуры на один тик, чтобы воспламенить газ с температурой ниже обычной температуры воспламенения.
ignition-positive-temperature-volume-required = Температура и объём воспламенения должны быть больше нуля.

message-project-add-gas = Добавлен газ { $name } с ID { $id }.
message-project-remove-gas = Удалён газ { $name }.
message-project-inject-gas-not-selected = Газ для впрыскивания не выбран.
message-project-inject-gas = Впрыснуто { $moles } молей { $name } в ({ $pos }).
message-project-inject-gas-failed = Не удалось впрыснуть газ: { $message }
message-project-ignite-source-success = Подожжён воксель ({ $pos }) чанка { $chunk } при { $temperature } K.

simulation-new-gas-name = Новый Газ
