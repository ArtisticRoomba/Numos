program-settings.configure = Configure

settings.graphics = Graphics
settings.interface = Interface
settings.custom-resolution = { $width } x { $height } (custom)
settings.target-resolution = Target resolution
settings.resolution-format = { $width } x { $height }
settings.fullscreen = Fullscreen
settings.frame-pacing = Frame pacing
settings.uncap-fps = Uncap FPS
settings.uncap-fps-tooltip = Removes the software frame limit. If VSync is disabled, this may max out a CPU core while updating the UI.
    Ill-advised to use, made accessible just for the love of the game.
settings.frame-rate-limit = Frame rate limit
settings.limit-fps-unfocused = Limit FPS when unfocused
settings.limit-fps-unfocused-tooltip = Uses a lower render rate while the viewer is in the background. The simulation keeps running at its normal fixed tick rate.
settings.unfocused-frame-rate = Unfocused frame rate
settings.vsync = VSync
settings.vsync-tooltip = Synchronizes presentation to the display refresh rate.
settings.event-based-rendering = Use event-based rendering
settings.event-based-rendering-tooltip = When enabled, the viewer waits for window events while the simulation is paused.
    Reduces CPU usage for drawing the UI while the simulation is paused.
    Experimental: the viewer may miss redraws in some cases.
settings.show-fps-overlay = Show FPS overlay
settings.show-fps-overlay-tooltip = Displays the current render rate in the upper-left corner.
settings.show-viewport-branding = Show viewport branding
settings.show-viewport-branding-tooltip = Displays the Numos logo and name in simulation views.
    How it feels to LARP as an actual CFD tool.
settings.branding-opacity = Branding opacity
settings.corner = Corner
settings.offset-x = Offset X
settings.offset-y = Offset Y
settings.logo-size = Logo size
settings.window-layout = Window layout
settings.save-current-layout = Save Current Layout
settings.save-current-layout-tooltip = Restores this arrangement the next time Numos starts.
settings.save-default-preset = Save as Default Preset
settings.save-default-preset-tooltip = Saves the default that future publishes use on first launch.

resolution.confirm-title = Confirm Resolution Change
resolution.confirm-message = Keep this resolution? The previous resolution will be restored automatically if you do not confirm.
resolution.reverting-countdown = Reverting in { $seconds } seconds.
resolution.keep = Keep Resolution
resolution.revert = Revert

performance.fps = { $fps } FPS
