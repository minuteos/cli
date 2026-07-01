# hello-host

The smallest possible minuteOS project: one source file, one host configuration.

```bash
minuteos build -c host      # compile + link -> out/host/hello.elf
minuteos run   -c host      # build, then execute it
```

`minuteos.yaml` is the whole configuration:

```yaml
name: hello
configurations:
  host:
    target: host        # compile/link for the build machine
    components: []       # no library components
```

There is no toolchain configuration — the `host` target uses plain `gcc`/`g++`.
Rebuilds are incremental (a no-op `build` runs nothing); see `docs/`.
