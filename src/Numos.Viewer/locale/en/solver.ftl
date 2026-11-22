move-up = Move up
move-down = Move down
remove-stage = Remove stage
solver-interval-tooltip = Ticks between runs of this stage.
custom-stage-removal-unavailable = The host registered this stage, so the viewer cannot remove it.
select-stage-description-settings = Select a stage to see its description and settings.
configured-reactions = Configured reactions: { $count }
all-built-in-stages-in-pipeline = Every built-in stage is in the pipeline.
add-stage-placeholder = Add stage...
add-stage-help = Adds the stage enabled, at its canonical position. It goes before the first built-in stage that normally runs after it, or last when none does. Custom stages never change the position.
transport = Transport:
euler = Euler
legacy = Legacy
transport-presets-help = Euler and Legacy swap the stages that move gas and keep every other stage. Reset to defaults restores the built-in order for the current model and enables every stage. It cannot discard custom stages.

catalog-solvers = Catalog solvers:
catalog-solvers-help = Solvers from loaded assemblies that the pipeline does not hold. Add puts the solver at its declared position and rate. The world records the change, so a replay adds it at the same tick.
catalog-solvers-empty = The solver catalog is empty. Load a solver assembly to add custom solvers.
catalog-solvers-all-added = Every catalog solver is in the pipeline.
catalog-solver-before = Runs before { $stage }.
catalog-solver-after = Runs after { $stage }.
catalog-solver-last = Runs last.
catalog-solver-runs-per-second =
    { $rate ->
        [one] { $rate } run per second.
       *[other] { $rate } runs per second.
    }
catalog-solver-interval =
    { $interval ->
        [one] Runs every { $interval } tick.
       *[other] Runs every { $interval } ticks.
    }
catalog-solver-every-tick = Runs every tick.

solver-state = Solver state
solver-state-help = The solver's saved members. Edits apply at the next tick, and the world records them like any other edit.
solver-state-loading = Reading the solver state...
solver-state-none = This stage has no saved members.

load-solvers-menu = Load Solvers...
load-solvers-title = Load Solvers
load-solvers-help = Loads a solver assembly so its solvers appear in the catalog.
load-solvers-unavailable = This build is compiled ahead of time (NativeAOT), so it cannot load solver assemblies. Run a JIT build, such as a release archive or dotnet run, to use them.
load-solvers-description = Load a .dll with Numos solvers, or a folder of them. The assembly runs code as it loads and stays loaded until the viewer exits. Load only assemblies you trust.
load-solvers-browse-file = Browse File...
load-solvers-browse-folder = Browse Folder...
load-solvers-button = Load
load-solvers-filter-name = .NET assembly
load-solvers-loaded = Loaded.
load-solvers-failed = Could not load every assembly.
