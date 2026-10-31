timeline-window = Timeline
timeline-branches = Branches
timeline-transport = Transport
timeline-selected-tick = Selected tick
timeline-visible-ticks = Visible ticks
timeline-keep-playhead-centered = Keep playhead centered
timeline-keep-playhead-centered-tooltip = Moves the visible tick range as the selected timeline position changes.
timeline-first-visible-tick = First visible tick
timeline-simulate-while-scrubbing = Simulate while scrubbing
timeline-simulate-while-scrubbing-tooltip-active = The simulation updates whenever the scrubber crosses a tick.
timeline-simulate-while-scrubbing-tooltip-inactive = The simulation updates when the scrubber is released.
timeline-show-branch-history = Show branch history
timeline-show-branch-history-tooltip = Shows every session branch as an aligned history lane. Operation and checkpoint detail remains on the selected branch.
timeline-branch = BRANCH
timeline-fork-tick = Fork tick { $tick }
timeline-start = START
timeline-head = HEAD
timeline-selected = SELECTED
timeline-mode = MODE
timeline-read-only = Read-only
timeline-live-recording = Live recording
timeline-selected-branch = Selected branch
timeline-head-tick = Head tick { $tick }
timeline-branch-from-tick = Branch from Tick
timeline-branch-from-tick-tooltip = Preserves this branch and starts a new branch before operations recorded at the selected tick.
timeline-continue-branch = Continue Branch
timeline-continue-branch-tooltip = Resumes live recording at the selected branch head.
timeline-tick = Tick { $tick }
timeline-through-operation = Through operation #{ $sequence }
timeline-start-button = Start
timeline-back-button = Back
timeline-play-button = Play
timeline-forward-button = Forward
timeline-return-to-head-button = Return to Head
timeline-checkpoint = CHECKPOINT
timeline-re-simulated = RE-SIMULATED
timeline-verified = Verified
timeline-replay-matches-reference-hash = Replay matches the reference hash.
timeline-divergent = Divergent
timeline-replay-does-not-match-reference-hash = Replay does not match the reference hash.
timeline-unverified = Unverified
timeline-no-reference-hash-available = No reference hash is available.
timeline-verification = VERIFICATION
timeline-done-in-tick = Done in tick { $tick } ({ $count })
timeline-no-external-operations-recorded = No external operations were recorded in this tick.
timeline-selected-operation = Selected operation
timeline-operation = OPERATION
timeline-done-in-tick-label = DONE IN TICK
timeline-order = ORDER
timeline-inspect-after-operation = Inspect After Operation
timeline-recorded-mixture-empty = The recorded mixture is empty.
timeline-recorded-amount = Recorded amount { $amount } mol
timeline-set-solver-layout = SetSolverLayout ({ $count } stages)
timeline-initial-solver-stages = Initial solver stages
timeline-built-in = Built-in
timeline-requested-solver-layout = Requested solver layout
timeline-error-create-branch = Could not create the replay branch
timeline-error-select-branch = Could not select the replay branch
timeline-error-continue-branch = Could not continue the replay branch
timeline-error-seek-tick = Could not seek the replay to tick { $tick }
timeline-error-seek-position = Could not seek the replay
timeline-error-return-to-head = Could not return the replay to its head
replay-stage-table-column-index = #
replay-stage-table-column-stage = Stage
replay-stage-table-column-kind = Kind
replay-stage-table-column-enabled = Enabled

replay-applied-configuration = Applied configuration
replay-configuration-column-setting = Setting
replay-configuration-column-value = Value
replay-configuration-column-unit = Unit

replay-gas-registry = Gas registry
replay-no-gases-registered = No gases are registered.
replay-gas-column-id = ID
replay-gas-column-name = Name
replay-gas-column-heat-capacity = Cv (J/(mol·K))
replay-gas-column-diffusion = Diffusion
replay-gas-column-boiling-point = Boiling point (K)
replay-gas-column-condensation = Condensation
replay-gas-column-latent-heat = Latent heat (J/mol)
replay-gas-column-liquid-id = Liquid ID

replay-solver-configurations = Solver configurations
replay-solver-configuration = Solver configuration

replay-timeline-axis-title = TICK / SIMULATION TIME
replay-restorable-tick-range-tooltip = Ticks { $startTick } through { $endTick } have recorded states that can be restored.
replay-single-operation-tooltip = { $code }
    Done in tick { $tick }
    Order 1
replay-multiple-operations-tooltip = { $count } operations in tick { $tick }
replay-checkpoint-tooltip = Checkpoint at tick { $tick }, after operation #{ $operationSequence }
    Reference hash { $digest }
replay-divergent-checkpoint-tooltip = Divergent: the replayed state does not match this reference hash.

replay-session-branches-title = SESSION BRANCHES
replay-branch-history-tooltip = { $name }
    Fork tick { $forkTick }
    Head tick { $headTick }
replay-selected-branch-tooltip = Selected branch
replay-inspect-branch-tooltip = Click to inspect this tick