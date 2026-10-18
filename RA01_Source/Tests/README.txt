RA01 TESTS (M47) - the inspector's checklist for the whole mod

RUN THEM
  In PowerShell, from the project folder:   dotnet test Tests\RA01.Tests.csproj
  (or open Test Explorer in Visual Studio). The first run downloads xUnit from NuGet (test-only; it never goes into RA01.dll).
  About 110 checks, a few seconds. All green = nothing the tests know about has changed.

WHAT THEY CHECK
  HookRegistryTests            the Rehooker table, its row numbers, and the bytes of every pattern (snapshot of 2.31)
  ExeScanAndAddressListTests   reading the exe header, FOUND / MOVED / OFF, the address list ini and json (on a fake exe in memory)
  SettingsFileTests            every saved setting comes back after save + load; the list of saved keys (snapshot)
  SettingsMenuTests            the F10 tab tables line up; sliders sane; defaults inside their sliders
  TuningTests                  ranges, Reset all, eye-trim mirror, steering presets, log categories
  VrikTests                    the arm solver on a fake arm (hand on target, beams keep their length), turn helpers
  HeadPoseAndNameCodeTests     head maths against System.Numerics, scope lens place, the game's name codes
  LayoutTests                  every OpenXR / D3D12 / Windows / XInput struct against the official headers
  ControlsTests                VR controllers -> the game's pad (buttons, triggers, grips, sticks, chord, F10)
  SmallPartsTests              banner, Auto tune profiles, gun calibration file
  CodeRulesTests               your rules: Core never calls Actions/Visuals (except Entry), RA01 comments, Cost lines,
                               no new static Vector3/Quaternion fields, namespaces, line endings

WHEN A TEST FAILS
  A "TRIPWIRE" message means something changed that the test remembers (a snapshot). If you changed it on purpose and the
  mod works in game, update the test to match your code - the message names the file and the list to edit.
  Any other failure points at the line that broke. Your code wins: if you like your change, change the test.
