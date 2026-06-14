# Application fonts

POE2 LootLens supports bundled Roboto fonts without installing them into Windows.
Place these files in this directory before building:

- `Roboto-Regular.ttf`
- `Roboto-Medium.ttf`
- `Roboto-SemiBold.ttf`
- `Roboto-Bold.ttf`

The project embeds matching `*.ttf` files as WPF resources. The main interface uses:

- Regular for body text and input fields;
- Medium for compact labels;
- SemiBold for buttons and section headings;
- Bold for window titles, module names and important values.

`Roboto-Black.ttf` and `Roboto-Thin.ttf` are intentionally unused because they reduce readability in the current compact dark interface.

If the files are absent, the application falls back to Segoe UI and still builds and runs.
