# SolidWorks to URDF Exporter

Authored and maintained by [Stephen Brawner](brawner@gmail.com). Past supporters include [PickNik Consulting](https://picknik.ai), Verb Surgical, Open Robotics, and Willow Garage. 

## Latest Release

**SolidWorks 2021**

https://github.com/ros/solidworks_urdf_exporter/releases/tag/1.6.1

**SolidWorks 2020**

https://github.com/ros/solidworks_urdf_exporter/releases/tag/1.6.0

**SolidWorks 2019 on 2018 SP 5**

https://github.com/ros/solidworks_urdf_exporter/releases/tag/1.5.1

## SolidWorks Version Requirements

1. The minimum required version of SolidWorks for use with this add-in is 2018 Service Pack 5. SolidWorks 2017 or earlier may work. See [this issue](https://github.com/ros/solidworks_urdf_exporter/issues/73).

## Usage

See the [ROS Wiki](http://wiki.ros.org/sw_urdf_exporter) and associated [tutorials](http://wiki.ros.org/sw_urdf_exporter/Tutorials).

Loading values from the CSV of an earlier export: [docs/LOAD_FROM_CSV.md](docs/LOAD_FROM_CSV.md) (English and 繁體中文).

### Settings per SolidWorks configuration

The exporter keeps its settings (link tree, joints and options) separately for
each SolidWorks configuration of the model. The *Active configuration* group of
the PropertyManager shows which configuration is active and where its settings come from:
*Stored for this configuration*, *Using shared (legacy) settings* (a model saved
by an older exporter, one set for all configurations) or *New: nothing stored
yet*. Activate the configuration you want to export before you open the exporter.

- Saving or exporting writes only the settings of the configuration that was
  active when the exporter was opened. If another configuration has been
  activated in the meantime, nothing is saved or exported, so the settings of
  that configuration are kept.
- When a configuration was renamed, the exporter offers it the settings stored
  under its old name.
- The settings live in the feature tree folder `URDF Export Configuration`, one
  entry per configuration. The data format is described in
  [CONFIGURATION_FORMAT.md](CONFIGURATION_FORMAT.md).

### Export options

- **Angle unit** (joint and link pages of the assembly form): show the angle
  boxes (origin roll/pitch/yaw, limits and velocity of revolute and continuous
  joints) in `rad` or `deg`. The URDF and the saved settings always hold
  radians. The choice is remembered per Windows user; the default is radians.
- **Write xacro** (assembly and part forms): write the robot as a xacro
  macro instead of `<robot_name>.urdf`. `<robot_name>_macro.xacro` defines
  the macro (parameters `prefix`, `parent`, `xyz`, `rpy`, all optional) and
  `<robot_name>.xacro` calls it with its defaults, which gives the same robot
  as the plain URDF; the generated launch files expand it with xacro. Another
  description can include the macro file and use the robot several times, for
  example `prefix="left_" parent="body" xyz="0 0.3 0"`. See
  `CONFIGURATION_FORMAT.md`.
- **OBJ + MTL** mesh format (assembly form) or **OBJ + MTL (native colors)**
  (part form): write OBJ meshes with their SolidWorks colors instead of STL.
  See [OBJ_EXPORT.md](OBJ_EXPORT.md).

### Questions during an export

- **The package folder exists already**: when the chosen package folder already
  holds files, the export asks before it replaces them (files edited by hand
  included). *No* goes back to the form and writes nothing.
- **Mesh files are missing**: when *Export Model File Only...* would write a
  URDF or xacro that refers to mesh files that are not in the package folder,
  the exporter lists the links. *Yes* exports the meshes as well, *No* writes
  only the model file anyway, *Cancel* goes back without writing anything.

### Interface language

When SolidWorks runs in Traditional Chinese, the export forms, PropertyManager
and menus are shown in Traditional Chinese (zh-TW); every other language uses
English. CAD names, URDF identifiers and numbers are not translated. Details,
including how to add a language: [I18N.md](I18N.md).

### Reference-only links

In the assembly exporter, leave **Link Components** empty to export a reference
frame without inertial, visual, collision, or mesh data. For a non-base link,
select an existing reference coordinate system for its origin; it is connected
to its parent by a fixed joint, so no reference axis is needed. Empty links may
have children. An empty base link may also use the automatically generated global
coordinate system.

## Development

1. Install Visual Studio 2017
1. Install .NET desktop development
    1. From Visual Studio: `Tools > Get Tools and Features...`
    1. Check `.NET desktop development` package
    1. Select `Modify`
1. Install the [SolidWorks API tools](https://help.solidworks.com/2019/english/api/sldworksapiprogguide/GettingStarted/SolidWorks_API_Getting_Started_Overview.htm)
1. Launch Visual Studio with admin privileges. Right click and select `Run as Administrator`
1. Open `sw2urdf/SW2URDF.sln`  
1. Enable Debugging
    1. Right click `SW2URDF` in the Solution Explorer
    1. Click the `Debug` Tab
    1. Ensure `Configuration:` is set to `Debug`
    1. Ensure `Start external program:` is pointing to the SolidWorks executable. For example `C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\SLDWORKS.exe`

## Testing

See [TESTING.md](TESTING.md). How to test the interplay of SolidWorks configurations with the per-configuration
URDF export settings (manual checklist and an automated live script) is in
[Testing configurations](TESTING.md#testing-configurations-solidworks-configuration-x-urdf-export-configuration).

## Converting mesh format from 3dxml to dae

Executing the following command will convert the format of the exported mesh from 3DXML to DAE, and rewrite the URDF, allowing you to display colored meshes in visualization tools like RViz:

```bash
pip3 install scikit-robot -U
convert-urdf-mesh <URDF_PATH> --output <OUTPUT_URDF_PATH>
```

### Trouble Shooting

1. `AxImp.exe` error - Check the installation of the .Net Tools. If there is no error, install the Windows 10 SDK.
1. `Resourse.resx` error - Check if `sw2urdf/SW2URDF/Resources.resx` exists and is empty. If empty, delete this file then right click the `SW2URDF` in the Solution Explorer and select `Properties`. Navigate to the Resources tab and click the button to create a new file.
