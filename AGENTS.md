# Repository Guidelines

## Project Structure & Module Organization

- `My project/` is the Unity 6000.0.62f1 Quest project. Runtime control, networking, and discovery code lives in `Assets/PuppyPi/`; scene utilities live in `Assets/Editor/`.
- `Assets/Scenes/SampleScene.unity` is the cockpit scene. Models, materials, and XR settings are organized under `Assets/`.
- Unity tests live in `Assets/PuppyPi/Tests/{EditMode,PlayMode}/`. Python discovery tests live in `raspberry_pi/tests/`.
- `raspberry_pi/` contains discovery and legacy receiver scripts; `tools/` contains validation helpers. Read `docs/` for protocol and device setup. `patches/` targets the separate robot repository.

## Build, Test, and Development Commands

Run commands from the repository root.

- Open `My project` through Unity Hub, load `SampleScene`, and press Play for Editor development.
- For Quest builds, activate Android in **File > Build Profiles**, then select **PuppyPi > Build Quest development APK**. Output: `.local/quest/PuppyPiQuest.apk`.
- On Windows: `powershell -NoProfile -File tools/run_unity_tests.ps1 -Mode EditMode`; repeat with `-Mode PlayMode`. Override the editor path with `-Unity`. Other platforms can use Unity's Test Runner.
- `python3 -m unittest discover -s raspberry_pi/tests -v` runs discovery tests.
- `python3 tools/validate_v2.py --repo .local/Puppy-PI-Cpp --serve` starts the motor-free loopback server. It requires a separate robot checkout and PyYAML.

## Coding Style & Naming Conventions

Use four-space indentation. Follow existing C# conventions: `PuppyPi` namespace, PascalCase types/methods/properties, camelCase serialized fields, and underscore-prefixed private fields. Python uses snake_case functions and UPPER_CASE constants. No repository-wide formatter or linter is configured. Preserve Unity `.meta` files and asset GUIDs; use Git LFS for configured binary assets.

## Testing Guidelines

Unity uses Test Framework/NUnit with `*Tests.cs` files; Python uses `unittest` with `test_*.py` files. Add regression coverage for changed input, ACK, timeout, and reconnection behavior. No numeric coverage threshold is configured. Reports go to ignored `TestResults/`. For protocol roundtrips, follow the EditMode → Python validator → EditMode/PlayMode sequence in `docs/BALLGRIP_ROS1_V2.md`.

## Commit & Pull Request Guidelines

History uses short Korean or English summaries, such as `add Console_BallGrip`; use concise descriptions of the change. Keep commits focused. PRs should explain behavior changes, link relevant issues, list validation and hardware limitations, and include screenshots or video for cockpit/UI changes.

## Configuration & Robot Integration

Current v2 control uses UDP 5001; discovery uses 5002. The legacy 5005 receiver is incompatible. Verify endpoint settings before device tests and preserve neutral-ACK/re-grab and emergency-stop behavior.
