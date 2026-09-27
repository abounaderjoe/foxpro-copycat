# Translating the IDE

The IDE's text goes through a string catalog, gettext-style: the English text is the key, so anything not yet
translated still reads normally.

## Choosing the language

The IDE uses the operating system's display language. To pick one explicitly, set `JOEPRO_UI_CULTURE` before
starting it, for example `JOEPRO_UI_CULTURE=de`. For a regional variant such as `de-CH`, entries in `de-CH.json`
override those in `de.json`.

## Catalogs

- Built-in catalogs live in `src/JoePro.Ide/Localization/<culture>.json`. The IDE ships in English only; no other catalog is included (it would cover
  the menus, palette, designers and dialogs).
- A file with the same name in the user's settings folder (`%APPDATA%\Joe Pro\Localization` on Windows,
  `~/.config/Joe Pro/Localization` on Linux and macOS) overrides the built-in one. Use it to try a translation
  without rebuilding.

A catalog maps English text to its translation:

```json
{
  "_File": "_Datei",
  "New form": "Neues Formular"
}
```

Keep the access-key underscore: every entry needs the same number of `_` as its key, and within one menu no two
items may use the same access key. `AccessibilityTests` checks both for the built-in catalogs, and also checks
that every menu command and palette action has a translation and that there are no stale entries.

## Finding text that bypasses the catalog

Start the IDE with `JOEPRO_UI_CULTURE=qps-ploc`, the pseudo-locale. Every string that goes through the catalog
is shown accented and in brackets (`[Ñéw fórm]`), so any plain English text left on screen still has to be
routed through `Strings.T`.

## Coverage today

The menus and the command palette are routed through the catalog. Designer panels, dialogs and accessible names
are not yet, and remain in English.
