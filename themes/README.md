# Sample themes

Color themes for the full-screen `fuseraft` REPL (the same format anchor uses), to use as they are or to start your own from.

| Theme                        | For               | Colors                                                  |
| ---------------------------- | ----------------- | ------------------------------------------------------- |
| [`ember`](ember.json)        | dark backgrounds  | Embers on charcoal: `rust`'s warmth in 24-bit color.    |
| [`tide`](tide.json)          | dark backgrounds  | Sea foam, deep water and coral.                         |
| [`forest`](forest.json)      | dark backgrounds  | Moss, bark and berries.                                 |
| [`neon`](neon.json)          | dark backgrounds  | Synthwave: hot pink and cyan on a purple grid.          |
| [`phosphor`](phosphor.json)  | dark backgrounds  | An amber CRT, in 256-colors only.                       |
| [`paper`](paper.json)        | light backgrounds | Ink on paper: deep colors that stay readable on white.  |

To use one, copy its file into `~/.fuseraft/themes/`, then run `/theme <name>` in the full-screen REPL:

```sh
mkdir -p ~/.fuseraft/themes
curl -fsSL https://raw.githubusercontent.com/fuseraft/fuseraft-cli/main/themes/ember.json -o ~/.fuseraft/themes/ember.json
```

Each one paints the full screen in its own background, with shaded code blocks, so it looks the same
whatever your terminal's colors are. All of them except `phosphor` use hex colors. On a terminal that doesn't show 24-bit color, fuseraft
draws each hex color as the closest 256-color.

[Making a theme](https://fuseraft.ai/anchor/guides/themes/) explains the file format. Each
file here is checked by the tests, so it has to load without warnings.
