# External dependencies (git submodules)

A project pulls in library code — the minuteOS `lib` and `lib-arm` repos, and any
other libraries — as **lib roots**: directories in the project root whose name
starts with `lib` and that contain a `targets/` tree. Conventionally these are
**git submodules**.

## How the builder finds them

`ProjectLayout` discovers every `lib*` directory in the project root and treats
each one's `targets/` as a source of components and targets (alongside the
project's own `targets/`). This is purely on-disk discovery — a `lib*` directory
is used whether or not it is declared anywhere.

The builder does **not** fetch anything on its own. If a submodule directory is
empty (a fresh clone without `--recursive`), its components/targets simply aren't
found and the build fails with an "unknown target/component" error.

## Declaring dependencies

Declare external dependencies in `minuteos.yaml`. Each resolves to a directory
that becomes a lib root — the directory does **not** have to live in the project
root or be named `lib*`. There are three kinds, cheapest first:

```yaml
dependencies:
  # 1. path - reference an existing directory (a submodule checked out here or
  #    shared elsewhere). Nothing is fetched.
  - name: lib
    path: ../shared/lib

  # 2. tar - fetch a specific commit's tarball into a shared cache. No .git, no
  #    full clone; content-addressed by commit, shared across projects.
  - name: lib-arm
    git: https://github.com/minuteos/lib-arm
    commit: 0a1b2c3d
    # or an explicit tarball:  tar: https://.../lib-arm-<sha>.tar.gz

  # 3. clone - a full git clone into the project.
  - name: lib-vendor
    git: https://example.com/vendor/lib
    ref: main            # optional branch/tag/commit
```

- **path** — the fastest option: point at a directory you already have (an
  initialized submodule, or a shared checkout). `restore` only verifies it
  exists.
- **tar** — avoids full clones and submodule history: `restore` downloads the
  commit tarball (GitHub `codeload`, or the generic `<url>/archive/<commit>.tar.gz`,
  or an explicit `tar:` URL / local `.tar.gz`) and extracts it into a cache.
  Because it is keyed by commit, several projects share one copy and nothing is
  re-fetched.
- **clone** — a plain `git clone` into `./<name>`.

### The tarball cache

Tarballs extract to a shared cache — `$MINUTEOS_CACHE`, or `~/.cache/minuteos/deps`
by default — under `<name>/<commit>/`. It is content-addressed, so a dependency
already cached is reported `cached` and never re-downloaded.

## Restoring

```bash
minuteos restore
```

`restore`:

1. runs `git submodule update --init --recursive` if the project has a
   `.gitmodules` (initializing lib submodules), then
2. resolves each declared dependency by its kind — verifies a **path**, fetches a
   **tar**ball into the cache, or **clone**s.

It is idempotent — a path/clone already present is `present`, a cached tarball is
`cached`, nothing is re-fetched. A **path** dependency whose directory is missing
is flagged (initialize its submodule / provide the directory).

`build`/`test`/`run` warn when a declared dependency directory is missing or
empty and point you at `minuteos restore`.

## Recommended workflow

Add the libs as submodules once:

```bash
git submodule add https://github.com/minuteos/lib      lib
git submodule add https://github.com/minuteos/lib-arm  lib-arm
```

Then on any fresh checkout, `minuteos restore` (or `git submodule update --init
--recursive`) makes the project buildable. Declaring the same repos under
`dependencies:` lets `restore` also clone them on a checkout that didn't use
submodules.
