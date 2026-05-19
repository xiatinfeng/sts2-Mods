# ModSyncChecker i18n — Translation Guide

## How to Add a New Language

1. Copy `en.json` and rename it to your language code (e.g., `ja.json` for Japanese, `ko.json` for Korean).
2. Update the `_meta` section:
   - `language`: Human-readable language name
   - `code`: ISO 639-1 language code
   - `contributors`: Your name or username
3. Translate all values (keep keys unchanged).
4. Place the file in the `i18n/` folder next to the mod DLL.
5. Restart the game — the mod will auto-detect and load your translation.

## Supported Language Codes

| Code | Language | Status |
|------|----------|--------|
| en | English | ✅ Built-in |
| zh | 简体中文 | ✅ Built-in |
| ja | 日本語 | 📝 Community needed |
| ko | 한국어 | 📝 Community needed |
| fr | Français | 📝 Community needed |
| de | Deutsch | 📝 Community needed |
| es | Español | 📝 Community needed |
| ru | Русский | 📝 Community needed |
| pt | Português | 📝 Community needed |

## File Format

```json
{
  "_meta": {
    "language": "Language Name",
    "code": "xx",
    "contributors": ["Your Name"],
    "version": "1.2.1"
  },
  "KeyName": "Translated text here",
  "AnotherKey": "More text with {0} placeholders"
}
```

## Notes

- Do NOT translate keys (left side), only values (right side).
- Keep `{0}`, `{1}` etc. placeholders in the same order.
- The mod auto-detects language from Godot's locale settings.
- If a key is missing in your translation, it falls back to English.
