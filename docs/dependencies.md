# External dependencies

A project pulls in library code — the minuteOS `lib` and `lib-arm` repos, and any
other libraries — as **lib roots**: directories containing a `targets/` tree of
components and targets.

## How the builder finds them

`ProjectLayout` uses, as lib roots:

- every `lib*` directory in the project root (purely on-disk discovery — used
  whether or not it is declared anywhere), and
- the resolved directory of every **declared dependency** (which may live outside
  the project root, e.g. in the tarball cache).

The build itself never fetches anything. If a dependency directory is empty (an
uninitialized submodule, an unfetched tarball), the build warns and points at
`minuteos restore`.

## Declaring dependencies

Two kinds:

```yaml
dependencies:
  # 1. path - an existing directory, typically a git submodule. Git pins the
  #    version through the submodule; nothing is fetched. `path` defaults to
  #    `name`, so a submodule at ./lib is just:
  - name: lib
  # ...and a checkout shared between projects:
  - name: lib-shared
    path: ../shared/lib

  # 2. remote - fetched as a commit tarball into a shared cache. Never a full
  #    clone, no .git, no history. `ref` is a commit SHA, branch, or tag
  #    (default: HEAD).
  - name: lib-arm
    git: https://github.com/minuteos/lib-arm
    ref: main
  # or an explicit tarball URL / local .tar.gz:
  - name: lib-vendor
    tar: https://example.com/lib-vendor-1.2.tar.gz
```

### path — submodules

A submodule is already version-pinned *by git*; the tool doesn't duplicate that.
Declaring it gives you `restore` (which initializes submodules) and the
missing-dependency warning. Any existing directory works, including ones outside
the project.

### remote — commit tarballs, `ref`, and the lock file

A remote dependency is fetched as a **tarball of one commit** (GitHub `codeload`,
the generic `<url>/archive/<commit>.tar.gz`, or an explicit `tar:`) into a shared
content-addressed cache — `$MINUTEOS_CACHE`, default `~/.cache/minuteos/deps`,
under `<name>/<commit>/`. Several projects share one copy; nothing is ever
re-fetched.

`ref` decides the version:

- **commit SHA** (7–40 hex chars) — immutable: it is its own cache key; no
  resolution, ever.
- **branch / tag / absent (HEAD)** — mutable: `restore` resolves it to a commit
  with `git ls-remote` (one cheap request, no clone) and records the result in
  **`minuteos.lock`** next to `minuteos.yaml`. Builds read the lock — they are
  offline and deterministic between restores. Re-running `restore` re-resolves,
  i.e. moves you to the current branch head / tag target. Commit the lock file
  for reproducible team builds (or ignore it to always float on restore).

Why restore-time resolution rather than per-build: resolving on every build would
add a network round-trip to each build and let inputs move mid-project; and an
offline fallback would be needed anyway — which is exactly the lock.

**Avoiding lock files entirely:** pin every remote dependency to a commit SHA.
The lock only exists to make mutable refs safe — a project with only
commit-pinned (or path) dependencies never produces a `minuteos.lock` at all.

## Restoring

```bash
minuteos restore
```

1. Runs `git submodule update --init --recursive` if the project has a
   `.gitmodules` (covers path dependencies that are submodules).
2. Verifies each **path** dependency exists.
3. For each **remote** dependency: resolves a mutable `ref` to a commit (updating
   `minuteos.lock`), then fetches the commit tarball into the cache if not
   already there. When the network is unavailable, a previously locked commit is
   used (`offline - using locked <sha>`).

Idempotent: `present` / `cached (main -> 0a1b2c3d)` on re-runs.

`restore --frozen` never re-resolves: mutable refs must already be in
`minuteos.lock` (it fails otherwise). Use it in CI so builds can't silently
float to a moved branch.

## Recommended workflow

- Libraries you also **edit** alongside the project: submodules —
  `git submodule add … lib`, declare `- name: lib`, done.
- Libraries you only **consume**: remote deps pinned to a commit (fully
  reproducible with no lock), or on a branch/tag with `minuteos.lock` committed.
