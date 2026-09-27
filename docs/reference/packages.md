# Packages

A package is a library project (class libraries, programs, forms, reports) that other projects use. Packages are
published to a registry and installed into a project's `packages/` folder.

## Using packages

The project file lists them:

```yaml
registry: ../registry            # a folder, or an http(s) location
dependencies:
  - name: uikit
    version: "^1.2.0"            # 1.2.0 exactly, ^1.2 compatible (same major), ~1.2.3 same minor, >=1.0, *
  - name: reports
    version: "*"
    source: ../reports-lib       # a folder with a project, or git+https://…/repo.git#v1.0
```

| Command | What it does |
|---|---|
| `joepro add uikit` | Adds the newest version from the registry as `^x.y.z` and installs it |
| `joepro add uikit@1.2.0` | Pins an exact version (any range works after `@`) |
| `joepro add reports --source ../reports-lib` | Uses a local folder (or `git+URL#tag`) |
| `joepro remove uikit` | Removes it and its files |
| `joepro restore` | Installs what the project lists, using the versions in `packages.lock.json` |
| `joepro restore --update` | Moves to the newest versions the ranges allow and rewrites the lock file |

The Project Manager's **Packages…** button does the same. `packages.lock.json` records the exact version, source and
SHA-256 of each package, so every machine installs the same files; commit it with the project. Restore fails if a
registry package no longer matches its recorded hash, and when two packages need incompatible versions of a third.

Opening or building a project adds its package folders to `SET PATH`, so `SET CLASSLIB TO uikit.jpclass` or
`DO util` finds package files. `BUILD APP` and `BUILD EXE` include the installed packages in the application.

## Publishing

Give the library project a name and a version (`version: number:` in the project file), then run
`joepro publish --registry <folder>`, or use **Publish** in the Packages dialog. The package holds the project's
included files, its header files and a `joepro-package.json` manifest with its registry dependencies. A published
version cannot be replaced; publish a new version instead. `joepro pack` writes the `.jppkg` without publishing.

## Registry layout (v1)

A registry is static files, so any folder or web server can host one:

```
index.json                       {"uikit": ["1.0.0", "1.1.0"]}
uikit/1.0.0/uikit-1.0.0.jppkg
uikit/1.1.0/uikit-1.1.0.jppkg
```

Publishing writes to folder registries; to serve one over HTTP, copy the folder to the web server. The
`JOEPRO_REGISTRY` environment variable sets the registry for projects that name none.
