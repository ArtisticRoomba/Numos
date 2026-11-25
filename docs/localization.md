# Localization

```bash
dotnet run --project eng/Numos.LocaleLint
dotnet run --project eng/Numos.LocaleLint -- --strict --culture ru --untranslated
```

| Option             | Effect                                                                                |
|--------------------|---------------------------------------------------------------------------------------|
| `--root <dir>`     | The repo root. The default is the nearest parent directory that holds `Numos.slnx`.   |
| `--culture <name>` | Reports only that language. `en` also gets the findings about code and `credits.ftl`. |
| `--strict`         | Warnings fail the run. Notes never do.                                                |
| `--untranslated`   | Also prints LOC201 notes.                                                             |

The exit code is 0 when clean, 1 when a finding fails the run, and 2 for a usage or setup error. Each finding is one
line
in the form `path:line:column: severity rule: message`, so IDEs and CI can link it. The test
`ShippedLocales_HaveNoErrors` in `tests/Numos.LocaleLint.Tests` runs the linter on the real files. A plain `dotnet test`
therefore fails on any error.

## Rules

The linter compares messages part by part. The value and each attribute are separate parts. A part's variables are the
union over all select variants, including selectors and function arguments.

| Rule   | Severity | Check                                                                                                                                |
|--------|----------|--------------------------------------------------------------------------------------------------------------------------------------|
| LOC001 | Error    | A Fluent parse error in any `.ftl` file, including `credits.ftl`.                                                                    |
| LOC002 | Error    | A message or term is defined twice in one language.                                                                                  |
| LOC003 | Error    | A message in `en` is missing from a language.                                                                                        |
| LOC004 | Error    | A message in a language does not exist in `en`.                                                                                      |
| LOC005 | Error    | A message's attributes, or whether it has a value, differ from `en`.                                                                 |
| LOC006 | Error    | A part's variable set differs from `en`.                                                                                             |
| LOC007 | Error    | A reference to a message, attribute, or term that does not exist in that language.                                                   |
| LOC008 | Error    | The ImGui `##id` suffix differs from `en`. One side having none counts.                                                              |
| LOC009 | Error    | A character has no glyph in the fonts the viewer loads.                                                                              |
| LOC010 | Error    | A key used in viewer code does not exist in `en`.                                                                                    |
| LOC011 | Error    | Code does not pass a variable that the `en` message uses.                                                                            |
| LOC012 | Error    | `credits.ftl` is missing, or has a duplicate id, no value, no `.en`, a non-plain-text value, or an attribute that names no language. |
| LOC101 | Warning  | A message is in a different file than in `en`.                                                                                       |
| LOC102 | Warning  | Single-line text ends in different sentence punctuation than `en`.                                                                   |
| LOC103 | Warning  | A message id appears in no string literal in viewer source, so it looks unused.                                                      |
| LOC104 | Warning  | A file has a byte order mark or CRLF line endings.                                                                                   |
| LOC105 | Warning  | A select on a number lacks a plural category the language uses.                                                                      |
| LOC106 | Warning  | Code passes an argument that the `en` message does not use.                                                                          |
| LOC201 | Note     | Text with at least one letter is identical to `en`. Printed only with `--untranslated`.                                              |

The code rules read `Locale.GetString` calls from the viewer's C# files. They handle a key that is a string literal or a
conditional of literals, and skip any other key expression. A key held in a table is covered by
`WorkspaceLocaleKeyTests`. When a call passes an argument that is not a tuple with a literal name, the linter skips
LOC011 and LOC106 for that call.
