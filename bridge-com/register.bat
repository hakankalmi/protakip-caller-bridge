@echo off
rem ProTakip Caller Id Bridge — CID v5 COM DLL kayıt scripti.
rem Bu dosyaya SAĞ TIK → "Yönetici olarak çalıştır" ile çalıştırın.
rem Bir kez çalıştırmanız yeterli; bridge bundan sonra COM'u tanıyacak.

pushd "%~dp0"
echo Registering cidv5callerid.dll...
regsvr32 /s "cidv5callerid.dll"
if errorlevel 1 (
    echo.
    echo HATA: Kayit basarisiz. Bu dosyayi YONETICI olarak calistirin.
    pause
    exit /b 1
)
echo.
echo cidv5callerid.dll basariyla kayit edildi.
echo Artik ProTakipCallerBridgeCom.exe'yi calistirabilirsiniz.
pause
popd
