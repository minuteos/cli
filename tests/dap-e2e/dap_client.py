#!/usr/bin/env python3
"""End-to-end test: drive `minuteos dap` against the cortex-m3 example.

Scenario 1 (qemu): initialize -> launch {config} -> breakpoints ->
stopped -> threads/stackTrace/scopes/variables (locals, globals, registers)
-> evaluate -> disassemble -> step -> continue -> second hit -> readMemory ->
semihosting output as `output` events -> pause -> disconnect.

Scenario 2 (renode, skipped when renode is not installed): launch with the
renode server driven over its telnet monitor, SWO via the ITM capture overlay
(firmware stimulus-port writes come back as `output` events), and SVD
peripheral scopes backed by a local .svd file.
"""
import json
import os
import re
import shutil
import subprocess
import sys
import threading
import queue
import time

ROOT = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else os.path.dirname(os.path.abspath(__file__))
CLI = os.environ.get("MINUTEOS_DLL", "../../src/MinuteOS.Cli/bin/Debug/net10.0/minuteos.dll")
GDB = os.environ.get("MINUTEOS_GDB", "gdb-multiarch")


class DapClient:
    def __init__(self, args, cwd):
        self.proc = subprocess.Popen(
            args, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.PIPE)
        self.seq = 0
        self.events = queue.Queue()
        self.responses = {}
        self.lock = threading.Condition()
        threading.Thread(target=self._reader, daemon=True).start()
        threading.Thread(target=self._stderr, daemon=True).start()

    def _stderr(self):
        for line in self.proc.stderr:
            sys.stderr.write("adapter: " + line.decode(errors="replace"))

    def _reader(self):
        f = self.proc.stdout
        while True:
            length = None
            while True:
                line = f.readline()
                if not line:
                    return
                line = line.strip()
                if not line:
                    break
                if line.lower().startswith(b"content-length:"):
                    length = int(line.split(b":")[1])
            msg = json.loads(f.read(length))
            if msg["type"] == "event":
                self.events.put(msg)
            elif msg["type"] == "response":
                with self.lock:
                    self.responses[msg["request_seq"]] = msg
                    self.lock.notify_all()

    def request(self, command, arguments=None, timeout=30, expect_success=True):
        self.seq += 1
        seq = self.seq
        msg = {"seq": seq, "type": "request", "command": command}
        if arguments is not None:
            msg["arguments"] = arguments
        body = json.dumps(msg).encode()
        self.proc.stdin.write(b"Content-Length: %d\r\n\r\n" % len(body) + body)
        self.proc.stdin.flush()
        deadline = time.time() + timeout
        with self.lock:
            while seq not in self.responses:
                remaining = deadline - time.time()
                if remaining <= 0:
                    raise TimeoutError(f"no response to {command}")
                self.lock.wait(remaining)
            resp = self.responses.pop(seq)
        if expect_success and not resp.get("success"):
            raise RuntimeError(f"{command} failed: {resp.get('message')}")
        return resp

    def wait_event(self, name, timeout=30, predicate=None):
        deadline = time.time() + timeout
        while True:
            remaining = deadline - time.time()
            if remaining <= 0:
                raise TimeoutError(f"no '{name}' event")
            evt = self.events.get(timeout=remaining)
            if evt["event"] == name and (predicate is None or predicate(evt)):
                return evt

    def collect_output(self, until, timeout=30, category="stdout"):
        output = ""
        deadline = time.time() + timeout
        while until not in output and time.time() < deadline:
            try:
                evt = self.events.get(timeout=1)
            except queue.Empty:
                continue
            if evt["event"] == "output" and evt["body"]["category"] == category:
                output += evt["body"]["output"]
        return output

    def finish(self):
        self.request("disconnect", {})
        self.proc.stdin.close()
        return self.proc.wait(timeout=10)


def check(cond, what):
    if not cond:
        raise AssertionError(what)
    print(f"  ok: {what}")


def strip_ansi(s):
    return re.sub(r"\x1b\[[0-9;]*m", "", s)


def scenario_qemu():
    print("== scenario: qemu")
    main_c = os.path.join(ROOT, "src", "main.c")
    client = DapClient(["dotnet", CLI, "dap", "--verbose-log"], cwd=ROOT)

    r = client.request("initialize", {"adapterID": "minute-debug", "clientName": "e2e"})
    check(r["body"]["supportsConfigurationDoneRequest"], "initialize reports capabilities")
    check(r["body"]["supportsDisassembleRequest"], "disassemble capability advertised")

    # Launch via config-name resolution with an inline gdb override.
    client.request("launch", {"config": "qemu", "gdb": GDB, "cwd": ROOT}, timeout=60)
    client.wait_event("initialized")
    print("  ok: launch via {config: qemu} resolved through the build system")

    bp_line = next(i + 1 for i, l in enumerate(open(main_c)) if "tick %d" in l)
    r = client.request("setBreakpoints", {
        "source": {"path": main_c},
        "breakpoints": [{"line": bp_line}],
    })
    bps = r["body"]["breakpoints"]
    check(len(bps) == 1 and bps[0]["verified"], f"breakpoint verified at line {bp_line}")

    client.request("setExceptionBreakpoints", {"filters": ["0x7F0", "0x1"]})
    client.request("configurationDone")

    evt = client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "breakpoint")
    thread_id = evt["body"]["threadId"]
    check(True, f"stopped at breakpoint (thread {thread_id})")

    r = client.request("threads")
    check(any(t["id"] == thread_id for t in r["body"]["threads"]), "threads lists the stopped thread")

    r = client.request("stackTrace", {"threadId": thread_id})
    frames = r["body"]["stackFrames"]
    check(frames[0]["name"] == "main", "top frame is main")
    check(frames[0]["line"] == bp_line, f"stopped at line {bp_line}")
    frame_id = frames[0]["id"]
    pc = frames[0]["instructionPointerReference"]

    r = client.request("scopes", {"frameId": frame_id})
    scopes = {s["name"]: s["variablesReference"] for s in r["body"]["scopes"]}
    check("Local" in scopes and "Registers" in scopes, "scopes include Local and Registers")

    r = client.request("variables", {"variablesReference": scopes["Local"]})
    local_vars = {v["name"]: v for v in r["body"]["variables"]}
    check("i" in local_vars and local_vars["i"]["value"] == "0", "local i == 0 on first hit")

    r = client.request("variables", {"variablesReference": scopes["Registers"]})
    regs = {v["name"]: v for v in r["body"]["variables"]}
    check("pc" in regs or "r0" in regs, "registers listed")

    r = client.request("variables", {"variablesReference": scopes["Global"]})
    global_vars = {v["name"]: v for v in r["body"]["variables"]}
    check("counter" in global_vars, "global counter visible")

    r = client.request("evaluate", {"expression": "counter + limit", "frameId": frame_id})
    check(r["body"]["result"] == "6", "evaluate counter + limit == 6")

    # disassemble a window around the pc
    r = client.request("disassemble", {"memoryReference": pc, "instructionOffset": -2, "count": 8})
    instructions = r["body"]["instructions"]
    check(len(instructions) == 8, "disassemble returns the requested window")
    check(any(int(i["address"], 16) == int(pc, 16) for i in instructions),
          "disassembly window contains the pc")
    check(any(i.get("location") for i in instructions), "disassembly maps to source")

    # step over, then continue to the second hit
    client.request("next", {"threadId": thread_id})
    client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "step")
    client.request("continue", {"threadId": thread_id})
    client.wait_event("continued")
    client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "breakpoint")
    r = client.request("stackTrace", {"threadId": thread_id})
    frame_id = r["body"]["stackFrames"][0]["id"]
    r = client.request("scopes", {"frameId": frame_id})
    local_ref = next(s["variablesReference"] for s in r["body"]["scopes"] if s["name"] == "Local")
    r = client.request("variables", {"variablesReference": local_ref})
    i_var = next(v for v in r["body"]["variables"] if v["name"] == "i")
    check(i_var["value"] == "1", "local i == 1 on second hit")

    r = client.request("readMemory", {"memoryReference": pc, "count": 4})
    check(len(r["body"]["data"]) > 0, "readMemory returns data")

    client.request("setBreakpoints", {"source": {"path": main_c}, "breakpoints": []})
    client.request("continue", {"threadId": thread_id})
    client.wait_event("continued")

    output = client.collect_output("done counter=15", timeout=15)
    check("tick 4 total 15" in output, "semihosting output forwarded as output events")
    check("done counter=15" in output, "program ran to completion")

    client.request("pause", {"threadId": thread_id})
    client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "pause")
    check(True, "pause stops the spinning target")

    check(client.finish() == 0, "adapter exits cleanly")


def scenario_renode():
    if not shutil.which("renode"):
        print("== scenario: renode SKIPPED (renode not installed)")
        return
    print("== scenario: renode (SWO + SVD)")

    elf = os.path.join(ROOT, "out", "renode", "dap-e2e.elf")
    repl = os.path.join(ROOT, "renode", "lm3s.repl")
    svd = os.path.join(ROOT, "test.svd")
    client = DapClient(["dotnet", CLI, "dap", "--verbose-log"], cwd=ROOT)

    client.request("initialize", {"adapterID": "minute-debug", "clientName": "e2e"})
    client.request("launch", {
        "config": "renode",
        "gdb": GDB,
        "cwd": ROOT,
        "server": {
            "type": "renode",
            "commands": [
                "mach create",
                f"machine LoadPlatformDescription @{repl}",
                f"sysbus LoadELF @{elf}",
            ],
        },
        "swo": "renode",
        "svd": svd,
    }, timeout=120)
    client.wait_event("initialized", timeout=120)
    print("  ok: launch with the renode server (telnet monitor + ITM/ROM-table overlay)")

    client.request("setExceptionBreakpoints", {"filters": ["0x7F0"]})
    client.request("configurationDone")

    # The firmware writes "swo-hello\n" to ITM stimulus port 0; the overlay
    # streams it back and the adapter decodes it into output events.
    output = client.collect_output("swo-hello", timeout=60)
    check("swo-hello" in output, "SWO stimulus-port writes decoded into output events")

    thread_id = client.request("threads")["body"]["threads"][0]["id"]
    client.request("pause", {"threadId": thread_id})
    client.wait_event("stopped")
    check(True, "pause stops the target")

    frame_id = client.request("stackTrace", {"threadId": thread_id})["body"]["stackFrames"][0]["id"]
    scopes = {s["name"]: s["variablesReference"]
              for s in client.request("scopes", {"frameId": frame_id})["body"]["scopes"]}
    check("Peripherals (TESTCHIP)" in scopes, "SVD peripherals scope present")

    peripherals = client.request("variables",
        {"variablesReference": scopes["Peripherals (TESTCHIP)"]})["body"]["variables"]
    testp = next(p for p in peripherals if p["name"] == "TESTP")
    check(True, "TESTP peripheral listed")

    registers = client.request("variables", {"variablesReference": testp["variablesReference"]})["body"]["variables"]
    magic = next(r for r in registers if r["name"].strip() == "MAGIC")
    check("0x0000cafe" in strip_ansi(magic["value"]), "MAGIC register reads the firmware-written value")

    fields = client.request("variables", {"variablesReference": magic["variablesReference"]})["body"]["variables"]
    lo = next(f for f in fields if f["name"].strip() == "LO")
    check("0xcafe" in strip_ansi(lo["value"]), "MAGIC.LO bitfield decoded")
    hi = next(f for f in fields if f["name"].strip() == "HI")
    check(strip_ansi(hi["value"]).startswith("0"), "MAGIC.HI bitfield decoded as zero")

    # SWO profiling: the firmware "samples itself" through the overlay's DWT
    # PC-sample emit register with a 3:1 skew between two functions.
    client.request("minuteos.profile.start")
    client.request("continue", {"threadId": thread_id})
    client.wait_event("continued")
    time.sleep(3)
    report = client.request("minuteos.profile.stop", {"top": 10}, timeout=60)["body"]
    check(report["totalSamples"] > 0, f"PC samples collected ({report['totalSamples']})")
    functions = {f["name"]: f for f in report["functions"]}
    check("profiled_hot" in functions and "profiled_cold" in functions,
          "samples symbolicated to function names from the ELF")
    check(functions["profiled_hot"]["samples"] > functions["profiled_cold"]["samples"],
          "sample weights follow the 3:1 call skew")
    check(report["unresolvedSamples"] == 0, "all samples resolved")

    check(client.finish() == 0, "adapter exits cleanly")
    deadline = time.time() + 15  # renode (mono) can take a few seconds to exit
    while time.time() < deadline and \
            subprocess.run(["pgrep", "-f", "renode.*--disable-gui"], capture_output=True).returncode == 0:
        time.sleep(0.5)
    check(subprocess.run(["pgrep", "-f", "renode.*--disable-gui"], capture_output=True).returncode != 0,
          "renode torn down")


if __name__ == "__main__":
    scenario_qemu()
    scenario_renode()
    print("\nE2E DAP test PASSED")
