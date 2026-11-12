# CMMC Assessment Artifact Staging & Hashing Utility

A lightweight VB.NET console utility that automates local staging, pre-flight validation, and execution of the official Department of Defense (DoD) CIO **CMMC Artifact Hashing Tool (Version 2.14 / Script v1.12)** for CMMC Level 2 (`32 CFR 170.17`) and Level 3 (`32 CFR 170.18`) assessments.

## Why This Tool Exists

During a CMMC Level 2 or Level 3 certification assessment, the Organization Seeking Assessment (OSA) must retain all proprietary and Controlled Unclassified Information (CUI) assessment artifacts for **six (6) years** and provide the C3PAO / DCMA DIBCAC assessment team with cryptographic SHA-256 integrity logs for entry into **CMMC eMASS**.

However, running the raw PowerShell script printed in Appendix A of the *DoD CMMC Hashing Guide (v2.14)* directly against a live repository (such as SharePoint or OneDrive) introduces several failure points:
1. **SharePoint / OneDrive Metadata Alteration:** Cloud document systems inject metadata into Office/PDF files on sync, altering their SHA-256 hashes.
2. **Appendix A Syntax & CLI Bugs:** The copy-paste script in Appendix A of the DoD guide contains an unclosed `<#` block comment and broken command-line parameter ordering (`powershell -command -ExecutionPolicy`).
3. **Self-Hashing & Re-Run Contamination:** Placing `ArtifactHash.ps1` and its outputs inside the artifact root folder causes the script to hash itself and previously generated `.log`/`.zip` files.
4. **Hidden OS Clutter (`-Force` Switch):** The DoD script uses `Get-ChildItem -Force`, which hashes hidden `desktop.ini`, `Thumbs.db`, and temporary Office lock files (`~$*.docx`).
5. **Subfolder Flattening Collisions:** `Compress-Archive` flattens subdirectories inside `HashedArtifacts.zip`. If two different control subfolders contain files with the same name (e.g., `AC.L2-3.1.1\screenshot.png` and `IA.L2-3.5.1\screenshot.png`), the zip archive collides.

## Features

* **Clean Local Staging:** Copies your evidence files into an isolated local staging folder (`Audit_Ready`) while stripping out hidden `desktop.ini`, `Thumbs.db`, `.DS_Store`, and `~$` temporary lock files.
* **Pre-Flight Validation:** Detects `0-byte` (empty or unhydrated cloud) files and duplicate filenames across subfolders before hashing begins.
* **Automated Duplicate Prefixing:** Optionally prefixes duplicate filenames in the staged copy with their subfolder name so `HashedArtifacts.zip` builds cleanly without modifying your original files.
* **Self-Contained DoD Script Generation:** Automatically generates the syntax-corrected DoD `ArtifactHash.ps1` (v1.12) in a separate `Output` folder so the script never hashes itself.
* **Smart PowerShell 7 (`pwsh.exe`) Discovery:** Automatically locates PowerShell 7 across standard MSI, `x86`, `AppData`, and `winget` (`WindowsApps`) paths, with automatic fallback to Windows PowerShell 5.1.
* **Ready-to-Submit eMASS Handoff Sheet:** Parses `CMMCAssessmentLogHash.log` and generates `eMASS_C3PAO_Handoff.txt` with the exact **Hashed Data List** and 64-character **Hash Value** required by your C3PAO Lead Assessor.
* **Read-Only Archival Locking:** Optionally copies the final package back to your long-term archive folder and sets the `+R` (Read-Only) file attribute to prevent accidental edits over the 6-year retention window.

## Usage

### Interactive Mode
Simply run `CMMC_Hashing.exe` and follow the on-screen prompts:
1. Paste or drag-and-drop your **Source Artifact Directory**.
2. Specify a local **Working Directory** (or press `Enter` to use `C:\CMMC_Hash`).
3. *(Optional)* Specify a long-term **Archive Directory** to store and lock (`Read-Only`) your 6-year retention files.

### Command-Line Mode (Automated / Scripting)
```cmd
CMMC_Hashing.exe "D:\My_CMMC_Evidence" "C:\CMMC_Hash" "D:\My_CMMC_6Year_Archive"
