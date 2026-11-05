FN AutoSync - Windows standalone edition

Files:
  FN_AutoSync.exe: Windows 10/11 64-bit standalone program, no Python installation required.
  FN_AutoSync_Source.zip: complete Go sources and unit tests.

Usage:
1. On the NAS, enable an SMB share and access it in Windows Explorer (for example \\NAS\Backup).
2. Run FN_AutoSync.exe. Choose a local source folder and an SMB backup destination.
3. Back up a few test files, including a long-name file; then try restoring to a separate empty folder.
4. Keep __FN_ORIGINAL_NAMES__.json together with its data files, or restored long names may be lost.

This build has passed local core-unit tests and a Windows x64 compilation check. It has not been verified on your physical Windows/NAS setup.
Do not use two sync tools on the same backup target. Do not restore over the original folder until tests pass.
