@echo off

SETLOCAL EnableDelayedExpansion 

set lP=%~dp0
set lP=%lP:~0,-1%

echo.
echo  Windows IoT Core Image Installer    

echo.
echo Finding the right disk type.

echo list disk  > %lp%\diskpart_script.txt
call diskpart /s %lP%\diskpart_script.txt > diskpart_output.txt

set /a CurrentDiskNumber=0
set NumberOfDisks=0

FOR /F "tokens=1,2,3" %%i IN (diskpart_output.txt) DO (

    if %%i == Disk (
        if %%j == ### (
            echo.
        ) else (

            set /A "NumberOfDisks+=1"

            echo.
            echo Checking Disk number %%j.

            if %%k == Online (
                echo disk number %%j is Online
                echo sel disk %%j > %lP%\diskpart_script.txt
                echo detail disk >> %lp%\diskpart_script.txt
                call diskpart /s %lP%\diskpart_script.txt > diskpart_output_1.txt

                FOR /F "tokens=1,3" %%a IN (diskpart_output_1.txt)  DO (
                    if %%a == Type  (
                        if %%b == SD (
                            echo disk number %%j is of type SD 
                            set /A CurrentDiskNumber=%%j
                            goto Install
                        ) else (
                            echo The Disk type is not SD. It is %%b. Move to next disk.
                        )
                    )
                )
            ) else (
                echo Disk number %%j is Offline. Move to next disk.
            )
        )
    )
)
goto Error

:Install
echo.
echo Installing IOTCore FFU on disk number %CurrentDiskNumber%.
echo.
echo.

if exist c:\flash.ffu (
    dism.exe /apply-image /ImageFile:c:\Flash.ffu /ApplyDrive:\\.\PhysicalDrive%CurrentDiskNumber%  /skipplatformcheck
) else if exist d:\flash.ffu (
    dism.exe /apply-image /ImageFile:d:\Flash.ffu /ApplyDrive:\\.\PhysicalDrive%CurrentDiskNumber%  /skipplatformcheck
) else if exist e:\flash.ffu (
    dism.exe /apply-image /ImageFile:e:\Flash.ffu /ApplyDrive:\\.\PhysicalDrive%CurrentDiskNumber%  /skipplatformcheck
) else if exist f:\flash.ffu (
    dism.exe /apply-image /ImageFile:f:\Flash.ffu /ApplyDrive:\\.\PhysicalDrive%CurrentDiskNumber%  /skipplatformcheck
) else (
    echo ERROR: Couldn't locate the FFU image "flash.ffu".
)

if exist c:\windows\system32\ACPITABL.dat (
    del c:\windows\system32\ACPITABL.dat
) else if exist d:\windows\system32\ACPITABL.dat (
    del d:\windows\system32\ACPITABL.dat
) else if exist e:\windows\system32\ACPITABL.dat (
    del e:\windows\system32\ACPITABL.dat
) else if exist f:\windows\system32\ACPITABL.dat (
    del f:\windows\system32\ACPITABL.dat
)

goto End

:Error
echo.
echo.
echo ERROR: Right disk type could not be found from total of %NumberOfDisks% disks checked. Exit
goto End

:End

pause