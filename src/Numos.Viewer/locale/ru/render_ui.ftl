viewer-description-line-one = An external viewer for Numos, an engine-agnostic,
viewer-description-line-two = pseudo-realistic, voxel-based atmospherics simulation library.
viewer-description = An external viewer for Numos, an engine-agnostic, pseudo-realistic, voxel-based atmospherics simulation library.
no-simulation-loaded = No simulation is currently loaded.
create-workspace-instruction = Choose File > New Simulation to create a workspace.

about = About
authors = Authors
thanks-to = Thanks To

copyright = Copyright
copyright-notice = © 2026 Numos contributors
license = License
build-provenance = Build provenance

viewer = Viewer
commit = Commit
configuration = Configuration
framework = Framework
sdk = SDK
repository = Repository
copy-hash = Copy hash

original-author-of-coresim = Original author of CoreSim
coresim-maintainer-viewer-author = CoreSim maintainer, Viewer author
numerical-methods-and-physics-guidance = Numerical methods and physics guidance
gas-reactions-solver = GasReactions solver

simulation-viewer-title = Numos Simulation Viewer

file = File
open-replay = Open Replay...
save-replay = Save Replay...
exit = Exit

view = View
solution = Solution
three-d-viewport = 3D Viewport
two-d-slice-viewport = 2D Slice Viewport

settings = Settings
help = Help

visualization = Visualization
rendering-style = Rendering style
three-d-focus = 3D focus
two-d-slice = 2D slice
show-slice-viewport = Show slice viewport

three-dimensional = 3D
two-dimensional = 2D

chunk-outlines = Chunk outlines
voxel-outlines = Voxel outlines
voxel-outlines-tooltip = Each outline is drawn per voxel, so large scenes skip them. The 3D view outlines voxels only for a focused chunk or a scene of at most { $max-outlined-voxels } voxels. The 2D view outlines cells only when they are at least { $min-outline-pixels } pixels wide.

transparent-voxels = Transparent voxels
velocity-arrows = Velocity arrows
velocity-arrows-tooltip = Draws at most { $max-arrows } arrows. A larger scene shows every n-th voxel. Zoom the 2D view in to see more of them.

arrow-scale = Arrow scale
velocity-arrow-scale-tooltip = Seconds of travel one arrow represents. At 0.05 s a voxel moving at 20 m/s draws a one-voxel arrow. Arrows longer than { $max-length } voxels are capped and keep only their direction.

solver-pipeline = Solver Pipeline
atmos-config = AtmosConfig
configuration-apply-instructions = Values apply on Enter, when a field loses focus, or when a slider is released.
gas-registry = Gas Registry

reset-configuration-success = Reset every AtmosConfig setting to the library defaults.
reset-configuration-failure = Could not reset the settings: { $reason }

config-value-minimum-error = {$field} must be 0 or more. The previous value is still applied.
config-field-set-feedback = {$field} set to {$value}.
config-field-apply-error = Could not apply {$field}: {$error}

viewing = Viewing
editing = Editing
simulation-inspection-status = {$mode} {$simulation}
chunks-section = Chunks
voxel-tools-section = Voxel Tools

viewport-tool-label = Viewport tool
select-box-tool = Select / Box
paint-room-tool = Paint Room
paint-gas-tool = Paint Gas
erase-gas-tool = Erase Gas
classification-label = Classification
amount-mol-label = Amount (mol)
temperature-k-label = Temperature (K)

selection-section = Selection
no-voxels-selected = No voxels selected.
selected-voxel-count-singular = {$count} voxel selected
selected-voxel-count-plural = {$count} voxels selected
clear-selection-button = Clear Selection

no-chunks-available = No chunks available.
axis-x-yz-plane = X (YZ plane)
axis-y-xz-plane = Y (XZ plane)
axis-z-xy-plane = Z (XY plane)
slice-plane-description = Global voxel coordinate of the plane. The slice view shows this plane across every chunk. Scroll to zoom and drag with the middle button to pan.
fit-plane-button = Fit Plane
frame-focused-chunk-button = Frame Focused Chunk
showing-label = Showing
of-label = of
to-label = to
plane-label = Plane
slice-plane-cell-count = {$width} × {$height} cells, {$chunks} chunks

minimum-label = Minimum
maximum-label = Maximum
custom-resolution-checkbox = Custom resolution
default-resolution-levels = {$count} levels (default)
fixed-range-mode = Fixed
automatic-range-offset-mode = Automatic ± {$offset}, recomputed each frame
automatic-range-mode = Automatic, recomputed each frame
automatic-bounds-checkbox = Automatic bounds
offset-label = ± Offset
automatic-bounds-offset-tooltip = Expands the automatic minimum and maximum by this amount.
minimum-short-label = Min
maximum-short-label = Max

all-chunks = All chunks
focus-chunk-id = Focus##chunk-focus
frame-view = Frame View
show-all-chunks = Show All Chunks
slice-cell-2d = 2D Slice Cell

chunk = Chunk
cell = Cell
slice-uv = Slice UV
temperature = Temperature
pressure = Pressure
velocity = Velocity
speed = Speed
total-amount = Total amount
amount-excluding-invalid = { $amount }, excluding invalid amounts
primary-gas = Primary gas
room = Room

selected-voxel-chunk-missing = Selected voxel/chunk does not exist at this timeline position.
details-unavailable-revision = Details are unavailable for this presented revision.

gas-makeup = Gas makeup
gas-count-singular = 1 gas
gas-count-plural = { $count } gases
no-valid-amounts = No valid amounts
vacuum = Vacuum

share = Share
invalid-not-in-bar = invalid, not in bar

room-solid = Solid ({ $id })
room-void = Void ({ $id })
room-unassigned = Unassigned ({ $id })

cell-index = index { $index }
none = none
gas-name-id = { $name } ({ $id })
gas-id = Gas { $id }

simulation-state = Simulation State
no-chunks-in-simulation = No chunks in this simulation.
waiting-for-chunk-snapshot = Waiting for a chunk snapshot.
dimensions = Dimensions
voxel-dimensions = { $x } × { $y } × { $z } voxels
total-voxels = Total voxels
active-voxels = Active voxels
active-gases = Active gases
simulation-state-label = State
sleep-timer = Sleep timer
max-pressure = Max pressure
average-pressure-non-zero-voxels = Avg pressure (non-zero voxels)
not-captured-current-visualization = Not captured for the current visualization
min-temperature-non-zero-voxels = Min temperature (non-zero voxels)
max-temperature = Max temperature
flow = Flow
