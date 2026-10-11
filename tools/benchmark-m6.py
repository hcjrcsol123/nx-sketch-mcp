"""Measure one real NX stdio edit/verify/save chain on an active registered copy."""
from __future__ import annotations

import argparse
import importlib.util
import json
from pathlib import Path
import statistics
import time

spec = importlib.util.spec_from_file_location("m4_bench", Path(__file__).with_name("smoke-m4.py"))
m4 = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m4)


class TimedBench(m4.Bench):
    def __init__(self, launch, window, output):
        super().__init__(launch, window, output)
        self.saved = self.input.with_name("m6-perf-saved.prt")
        if self.saved.exists():
            raise ValueError("performance save target must be absent")
        self.report["timings"] = []
        self.report["timing_method"] = (
            "perf_counter wall time around actual stdio calls and complete asynchronous operations; "
            "operation time includes operation queries and the existing 100ms poll interval, no AI reasoning. "
            "Single-session representative baseline, not a latency guarantee."
        )

    def call(self, name, arguments, expect_error=False):
        started = time.perf_counter()
        try:
            return super().call(name, arguments, expect_error)
        finally:
            self.report["timings"].append({"kind": "call", "tool": name,
                "elapsed_ms": (time.perf_counter() - started) * 1000})

    def operation(self, name, fields, expect_success=True):
        started = time.perf_counter()
        try:
            return super().operation(name, fields, expect_success)
        finally:
            self.report["timings"].append({"kind": "operation", "tool": name,
                "elapsed_ms": (time.perf_counter() - started) * 1000})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--launch-report", required=True, type=Path)
    parser.add_argument("--window-report", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--expression", default="p11")
    parser.add_argument("--value", default="72")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("output must be absent")
    launch = json.loads(args.launch_report.read_text(encoding="utf-8-sig"))
    window = json.loads(args.window_report.read_text(encoding="utf-8-sig"))
    bench = TimedBench(launch, window, args.output)
    try:
        bench.start()
        before = bench.report["before"]
        matches = [(s["journal_id"], c["journal_id"]) for s in before["sketches"]
                   for c in s["constraints"] if c.get("is_driving") is True
                   and c.get("expression_name") == args.expression]
        assert len(matches) == 1, matches
        bench.apply("nx_prepare_operation", {"sketch_id": matches[0][0],
            "constraint_id": matches[0][1], "value": args.value, "unit": "mm"}, "performance-edit")
        bench.save_and_return()
        after = bench.report["after_reopen"]
        edited = next(e for e in after["expressions"] if e["name"] == args.expression)
        assert float(edited["value"]) == float(args.value), edited
        bench.report["model_scale"] = {phase: {key: len(part[key]) for key in
            ("bodies", "features", "sketches", "expressions")} for phase, part in
            (("before", before), ("after_reopen", after))}
        groups = {"read": ("call", "nx_inspect_part"),
                  "prepare": ("operation", "nx_prepare_operation"),
                  "execute": ("operation", "nx_execute_plan"),
                  "verify": ("operation", "nx_verify_part"),
                  "save_and_reopen": ("operation", "nx_save_copy")}
        bench.report["performance_baseline_ms"] = {}
        for stage, (kind, tool) in groups.items():
            values = [t["elapsed_ms"] for t in bench.report["timings"]
                      if t["kind"] == kind and t["tool"] == tool]
            bench.report["performance_baseline_ms"][stage] = {"count": len(values),
                "min": min(values), "median": statistics.median(values), "max": max(values)}
        bench.report["saved_path"] = str(bench.saved)
        bench.report["result"] = "PASS"
    except Exception as exc:
        bench.report["failure"] = repr(exc)
    finally:
        bench.finish()
    print(json.dumps({key: bench.report.get(key) for key in
        ("result", "failure", "model_scale", "performance_baseline_ms", "saved_path")}, ensure_ascii=False))
    return 0 if bench.report["result"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
