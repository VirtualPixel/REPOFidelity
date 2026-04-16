@echo off
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
cd /d "%~dp0"
cl /LD /EHsc /O2 /MD /I"D:\downloads\streamline-sdk\external\ngx-sdk\include" ngx_bridge.cpp /Fe:ngx_bridge.dll /link d3d11.lib advapi32.lib user32.lib "D:\downloads\streamline-sdk\external\ngx-sdk\lib\Windows_x86_64\nvsdk_ngx_d.lib"
