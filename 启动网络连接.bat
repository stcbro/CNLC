@echo off
rem Campus Network Auto Login - delegate to hidden VBS launcher (no black window)
start "" wscript.exe "%~dp0launch_login.vbs"
