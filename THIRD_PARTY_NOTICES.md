# Third-party and upstream notice

This project is derived from `pedro-quiterio/PoeAncientsPriceHelper`.

At the time this fork was prepared, the upstream repository did not contain an
explicit software license. The MIT License in this distribution therefore
applies only to the original modifications and additions authored for this
fork. It does not grant rights to upstream code that the upstream copyright
holder has not licensed.

Before publicly redistributing the complete derivative work, obtain permission
from the upstream copyright holder or wait until the upstream project publishes
an explicit compatible license.

Bundled/runtime dependencies retain their own licenses, including .NET,
MahApps.Metro, SharpHook, Newtonsoft.Json, Tesseract and its language data.

## PoE2 atlas rumor reference data

`rumor_catalog.default.json` contains identifiers and map/rumor names derived from publicly extracted
Path of Exile 2 game-data tables (`EndgameMaps` and `WorldAreas`) maintained by the
`repoe-fork/dat-export` project. Path of Exile 2 names and game data are property of Grinding Gear Games.
Community ratings and practical notes are separately identified as community-maintained data.
## Optional Roboto fonts

The application can embed Roboto font files placed under `src/POE2LootLens/Assets/Fonts`.
When distributing a build that contains those files, retain the font copyright and license notices
that accompany the font package. The application falls back to Segoe UI when the resources are absent.
