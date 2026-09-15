# models

3D model inputs (`.glb` / `.gltf`) that cards bind to. Everything in this folder except this file is
gitignored: models are third-party licensed (the first one is "Sketchfab Standard", which does not allow
redistributing the model file), so each machine supplies its own copies.

A card stores the model's workspace-relative path and its SHA-256. If the file on disk no longer matches
that hash the card shows **model changed** instead of silently baking a different model; re-bind it from
the card's Details tab (Rehash) once the change is intended.
