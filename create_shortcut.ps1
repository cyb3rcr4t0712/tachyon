$WshShell = New-Object -comObject WScript.Shell
$Shortcut = $WshShell.CreateShortcut("$env:USERPROFILE\Desktop\Antigravity - Profile 2.lnk")
$Shortcut.TargetPath = "C:\Windows\System32\cmd.exe"
$Shortcut.Arguments = '/c "set USERPROFILE=X:\Antigravity\Antigravity_Profile_2&& start """" "X:\Antigravity\Antigravity\Antigravity.exe" --user-data-dir="X:\Antigravity\Antigravity_Profile_2" --password-store=basic"'
$Shortcut.WindowStyle = 7
$Shortcut.IconLocation = "X:\Antigravity\Antigravity\Antigravity.exe, 0"
$Shortcut.Save()
