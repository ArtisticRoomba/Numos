three-d-voxel = 3D Voxel

selected-voxel-count = { $count } voxels

clear-cell = Clear Cell
unassigned-classification = Unassigned (0)
solid-classification = Solid (-2)
void-classification = Void (-1)
draft-classification = Draft ({ $classification })
set-temperature = Set Temperature to { $temperature } K
frame-voxel = Frame Voxel

portal = Portal
start-portal-here = Start Portal Here
create-portal-to-here = Create Portal to Here
portal-first-endpoint = From: { $cell }
portal-first-endpoint-instruction = Start a portal at its first endpoint.
enable-transport-mode-in-world-topology = Enable a transport mode in World & Topology.

inject = Inject
no-gases-registered = No gases registered
moles-context-injection = Moles##context-injection
temperature-kelvin = Temperature: { $temperature } K
inject-context-confirm = Inject##context-confirm

set-classification-action = Set classification { $classification }
register-gas-before-injecting-voxels = Register a gas before injecting voxels.
injected-gas-into = Injected { $moles } mol { $gas } into
cleared-gas-from = Cleared gas from
cleared-cells = Cleared
set-temperature-for = Set temperature to { $temperature } K for
voxel-mutation-feedback = { $action } { $edited } voxel{ $edited ->
    [one] .
   *[other] s.
    }
