; Custom NSIS hooks, inserted by Tauri's own template.

; Releases up to 0.1.2 wrote the bundled scripts as `engines\tts` / `engines\kws-asr` files. Remove that stray
; file before the resource copy creates the folders — only when it is a file, never a directory: the directory
; is the downloaded engine. `engines\tts\*.*` matches only a directory, which is how NSIS tells them apart.
!macro NSIS_HOOK_PREINSTALL
  ${If} ${FileExists} "$INSTDIR\engines\tts"
  ${AndIfNot} ${FileExists} "$INSTDIR\engines\tts\*.*"
    Delete "$INSTDIR\engines\tts"
  ${EndIf}
  ${If} ${FileExists} "$INSTDIR\engines\kws-asr"
  ${AndIfNot} ${FileExists} "$INSTDIR\engines\kws-asr\*.*"
    Delete "$INSTDIR\engines\kws-asr"
  ${EndIf}
  CreateDirectory "$INSTDIR\engines\tts"
  CreateDirectory "$INSTDIR\engines\kws-asr"
!macroend
