Option Explicit
' Launch the tray asynchronously so the scheduled task can finish.
Dim fso, shell, exe, command
Set fso = CreateObject("Scripting.FileSystemObject")
Set shell = CreateObject("WScript.Shell")
exe = fso.BuildPath(fso.GetParentFolderName(WScript.ScriptFullName), "NetCenter.exe")
command = """" & exe & """ --background"
shell.Run command, 0, False
