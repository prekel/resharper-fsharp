:<<"::CMDLITERAL"
@PUSHD "%~dp0..\..\rider-fsharp"
@CALL "gradlew.bat" "rdgenPwc"
@POPD  
@GOTO :EOF
::CMDLITERAL
pushd "../../rider-fsharp"
./gradlew rdgenPwc
popd
