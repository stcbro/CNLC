' Campus Auto Login - hidden launcher (no console window at all)
' Runs AutoLogin.ps1 via powershell.exe with a fully hidden window.
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

dirPath = fso.GetParentFolderName(WScript.ScriptFullName)
ps1Path = fso.BuildPath(dirPath, "AutoLogin.ps1")

exe = "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
If Not fso.FileExists(exe) Then exe = "powershell.exe"

cmd = """" & exe & """ -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File """ & ps1Path & """"
sh.Run cmd, 0, False
