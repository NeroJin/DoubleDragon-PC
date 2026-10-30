@echo off
rem 以繁体中文界面启动 Double Dragon
rem 8 = XLanguage::kTChinese，游戏会读取 game\ui\zh-cht\ 下的串表与图片
start "" "%~dp0build\Release\DoubleDragon.exe" --user_language=8 %*
