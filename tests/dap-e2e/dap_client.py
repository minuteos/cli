#!/usr/bin/env python3
"""End-to-end test: drive `minuteos dap` debugging the cortex-m3 ELF under qemu.

Flow: initialize -> launch (config or inline) -> setBreakpoints ->
configurationDone -> stopped(breakpoint) -> threads/stackTrace/scopes/variables
-> evaluate -> next (step) -> continue -> hit again -> readMemory -> disconnect.
"""
import json
import os
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
            # headers
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
            body = f.read(length)
            msg = json.loads(body)
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


def check(cond, what):
    if not cond:
        raise AssertionError(what)
    print(f"  ok: {what}")


def main():
    main_c = os.path.join(ROOT, "src", "main.c")
    client = DapClient(["dotnet", CLI, "dap", "--verbose-log"], cwd=ROOT)

    r = client.request("initialize", {"adapterID": "minute-debug", "clientName": "e2e"})
    check(r["body"]["supportsConfigurationDoneRequest"], "initialize reports capabilities")

    # Launch via config-name resolution (the boundary-crossing feature) with an
    # inline gdb override for machines that only have gdb-multiarch.
    client.request("launch", {"config": "qemu", "gdb": GDB, "cwd": ROOT}, timeout=60)
    client.wait_event("initialized")
    print("  ok: launch via {config: qemu} resolved through the build system")

    # breakpoint on the printf line inside the loop
    bp_line = next(i + 1 for i, l in enumerate(open(main_c)) if "tick %d" in l)
    r = client.request("setBreakpoints", {
        "source": {"path": main_c},
        "breakpoints": [{"line": bp_line}],
    })
    bps = r["body"]["breakpoints"]
    check(len(bps) == 1 and bps[0]["verified"], f"breakpoint verified at line {bp_line}")
    bp_id = bps[0]["id"]

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
    check(frames[0]["source"]["path"].endswith("main.c"), "frame maps to main.c")
    frame_id = frames[0]["id"]

    r = client.request("scopes", {"frameId": frame_id})
    scopes = {s["name"]: s["variablesReference"] for s in r["body"]["scopes"]}
    check("Local" in scopes and "Registers" in scopes, "scopes include Local and Registers")

    r = client.request("variables", {"variablesReference": scopes["Local"]})
    local_vars = {v["name"]: v for v in r["body"]["variables"]}
    check("i" in local_vars and local_vars["i"]["value"] == "0", "local i == 0 on first hit")
    check("total" in local_vars, "local total visible")

    r = client.request("variables", {"variablesReference": scopes["Registers"]})
    regs = {v["name"]: v for v in r["body"]["variables"]}
    check("pc" in regs or "r0" in regs, "registers listed")

    r = client.request("variables", {"variablesReference": scopes["Global"]})
    global_vars = {v["name"]: v for v in r["body"]["variables"]}
    check("counter" in global_vars, "global counter visible")
    check(global_vars["counter"]["value"] == "1", "counter == 1 at first breakpoint hit")

    r = client.request("evaluate", {"expression": "counter + limit", "frameId": frame_id})
    check(r["body"]["result"] == "6", "evaluate counter + limit == 6")

    # step over the printf
    client.request("next", {"threadId": thread_id})
    evt = client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "step")
    r = client.request("stackTrace", {"threadId": thread_id})
    check(r["body"]["stackFrames"][0]["line"] != bp_line, "step moved past the breakpoint line")

    # continue -> hit the breakpoint again with i == 1
    client.request("continue", {"threadId": thread_id})
    client.wait_event("continued")
    evt = client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "breakpoint")
    r = client.request("stackTrace", {"threadId": thread_id})
    frame_id = r["body"]["stackFrames"][0]["id"]
    r = client.request("scopes", {"frameId": frame_id})
    local_ref = next(s["variablesReference"] for s in r["body"]["scopes"] if s["name"] == "Local")
    r = client.request("variables", {"variablesReference": local_ref})
    i_var = next(v for v in r["body"]["variables"] if v["name"] == "i")
    check(i_var["value"] == "1", "local i == 1 on second hit")

    # readMemory at the current pc
    pc = client.request("stackTrace", {"threadId": thread_id})["body"]["stackFrames"][0][
        "instructionPointerReference"]
    r = client.request("readMemory", {"memoryReference": pc, "count": 4})
    check(len(r["body"]["data"]) > 0, "readMemory returns data")

    # clear breakpoints, continue, collect semihosting output
    r = client.request("setBreakpoints", {"source": {"path": main_c}, "breakpoints": []})
    client.request("continue", {"threadId": thread_id})
    client.wait_event("continued")

    output = ""
    deadline = time.time() + 15
    while "done counter=15" not in output and time.time() < deadline:
        try:
            evt = client.events.get(timeout=1)
        except queue.Empty:
            continue
        if evt["event"] == "output" and evt["body"]["category"] == "stdout":
            output += evt["body"]["output"]
    check("tick 4 total 15" in output, "semihosting output forwarded as output events")
    check("done counter=15" in output, "program ran to completion")

    # pause the spinning target
    client.request("pause", {"threadId": thread_id})
    client.wait_event("stopped", predicate=lambda e: e["body"]["reason"] == "pause")
    check(True, "pause stops the spinning target")

    client.request("disconnect", {})
    client.proc.stdin.close()
    check(client.proc.wait(timeout=10) == 0, "adapter exits cleanly")
    print("\nE2E DAP test PASSED")


if __name__ == "__main__":
    main()
