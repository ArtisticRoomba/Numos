# Default strings
simulation-title = Numos Simulation Viewer
simulation-new-gas-name = New Gas
simulation-name-default = Untitled Simulation
simulation-name-copy = { $name } Copy

# Console Messages

## Sources
source-layout = Layout
source-viewer = Viewer
source-runtime = Runtime
source-exception = Exception
source-simulation = Simulation

message-layout-not-initialized = The ImGui layout has not been initialized yet.
message-layout-saved-success = Saved. This layout will be restored at the next launch.
message-layout-exception = Could not save the layout

message-layout-not-initialized-packaged = The packaged default layout has not been initialized yet.
message-layout-saved-success-packaged = Saved as the packaged default for first-time launches.
message-layout-exception = Could not save the packaged default layout

message-viewer-startup = Numos Viewer { $version } started.

message-fatal-unhandled-exception = Unhandled exception
message-fatal-unhandled-exception-trace = Unhandled exception: { $stackTrace }
message-exception-unobserved-task = Unobserved task exception

# Project
message-project-create = Created project '{ $name }'.
message-project-chunk-add = Added chunk { $pos }.
message-project-exception-chunk-add = Could not add the chunk

message-project-chunk-remove = Removed chunk { $pos }.
message-project-chunk-remove-fail = Chunk { $pos } no longer exists.

message-project-chunk-seal = Replaced the outer faces of chunk { $pos } with solid walls.
message-project-chunk-seal-fail = Could not seal the chunk

message-project-unslept-chunk = Unslept chunk { $pos }.

message-project-add-gas-name-required = A gas name is required.
message-project-add-gas = Added gas { $name } with ID { $id }.
message-project-gas-properties-invalid = Gas properties must be finite, non-negative values.

message-project-remove-gas-chunk-contained-error =
     That gas cannot be removed because it, or a later gas ID, has already been used by a chunk.
     Remove the affected chunks first so gas IDs remain stable.
message-project-remove-gas = Removed gas { $name }.

message-project-inject-gas = Injected { $moles } mol of { $name } into ({ $x }, { $y }, { $z }).
message-project-inject-gas-not-selected = Select a registered gas before injecting.
message-project-inject-gas-exception = Could not inject gas

message-simulation-create-exception = Could not create the simulation
message-simulation-closed-project = Closed project '{ $name }'.

# UI

button-copy = Copy
button-clear = Clear
button-create = Create
button-cancel = Cancel
button-close-project = Close Project
button-run = Run
button-pause = Pause
button-step = Step

text-paused = Paused
text-running = Running

progress-bar-ticking = Ticking...
progress-bar-step-complete = Step complete - Tick { $tick }

header-simulation-details = Simulation Details

table-simulations = SIMULATIONS
table-active-chunks = ACTIVE CHUNKS
table-gases = GASES
table-current-tick = CURRENT TICK

popup-create-simulation = Create Simulation
popup-close-simulation = Close Simulation Project?

checkbox-autoscroll = Auto-scroll
checkbox-default-gases = Include Oxygen and Nitrogen

warning-discard-branches = Creating this project will discard all { $count } in-memory branches.
info-discard-branches = Creating this project will close the current in-memory project.

tooltip-default-gases = The default gas definitions will be appended to the new project.
tooltip-default-gases-disabled = The project will start with a blank gas registry.

log-entry-counter = { $visible } shown / { $entries } total


