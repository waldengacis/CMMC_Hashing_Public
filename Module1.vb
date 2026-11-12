Imports System
Imports System.IO
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Text

' =============================================================================
' Project : CMMC Artifact Hashing & Staging Utility
' Purpose : Automates local staging, pre-flight validation, and execution of the
'           DoD CIO CMMC Artifact Hashing Tool (Version 2.14 / Script v1.12)
'           for CMMC Level 2 & Level 3 Assessments (32 CFR 170.17 / 170.18).
' License : MIT License
' Author  : Walden Gajo
' =============================================================================
Module Module1

    Sub Main(args As String())
        Console.Title = "CMMC Assessment Artifact Hashing Utility (DoD Guide v2.14)"
        PrintBanner()

        Dim sourceRoot As String = ""
        Dim workingRoot As String = ""
        Dim archiveRoot As String = ""

        ' 1. Accept CLI Arguments or Prompt Interactively
        If args IsNot Nothing AndAlso args.Length >= 2 Then
            sourceRoot = CleanPathInput(args(0))
            workingRoot = CleanPathInput(args(1))
            If args.Length >= 3 Then
                archiveRoot = CleanPathInput(args(2))
            End If
        Else
            sourceRoot = PromptForDirectory("Enter the SOURCE directory containing your CMMC artifacts" & Environment.NewLine &
                                            "(Tip: You can drag-and-drop the folder here or use 'Copy as path'):", mustExist:=True)

            Console.WriteLine()
            Console.WriteLine("Enter a local WORKING directory for staging and hashing")
            Console.Write("Press [ENTER] to use default (C:\CMMC_Hash): ")
            Dim workInput As String = CleanPathInput(Console.ReadLine())
            workingRoot = If(String.IsNullOrWhiteSpace(workInput), "C:\CMMC_Hash", workInput)

            Console.WriteLine()
            Console.WriteLine("Optional: Enter a long-term ARCHIVE directory (e.g., OneDrive/SharePoint/NAS)")
            Console.Write("to copy and lock (Read-Only) the final 6-year retention package, or press [ENTER] to skip: ")
            archiveRoot = CleanPathInput(Console.ReadLine())
        End If

        Dim localArtifactRoot As String = Path.Combine(workingRoot, "Audit_Ready")
        Dim localOutputRoot As String = Path.Combine(workingRoot, "Output")

        ' Prevent user from setting Working Directory inside the Source Directory
        If localArtifactRoot.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase) Then
            WriteColor("ERROR: The local working directory cannot be inside your source artifact directory.", ConsoleColor.Red)
            PauseAndExit(1)
        End If

        ' 2. Reset and Create Clean Local Staging & Output Directories
        Try
            If Directory.Exists(localArtifactRoot) Then Directory.Delete(localArtifactRoot, True)
            If Directory.Exists(localOutputRoot) Then Directory.Delete(localOutputRoot, True)
            Directory.CreateDirectory(localArtifactRoot)
            Directory.CreateDirectory(localOutputRoot)
        Catch ex As Exception
            WriteColor("ERROR initializing local working directories: " & ex.Message, ConsoleColor.Red)
            PauseAndExit(1)
        End Try

        ' 3. Scan Source Artifacts & Pre-Check for Duplicates / 0-Byte Files / System Clutter
        Console.WriteLine()
        Console.WriteLine("Scanning and validating source artifacts...")

        Dim allSourceFiles As String() = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories)
        Dim validFiles As New List(Of String)()
        Dim fileNamesSeen As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim duplicateErrors As New List(Of String)()
        Dim zeroByteFiles As New List(Of String)()

        For Each srcFile As String In allSourceFiles
            Dim fi As New FileInfo(srcFile)

            ' Skip hidden/system files and Office temporary lock files (~$*.docx, desktop.ini, Thumbs.db)
            If (fi.Attributes And FileAttributes.Hidden) = FileAttributes.Hidden OrElse
               (fi.Attributes And FileAttributes.System) = FileAttributes.System OrElse
               fi.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) OrElse
               fi.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase) OrElse
               fi.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase) OrElse
               fi.Name.StartsWith("~$") Then
                Console.WriteLine("  [Skipped Non-Artifact File] " & fi.Name)
                Continue For
            End If

            validFiles.Add(srcFile)

            If fi.Length = 0 Then
                zeroByteFiles.Add(srcFile)
            End If

            If fileNamesSeen.ContainsKey(fi.Name) Then
                duplicateErrors.Add(String.Format("  Duplicate Filename '{0}':" & Environment.NewLine &
                                                  "    1) {1}" & Environment.NewLine &
                                                  "    2) {2}", fi.Name, fileNamesSeen(fi.Name), srcFile))
            Else
                fileNamesSeen.Add(fi.Name, srcFile)
            End If
        Next

        If validFiles.Count = 0 Then
            WriteColor("ERROR: No valid artifact files were found in: " & sourceRoot, ConsoleColor.Red)
            PauseAndExit(1)
        End If

        ' Warn on 0-byte files (produces empty SHA-256 hash E3B0C442... or indicates unhydrated cloud file)
        If zeroByteFiles.Count > 0 Then
            WriteColor(Environment.NewLine & "WARNING: " & zeroByteFiles.Count.ToString() & " Zero-Byte (0 KB) file(s) detected!", ConsoleColor.Yellow)
            Console.WriteLine("Empty files produce the generic SHA-256 hash E3B0C442... and may indicate")
            Console.WriteLine("cloud files that have not finished downloading locally:")
            For Each z As String In zeroByteFiles
                Console.WriteLine("  * " & z)
            Next
            Console.Write(Environment.NewLine & "Do you want to continue anyway? (Y/N): ")
            If Not Console.ReadLine().Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) Then
                PauseAndExit(1)
            End If
        End If

        ' Handle Duplicate Filenames Across Subfolders (prevents Compress-Archive collision)
        Dim autoPrefixDuplicates As Boolean = False
        If duplicateErrors.Count > 0 Then
            WriteColor(Environment.NewLine & "WARNING: Duplicate filenames detected across different subfolders!", ConsoleColor.Yellow)
            Console.WriteLine("Because the DoD script flattens subfolders into the root of HashedArtifacts.zip,")
            Console.WriteLine("files with identical names in different folders will collide during zipping:")
            For Each d As String In duplicateErrors
                Console.WriteLine(d)
            Next
            Console.WriteLine()
            Console.WriteLine("How would you like to handle duplicate filenames?")
            Console.WriteLine("  [1] Auto-prefix staged files with their subfolder path (Recommended - leaves originals untouched)")
            Console.WriteLine("  [2] Abort now so I can rename the original files manually")
            Console.Write("Select [1] or [2]: ")
            Dim choice As String = Console.ReadLine().Trim()
            If choice = "1" Then
                autoPrefixDuplicates = True
            Else
                PauseAndExit(1)
            End If
        End If

        ' 4. Copy & Stage Artifacts to Local Working Directory
        Dim stagedCount As Integer = 0
        For Each srcFile As String In validFiles
            Dim relativePath As String = srcFile.Substring(sourceRoot.Length).TrimStart("\"c, "/"c)
            Dim destFile As String = Path.Combine(localArtifactRoot, relativePath)

            If autoPrefixDuplicates Then
                Dim relDir As String = Path.GetDirectoryName(relativePath)
                If Not String.IsNullOrEmpty(relDir) Then
                    Dim safePrefix As String = relDir.Replace("\", "_").Replace("/", "_").Replace(" ", "_")
                    Dim newFileName As String = safePrefix & "_" & Path.GetFileName(srcFile)
                    destFile = Path.Combine(localArtifactRoot, relDir, newFileName)
                End If
            End If

            Dim destDir As String = Path.GetDirectoryName(destFile)
            If Not Directory.Exists(destDir) Then Directory.CreateDirectory(destDir)

            File.Copy(srcFile, destFile, True)
            File.SetAttributes(destFile, FileAttributes.Normal)
            stagedCount += 1
        Next

        WriteColor("Successfully staged " & stagedCount.ToString() & " artifact files to: " & localArtifactRoot, ConsoleColor.Cyan)

        ' 5. Write Syntax-Corrected DoD ArtifactHash.ps1 (Version 1.12) to Output Folder
        Dim psScriptPath As String = Path.Combine(localOutputRoot, "ArtifactHash.ps1")
        File.WriteAllText(psScriptPath, GetCleanDoDScript(), Encoding.ASCII)
        Console.WriteLine("Generated DoD script at: " & psScriptPath)

        ' 6. Detect PowerShell 7 (pwsh.exe) or Fallback to Windows PowerShell 5.1
        Dim pwshExecutable As String = ResolvePowerShellExecutable(psScriptPath)

        Dim psi As New ProcessStartInfo()
        psi.FileName = pwshExecutable
        psi.WorkingDirectory = localOutputRoot
        psi.Arguments = String.Format("-NoProfile -ExecutionPolicy ByPass -File ""{0}"" -ArtifactRootDirectory ""{1}"" -ArtifactOutputDirectory ""{2}""",
                                      psScriptPath, localArtifactRoot, localOutputRoot)
        psi.UseShellExecute = False

        Console.WriteLine(Environment.NewLine & "Executing DoD ArtifactHash.ps1..." & Environment.NewLine)
        Try
            Using proc As Process = Process.Start(psi)
                proc.WaitForExit()
                If proc.ExitCode <> 0 Then
                    WriteColor(Environment.NewLine & "ERROR: PowerShell script exited with code " & proc.ExitCode.ToString(), ConsoleColor.Red)
                    PauseAndExit(proc.ExitCode)
                End If
            End Using
        Catch ex As Exception
            WriteColor("ERROR executing PowerShell: " & ex.Message, ConsoleColor.Red)
            PauseAndExit(1)
        End Try

        ' 7. Generate eMASS Handoff Summary & Optionally Archive as Read-Only
        CreateEmassSummaryAndArchive(localOutputRoot, archiveRoot)

        ' 8. Optional Cleanup of Temporary Local Staging Folder
        Console.Write(Environment.NewLine & "Delete the temporary unzipped staging folder (" & localArtifactRoot & ")? (Y/N): ")
        If Console.ReadLine().Trim().Equals("Y", StringComparison.OrdinalIgnoreCase) Then
            Try
                Directory.Delete(localArtifactRoot, True)
                Console.WriteLine("Cleaned up temporary staging folder: " & localArtifactRoot)
            Catch ex As Exception
                WriteColor("Could not delete staging folder: " & ex.Message, ConsoleColor.Yellow)
            End Try
        End If

        PauseAndExit(0)
    End Sub

    ''' <summary>
    ''' Finds PowerShell 7 (pwsh.exe) across standard MSI, x86, AppData, and WindowsApps locations,
    ''' or falls back to Windows PowerShell 5.1 if PowerShell 7 is not installed.
    ''' </summary>
    Private Function ResolvePowerShellExecutable(psScriptPath As String) As String
        Dim candidatePaths As String() = {
            "C:\Program Files\PowerShell\7\pwsh.exe",
            "C:\Program Files (x86)\PowerShell\7\pwsh.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs\PowerShell\7\pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\WindowsApps\pwsh.exe"),
            "pwsh.exe"
        }

        For Each candidate As String In candidatePaths
            If CanExecuteProcess(candidate, "-v") Then
                Console.WriteLine("Using PowerShell 7: " & candidate)
                Return candidate
            End If
        Next

        ' Fallback to built-in Windows PowerShell 5.1
        Dim ps5Path As String = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\v1.0\powershell.exe")
        WriteColor("PowerShell 7 (pwsh.exe) not found. Falling back to Windows PowerShell 5.1...", ConsoleColor.Yellow)
        Dim scriptText As String = File.ReadAllText(psScriptPath)
        scriptText = scriptText.Replace("-lt 7", "-lt 5")
        File.WriteAllText(psScriptPath, scriptText, Encoding.ASCII)
        Return ps5Path
    End Function

    Private Function CanExecuteProcess(fileName As String, arguments As String) As Boolean
        Try
            Dim psi As New ProcessStartInfo()
            psi.FileName = fileName
            psi.Arguments = arguments
            psi.UseShellExecute = False
            psi.RedirectStandardOutput = True
            psi.RedirectStandardError = True
            psi.CreateNoWindow = True
            Using p As Process = Process.Start(psi)
                p.WaitForExit(3000)
                Return (p.ExitCode = 0)
            End Using
        Catch
            Return False
        End Try
    End Function

    ''' <summary>
    ''' Extracts the eMASS submission values, creates a C3PAO handoff summary text file,
    ''' and optionally copies and locks (Read-Only) the outputs into an archive directory.
    ''' </summary>
    Private Sub CreateEmassSummaryAndArchive(localOutputRoot As String, archiveRoot As String)
        Dim artifactsLog As String = Path.Combine(localOutputRoot, "CMMCAssessmentArtifacts.log")
        Dim masterHashLog As String = Path.Combine(localOutputRoot, "CMMCAssessmentLogHash.log")
        Dim zipArchive As String = Path.Combine(localOutputRoot, "HashedArtifacts.zip")

        If Not File.Exists(masterHashLog) Then Return

        Dim masterHashValue As String = "NOT_FOUND"
        For Each line As String In File.ReadAllLines(masterHashLog)
            Dim trimmed As String = line.Trim()
            If trimmed.StartsWith("SHA256", StringComparison.OrdinalIgnoreCase) Then
                Dim parts As String() = trimmed.Split(New Char() {" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length >= 2 Then
                    masterHashValue = parts(1).Trim()
                    Exit For
                End If
            End If
        Next

        Dim handoffFile As String = Path.Combine(localOutputRoot, "eMASS_C3PAO_Handoff.txt")
        Dim sb As New StringBuilder()
        sb.AppendLine("======================================================================")
        sb.AppendLine("         CMMC eMASS ARTIFACT HASHING SUBMISSION DETAILS               ")
        sb.AppendLine("======================================================================")
        sb.AppendLine("Generated Timestamp            : " & DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
        sb.AppendLine("Hashing Algorithm              : SHA-256")
        sb.AppendLine("eMASS Field [Hashed Data List] : CMMCAssessmentArtifacts.log")
        sb.AppendLine("eMASS Field [Hash Value]       : " & masterHashValue)
        sb.AppendLine("----------------------------------------------------------------------")
        sb.AppendLine("CUSTODY & RETENTION INSTRUCTIONS (32 CFR 170.17 / 32 CFR 170.18):")
        sb.AppendLine("1. PROVIDE TO C3PAO / ASSESSOR :")
        sb.AppendLine("   - CMMCAssessmentArtifacts.log")
        sb.AppendLine("   - CMMCAssessmentLogHash.log")
        sb.AppendLine("2. RETAIN INTERNAL TO OSA ONLY (DO NOT SEND TO ASSESSOR):")
        sb.AppendLine("   - HashedArtifacts.zip (Retain securely for 6 years from assessment)")
        sb.AppendLine("======================================================================")

        File.WriteAllText(handoffFile, sb.ToString(), Encoding.ASCII)

        Console.WriteLine()
        WriteColor(sb.ToString(), ConsoleColor.Green)
        Console.WriteLine("Output files saved in: " & localOutputRoot)

        If Not String.IsNullOrWhiteSpace(archiveRoot) Then
            Try
                Directory.CreateDirectory(archiveRoot)
                Dim filesToArchive As String() = {artifactsLog, masterHashLog, zipArchive, handoffFile}
                For Each srcFile As String In filesToArchive
                    If File.Exists(srcFile) Then
                        Dim destFile As String = Path.Combine(archiveRoot, Path.GetFileName(srcFile))
                        If File.Exists(destFile) Then
                            File.SetAttributes(destFile, FileAttributes.Normal)
                        End If
                        File.Copy(srcFile, destFile, True)
                        File.SetAttributes(destFile, FileAttributes.ReadOnly)
                    End If
                Next
                WriteColor("Archived and locked (Read-Only) 6-year retention files in: " & archiveRoot, ConsoleColor.Cyan)
            Catch ex As Exception
                WriteColor("WARNING: Failed to copy files to archive directory: " & ex.Message, ConsoleColor.Yellow)
            End Try
        End If
    End Sub

    Private Function PromptForDirectory(promptMessage As String, mustExist As Boolean) As String
        While True
            Console.WriteLine(promptMessage)
            Console.Write("> ")
            Dim input As String = CleanPathInput(Console.ReadLine())
            If Not String.IsNullOrWhiteSpace(input) Then
                If Not mustExist OrElse Directory.Exists(input) Then
                    Return input
                End If
                WriteColor("Directory not found: " & input & ". Please try again.", ConsoleColor.Yellow)
            End If
        End While
        Return ""
    End Function

    Private Function CleanPathInput(rawInput As String) As String
        If String.IsNullOrWhiteSpace(rawInput) Then Return ""
        Return rawInput.Trim().Trim(""""c, "'"c).TrimEnd("\"c, "/"c)
    End Function

    Private Sub PrintBanner()
        Console.ForegroundColor = ConsoleColor.Cyan
        Console.WriteLine(New String("="c, 70))
        Console.WriteLine("   CMMC Level 2 / Level 3 Artifact Staging & Hashing Utility")
        Console.WriteLine("   Compliant with DoD CIO CMMC Hashing Guide v2.14 (Script v1.12)")
        Console.WriteLine(New String("="c, 70))
        Console.ResetColor()
        Console.WriteLine()
    End Sub

    Private Sub WriteColor(message As String, color As ConsoleColor)
        Console.ForegroundColor = color
        Console.WriteLine(message)
        Console.ResetColor()
    End Sub

    Private Sub PauseAndExit(exitCode As Integer)
        Console.WriteLine(Environment.NewLine & "Press any key to exit...")
        Console.ReadKey(True)
        Environment.Exit(exitCode)
    End Sub

    ''' <summary>
    ''' Returns the official DoD Appendix A PowerShell Script (Version 1.12)
    ''' with PDF OCR typos and unclosed comment block errors fixed.
    ''' </summary>
    Private Function GetCleanDoDScript() As String
        Dim sb As New StringBuilder()
        sb.AppendLine("<#")
        sb.AppendLine(".SYNOPSIS")
        sb.AppendLine("Hash artifacts for a CMMC Assessment to maintain integrity in the event")
        sb.AppendLine("any files are needed in the future.")
        sb.AppendLine(".DESCRIPTION")
        sb.AppendLine("This script will recursively evaluate all files in a local or UNC path.")
        sb.AppendLine("Each file will be hashed and written to a text file, and all of the files")
        sb.AppendLine("will be zipped together to preserve their hashes. Additionally, the")
        sb.AppendLine("hash record is hashed to preserve the integrity of the output.")
        sb.AppendLine("#>")
        sb.AppendLine("param(")
        sb.AppendLine("    [Parameter(Mandatory=$false)][string]$ArtifactRootDirectory = "".\"",")
        sb.AppendLine("    [Parameter(Mandatory=$false)][string]$ArtifactOutputDirectory = "".\""")
        sb.AppendLine(")")
        sb.AppendLine("#VERSION 1.12")
        sb.AppendLine("$version = ""1.12""")
        sb.AppendLine("if ($PSVersionTable.PSVersion.Major -lt 7) {")
        sb.AppendLine("    Write-Error ""This script requires PowerShell 7.0 or higher. Current version: $($PSVersionTable.PSVersion)""")
        sb.AppendLine("    exit 1")
        sb.AppendLine("}")
        sb.AppendLine("function GetFileHashes ([string] $rootLocation, [boolean] $isDirectory) {")
        sb.AppendLine("    if ($isDirectory) {")
        sb.AppendLine("        $hashList = Get-ChildItem -Path $rootLocation -Recurse -Force -File | Get-FileHash")
        sb.AppendLine("    } else {")
        sb.AppendLine("        $hashList = Get-FileHash $rootLocation")
        sb.AppendLine("    }")
        sb.AppendLine("    return $hashList")
        sb.AppendLine("}")
        sb.AppendLine("function WriteASCIIFile ([string] $filePath, [object] $fileContent) {")
        sb.AppendLine("    Out-File -FilePath $filePath -Force -Encoding ASCII -InputObject $fileContent -Width 1024")
        sb.AppendLine("}")
        sb.AppendLine("function VerifyLocationExist ([string] $location) {")
        sb.AppendLine("    try {")
        sb.AppendLine("        $doesExist = Test-Path $location")
        sb.AppendLine("        if (-Not $doesExist) {")
        sb.AppendLine("            ECHO ""Location $location does not exist""")
        sb.AppendLine("            throw")
        sb.AppendLine("        }")
        sb.AppendLine("    } catch {")
        sb.AppendLine("        ECHO ""The program failed to evaluate the path. Perhaps you specified an incorrectly formatted command line parameter?""")
        sb.AppendLine("        EXIT")
        sb.AppendLine("    }")
        sb.AppendLine("}")
        sb.AppendLine("function IsDirectory ([string] $location) {")
        sb.AppendLine("    $isDirectory = (Get-Item $location) -is [System.IO.DirectoryInfo]")
        sb.AppendLine("    return $isDirectory")
        sb.AppendLine("}")
        sb.AppendLine("function ZipHashedFiles ([array] $fileList, [string] $outputDirectory) {")
        sb.AppendLine("    $zipFilePath = Join-Path $outputDirectory ""HashedArtifacts.zip""")
        sb.AppendLine("    Compress-Archive -Path $fileList.Path -DestinationPath $zipFilePath -Force")
        sb.AppendLine("    ECHO ""Created zip archive at $zipFilePath""")
        sb.AppendLine("}")
        sb.AppendLine("ECHO ""Artifact Hashing Script Version $version""")
        sb.AppendLine("ECHO ""Verifying existence of $ArtifactRootDirectory""")
        sb.AppendLine("VerifyLocationExist $ArtifactRootDirectory")
        sb.AppendLine("ECHO ""Verifying existence of $ArtifactOutputDirectory""")
        sb.AppendLine("VerifyLocationExist $ArtifactOutputDirectory")
        sb.AppendLine("$artifactLocationIsDir = IsDirectory($ArtifactRootDirectory)")
        sb.AppendLine("$logFileLocationIsDir = IsDirectory($ArtifactOutputDirectory)")
        sb.AppendLine("if ($logFileLocationIsDir) {")
        sb.AppendLine("    $logFileLocation = $ArtifactOutputDirectory + ""\CMMCAssessmentArtifacts.log""")
        sb.AppendLine("    $hashedLogFileLocation = $ArtifactOutputDirectory + ""\CMMCAssessmentLogHash.log""")
        sb.AppendLine("} else {")
        sb.AppendLine("    $endOfString = $ArtifactOutputDirectory.LastIndexOf(""\"")")
        sb.AppendLine("    $logFileLocation = $ArtifactOutputDirectory.Substring(0, $endOfString) + ""\CMMCAssessmentArtifacts.log""")
        sb.AppendLine("    $hashedLogFileLocation = $ArtifactOutputDirectory.Substring(0, $endOfString) + ""\CMMCAssessmentLogHash.log""")
        sb.AppendLine("}")
        sb.AppendLine("$hashedFiles = GetFileHashes $ArtifactRootDirectory $artifactLocationIsDir")
        sb.AppendLine("ECHO ""Writing artifact file listing to $logFileLocation""")
        sb.AppendLine("WriteASCIIFile $logFileLocation $hashedFiles")
        sb.AppendLine("$hashTheHash = GetFileHashes $logFileLocation $false")
        sb.AppendLine("ECHO ""Writing hashed value of artifact file listing to $hashedLogFileLocation""")
        sb.AppendLine("WriteASCIIFile $hashedLogFileLocation $hashTheHash")
        sb.AppendLine("ECHO ""Creating zip archive of hashed files""")
        sb.AppendLine("ZipHashedFiles $hashedFiles $ArtifactOutputDirectory")
        sb.AppendLine("ECHO ""SCRIPT COMPLETE""")
        Return sb.ToString()
    End Function

End Module
