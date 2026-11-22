three-d-voxel = 3D Voxel
selected-voxel-count =
    { $count ->
        [one] { $count } voxel
       *[other] { $count } voxels
    }
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
enable-transport-mode-in-topology = Enable a transport mode in the Topology properties.
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

select-box-tool = Select / Box
paint-room-tool = Paint Room
paint-gas-tool = Paint Gas
erase-gas-tool = Erase Gas
classification-label = Classification
toolbar-room-id-label = Room ID
amount-mol-label = Amount (mol)
temperature-k-label = Temperature (K)
no-voxels-selected = No voxels selected.
voxels-selected =
    { $count ->
        [one] { $count } voxel selected
       *[other] { $count } voxels selected
    }
clear-selection-button = Clear Selection

ignite-tool = Ignite
