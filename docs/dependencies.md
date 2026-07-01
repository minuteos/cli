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

Declare external dependencies in `minuteos.yaml` so the tool can restore them:

```yaml
name: my-firmware

dependencies:
  - name: lib
    git: https://github.com/minuteos/lib
  - name: lib-arm
    git: https://github.com/minuteos/lib-arm
    ref: main            # optional branch/tag/commit

configurations:
  qemu:
    target: qemu-arm
    components: [kernel]
```

Each dependency resolves to a directory (`path`, else `name`) under the project
root. Because discovery keys off the `lib*` prefix, name your lib dependencies
accordingly (`lib`, `lib-arm`, `lib-vendor`, ...).

## Restoring

```bash
minuteos restore
```

`restore`:

1. runs `git submodule update --init --recursive` if the project has a
   `.gitmodules` (initializing lib submodules), then
2. **clones** any declared dependency that is still missing and has a `git:` URL
   (checking out `ref` if given).

It is idempotent — dependencies already present are reported as `present`. A
dependency that is missing and has no `git:` URL is flagged (initialize its
submodule manually).

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
