# SE_Stabiliser
Space Engineers: Configures gyroscopes to keep the ship level relative to local gravity.

> This project is configured to be built within a separate IDE to allow for error checking, code completion etc..
> Only the section in `#region CodeEditor` should be copied into the Space Engineers editor. This region has been automatically extracted into the corresponding `txt` file.

##Description
Attempts to keep pitch and roll of the ship at 0
+ measures pitch and roll
  - relative to local natural gravity
  - accounting for ship orientation
+ uses a [PID](https://en.wikipedia.org/wiki/PID_controller) to determine correct response strength
+ sets gyroscope overrides to return to level flight
  - accounting for ship and gyroscope orientation

##Known Issues
+ Can only handle single override gyroscope
+ PIDs are not capped or wrapped: do not behave well if forces can induce more than half-rolls
+ PID total error accumulates if running but not in control, leading to huge residual forces when enabled

##Hardware
| Block(s)      | number        | Configurable  |
| ------------- | ------------- | ------------- |
| Remote Control| single        | by name constant
| Gyroscope     | single        | by name constant

##Configuration
+ `nameRemoteControl`: the name of the Remote Control used to identify the main grid and get local gravity
+ `nameGyro`: the name of the Gyroscope that will have the override speeds set to stabilize the ship.
+ `period`: ticks between processing taking place
+ `shipOrientation`: orientation of the ship compared to the grid

##Standard Blocks
+ `Gyro`: IMyGyro API wrapper for setting Gyroscope values
+ `PID`: PID data, logic, storage for stabilising forces
+ `ValidateBlock()`: check that found blocks are usable
+ Status/Initialise/Validate framework

##"Undocumented"
+ Writes status to IMyTextPanel named `Display`
