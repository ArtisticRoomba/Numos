# Workspace shell: toolbar, dock panes, start page, and layout reset.

slice-viewport-title = Simulation Slice 2D
viewport-3d-title = { $name } 3D
viewport-3d-title-active = { $name } 3D (Active)
start-page-title = Start
pane-no-project = No project is open.
pane-no-simulation = No simulation is active.

toolbar-new-tooltip = New simulation
toolbar-open-tooltip = Open a replay file
toolbar-save-tooltip = Save the replay to a file
toolbar-tick = TICK
toolbar-no-value = -
toolbar-messages-tooltip = Show or hide Messages / Logs
toolbar-realtime = SPEED
toolbar-realtime-tooltip = Simulated seconds per wall-clock second. Below 1x, ticks take longer than real time and the simulation falls behind.
toolbar-busy-advance = Stepping
toolbar-busy-seek = Seeking
toolbar-busy-load-replay = Loading replay
toolbar-busy-branch = Switching branch
toolbar-busy-other = Working

layout-reset-menu = Reset Layout...
layout-reset-tooltip = Returns every panel to its default dock position.
layout-reset-title = Reset layout
layout-reset-confirmation = Every panel returns to its default dock position, and closed panels open again. The current arrangement is lost.
layout-reset-confirm = Reset Layout

# Simulation tree and Properties.

tree-pane-title = Simulation Tree
properties-pane-title = Properties

tree-kind-project = Project
tree-kind-world = World
tree-kind-simulation = Simulation
tree-kind-chunk = Chunk
tree-kind-topology = Topology
tree-kind-portal = Portal
tree-kind-dock = Dock
tree-kind-link-set = Link set
tree-kind-solvers = Solvers
tree-kind-solver-stage = Solver stage
tree-kind-gases = Gases
tree-kind-gas = Gas
tree-kind-atmos-configuration = Atmos configuration
tree-kind-timeline = Timeline
tree-kind-branch = Branch
tree-kind-checkpoint = Checkpoint
tree-kind-visualization = Visualization
tree-kind-views-3d = 3D views
tree-kind-slice-2d = 2D slice

tree-chunk = Chunk { $position }
tree-portal = Portal { $number }
tree-dock = Dock { $number }
tree-link-set = Link set { $number }
tree-checkpoint = Tick { $tick }
tree-active-marker = active
tree-current-marker = current

tree-menu-close-project = Close Project...
tree-menu-add-simulation = Add Simulation...
tree-menu-add-chunk = Add Chunk...
tree-menu-add-portal = Add Portal...
tree-menu-add-dock = Add Dock...
tree-menu-add-stage = Add Stage...
tree-menu-enable-stage = Enable Stage
tree-menu-disable-stage = Disable Stage
tree-menu-add-gas = Add Gas...
tree-menu-remove-gas = Remove Gas
tree-menu-no-actions = No actions

properties-none-selected = Select a node in the simulation tree to see its properties.
properties-missing-at-tick = This item is not present at tick { $tick }. It shows again at a tick that has it.
properties-name = Name
properties-state = State
properties-tick = Tick
properties-tick-rate = Tick rate
properties-start = Start tick
properties-head = Head tick
properties-fork = Fork tick
properties-simulations = Simulations
properties-simulation = Simulation
properties-chunks = Chunks
properties-chunk-size = Chunk size
properties-active = Active
properties-position = Position
properties-status = Status
properties-gases = Gases
properties-active-links = Active links
properties-topology-version = Topology version
properties-portals = Portals
properties-docks = Docks
properties-type = Type
properties-built-in = Built-in
properties-enabled = Enabled
properties-interval-ticks = Interval (ticks)
properties-gas-id = Gas ID
properties-molar-cv = Molar Cv
properties-boiling-point = Boiling point
properties-condensation = Condensation
properties-vaporization-enthalpy = Vaporization enthalpy
properties-liquid-id = Liquid ID
properties-diffusion = Diffusion coefficient
properties-molar-mass = Molar mass
properties-branch = Branch
properties-current-branch = Current branch
properties-operation-sequence = Operation sequence
properties-yes = Yes
properties-no = No
properties-switch-branch = Switch to Branch
properties-switch-branch-tooltip = Moves the timeline to this branch. The world shows the branch's state after the switch.
properties-seek-checkpoint = Seek to Checkpoint
properties-seek-checkpoint-tooltip = Moves the timeline to this checkpoint. The world becomes read-only until the timeline returns to the head.

legend-pane-title = Legend
ignite-tool = Ignite
properties-stepped-to = Stepped to tick
properties-active-simulation = Active simulation
properties-awake-chunks = Awake chunks
properties-chunk-state = Chunk state
properties-selected-voxels = Selected voxels
properties-inject-by-coordinates = Inject by coordinates
