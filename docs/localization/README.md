# Localization reference material

`strings.de.rc5-reference.json` preserves the German translation catalog supplied
in `TuneIn-1.2.8-rc5-de.1-autor-integration.zip`, based on RC5 commit `3c921f9`.
It is reference material for [localization issue #10](https://github.com/oconnoradv/EmbyTuneIn/issues/10),
not a runtime resource or a shipped translation.

The catalog contains English source text, German translations, message key paths,
and status labels. Review the translations, placeholders, accessibility text,
and current source strings before adapting them to the eventual localization
format. The supplied Swiss variant replaced `ß` with `ss`; it requires separate
locale and linguistic review rather than automatic conversion.

The supplied patch hard-coded German for every user and did not implement Emby
language selection or English fallback. Do not apply it as the localization
implementation; preserve the existing English interface until that work is ready.
