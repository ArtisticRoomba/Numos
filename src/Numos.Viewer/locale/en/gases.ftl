blank-gas-registry = Blank gas registry. Add a gas before injecting.
add-gas-definition = Add gas definition
new-gas-name = Name##new-gas
new-gas-molar-cv = Molar Cv (J/(mol·K))##new-gas
new-gas-boiling-point = Boiling point (K)##new-gas
new-gas-boiling-point-tooltip = Boiling temperature at the saturation reference pressure in AtmosConfig.
new-gas-condensation-enabled = Condensation enabled##new-gas
new-gas-vaporization-enthalpy = Vaporization enthalpy (J/mol)##new-gas
new-gas-liquid-id = Liquid ID##new-gas
new-gas-liquid-id-tooltip = Reserved for a custom liquid integration; the built-in solver ignores it. -1 is the Viewer's placeholder for no liquid.
new-gas-diffusion-coefficient = Diffusion coefficient##new-gas
new-gas-diffusion-coefficient-tooltip = Dimensionless fraction of this gas's mole imbalance mixed per tick, from 0 to 1. The solver clamps values above 1.
new-gas-molar-mass = Molar mass (kg/mol)##new-gas
new-gas-molar-mass-tooltip = Mass of one mole of this gas. 0 means unknown, so the gas weighs the configured reference molar mass.
add-gas = Add Gas
gas-id-column = ID
gas-name-column = Name
gas-cv-column = Cv (J/(mol·K))
gas-boiling-column = Boiling (K)
remove = Remove
gas-removal-tooltip = Removal is allowed while no stored chunk gas IDs would be shifted.
gas-details = { $name }
    Condensation: { $condensation }
    Vaporization enthalpy: { $vaporization-enthalpy }
    Liquid ID: { $liquid-id }
    Diffusion coefficient: { $diffusion-coefficient }
    Molar mass: { $molar-mass }
enabled = enabled
disabled = disabled
use-selected-cell = Use Selected Cell
injection-voxel-x = Voxel X##inject
injection-voxel-y = Voxel Y##inject
injection-voxel-z = Voxel Z##inject
move-camera-to-voxel = Move Camera to Voxel
injection-gas = Gas##inject
no-gas-registered = Gas: none registered
injection-amount = Amount (mol)##inject
injection-temperature = Temperature (K)##inject
inject-gas = Inject Gas##inject-action
ignition-volume = Volume (m³)
ignition-source-tooltip = Heats a very small volume to a high temperature for one tick, enough to ignite gas below its usual ignition temperature.
ignition-positive-temperature-volume-required = Ignition temperature and volume must be greater than zero.

message-project-add-gas = Added gas { $name } with ID { $id }.
message-project-remove-gas = Removed gas { $name }.
message-project-inject-gas-not-selected = No gas selected for injection.
message-project-inject-gas = Injected { $moles } moles of { $name } at ({ $pos }).
message-project-inject-gas-failed = Could not inject gas: { $message }
message-project-ignite-source-success = Ignited voxel ({ $pos }) of chunk { $chunk } at { $temperature } K.

simulation-new-gas-name = New Gas
