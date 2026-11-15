# Languages
The panel comes in 13 languages:

| Code | Language | Code | Language |
| --- | --- | --- | --- |
| `en` | English | `nl` | Nederlands |
| `de` | Deutsch | `pl` | Polski |
| `es` | Español | `pt-BR` | Português (Brasil) |
| `fr` | Français | `ru` | Русский |
| `hu` | Magyar | `tr` | Türkçe |
| `lv` | Latviešu | `uk` | Українська |
| | | `zh-Hans` | 简体中文 |

## Which Language People See
1. The language they picked with the picker in the footer (remembered in their browser).
2. Else the site's default language, set under **Site settings → Site look** (Auto = each browser's own).
3. Else their browser's language, if the panel has it, else English.

Names that come from the servers, like map, mode and setting names, are shown as the server sends them. Country names come from the browser, in the page's language.

## Changing or Adding a Translation
The texts are in `src/GmmPanel/lang/<code>.json`: each key is the English text, and its value is the translation.

1. Edit the file, or copy `en.json` to a new `<code>.json` for a new language and add it to `Lang.Languages` in `src/GmmPanel/Data/Lang.cs`.
2. Check that every file has every key:
   ```bash
   dotnet run --project tools/LangTool -- check
   ```
3. `missing` lists texts in the pages that no file has yet, and `add rows.json` adds new texts to every file at once:
   ```json
   { "Send reply": { "de": "Antwort senden", "es": "Enviar respuesta", "fr": "Envoyer la réponse" } }
   ```

Please send a pull request with your translation. See [Contributing](https://github.com/nickj609/GameModeManager-WebPanel/blob/main/CONTRIBUTING.md).
